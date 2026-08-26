#include "neo_admin_menu.h"

#include "adminsystem.h"
#include "commands.h"
#include "common.h"
#include "cstrike15_usermessages.pb.h"
#include "detours.h"
#include "engine/igameeventsystem.h"
#include "entity/ccsplayercontroller.h"
#include "entity/cpointorient.h"
#include "entity/cpointworldtext.h"
#include "gamesystem.h"
#include "neo_admin_game_permissions.h"
#include "neo_ptt.h"
#include "networksystem/inetworkmessages.h"
#include "playermanager.h"
#include "recipientfilters.h"
#include "utils/entity.h"
#include "vendor/nlohmann/json.hpp"

#include <algorithm>
#include <array>
#include <cctype>
#include <chrono>
#include <cstdint>
#include <ctime>
#include <string>
#include <vector>

namespace
{
constexpr std::uint64_t kSteamId64Base = 76561197960265728ULL;
constexpr int kMenuSeconds = 45;
constexpr int kPlayerPageSize = 5;
constexpr int kMapPageSize = 5;
constexpr std::uint64_t kMenuButtons = IN_FORWARD | IN_BACK | IN_USE;
constexpr auto kMenuInputDebounce = std::chrono::milliseconds(140);

enum class ChoiceKind
{
    None,
    Close,
    Main,
    Players,
    Bots,
    Match,
    Maps,
    Announcement,
    SelectPlayer,
    PreviousPlayers,
    NextPlayers,
    SelectMap,
    PreviousMaps,
    NextMaps,
    ConfirmMap,
    Kick,
    Punishment,
    RemoveMute,
    RemoveGag,
    Slay,
    Respawn,
    MoveMenu,
    MoveTeam,
    Reason,
    CustomReason,
    Duration,
    AddBot,
    RemoveBots,
    MatchCommand,
};

enum class Punishment
{
    None,
    Ban,
    Mute,
    Gag,
};

enum class InputPurpose
{
    None,
    Reason,
    Announcement,
};

struct Choice
{
    ChoiceKind kind = ChoiceKind::None;
    int value = 0;
};

struct Actor
{
    std::uint64_t steam_id = 0;
    std::uint64_t permissions = 0;
    std::string account_id;
    std::string display_name;
};

struct MenuSession
{
    bool active = false;
    std::uint64_t actor_steam_id = 0;
    std::time_t expires_at = 0;
    std::array<Choice, 11> choices{};
    std::array<std::string, 11> labels{};
    std::string title;
    int selected = 0;
    std::uint64_t last_buttons = 0;
    std::uint64_t latched_buttons = 0;
    std::array<std::chrono::steady_clock::time_point, 3> last_input_at{};
    CHandle<CPointWorldText> display;
    int player_page = 0;
    int map_page = 0;
    std::vector<std::string> map_catalog;
    std::string pending_map;
    ZEPlayerHandle target;
    std::uint64_t target_steam_id = 0;
    std::string target_name;
    Punishment punishment = Punishment::None;
    InputPurpose input = InputPurpose::None;
    std::string reason;
};

std::array<MenuSession, MAXPLAYERS> g_sessions{};
std::array<std::uint64_t, MAXPLAYERS> g_consumed_buttons{};

bool Has(std::uint64_t permissions, neo_admin::GamePermission permission)
{
    return neo_admin::HasGamePermission(permissions, permission);
}

std::uint64_t LegacyPermissions(std::uint64_t flags)
{
    if ((flags & ADMFLAG_ROOT) != 0)
        return neo_admin::kAllGamePermissions;

    std::uint64_t result = 0;
    if ((flags & (ADMFLAG_KICK | ADMFLAG_SLAY)) != 0)
        result |= neo_admin::ToGameMask(neo_admin::GamePermission::ModeratePlayers);
    if ((flags & (ADMFLAG_BAN | ADMFLAG_UNBAN)) != 0)
    {
        result |= neo_admin::ToGameMask(neo_admin::GamePermission::ManageBans);
        result |= neo_admin::ToGameMask(neo_admin::GamePermission::ManageDiscipline);
    }
    if ((flags & ADMFLAG_CHANGEMAP) != 0)
    {
        result |= neo_admin::ToGameMask(neo_admin::GamePermission::ChangeMap);
        result |= neo_admin::ToGameMask(neo_admin::GamePermission::ManageMapRotation);
    }
    if ((flags & (ADMFLAG_CONVARS | ADMFLAG_RCON)) != 0)
    {
        result |= neo_admin::ToGameMask(neo_admin::GamePermission::ControlBots);
        result |= neo_admin::ToGameMask(neo_admin::GamePermission::ControlMatch);
    }
    if ((flags & ADMFLAG_CHAT) != 0)
    {
        result |= neo_admin::ToGameMask(neo_admin::GamePermission::ManageAnnouncements);
    }
    return result;
}

std::uint64_t SteamIdFor(CCSPlayerController* player)
{
    if (!player || player->IsBot())
        return 0;

    const std::uint64_t controller_steam_id = player->m_steamID();
    if (controller_steam_id >= kSteamId64Base)
        return controller_steam_id;

    ZEPlayer* managed = player->GetZEPlayer();
    if (managed)
    {
        const CSteamID* managed_id = managed->IsAuthenticated()
            ? managed->GetSteamId()
            : managed->GetUnauthenticatedSteamId();
        const std::uint64_t managed_steam_id = managed_id
            ? managed_id->ConvertToUint64() : 0;
        if (managed_steam_id >= kSteamId64Base)
            return managed_steam_id;
    }
    return 0;
}

bool ResolveActor(CCSPlayerController* player, Actor& actor)
{
    if (!player || !player->IsConnected() || player->IsBot() || player->m_bIsHLTV())
        return false;
    actor.steam_id = SteamIdFor(player);
    if (actor.steam_id == 0)
        return false;
    if (NeoPtt_GetInGameAdmin(actor.steam_id, actor.account_id,
            actor.display_name, actor.permissions))
        return actor.permissions != 0;

    ZEPlayer* managed = player->GetZEPlayer();
    actor.permissions = managed ? LegacyPermissions(managed->GetAdminFlags()) : 0;
    if (actor.permissions == 0)
        return false;
    actor.account_id = "steam:" + std::to_string(actor.steam_id);
    actor.display_name = player->GetPlayerName();
    return true;
}

std::string CleanText(std::string_view input, std::size_t maximum)
{
    while (!input.empty() && std::isspace(static_cast<unsigned char>(input.front())))
        input.remove_prefix(1);
    while (!input.empty() && std::isspace(static_cast<unsigned char>(input.back())))
        input.remove_suffix(1);
    if (input.size() >= 2 && input.front() == '"' && input.back() == '"')
    {
        input.remove_prefix(1);
        input.remove_suffix(1);
    }
    std::string output;
    output.reserve(std::min(maximum, input.size()));
    for (unsigned char ch : input)
    {
        if (output.size() >= maximum)
            break;
        if (ch >= 0x20U && ch != 0x7fU && ch != '\r' && ch != '\n')
            output.push_back(static_cast<char>(ch));
    }
    return output;
}

std::string Lower(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(),
        [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
    return value;
}

std::string MenuText(std::string_view input)
{
    std::string value = CleanText(input, 34);
    return value.empty() ? "Unnamed player" : value;
}

CPointWorldText* EnsureMenuDisplay(CCSPlayerController* player,
    MenuSession& session)
{
    CPointWorldText* text = session.display.Get();
    if (text)
        return text;

    ZEPlayer* managed = player ? player->GetZEPlayer() : nullptr;
    if (!managed)
        return nullptr;
    CPointOrient* orient = managed->GetPointOrient();
    if (!orient)
    {
        managed->CreatePointOrient();
        orient = managed->GetPointOrient();
    }
    if (!orient)
        return nullptr;

    text = CreateEntityByName<CPointWorldText>("point_worldtext");
    if (!text)
        return nullptr;
    text->m_bEnabled(true);
    text->m_bFullbright(true);
    text->m_flFontSize(42.0f);
    text->m_flWorldUnitsPerPx(0.005f);
    text->m_Color = Color(242, 244, 248, 254);
    text->m_nJustifyHorizontal(
        PointWorldTextJustifyHorizontal_t::POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER);
    text->m_nJustifyVertical(
        PointWorldTextJustifyVertical_t::POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER);
    V_strncpy(text->m_FontName, "Verdana Bold", 64);
    text->SetMessage("");
    text->DispatchSpawn();
    session.display.Set(text);

    text->AcceptInput("SetParent", "!activator", orient);
    Vector origin = orient->GetAbsOrigin();
    const QAngle view_angles = orient->GetAbsRotation();
    Vector forward;
    Vector right;
    Vector up;
    AngleVectors(view_angles, &forward, &right, &up);
    origin += forward * 7.0f;
    origin += up * 0.5f;

    QAngle angles;
    angles.x = 0.0f;
    angles.y = AngleNormalize(view_angles.y - 90.0f);
    angles.z = AngleNormalize(-view_angles.x + 90.0f);
    text->Teleport(&origin, &angles, nullptr);
    return text;
}

int FirstChoice(const MenuSession& session)
{
    for (int key = 1; key <= 10; ++key)
    {
        if (session.choices[key].kind != ChoiceKind::None)
            return key;
    }
    return 0;
}

void RenderMenu(CCSPlayerController* player, MenuSession& session)
{
    if (!player || !session.active || session.input != InputPurpose::None)
        return;

    CPointWorldText* display = EnsureMenuDisplay(player, session);
    if (!display)
        return;

    std::string text;
    text.reserve(512);
    text.append("NEO ADMIN\n");
    text.append(session.title);
    text.append("\n\n");

    for (int key = 1; key <= 10; ++key)
    {
        if (session.choices[key].kind == ChoiceKind::None)
            continue;
        if (key == session.selected)
            text.append(">  ");
        else
            text.append("   ");
        text.append(session.labels[key]);
        text.push_back('\n');
    }

    text.append("\nW / S  Navigate     E  Select");
    display->AcceptInput("SetMessage", text.c_str());
}

void HideMenuDisplay(CCSPlayerController* player)
{
    if (!player)
        return;

    const int slot = player->GetPlayerSlot();
    if (slot >= 0 && slot < MAXPLAYERS)
    {
        CPointWorldText* display = g_sessions[slot].display.Get();
        if (display)
            display->Remove();
        g_sessions[slot].display.Set(nullptr);
    }

    INetworkMessageInternal* message =
        g_pNetworkMessages->FindNetworkMessagePartial("ShowMenu");
    if (!message)
        return;
    auto data = message->AllocateMessage()->ToPB<CCSUsrMsg_ShowMenu>();
    data->set_bits_valid_slots(0);
    data->set_display_time(0);
    data->set_menu_string("");
    CSingleRecipientFilter filter(player->GetPlayerSlot());
    g_gameEventSystem->PostEventAbstract(-1, false, &filter, message, data, 0);
    delete data;
}

void CloseMenu(CCSPlayerController* player)
{
    if (!player)
        return;
    const int slot = player->GetPlayerSlot();
    HideMenuDisplay(player);
    if (slot >= 0 && slot < MAXPLAYERS)
        g_sessions[slot] = {};
}

class MenuBuilder
{
public:
    MenuBuilder(MenuSession& session, std::string_view title) : session_(session)
    {
        session_.choices.fill({});
        session_.labels.fill({});
        session_.title = CleanText(title, 48);
        session_.selected = 0;
    }

    void Add(int key, std::string_view label, Choice choice)
    {
        if (key < 1 || key > 10)
            return;
        session_.choices[key] = choice;
        session_.labels[key] = CleanText(label, 48);
    }

    bool Send(CCSPlayerController* player)
    {
        if (!player || !player->IsConnected())
            return false;

        session_.active = true;
        session_.expires_at = std::time(nullptr) + kMenuSeconds;
        session_.selected = FirstChoice(session_);
        RenderMenu(player, session_);
        return true;
    }

private:
    MenuSession& session_;
};

bool ResolveTarget(MenuSession& session, CCSPlayerController*& controller,
    ZEPlayer*& managed)
{
    managed = session.target.Get();
    if (!managed || !managed->IsConnected())
        return false;
    controller = CCSPlayerController::FromSlot(managed->GetPlayerSlot());
    if (!controller || !controller->IsConnected() || controller->m_bIsHLTV())
        return false;
    const std::uint64_t current_steam_id = SteamIdFor(controller);
    if (session.target_steam_id != 0 && current_steam_id != session.target_steam_id)
        return false;
    if (session.target_steam_id == 0 && !controller->IsBot())
        return false;
    session.target_name = controller->GetPlayerName();
    return true;
}

std::string TargetLabel(MenuSession& session)
{
    return session.target_name + (session.target_steam_id == 0
        ? "" : " (" + std::to_string(session.target_steam_id) + ")");
}

void ShowMain(CCSPlayerController* player, const Actor& actor);
void ShowPlayers(CCSPlayerController* player, const Actor& actor, int page);
void ShowTarget(CCSPlayerController* player, const Actor& actor);
void ShowReasons(CCSPlayerController* player, const Actor& actor);
void ShowDurations(CCSPlayerController* player, const Actor& actor);

void ShowMain(CCSPlayerController* player, const Actor& actor)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    session.actor_steam_id = actor.steam_id;
    session.input = InputPurpose::None;
    MenuBuilder menu(session, actor.display_name);
    int key = 1;
    if (Has(actor.permissions, neo_admin::GamePermission::ModeratePlayers) ||
        Has(actor.permissions, neo_admin::GamePermission::ManageBans) ||
        Has(actor.permissions, neo_admin::GamePermission::ManageDiscipline))
        menu.Add(key++, "Player management", {ChoiceKind::Players});
    if (Has(actor.permissions, neo_admin::GamePermission::ControlBots))
        menu.Add(key++, "Bot management", {ChoiceKind::Bots});
    if (Has(actor.permissions, neo_admin::GamePermission::ControlMatch))
        menu.Add(key++, "Match control", {ChoiceKind::Match});
    if (Has(actor.permissions, neo_admin::GamePermission::ChangeMap))
        menu.Add(key++, "Map selector", {ChoiceKind::Maps});
    if (Has(actor.permissions, neo_admin::GamePermission::ManageAnnouncements))
        menu.Add(key++, "Send announcement", {ChoiceKind::Announcement});
    menu.Add(10, "Close", {ChoiceKind::Close});
    menu.Send(player);
}

void ShowPlayers(CCSPlayerController* player, const Actor& actor, int page)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    std::vector<int> slots;
    if (GetGlobals())
    {
        for (int slot = 0; slot < GetGlobals()->maxClients; ++slot)
        {
            CCSPlayerController* candidate = CCSPlayerController::FromSlot(slot);
            if (candidate && candidate->IsConnected() && !candidate->m_bIsHLTV())
                slots.push_back(slot);
        }
    }
    const int pages = std::max(1, static_cast<int>(
        (slots.size() + kPlayerPageSize - 1) / kPlayerPageSize));
    session.player_page = std::clamp(page, 0, pages - 1);
    MenuBuilder menu(session, "Select player");
    const int first = session.player_page * kPlayerPageSize;
    const int last = std::min(first + kPlayerPageSize,
        static_cast<int>(slots.size()));
    int key = 1;
    for (int index = first; index < last; ++index)
    {
        CCSPlayerController* candidate = CCSPlayerController::FromSlot(slots[index]);
        std::string label = MenuText(candidate->GetPlayerName());
        if (candidate->IsBot())
            label.append(" [BOT]");
        menu.Add(key++, label, {ChoiceKind::SelectPlayer, slots[index]});
    }
    if (session.player_page > 0)
        menu.Add(8, "Previous page", {ChoiceKind::PreviousPlayers});
    if (session.player_page + 1 < pages)
        menu.Add(9, "Next page", {ChoiceKind::NextPlayers});
    menu.Add(10, "Back", {ChoiceKind::Main});
    menu.Send(player);
    if (slots.empty())
        ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX "No targetable players are connected.");
}

void ShowTarget(CCSPlayerController* player, const Actor& actor)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    CCSPlayerController* target_controller = nullptr;
    ZEPlayer* target = nullptr;
    if (!ResolveTarget(session, target_controller, target))
    {
        ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX "That player is no longer connected.");
        ShowPlayers(player, actor, session.player_page);
        return;
    }

    MenuBuilder menu(session, MenuText(session.target_name));
    int key = 1;
    if (Has(actor.permissions, neo_admin::GamePermission::ModeratePlayers))
        menu.Add(key++, "Kick", {ChoiceKind::Kick});
    if (session.target_steam_id >= kSteamId64Base &&
        Has(actor.permissions, neo_admin::GamePermission::ManageBans))
        menu.Add(key++, "Ban", {ChoiceKind::Punishment, static_cast<int>(Punishment::Ban)});
    if (session.target_steam_id >= kSteamId64Base &&
        Has(actor.permissions, neo_admin::GamePermission::ManageDiscipline))
    {
        menu.Add(key++, target->IsMuted() ? "Unmute" : "Mute",
            {target->IsMuted() ? ChoiceKind::RemoveMute : ChoiceKind::Punishment,
             static_cast<int>(Punishment::Mute)});
        menu.Add(key++, target->IsGagged() ? "Ungag" : "Gag",
            {target->IsGagged() ? ChoiceKind::RemoveGag : ChoiceKind::Punishment,
             static_cast<int>(Punishment::Gag)});
    }
    if (Has(actor.permissions, neo_admin::GamePermission::ModeratePlayers))
    {
        CCSPlayerPawn* pawn = target_controller->GetPlayerPawn();
        menu.Add(key++, pawn && pawn->IsAlive() ? "Slay" : "Respawn",
            {pawn && pawn->IsAlive() ? ChoiceKind::Slay : ChoiceKind::Respawn});
        menu.Add(key++, "Move team", {ChoiceKind::MoveMenu});
    }
    menu.Add(10, "Player list", {ChoiceKind::Players});
    menu.Send(player);
}

void ShowMoveMenu(CCSPlayerController* player)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    MenuBuilder menu(session, "Move " + MenuText(session.target_name));
    menu.Add(1, "Terrorists", {ChoiceKind::MoveTeam, CS_TEAM_T});
    menu.Add(2, "Counter-Terrorists", {ChoiceKind::MoveTeam, CS_TEAM_CT});
    menu.Add(3, "Spectator", {ChoiceKind::MoveTeam, CS_TEAM_SPECTATOR});
    menu.Add(10, "Back", {ChoiceKind::SelectPlayer, session.target.Get()
        ? session.target.Get()->GetPlayerSlot().Get() : -1});
    menu.Send(player);
}

void ShowReasons(CCSPlayerController* player, const Actor&)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    const char* title = session.punishment == Punishment::Ban ? "Ban reason" :
        session.punishment == Punishment::Mute ? "Mute reason" : "Gag reason";
    MenuBuilder menu(session, title);
    if (session.punishment == Punishment::Ban)
    {
        menu.Add(1, "Cheating", {ChoiceKind::Reason, 1});
        menu.Add(2, "Griefing", {ChoiceKind::Reason, 2});
        menu.Add(3, "Exploiting", {ChoiceKind::Reason, 3});
        menu.Add(4, "Abusive behavior", {ChoiceKind::Reason, 4});
    }
    else if (session.punishment == Punishment::Mute)
    {
        menu.Add(1, "Voice abuse", {ChoiceKind::Reason, 5});
        menu.Add(2, "Microphone spam", {ChoiceKind::Reason, 6});
        menu.Add(3, "Harassment", {ChoiceKind::Reason, 7});
    }
    else
    {
        menu.Add(1, "Chat spam", {ChoiceKind::Reason, 8});
        menu.Add(2, "Abusive language", {ChoiceKind::Reason, 9});
        menu.Add(3, "Harassment", {ChoiceKind::Reason, 7});
    }
    menu.Add(7, "Custom reason", {ChoiceKind::CustomReason});
    menu.Add(10, "Back", {ChoiceKind::SelectPlayer,
        session.target.Get() ? session.target.Get()->GetPlayerSlot().Get() : -1});
    menu.Send(player);
}

std::string ReasonFor(int value)
{
    switch (value)
    {
        case 1: return "Cheating";
        case 2: return "Griefing";
        case 3: return "Exploiting";
        case 4: return "Abusive behavior";
        case 5: return "Voice abuse";
        case 6: return "Microphone spam";
        case 7: return "Harassment";
        case 8: return "Chat spam";
        case 9: return "Abusive language";
        default: return "Administrator action";
    }
}

void ShowDurations(CCSPlayerController* player, const Actor&)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    MenuBuilder menu(session, "Duration");
    menu.Add(1, "10 minutes", {ChoiceKind::Duration, 10});
    menu.Add(2, "30 minutes", {ChoiceKind::Duration, 30});
    menu.Add(3, "1 hour", {ChoiceKind::Duration, 60});
    menu.Add(4, "1 day", {ChoiceKind::Duration, 1440});
    menu.Add(5, "1 week", {ChoiceKind::Duration, 10080});
    menu.Add(6, "Permanent", {ChoiceKind::Duration, 0});
    menu.Add(10, "Back", {ChoiceKind::Punishment,
        static_cast<int>(session.punishment)});
    menu.Send(player);
}

void Finish(CCSPlayerController* player, const Actor& actor,
    std::string_view action, std::string_view target, bool success,
    std::string_view message)
{
    NeoPtt_RecordAudit(actor.account_id, action, target, success, message);
    ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX "%s", std::string(message).c_str());
    CloseMenu(player);
}

void ExecuteKick(CCSPlayerController* player, const Actor& actor)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    CCSPlayerController* target_controller = nullptr;
    ZEPlayer* target = nullptr;
    if (!Has(actor.permissions, neo_admin::GamePermission::ModeratePlayers) ||
        !ResolveTarget(session, target_controller, target))
    {
        Finish(player, actor, "Kick player", session.target_name, false,
            "Player is no longer available or permission was denied.");
        return;
    }
    const std::string label = TargetLabel(session);
    if (session.target_steam_id >= kSteamId64Base)
        (void)NeoPtt_RecordDiscipline(session.target_steam_id, session.target_name,
            "Kick", "Kicked through in-game menu", actor.account_id);
    Finish(player, actor, "Kick player", label, true, "Player kicked.");
    g_pEngineServer2->DisconnectClient(target->GetPlayerSlot(),
        NETWORK_DISCONNECT_KICKED, "Kicked by NEO ADMIN");
}

void ExecutePunishment(CCSPlayerController* player, const Actor& actor, int minutes)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    CCSPlayerController* target_controller = nullptr;
    ZEPlayer* target = nullptr;
    if (!ResolveTarget(session, target_controller, target) ||
        session.target_steam_id < kSteamId64Base)
    {
        Finish(player, actor, "Apply player discipline", session.target_name,
            false, "Player is no longer available.");
        return;
    }

    const bool is_ban = session.punishment == Punishment::Ban;
    const neo_admin::GamePermission required = is_ban
        ? neo_admin::GamePermission::ManageBans
        : neo_admin::GamePermission::ManageDiscipline;
    if (!Has(actor.permissions, required))
    {
        Finish(player, actor, is_ban ? "Ban player" : "Apply player restriction",
            TargetLabel(session), false, "Permission denied.");
        return;
    }

    nlohmann::json request{
        {"steamId", std::to_string(session.target_steam_id)},
        {"playerName", session.target_name},
        {"reason", session.reason},
        {"durationMinutes", minutes},
    };
    std::string message;
    if (is_ban)
    {
        std::uint64_t saved_steam_id = 0;
        std::string saved_target;
        const bool saved = NeoPtt_SaveBan(request.dump(), actor.account_id,
            saved_steam_id, saved_target, message);
        Finish(player, actor, "Ban player", TargetLabel(session), saved, message);
        if (saved)
            g_pEngineServer2->DisconnectClient(target->GetPlayerSlot(),
                NETWORK_DISCONNECT_KICKBANADDED, "Banned by NEO ADMIN");
        return;
    }

    const std::string type = session.punishment == Punishment::Mute ? "Mute" : "Gag";
    request["type"] = type;
    neo_admin::RestrictionRecord restriction{};
    const bool saved = NeoPtt_SaveRestriction(request.dump(), actor.account_id,
        restriction, message);
    if (saved)
    {
        const CInfractionBase::EInfractionType infraction_type =
            type == "Mute" ? CInfractionBase::Mute : CInfractionBase::Gag;
        (void)g_pAdminSystem->FindAndRemoveInfractionSteamId64(
            restriction.steam_id, infraction_type);
        std::shared_ptr<CInfractionBase> infraction = type == "Mute"
            ? std::static_pointer_cast<CInfractionBase>(std::make_shared<CMuteInfraction>(
                static_cast<time_t>(restriction.duration_minutes), restriction.steam_id))
            : std::static_pointer_cast<CInfractionBase>(std::make_shared<CGagInfraction>(
                static_cast<time_t>(restriction.duration_minutes), restriction.steam_id));
        g_pAdminSystem->AddInfraction(infraction);
        g_pAdminSystem->SaveInfractions();
        infraction->ApplyInfraction(target);
    }
    Finish(player, actor, "Apply player restriction", TargetLabel(session), saved, message);
}

void ExecuteRemoveRestriction(CCSPlayerController* player, const Actor& actor,
    const char* type)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    CCSPlayerController* target_controller = nullptr;
    ZEPlayer* target = nullptr;
    if (!Has(actor.permissions, neo_admin::GamePermission::ManageDiscipline) ||
        !ResolveTarget(session, target_controller, target))
    {
        Finish(player, actor, "Remove player restriction", session.target_name,
            false, "Player is no longer available or permission was denied.");
        return;
    }
    nlohmann::json request{
        {"steamId", std::to_string(session.target_steam_id)},
        {"type", type},
    };
    neo_admin::RestrictionRecord removed{};
    std::string message;
    const bool stored_removed = NeoPtt_DeleteRestriction(
        request.dump(), actor.account_id, removed, message);
    const CInfractionBase::EInfractionType infraction_type =
        std::string_view(type) == "Mute" ? CInfractionBase::Mute : CInfractionBase::Gag;
    bool live_removed = g_pAdminSystem->FindAndRemoveInfraction(target, infraction_type);
    if (!live_removed)
        live_removed = g_pAdminSystem->FindAndRemoveInfractionSteamId64(
            session.target_steam_id, infraction_type);
    if (live_removed)
        g_pAdminSystem->SaveInfractions();

    const bool success = stored_removed || live_removed;
    if (!stored_removed && live_removed)
    {
        message = std::string(type) == "Mute" ? "Player unmuted." : "Player ungagged.";
        (void)NeoPtt_RecordDiscipline(session.target_steam_id, session.target_name,
            std::string(type) == "Mute" ? "Unmute" : "Ungag",
            "Restriction removed", actor.account_id);
    }
    Finish(player, actor, "Remove player restriction", TargetLabel(session),
        success, message);
}

void ExecutePlayerState(CCSPlayerController* player, const Actor& actor,
    ChoiceKind kind, int value)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    CCSPlayerController* target_controller = nullptr;
    ZEPlayer* target = nullptr;
    if (!Has(actor.permissions, neo_admin::GamePermission::ModeratePlayers) ||
        !ResolveTarget(session, target_controller, target))
    {
        Finish(player, actor, "Moderate player", session.target_name, false,
            "Player is no longer available or permission was denied.");
        return;
    }
    bool success = true;
    std::string action;
    std::string message;
    if (kind == ChoiceKind::Slay)
    {
        CCSPlayerPawn* pawn = target_controller->GetPlayerPawn();
        success = pawn && pawn->IsAlive();
        if (success)
            pawn->CommitSuicide(false, true);
        action = "Slay player";
        message = success ? "Player slayed." : "Player is not alive.";
    }
    else if (kind == ChoiceKind::Respawn)
    {
        CCSPlayerPawn* pawn = target_controller->GetPlayerPawn();
        success = pawn && !pawn->IsAlive();
        if (success)
            target_controller->Respawn();
        action = "Respawn player";
        message = success ? "Player respawned." : "Player is already alive.";
    }
    else
    {
        success = value == CS_TEAM_T || value == CS_TEAM_CT || value == CS_TEAM_SPECTATOR;
        if (success)
            target_controller->SwitchTeam(value);
        action = value == CS_TEAM_T ? "Move player to Terrorists" :
            value == CS_TEAM_CT ? "Move player to Counter-Terrorists" :
            "Move player to Spectator";
        message = success ? "Player moved." : "Invalid team selection.";
    }
    Finish(player, actor, action, TargetLabel(session), success, message);
}

void ShowBots(CCSPlayerController* player)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    MenuBuilder menu(session, "Bot management");
    menu.Add(1, "Add Terrorist bot", {ChoiceKind::AddBot, CS_TEAM_T});
    menu.Add(2, "Add Counter-Terrorist bot", {ChoiceKind::AddBot, CS_TEAM_CT});
    menu.Add(3, "Remove all bots", {ChoiceKind::RemoveBots});
    menu.Add(10, "Back", {ChoiceKind::Main});
    menu.Send(player);
}

void ShowMatch(CCSPlayerController* player)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    MenuBuilder menu(session, "Match control");
    menu.Add(1, "Restart round", {ChoiceKind::MatchCommand, 1});
    menu.Add(2, "Restart match", {ChoiceKind::MatchCommand, 2});
    menu.Add(3, "End warmup", {ChoiceKind::MatchCommand, 3});
    menu.Add(4, "Pause match", {ChoiceKind::MatchCommand, 4});
    menu.Add(5, "Unpause match", {ChoiceKind::MatchCommand, 5});
    menu.Add(6, "Swap teams", {ChoiceKind::MatchCommand, 6});
    menu.Add(10, "Back", {ChoiceKind::Main});
    menu.Send(player);
}

std::string MapLabel(std::string_view token)
{
    const std::size_t slash = token.find_last_of('/');
    std::string label(slash == std::string_view::npos
        ? token : token.substr(slash + 1));

    constexpr std::string_view workshop_prefix = "workshop/";
    if (token.starts_with(workshop_prefix))
    {
        const std::size_t id_end = token.find('/', workshop_prefix.size());
        if (id_end != std::string_view::npos)
        {
            label.append(" [WS ");
            label.append(token.substr(workshop_prefix.size(),
                id_end - workshop_prefix.size()));
            label.push_back(']');
        }
    }
    return label;
}

void ShowMaps(CCSPlayerController* player, int page)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    if (session.map_catalog.empty())
    {
        const auto maps = NeoAdmin_ScanFilesystemMaps();
        session.map_catalog.reserve(maps.size());
        for (const NeoFilesystemMapEntry& map : maps)
            session.map_catalog.push_back(map.token);
    }

    const int pages = std::max(1, static_cast<int>(
        (session.map_catalog.size() + kMapPageSize - 1) / kMapPageSize));
    session.map_page = std::clamp(page, 0, pages - 1);

    MenuBuilder menu(session, "Select map (" +
        std::to_string(session.map_page + 1) + "/" +
        std::to_string(pages) + ")");
    const int first = session.map_page * kMapPageSize;
    const int last = std::min(first + kMapPageSize,
        static_cast<int>(session.map_catalog.size()));
    int key = 1;
    for (int index = first; index < last; ++index)
    {
        menu.Add(key++, MapLabel(session.map_catalog[index]),
            {ChoiceKind::SelectMap, index});
    }
    if (session.map_page > 0)
        menu.Add(8, "Previous page", {ChoiceKind::PreviousMaps});
    if (session.map_page + 1 < pages)
        menu.Add(9, "Next page", {ChoiceKind::NextMaps});
    menu.Add(10, "Back", {ChoiceKind::Main});
    menu.Send(player);

    if (session.map_catalog.empty())
        ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX
            "No playable maps were found in the server maps folder.");
}

void ShowMapConfirmation(CCSPlayerController* player)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    MenuBuilder menu(session, "Change map?");
    menu.Add(1, "Change to " + MapLabel(session.pending_map),
        {ChoiceKind::ConfirmMap});
    menu.Add(10, "Back", {ChoiceKind::Maps});
    menu.Send(player);
}

void ExecuteServerChoice(CCSPlayerController* player, const Actor& actor,
    Choice choice)
{
    if (choice.kind == ChoiceKind::AddBot || choice.kind == ChoiceKind::RemoveBots)
    {
        if (!Has(actor.permissions, neo_admin::GamePermission::ControlBots))
        {
            Finish(player, actor, "Control bots", "server", false, "Permission denied.");
            return;
        }
        const char* command = choice.kind == ChoiceKind::RemoveBots ? "bot_kick" :
            choice.value == CS_TEAM_T ? "bot_add_t" : "bot_add_ct";
        const char* action = choice.kind == ChoiceKind::RemoveBots ? "Remove bots" : "Add bot";
        const char* message = choice.kind == ChoiceKind::RemoveBots ?
            "Removing bots." : "Adding bot.";
        g_pEngineServer2->ServerCommand(command);
        Finish(player, actor, action, "server", true, message);
        return;
    }
    if (!Has(actor.permissions, neo_admin::GamePermission::ControlMatch))
    {
        Finish(player, actor, "Control match", "server", false, "Permission denied.");
        return;
    }
    static const std::array<const char*, 6> commands{
        "mp_restartround 1", "mp_restartgame 1", "mp_warmup_end",
        "mp_pause_match", "mp_unpause_match", "mp_swapteams"};
    static const std::array<const char*, 6> actions{
        "Restart round", "Restart match", "End warmup",
        "Pause match", "Unpause match", "Swap teams"};
    if (choice.value < 1 || choice.value > static_cast<int>(commands.size()))
    {
        Finish(player, actor, "Control match", "server", false, "Invalid match action.");
        return;
    }
    g_pEngineServer2->ServerCommand(commands[choice.value - 1]);
    Finish(player, actor, actions[choice.value - 1], "server", true,
        "Match command sent.");
}

void ExecuteMapChange(CCSPlayerController* player, const Actor& actor)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    if (!Has(actor.permissions, neo_admin::GamePermission::ChangeMap))
    {
        Finish(player, actor, "Change map", session.pending_map, false,
            "Permission denied.");
        return;
    }

    const auto maps = NeoAdmin_ScanFilesystemMaps();
    const NeoFilesystemMapEntry* selected =
        NeoAdmin_FindFilesystemMap(maps, session.pending_map);
    if (!selected)
    {
        Finish(player, actor, "Change map", session.pending_map, false,
            "The selected map is no longer available.");
        return;
    }

    const std::string map = selected->token;
	std::string profile_error;
	if (!NeoAdmin_PrepareMapProfile(*selected, profile_error))
	{
		Finish(player, actor, "Change map", map, false, profile_error);
		return;
	}
    const std::string message = "Changing map to " + MapLabel(map) + ".";
    Finish(player, actor, "Change map", map, true, message);
    Message("[NEO ADMIN] in-game map change to \"%s\" by \"%s\"\n",
        map.c_str(), actor.account_id.c_str());
    NeoAdmin_ChangeStoredMap(map);
}

void BeginAnnouncement(CCSPlayerController* player, const Actor& actor)
{
    MenuSession& session = g_sessions[player->GetPlayerSlot()];
    if (!Has(actor.permissions, neo_admin::GamePermission::ManageAnnouncements))
    {
        Finish(player, actor, "Send announcement", "all players", false,
            "Permission denied.");
        return;
    }
    session.input = InputPurpose::Announcement;
    session.active = true;
    session.expires_at = std::time(nullptr) + 60;
    HideMenuDisplay(player);
    ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX
        "Type the announcement in chat, or type cancel.");
}
} // namespace

bool NeoAdminMenu_TryOpenChatCommand(
    CCSPlayerController* player,
    std::string_view message)
{
    const std::string command = Lower(CleanText(message, 64));
    if (command != "!admin" && command != "/admin" &&
        command != "!neoadmin" && command != "/neoadmin" &&
        command != "!adminmenu" && command != "/adminmenu")
        return false;

    Actor actor{};
    if (!ResolveActor(player, actor))
    {
        const std::uint64_t steam_id = SteamIdFor(player);
        if (steam_id == 0)
        {
            ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX
                "Your Steam identity is not ready yet. Reconnect, then try !admin again.");
        }
        else
        {
            ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX
                "SteamID64 %llu is not linked to an enabled in-game administrator.",
                steam_id);
        }
        return true;
    }
    const int slot = player->GetPlayerSlot();
    if (slot >= 0 && slot < MAXPLAYERS)
        CloseMenu(player);
    ShowMain(player, actor);
    return true;
}

bool NeoAdminMenu_HandleChatInput(
    CCSPlayerController* player,
    std::string_view message)
{
    if (!player)
        return false;
    const int slot = player->GetPlayerSlot();
    if (slot < 0 || slot >= MAXPLAYERS)
        return false;
    MenuSession& session = g_sessions[slot];
    if (!session.active || session.input == InputPurpose::None)
        return false;

    Actor actor{};
    if (!ResolveActor(player, actor) || actor.steam_id != session.actor_steam_id ||
        std::time(nullptr) > session.expires_at)
    {
        CloseMenu(player);
        ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX "The menu input expired.");
        return true;
    }

    std::string value = CleanText(message,
        session.input == InputPurpose::Announcement ? 220 : 160);
    if (Lower(value) == "cancel" || Lower(value) == "/cancel")
    {
        session.input = InputPurpose::None;
        ShowMain(player, actor);
        return true;
    }
    if (value.empty())
    {
        ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX "Enter text or type cancel.");
        return true;
    }

    if (session.input == InputPurpose::Announcement)
    {
        if (!Has(actor.permissions, neo_admin::GamePermission::ManageAnnouncements))
        {
            Finish(player, actor, "Send announcement", "all players", false,
                "Permission denied.");
            return true;
        }
        NeoAdmin_BroadcastChat(value.c_str());
        Finish(player, actor, "Send announcement", "all players", true, value);
        return true;
    }

    session.input = InputPurpose::None;
    session.reason = value;
    ShowDurations(player, actor);
    return true;
}

bool NeoAdminMenu_HandleSelection(
    CCSPlayerController* player,
    int selection)
{
    if (!player)
        return false;
    const int slot = player->GetPlayerSlot();
    if (slot < 0 || slot >= MAXPLAYERS)
        return false;
    MenuSession& session = g_sessions[slot];
    if (!session.active || selection < 1 || selection > 10)
        return false;
    if (session.input != InputPurpose::None)
        return true;

    Actor actor{};
    if (!ResolveActor(player, actor) || actor.steam_id != session.actor_steam_id ||
        std::time(nullptr) > session.expires_at)
    {
        CloseMenu(player);
        ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX "The administrator menu expired.");
        return true;
    }
    const Choice choice = session.choices[selection];
    if (choice.kind == ChoiceKind::None)
        return true;

    switch (choice.kind)
    {
        case ChoiceKind::Close: CloseMenu(player); break;
        case ChoiceKind::Main: ShowMain(player, actor); break;
        case ChoiceKind::Players: ShowPlayers(player, actor, session.player_page); break;
        case ChoiceKind::PreviousPlayers: ShowPlayers(player, actor, session.player_page - 1); break;
        case ChoiceKind::NextPlayers: ShowPlayers(player, actor, session.player_page + 1); break;
        case ChoiceKind::Bots: ShowBots(player); break;
        case ChoiceKind::Match: ShowMatch(player); break;
        case ChoiceKind::Maps: ShowMaps(player, session.map_page); break;
        case ChoiceKind::PreviousMaps: ShowMaps(player, session.map_page - 1); break;
        case ChoiceKind::NextMaps: ShowMaps(player, session.map_page + 1); break;
        case ChoiceKind::SelectMap:
            if (choice.value < 0 ||
                choice.value >= static_cast<int>(session.map_catalog.size()))
            {
                ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX
                    "That map is no longer available in this menu.");
                ShowMaps(player, session.map_page);
                break;
            }
            session.pending_map = session.map_catalog[choice.value];
            ShowMapConfirmation(player);
            break;
        case ChoiceKind::ConfirmMap: ExecuteMapChange(player, actor); break;
        case ChoiceKind::Announcement: BeginAnnouncement(player, actor); break;
        case ChoiceKind::SelectPlayer:
        {
            CCSPlayerController* target = choice.value >= 0
                ? CCSPlayerController::FromSlot(choice.value) : nullptr;
            ZEPlayer* managed = target && target->IsConnected() && !target->m_bIsHLTV()
                ? target->GetZEPlayer() : nullptr;
            if (!managed)
            {
                ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX "That player is no longer connected.");
                ShowPlayers(player, actor, session.player_page);
                break;
            }
            session.target = managed->GetHandle();
            session.target_steam_id = SteamIdFor(target);
            session.target_name = target->GetPlayerName();
            ShowTarget(player, actor);
            break;
        }
        case ChoiceKind::Kick: ExecuteKick(player, actor); break;
        case ChoiceKind::Punishment:
            session.punishment = static_cast<Punishment>(choice.value);
            session.reason.clear();
            ShowReasons(player, actor);
            break;
        case ChoiceKind::Reason:
            session.reason = ReasonFor(choice.value);
            ShowDurations(player, actor);
            break;
        case ChoiceKind::CustomReason:
            session.input = InputPurpose::Reason;
            session.expires_at = std::time(nullptr) + 60;
            HideMenuDisplay(player);
            ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX
                "Type the reason in chat, or type cancel.");
            break;
        case ChoiceKind::Duration: ExecutePunishment(player, actor, choice.value); break;
        case ChoiceKind::RemoveMute: ExecuteRemoveRestriction(player, actor, "Mute"); break;
        case ChoiceKind::RemoveGag: ExecuteRemoveRestriction(player, actor, "Gag"); break;
        case ChoiceKind::Slay:
        case ChoiceKind::Respawn:
        case ChoiceKind::MoveTeam:
            ExecutePlayerState(player, actor, choice.kind, choice.value);
            break;
        case ChoiceKind::MoveMenu: ShowMoveMenu(player); break;
        case ChoiceKind::AddBot:
        case ChoiceKind::RemoveBots:
        case ChoiceKind::MatchCommand:
            ExecuteServerChoice(player, actor, choice);
            break;
        default: break;
    }
    return true;
}

bool NeoAdminMenu_HandleButtons(
    CCSPlayerController* player,
    std::uint64_t buttons,
    std::uint64_t pressed_buttons)
{
    if (!player)
        return false;
    const int slot = player->GetPlayerSlot();
    if (slot < 0 || slot >= MAXPLAYERS)
        return false;

    MenuSession& session = g_sessions[slot];
    if (!session.active || session.input != InputPurpose::None)
    {
        const std::uint64_t blocked = g_consumed_buttons[slot];
        if (blocked == 0)
            return false;
        g_consumed_buttons[slot] &= buttons;
        return true;
    }

    if (std::time(nullptr) > session.expires_at)
    {
        CloseMenu(player);
        ClientPrint(player, HUD_PRINTTALK, CHAT_PREFIX
            "The administrator menu expired.");
        return true;
    }

    const std::uint64_t current = buttons & kMenuButtons;
    const std::uint64_t reported_pressed = pressed_buttons & kMenuButtons;
    const std::uint64_t active_signals = current | reported_pressed;
    session.latched_buttons &= active_signals;

    const std::uint64_t held_edges = current & ~session.last_buttons;
    const std::uint64_t transient_edges = reported_pressed & ~current;
    const std::uint64_t raw_edges = held_edges | transient_edges;
    const std::uint64_t pressed = raw_edges & ~session.latched_buttons;
    session.latched_buttons |= raw_edges;
    session.last_buttons = current;
    g_consumed_buttons[slot] |= active_signals | pressed;

    const auto now = std::chrono::steady_clock::now();
    const auto accept_press = [&](std::uint64_t button, std::size_t index)
    {
        if ((pressed & button) == 0)
            return false;
        auto& last = session.last_input_at[index];
        if (last.time_since_epoch().count() != 0 &&
            now - last < kMenuInputDebounce)
            return false;
        last = now;
        return true;
    };

    const bool forward_pressed = accept_press(IN_FORWARD, 0);
    const bool back_pressed = accept_press(IN_BACK, 1);
    const int direction = forward_pressed ? -1 : back_pressed ? 1 : 0;
    if (direction != 0)
    {
        int candidate = session.selected;
        for (int step = 0; step < 10; ++step)
        {
            candidate += direction;
            if (candidate < 1)
                candidate = 10;
            else if (candidate > 10)
                candidate = 1;
            if (session.choices[candidate].kind != ChoiceKind::None)
            {
                session.selected = candidate;
                break;
            }
        }
    }

    if (accept_press(IN_USE, 2) && session.selected != 0)
    {
        const int selected = session.selected;
        (void)NeoAdminMenu_HandleSelection(player, selected);
        return true;
    }

    if (direction != 0 || !session.display.Get())
        RenderMenu(player, session);
    return true;
}

CPointWorldText* NeoAdminMenu_GetDisplay(int slot)
{
    if (slot < 0 || slot >= MAXPLAYERS)
        return nullptr;
    return g_sessions[slot].display.Get();
}

void NeoAdminMenu_OnClientDisconnect(int slot)
{
    if (slot < 0 || slot >= MAXPLAYERS)
        return;
    CPointWorldText* display = g_sessions[slot].display.Get();
    if (display)
        display->Remove();
    g_sessions[slot] = {};
    g_consumed_buttons[slot] = 0;
}
