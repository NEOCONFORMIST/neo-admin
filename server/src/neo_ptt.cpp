#include "neo_ptt.h"
#include "neo_admin_accounts.h"
#include "neo_admin_game_admins.h"
#include "neo_admin_audit.h"
#include "neo_admin_bans.h"
#include "neo_admin_discipline.h"
#include "neo_admin_permissions.h"
#include "neo_admin_operations.h"
#include "neo_admin_persistence.h"
#include "voicebridge_protocol.h"

#include <algorithm>
#include <array>
#include <cerrno>
#include <charconv>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <deque>
#include <filesystem>
#include <span>
#include <string_view>
#include <vector>

#include <arpa/inet.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/random.h>
#include <unistd.h>

namespace
{
    constexpr std::uint16_t kDefaultPttPort = 27122;

    // Account updates can carry a compact JSON document.
    constexpr std::size_t kReceiveBufferSize = 4096;

    // Do not monopolize one CS2 frame.
    constexpr int kMaxPacketsPerPoll = 64;

    int g_socket = -1;
    std::uint16_t g_port = 0;
    char g_error[256] = {};

    std::vector<std::uint8_t> g_secret;
    std::vector<std::uint8_t> g_first_owner_setup_secret;
    std::string g_first_owner_setup_code;
    std::string g_bootstrap_claimed_account_id;
    neo_admin::AccountStore g_accounts;
    neo_admin::GameAdminStore g_game_admins;
    neo_admin::AuditStore g_audit;
    neo_admin::BanStore g_bans;
    neo_admin::DisciplineStore g_discipline;
    neo_admin::OperationsStore g_operations;
    std::string g_accounts_path;
    std::string g_game_admins_path;
    std::string g_audit_path;
    std::string g_bans_path;
    std::string g_discipline_path;
    std::string g_operations_path;
    NeoPttStats g_stats{};
    std::uint32_t g_server_sequence = 0;

    constexpr std::size_t kMaxPendingPttFrames = 128;
    std::deque<NeoPttFrame> g_pending_frames{};

    NeoPttTeleportCommand g_pending_teleport{};
    bool g_has_pending_teleport = false;

    constexpr std::size_t
        kMaxPendingAdminChats = 32;

    std::deque<NeoPttAdminChatCommand>
        g_pending_admin_chats{};

    constexpr std::size_t
        kMaxPendingAdminActions = 32;

    std::deque<NeoPttAdminActionCommand>
        g_pending_admin_actions{};

    constexpr std::size_t kMaxAdminSessions = 16;

    struct AdminSessionState
    {
        NeoPttSessionId id = kInvalidNeoPttSessionId;
        sockaddr_storage endpoint{};
        socklen_t endpoint_length = 0;
        std::vector<std::uint8_t> secret;
        std::string account_id;
        std::string operator_name;
        std::string role;
        std::uint64_t permissions = 0;
        std::uint32_t last_connect_sequence = 0;
        std::uint32_t last_connect_time = 0;
        std::uint32_t last_teleport_sequence = 0;
        std::uint32_t last_teleport_time = 0;
        std::uint32_t last_admin_chat_sequence = 0;
        std::uint32_t last_admin_chat_time = 0;
        std::uint32_t last_admin_action_sequence = 0;
        std::uint32_t last_admin_action_time = 0;
        std::uint64_t activity_order = 0;
    };

    std::vector<AdminSessionState> g_sessions;
    NeoPttSessionId g_next_session_id = 1;
    std::uint64_t g_session_activity_order = 0;


    void SetError(const char* prefix)
    {
        std::snprintf(
            g_error,
            sizeof(g_error),
            "%s: %s",
            prefix,
            std::strerror(errno));
    }

    void ClearSensitiveBytes(std::vector<std::uint8_t>& bytes)
    {
        std::fill(bytes.begin(), bytes.end(), 0);
        bytes.clear();
    }

    void ClearFirstOwnerSetup()
    {
        ClearSensitiveBytes(g_first_owner_setup_secret);
        std::fill(
            g_first_owner_setup_code.begin(),
            g_first_owner_setup_code.end(),
            '\0');
        g_first_owner_setup_code.clear();
        g_bootstrap_claimed_account_id.clear();
    }

    bool FillRandom(std::span<std::uint8_t> output)
    {
        std::size_t completed = 0;
        while (completed < output.size())
        {
            const ssize_t count = ::getrandom(
                output.data() + completed,
                output.size() - completed,
                0);
            if (count > 0)
            {
                completed += static_cast<std::size_t>(count);
                continue;
            }
            if (count < 0 && errno == EINTR)
                continue;
            return false;
        }
        return true;
    }

    bool GenerateFallbackSecret()
    {
        g_secret.assign(32, 0);
        if (FillRandom(g_secret))
            return true;
        ClearSensitiveBytes(g_secret);
        return false;
    }

    bool GenerateFirstOwnerSetupCode()
    {
        constexpr std::string_view alphabet =
            "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        std::array<std::uint8_t, 24> random{};
        if (!FillRandom(random))
            return false;

        g_first_owner_setup_code.clear();
        g_first_owner_setup_code.reserve(29);
        for (std::size_t index = 0; index < random.size(); ++index)
        {
            if (index > 0 && index % 4 == 0)
                g_first_owner_setup_code.push_back('-');
            g_first_owner_setup_code.push_back(
                alphabet[random[index] & 0x1fU]);
        }
        g_first_owner_setup_secret.assign(
            g_first_owner_setup_code.begin(),
            g_first_owner_setup_code.end());
        std::fill(random.begin(), random.end(), 0);
        return true;
    }

    bool SameUdpEndpoint(
        const sockaddr_storage& left,
        socklen_t left_length,
        const sockaddr_storage& right,
        socklen_t right_length)
    {
        if (left.ss_family != AF_INET ||
            right.ss_family != AF_INET ||
            left_length < sizeof(sockaddr_in) ||
            right_length < sizeof(sockaddr_in))
        {
            return false;
        }

        const auto* a =
            reinterpret_cast<const sockaddr_in*>(&left);

        const auto* b =
            reinterpret_cast<const sockaddr_in*>(&right);

        return
            a->sin_port == b->sin_port &&
            a->sin_addr.s_addr == b->sin_addr.s_addr;
    }

    bool IsFresh(std::uint32_t unix_time)
    {
        const std::time_t now =
            std::time(nullptr);

        if (now < 0)
            return false;

        const std::uint64_t current =
            static_cast<std::uint64_t>(now);

        const std::uint64_t sent =
            unix_time;

        return
            !(sent + 15U < current ||
              sent > current + 15U);
    }

    void ClearSession(AdminSessionState& session)
    {
        ClearSensitiveBytes(session.secret);
        session = {};
    }

    void ClearSessions()
    {
        for (AdminSessionState& session : g_sessions)
            ClearSession(session);
        g_sessions.clear();
        g_next_session_id = 1;
        g_session_activity_order = 0;
    }

    AdminSessionState* FindSessionByEndpoint(
        const sockaddr_storage& sender,
        socklen_t sender_length)
    {
        const auto found = std::find_if(
            g_sessions.begin(),
            g_sessions.end(),
            [&](const AdminSessionState& session)
            {
                return SameUdpEndpoint(
                    sender,
                    sender_length,
                    session.endpoint,
                    session.endpoint_length);
            });
        return found == g_sessions.end() ? nullptr : &*found;
    }

    AdminSessionState* FindSessionById(NeoPttSessionId session_id)
    {
        if (session_id == kInvalidNeoPttSessionId)
            return nullptr;
        const auto found = std::find_if(
            g_sessions.begin(),
            g_sessions.end(),
            [session_id](const AdminSessionState& session)
            {
                return session.id == session_id;
            });
        return found == g_sessions.end() ? nullptr : &*found;
    }

    void RemoveSession(NeoPttSessionId session_id)
    {
        const auto found = std::find_if(
            g_sessions.begin(),
            g_sessions.end(),
            [session_id](const AdminSessionState& session)
            {
                return session.id == session_id;
            });
        if (found == g_sessions.end())
            return;
        ClearSession(*found);
        g_sessions.erase(found);
    }

    bool RefreshSession(AdminSessionState& session)
    {
        const neo_admin::Account* account =
            g_accounts.Find(session.account_id);
        if (!account || !account->enabled || g_accounts.IsExpired(*account))
            return false;

        const std::vector<std::uint8_t> current_secret =
            g_accounts.ResolveSecret(*account);
        if (current_secret != session.secret)
            return false;

        session.role = account->role;
        session.permissions = account->permissions;
        return true;
    }

    AdminSessionState* FindAuthorizedSession(
        NeoPttSessionId session_id,
        neo_admin::Permission permission)
    {
        AdminSessionState* session = FindSessionById(session_id);
        if (!session)
            return nullptr;
        if (!RefreshSession(*session))
        {
            RemoveSession(session_id);
            return nullptr;
        }
        return neo_admin::HasPermission(session->permissions, permission)
            ? session
            : nullptr;
    }

    AdminSessionState* UpsertSession(
        const sockaddr_storage& sender,
        socklen_t sender_length,
        const neo_admin::Account& account,
        std::string_view operator_name,
        std::vector<std::uint8_t> secret,
        std::uint32_t connect_sequence,
        std::uint32_t connect_time)
    {
        if (sender.ss_family != AF_INET ||
            sender_length < sizeof(sockaddr_in))
        {
            return nullptr;
        }

        AdminSessionState* session =
            FindSessionByEndpoint(sender, sender_length);
        if (!session)
        {
            if (g_sessions.size() >= kMaxAdminSessions)
            {
                const auto oldest = std::min_element(
                    g_sessions.begin(),
                    g_sessions.end(),
                    [](const AdminSessionState& left,
                        const AdminSessionState& right)
                    {
                        return left.activity_order < right.activity_order;
                    });
                ClearSession(*oldest);
                g_sessions.erase(oldest);
            }

            AdminSessionState created{};
            g_sessions.push_back(std::move(created));
            session = &g_sessions.back();
        }

        ClearSensitiveBytes(session->secret);
        // A successful re-login replaces only this endpoint and receives a
        // fresh identity. Any command queued by its previous credentials can
        // no longer send a reply into the new account session.
        session->id = g_next_session_id++;
        if (session->id == kInvalidNeoPttSessionId)
            session->id = g_next_session_id++;
        session->endpoint = {};
        std::memcpy(&session->endpoint, &sender, sizeof(sockaddr_in));
        session->endpoint_length = sizeof(sockaddr_in);
        session->secret = std::move(secret);
        session->account_id = account.id;
        session->operator_name = operator_name.empty()
            ? account.display_name
            : std::string(operator_name);
        session->role = account.role;
        session->permissions = account.permissions;
        session->last_connect_sequence = connect_sequence;
        session->last_connect_time = connect_time;
        session->last_teleport_sequence = 0;
        session->last_teleport_time = 0;
        session->last_admin_chat_sequence = 0;
        session->last_admin_chat_time = 0;
        session->last_admin_action_sequence = 0;
        session->last_admin_action_time = 0;
        session->activity_order = ++g_session_activity_order;
        return session;
    }

    std::uint32_t RoleCode(std::string_view role)
    {
        if (role == "Viewer")
            return 1;
        if (role == "Moderator")
            return 2;
        if (role == "Event Admin")
            return 2;
        if (role == "Administrator" || role == "Senior Admin")
            return 3;
        if (role == "Owner")
            return 4;
        return 5;
    }

    bool SendPacketTo(
        const sockaddr_storage& destination,
        socklen_t destination_length,
        const std::vector<std::uint8_t>& packet)
    {
        if (g_socket < 0 || packet.empty())
            return false;

        const ssize_t sent = ::sendto(
            g_socket,
            packet.data(),
            packet.size(),
            MSG_DONTWAIT,
            reinterpret_cast<const sockaddr*>(&destination),
            destination_length);
        return sent == static_cast<ssize_t>(packet.size());
    }

    bool SendAdminSession(
        const sockaddr_storage& destination,
        socklen_t destination_length,
        const neo_admin::Account& account,
        std::span<const std::uint8_t> account_secret,
        bool success,
        std::string_view message,
        std::string_view display_name = {})
    {
        if (display_name.empty())
            display_name = account.display_name;
        voicebridge::VoicePacketData data{
            .message_type = voicebridge::kMessageAdminSession,
            .flags = static_cast<std::uint8_t>(success ? 0x01U : 0U),
            .sequence = ++g_server_sequence,
            .tick = RoleCode(account.role),
            .steam_id = account.permissions,
            .player_slot = -1,
            .player_name = display_name,
            .payload = std::span<const std::uint8_t>(
                reinterpret_cast<const std::uint8_t*>(message.data()),
                message.size()),
        };

        return SendPacketTo(
            destination,
            destination_length,
            voicebridge::BuildAuthenticatedVoicePacket(data, account_secret));
    }

    neo_admin::Permission PermissionForAction(std::uint32_t action)
    {
        if ((action >= 1 && action <= 6) || action == 23)
            return neo_admin::Permission::ModeratePlayers;

        switch (action)
        {
            case 40:
                return neo_admin::Permission::ChangeMap;
            case 41:
            case 42:
            case 43:
            case 44:
            case 45:
            case 46:
                return neo_admin::Permission::ControlMatch;
            case 47:
            case 48:
                return neo_admin::Permission::ControlBots;
            case 49:
            case 50:
            case 51:
                return neo_admin::Permission::ViewDashboard;
            case 100:
            case 101:
            case 102:
                return neo_admin::Permission::ManageAccounts;
            case 103:
                return neo_admin::Permission::ViewAuditLog;
            case 104:
            case 105:
            case 106:
                return neo_admin::Permission::ManageGameAdmins;
            case 110:
            case 111:
            case 112:
                return neo_admin::Permission::ManageBans;
            case 113:
            case 114:
            case 115:
            case 116:
                return neo_admin::Permission::ManageDiscipline;
            case 120:
            case 121:
            case 122:
            case 123:
            case 124:
                return neo_admin::Permission::ManageMapRotation;
            case 130:
            case 131:
            case 132:
            case 133:
                return neo_admin::Permission::ManageAnnouncements;
            case 140:
                return neo_admin::Permission::RunServerConsole;
            case 141:
            case 142:
                return neo_admin::Permission::ManageZombieMode;
            case 143:
                return neo_admin::Permission::ManageWorkshopMaps;
            default:
                return neo_admin::Permission::None;
        }
    }

}

bool NeoPtt_Start()
{
    if (g_socket >= 0)
        return true;

    g_error[0] = '\0';
    g_port = 0;
    g_stats = {};
    g_server_sequence = 0;

    g_pending_frames.clear();

    g_pending_teleport = {};
    g_has_pending_teleport = false;

    g_pending_admin_chats.clear();
    g_pending_admin_actions.clear();

    ClearSessions();

    ClearFirstOwnerSetup();

    const char* secret =
        std::getenv("VOICEBRIDGE_SECRET");

    std::vector<std::uint8_t> configured_secret;
    if (secret && *secret && std::strlen(secret) < 16)
    {
        std::snprintf(
            g_error,
            sizeof(g_error),
            "VOICEBRIDGE_SECRET is set but too short");

        return false;
    }
    if (secret && std::strlen(secret) >= 16)
    {
        configured_secret.assign(
            reinterpret_cast<const std::uint8_t*>(secret),
            reinterpret_cast<const std::uint8_t*>(secret) +
                std::strlen(secret));
        g_secret = configured_secret;
    }

    const char* configured_accounts_path =
        std::getenv("VOICEBRIDGE_ACCOUNTS_FILE");
    g_accounts_path = configured_accounts_path && *configured_accounts_path
        ? configured_accounts_path
        // CS2 changes its process directory to game/bin/linuxsteamrt64.
        : "../../csgo/addons/cs2fixes/configs/neo_admin_accounts.json";

    const std::filesystem::path accounts_directory =
        std::filesystem::path(g_accounts_path).parent_path();
    const char* configured_database_path =
        std::getenv("VOICEBRIDGE_DATABASE_FILE");
    const std::string database_path =
        configured_database_path && *configured_database_path
            ? configured_database_path
            : (accounts_directory / "neo_admin.sqlite3").string();
    std::string database_error;
    if (!neo_admin::ConfigureDatabase(database_path, database_error))
    {
        std::snprintf(
            g_error,
            sizeof(g_error),
            "administrator database: %s",
            database_error.c_str());
        g_secret.clear();
        return false;
    }

    std::string accounts_error;
    if (!g_accounts.Load(g_accounts_path, configured_secret, accounts_error))
    {
        std::snprintf(
            g_error,
            sizeof(g_error),
            "administrator accounts: %s",
            accounts_error.c_str());
        g_secret.clear();
        neo_admin::CloseDatabase();
        return false;
    }

    const char* configured_game_admins_path =
        std::getenv("VOICEBRIDGE_GAME_ADMINS_FILE");
    g_game_admins_path = configured_game_admins_path && *configured_game_admins_path
        ? configured_game_admins_path
        : (accounts_directory / "neo_admin_game_admins.json").string();
    const std::vector<neo_admin::LegacySteamLink> legacy_links =
        g_accounts.LegacySteamLinks();
    std::string game_admins_error;
    if (!g_game_admins.Load(g_game_admins_path, legacy_links, game_admins_error))
    {
        std::snprintf(
            g_error,
            sizeof(g_error),
            "in-game administrators: %s",
            game_admins_error.c_str());
        g_secret.clear();
        neo_admin::CloseDatabase();
        return false;
    }
    if (!legacy_links.empty() &&
        !g_accounts.ClearLegacySteamLinks(game_admins_error))
    {
        std::snprintf(
            g_error,
            sizeof(g_error),
            "administrator migration: %s",
            game_admins_error.c_str());
        g_secret.clear();
        neo_admin::CloseDatabase();
        return false;
    }
    const char* configured_audit_path = std::getenv("VOICEBRIDGE_AUDIT_FILE");
    g_audit_path = configured_audit_path && *configured_audit_path
        ? configured_audit_path
        : (accounts_directory / "neo_admin_audit.json").string();
    const char* configured_bans_path = std::getenv("VOICEBRIDGE_BANS_FILE");
    g_bans_path = configured_bans_path && *configured_bans_path
        ? configured_bans_path
        : (accounts_directory / "neo_admin_bans.json").string();
    const char* configured_discipline_path = std::getenv("VOICEBRIDGE_DISCIPLINE_FILE");
    g_discipline_path = configured_discipline_path && *configured_discipline_path
        ? configured_discipline_path
        : (accounts_directory / "neo_admin_discipline.json").string();
    const char* configured_operations_path = std::getenv("VOICEBRIDGE_OPERATIONS_FILE");
    g_operations_path = configured_operations_path && *configured_operations_path
        ? configured_operations_path
        : (accounts_directory / "neo_admin_operations.json").string();

    std::string security_error;
    if (!g_audit.Load(g_audit_path, security_error))
    {
        std::snprintf(g_error, sizeof(g_error), "administrator audit log: %s",
            security_error.c_str());
        g_secret.clear();
        neo_admin::CloseDatabase();
        return false;
    }
    if (!g_bans.Load(g_bans_path, security_error))
    {
        std::snprintf(g_error, sizeof(g_error), "administrator bans: %s",
            security_error.c_str());
        g_secret.clear();
        neo_admin::CloseDatabase();
        return false;
    }
    if (!g_discipline.Load(g_discipline_path, security_error))
    {
        std::snprintf(g_error, sizeof(g_error), "administrator discipline: %s",
            security_error.c_str());
        g_secret.clear();
        neo_admin::CloseDatabase();
        return false;
    }
    if (!g_operations.Load(g_operations_path, security_error))
    {
        std::snprintf(g_error, sizeof(g_error), "administrator operations: %s",
            security_error.c_str());
        g_secret.clear();
        neo_admin::CloseDatabase();
        return false;
    }

    if (g_secret.empty() && !GenerateFallbackSecret())
    {
        std::snprintf(
            g_error,
            sizeof(g_error),
            "could not generate a secure runtime secret");
        neo_admin::CloseDatabase();
        return false;
    }

    if (g_accounts.Size() == 0 && !GenerateFirstOwnerSetupCode())
    {
        std::snprintf(
            g_error,
            sizeof(g_error),
            "could not generate the first-owner setup code");
        ClearSensitiveBytes(g_secret);
        neo_admin::CloseDatabase();
        return false;
    }

    const int sock =
        ::socket(
            AF_INET,
            SOCK_DGRAM,
            0);

    if (sock < 0)
    {
        SetError("socket");
        g_secret.clear();
        neo_admin::CloseDatabase();
        return false;
    }

    int reuse = 1;

    (void)::setsockopt(
        sock,
        SOL_SOCKET,
        SO_REUSEADDR,
        &reuse,
        sizeof(reuse));

    const int flags =
        ::fcntl(
            sock,
            F_GETFL,
            0);

    if (flags < 0 ||
        ::fcntl(
            sock,
            F_SETFL,
            flags | O_NONBLOCK) < 0)
    {
        SetError("fcntl");
        ::close(sock);
        g_secret.clear();
        neo_admin::CloseDatabase();
        return false;
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port =
        htons(kDefaultPttPort);

    address.sin_addr.s_addr =
        htonl(INADDR_ANY);

    if (::bind(
            sock,
            reinterpret_cast<const sockaddr*>(
                &address),
            sizeof(address)) < 0)
    {
        SetError("bind");
        ::close(sock);
        g_secret.clear();
        neo_admin::CloseDatabase();
        return false;
    }

    g_socket = sock;
    g_port = kDefaultPttPort;

    return true;
}

void NeoPtt_Shutdown()
{
    if (g_socket >= 0)
    {
        ::close(g_socket);
        g_socket = -1;
    }

    ClearSensitiveBytes(g_secret);
    ClearFirstOwnerSetup();

    g_port = 0;

    ClearSessions();

    g_pending_frames.clear();

    g_pending_teleport = {};
    g_has_pending_teleport = false;

    g_pending_admin_chats.clear();
    g_pending_admin_actions.clear();
    neo_admin::CloseDatabase();
}

bool NeoPtt_IsListening()
{
    return g_socket >= 0;
}

std::uint16_t NeoPtt_GetPort()
{
    return g_port;
}

const char* NeoPtt_GetLastError()
{
    return g_error;
}

bool NeoPtt_NeedsFirstOwnerSetup()
{
    return g_accounts.Size() == 0 && !g_first_owner_setup_code.empty();
}

const char* NeoPtt_GetFirstOwnerSetupCode()
{
    return NeoPtt_NeedsFirstOwnerSetup()
        ? g_first_owner_setup_code.c_str()
        : "";
}

bool NeoPtt_HasPeer()
{
    return g_socket >= 0 && !g_sessions.empty();
}

std::size_t NeoPtt_GetPeerCount()
{
    return g_socket >= 0 ? g_sessions.size() : 0;
}

bool NeoPtt_SendVoicePacket(
    const voicebridge::VoicePacketData& data)
{
    if (g_socket < 0)
        return false;

    bool sent_any = false;
    std::size_t index = 0;
    while (index < g_sessions.size())
    {
        AdminSessionState& session = g_sessions[index];
        if (!RefreshSession(session))
        {
            const NeoPttSessionId invalid_id = session.id;
            RemoveSession(invalid_id);
            continue;
        }

        const std::vector<std::uint8_t> packet =
            voicebridge::BuildAuthenticatedVoicePacket(data, session.secret);
        sent_any = SendPacketTo(
            session.endpoint,
            session.endpoint_length,
            packet) || sent_any;
        ++index;
    }
    return sent_any;
}

bool NeoPtt_SendVoicePacketTo(
    NeoPttSessionId session_id,
    const voicebridge::VoicePacketData& data)
{
    AdminSessionState* session = FindSessionById(session_id);
    if (!session)
        return false;
    if (!RefreshSession(*session))
    {
        RemoveSession(session_id);
        return false;
    }

    return SendPacketTo(
        session->endpoint,
        session->endpoint_length,
        voicebridge::BuildAuthenticatedVoicePacket(data, session->secret));
}

std::uint64_t NeoPtt_Poll()
{
    if (g_socket < 0 ||
        g_secret.empty())
    {
        return 0;
    }

    std::uint64_t accepted_this_poll = 0;

    for (int packet_index = 0;
         packet_index < kMaxPacketsPerPoll;
         ++packet_index)
    {
        std::array<
            std::uint8_t,
            kReceiveBufferSize> datagram{};

        sockaddr_storage sender{};
        socklen_t sender_length =
            sizeof(sender);

        const ssize_t received =
            ::recvfrom(
                g_socket,
                datagram.data(),
                datagram.size(),
                MSG_DONTWAIT,
                reinterpret_cast<sockaddr*>(
                    &sender),
                &sender_length);

        if (received < 0)
        {
            if (errno == EAGAIN ||
                errno == EWOULDBLOCK)
            {
                break;
            }

            ++g_stats.rejected;
            break;
        }

        if (received == 0)
            continue;

        ++g_stats.received;

        const std::span<const std::uint8_t>
            packet(
                datagram.data(),
                static_cast<std::size_t>(
                    received));

        // -------------------------------------------------
        // TYPE 18: one-time first-owner claim.
        // -------------------------------------------------
        voicebridge::FirstOwnerClaimData untrusted_claim{};
        if (voicebridge::TryReadFirstOwnerClaim(packet, untrusted_claim))
        {
            voicebridge::FirstOwnerClaimData claim{};
            if (g_first_owner_setup_secret.empty() ||
                !voicebridge::TryParseAuthenticatedFirstOwnerClaim(
                    packet,
                    g_first_owner_setup_secret,
                    claim) ||
                !IsFresh(claim.unix_time))
            {
                ++g_stats.rejected;
                continue;
            }

            const bool exact_retry =
                !g_bootstrap_claimed_account_id.empty() &&
                claim.account_id == g_bootstrap_claimed_account_id;

            std::string bootstrap_message;
            if (g_accounts.Size() == 0)
            {
                if (!g_accounts.BootstrapOwner(
                        claim.account_id,
                        claim.display_name,
                        claim.access_key,
                        bootstrap_message))
                {
                    ++g_stats.rejected;
                    continue;
                }

                std::string audit_error;
                (void)g_audit.Append(
                    claim.account_id,
                    "Create first Owner",
                    claim.account_id,
                    true,
                    bootstrap_message,
                    audit_error);
                g_bootstrap_claimed_account_id = claim.account_id;
                std::fill(
                    g_first_owner_setup_code.begin(),
                    g_first_owner_setup_code.end(),
                    '\0');
                g_first_owner_setup_code.clear();
            }
            else if (!exact_retry)
            {
                ++g_stats.rejected;
                continue;
            }

            const neo_admin::Account* owner =
                g_accounts.Find(claim.account_id);
            if (!owner || owner->role != "Owner" ||
                owner->uses_server_secret ||
                owner->secret != claim.access_key)
            {
                ++g_stats.rejected;
                continue;
            }

            const std::vector<std::uint8_t> owner_secret(
                claim.access_key.begin(),
                claim.access_key.end());
            (void)SendAdminSession(
                sender,
                sender_length,
                *owner,
                owner_secret,
                true,
                owner->id);

            ++g_stats.authenticated;
            g_stats.last_sequence = claim.sequence;
            ++accepted_this_poll;
            continue;
        }

        // -------------------------------------------------
        // TYPE 15: permission-aware administrator login.
        // -------------------------------------------------
        std::string claimed_account_id;
        if (voicebridge::TryReadAdminLoginAccountId(
                packet,
                claimed_account_id))
        {
            const neo_admin::Account* account =
                g_accounts.Find(claimed_account_id);
            if (!account)
            {
                account = g_accounts.FindByAccessSelector(
                    claimed_account_id);
            }
            if (!account)
            {
                ++g_stats.rejected;
                continue;
            }

            std::vector<std::uint8_t> account_secret =
                g_accounts.ResolveSecret(*account);
            voicebridge::AdminLoginCommandData login{};
            if (!voicebridge::TryParseAuthenticatedAdminLoginCommand(
                    packet,
                    account_secret,
                    login) ||
                !IsFresh(login.unix_time))
            {
                ++g_stats.rejected;
                continue;
            }

            if (!account->enabled || g_accounts.IsExpired(*account))
            {
                (void)SendAdminSession(
                    sender,
                    sender_length,
                    *account,
                    account_secret,
                    false,
                    g_accounts.IsExpired(*account)
                        ? "This administrator account has expired."
                        : "This administrator account is disabled.");
                ++g_stats.rejected;
                continue;
            }

            AdminSessionState* session = UpsertSession(
                sender,
                sender_length,
                *account,
                login.display_name,
                std::move(account_secret),
                login.sequence,
                login.unix_time);
            if (!session)
            {
                ++g_stats.rejected;
                continue;
            }

            if (account->id == g_bootstrap_claimed_account_id)
                ClearFirstOwnerSetup();

            (void)SendAdminSession(
                sender,
                sender_length,
                *account,
                session->secret,
                true,
                account->id,
                session->operator_name);

            ++g_stats.authenticated;
            g_stats.last_sequence = login.sequence;
            ++accepted_this_poll;
            continue;
        }

        // Revalidate the session associated with this endpoint so disabled,
        // expired, deleted, or re-keyed accounts stop immediately without
        // disturbing any other connected administrator.
        AdminSessionState* session =
            FindSessionByEndpoint(sender, sender_length);
        if (session && !RefreshSession(*session))
        {
            const neo_admin::Account* active_account =
                g_accounts.Find(session->account_id);
            if (active_account)
            {
                (void)SendAdminSession(
                    sender,
                    sender_length,
                    *active_account,
                    session->secret,
                    false,
                    g_accounts.IsExpired(*active_account)
                        ? "This administrator account has expired."
                        : (!active_account->enabled
                            ? "This administrator account is disabled."
                            : "This administrator access key has changed."));
            }
            const NeoPttSessionId invalid_session_id = session->id;
            RemoveSession(invalid_session_id);
            ++g_stats.rejected;
            continue;
        }

        // -------------------------------------------------
        // TYPE 11: authenticated NEO ADMIN control.
        //
        // Requires an already authenticated CONNECT/PTT peer
        // and the exact same UDP endpoint.
        // -------------------------------------------------
        voicebridge::AdminActionCommandData
            admin_action{};

        if (session &&
            !session->secret.empty() &&
            voicebridge::
                TryParseAuthenticatedAdminActionCommand(
                    packet,
                    session->secret,
                    admin_action))
        {
            if (!IsFresh(admin_action.unix_time))
            {
                ++g_stats.rejected;
                continue;
            }

            if (admin_action.sequence ==
                    session->last_admin_action_sequence &&
                admin_action.unix_time ==
                    session->last_admin_action_time)
            {
                ++g_stats.rejected;
                continue;
            }

            session->last_admin_action_sequence =
                admin_action.sequence;

            session->last_admin_action_time =
                admin_action.unix_time;
            session->activity_order = ++g_session_activity_order;

            if (g_pending_admin_actions.size() >=
                kMaxPendingAdminActions)
            {
                g_pending_admin_actions.pop_front();
            }

            NeoPttAdminActionCommand queued{};

            queued.session_id = session->id;

            queued.sequence =
                admin_action.sequence;

            queued.unix_time =
                admin_action.unix_time;

            queued.action =
                admin_action.action;

            queued.player_slot =
                admin_action.player_slot;

            queued.value =
                admin_action.value;

            queued.text =
                admin_action.text;

            queued.account_id =
                session->account_id;

            queued.operator_name =
                session->operator_name;

            queued.permissions =
                session->permissions;

            const neo_admin::Permission required =
                PermissionForAction(admin_action.action);

            queued.authorized =
                required != neo_admin::Permission::None &&
                neo_admin::HasPermission(
                    session->permissions,
                    required);

            if (!queued.authorized)
            {
                queued.denial_message =
                    "Permission denied for this administrator action.";
            }

            g_pending_admin_actions.push_back(
                queued);

            ++g_stats.authenticated;

            g_stats.payload_bytes +=
                static_cast<std::uint64_t>(
                    admin_action.text.size());

            g_stats.last_sequence =
                admin_action.sequence;

            ++accepted_this_poll;
            continue;
        }

        // -------------------------------------------------
        // TYPE 10: authenticated NEO ADMIN chat.
        // -------------------------------------------------
        voicebridge::AdminChatCommandData
            admin_chat{};

        if (session &&
            !session->secret.empty() &&
            voicebridge::
                TryParseAuthenticatedAdminChatCommand(
                    packet,
                    session->secret,
                    admin_chat))
        {
            if (!IsFresh(admin_chat.unix_time))
            {
                ++g_stats.rejected;
                continue;
            }

            if (admin_chat.sequence ==
                    session->last_admin_chat_sequence &&
                admin_chat.unix_time ==
                    session->last_admin_chat_time)
            {
                ++g_stats.rejected;
                continue;
            }

            session->last_admin_chat_sequence =
                admin_chat.sequence;

            session->last_admin_chat_time =
                admin_chat.unix_time;
            session->activity_order = ++g_session_activity_order;

            if (g_pending_admin_chats.size() >=
                kMaxPendingAdminChats)
            {
                g_pending_admin_chats.pop_front();
            }

            NeoPttAdminChatCommand queued{};

            queued.session_id = session->id;

            queued.sequence =
                admin_chat.sequence;

            queued.unix_time =
                admin_chat.unix_time;

            queued.message =
                admin_chat.message;

            queued.account_id =
                session->account_id;

            queued.operator_name =
                session->operator_name;

            queued.authorized =
                neo_admin::HasPermission(
                    session->permissions,
                    neo_admin::Permission::SendChat);

            if (!queued.authorized)
                queued.denial_message = "Permission denied: SendChat is required.";

            g_pending_admin_chats.push_back(
                queued);

            ++g_stats.authenticated;

            g_stats.payload_bytes +=
                static_cast<std::uint64_t>(
                    admin_chat.message.size());

            g_stats.last_sequence =
                admin_chat.sequence;

            ++accepted_this_poll;
            continue;
        }

        // -------------------------------------------------
        // TYPE 6: authenticated drag teleport.
        //
        // Now accepted on the same UDP 27122 socket.
        // -------------------------------------------------
        voicebridge::TeleportCommandData
            teleport{};

        if (session &&
            !session->secret.empty() &&
            neo_admin::HasPermission(
                session->permissions,
                neo_admin::Permission::TeleportPlayers) &&
            voicebridge::
                TryParseAuthenticatedTeleportCommand(
                    packet,
                    session->secret,
                    teleport))
        {
            if (!IsFresh(teleport.unix_time))
            {
                ++g_stats.rejected;
                continue;
            }

            if (teleport.sequence ==
                    session->last_teleport_sequence &&
                teleport.unix_time ==
                    session->last_teleport_time)
            {
                ++g_stats.rejected;
                continue;
            }

            session->last_teleport_sequence =
                teleport.sequence;

            session->last_teleport_time =
                teleport.unix_time;
            session->activity_order = ++g_session_activity_order;

            g_pending_teleport.session_id = session->id;

            g_pending_teleport.sequence =
                teleport.sequence;

            g_pending_teleport.unix_time =
                teleport.unix_time;

            g_pending_teleport.steam_id =
                teleport.steam_id;

            g_pending_teleport.player_slot =
                teleport.player_slot;

            g_pending_teleport.x =
                teleport.x;

            g_pending_teleport.y =
                teleport.y;

            g_pending_teleport.z =
                teleport.z;

            g_has_pending_teleport = true;
            continue;
        }

        // -------------------------------------------------
        // TYPE 7: authenticated PTT.
        //
        // A valid PTT can also refresh the authenticated
        // peer endpoint, useful if the CONNECT datagram was
        // lost.
        // -------------------------------------------------
        voicebridge::PushToTalkCommandData
            command{};

        if (!session ||
            session->secret.empty() ||
            !neo_admin::HasPermission(
                session->permissions,
                neo_admin::Permission::BroadcastVoice) ||
            !voicebridge::
                TryParseAuthenticatedPushToTalkCommand(
                    packet,
                    session->secret,
                    command))
        {
            ++g_stats.rejected;
            continue;
        }

        if (!IsFresh(command.unix_time))
        {
            ++g_stats.rejected;
            continue;
        }

        session->activity_order = ++g_session_activity_order;

        ++g_stats.authenticated;

        g_stats.payload_bytes +=
            static_cast<std::uint64_t>(
                command.payload.size());

        g_stats.last_sequence =
            command.sequence;

        ++accepted_this_poll;

        NeoPttFrame pending{};
        pending.sequence = command.sequence;
        pending.unix_time = command.unix_time;
        pending.sample_rate = command.sample_rate;
        pending.sequence_bytes = command.sequence_bytes;
        pending.section_number = command.section_number;
        pending.uncompressed_sample_offset =
            command.uncompressed_sample_offset;
        pending.num_packets = command.num_packets;
        pending.voice_level = command.voice_level;
        pending.payload = std::move(command.payload);

        // UDP packets can arrive in a burst between CS2 frames. Retaining only
        // the last packet clipped most syllables; keep a short, latency-bounded
        // queue and let the game thread drain it in order.
        if (g_pending_frames.size() >= kMaxPendingPttFrames)
            g_pending_frames.pop_front();
        g_pending_frames.push_back(std::move(pending));
    }

    return accepted_this_poll;
}

bool NeoPtt_TryPop(
    NeoPttFrame& frame)
{
    if (g_pending_frames.empty())
        return false;

    frame = std::move(g_pending_frames.front());
    g_pending_frames.pop_front();

    return true;
}

bool NeoPtt_TakeAdminAction(
    NeoPttAdminActionCommand& command)
{
    if (g_pending_admin_actions.empty())
        return false;

    command =
        g_pending_admin_actions.front();

    g_pending_admin_actions.pop_front();
    return true;
}


bool NeoPtt_TakeAdminChat(
    NeoPttAdminChatCommand& command)
{
    if (g_pending_admin_chats.empty())
        return false;

    command =
        g_pending_admin_chats.front();

    g_pending_admin_chats.pop_front();
    return true;
}

bool NeoPtt_TakeTeleport(
    NeoPttTeleportCommand& command)
{
    if (!g_has_pending_teleport)
        return false;

    command = g_pending_teleport;

    g_pending_teleport = {};
    g_has_pending_teleport = false;

    return true;
}

bool NeoPtt_SendAccountCatalog(NeoPttSessionId session_id)
{
    if (!FindAuthorizedSession(
            session_id,
            neo_admin::Permission::ManageAccounts))
        return false;

    const std::string catalog = g_accounts.BuildCatalogJson();
    if (catalog.empty() || catalog.size() > 60000)
        return false;

    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageAdminAccountCatalog,
        .sequence = ++g_server_sequence,
        .tick = static_cast<std::uint32_t>(g_accounts.Size()),
        .player_slot = -1,
        .payload = std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(catalog.data()),
            catalog.size()),
    };

    return NeoPtt_SendVoicePacketTo(session_id, data);
}

bool NeoPtt_SaveAdminAccount(
    std::string_view request_json,
    std::string_view acting_account_id,
    std::string& message)
{
    return g_accounts.Upsert(request_json, acting_account_id, message);
}

bool NeoPtt_SendGameAdminCatalog(NeoPttSessionId session_id)
{
    if (!FindAuthorizedSession(
            session_id,
            neo_admin::Permission::ManageGameAdmins))
        return false;
    const std::string catalog = g_game_admins.BuildCatalogJson();
    if (catalog.empty() || catalog.size() > 60000)
        return false;
    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageGameAdminCatalog,
        .sequence = ++g_server_sequence,
        .tick = static_cast<std::uint32_t>(g_game_admins.Size()),
        .player_slot = -1,
        .payload = std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(catalog.data()),
            catalog.size()),
    };
    return NeoPtt_SendVoicePacketTo(session_id, data);
}

bool NeoPtt_SaveGameAdmin(
    std::string_view request_json,
    std::string& message)
{
    return g_game_admins.Upsert(request_json, message);
}

bool NeoPtt_DeleteGameAdmin(
    std::string_view steam_id,
    std::string& message)
{
    return g_game_admins.Remove(steam_id, message);
}

bool NeoPtt_SendAuditCatalog(NeoPttSessionId session_id)
{
    if (!FindAuthorizedSession(
            session_id,
            neo_admin::Permission::ViewAuditLog))
        return false;

    const std::string catalog = g_audit.BuildCatalogJson();
    if (catalog.empty() || catalog.size() > 60000)
        return false;
    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageAdminAuditCatalog,
        .sequence = ++g_server_sequence,
        .tick = static_cast<std::uint32_t>(g_audit.Size()),
        .player_slot = -1,
        .payload = std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(catalog.data()),
            catalog.size()),
    };
    return NeoPtt_SendVoicePacketTo(session_id, data);
}

bool NeoPtt_SendBanCatalog(NeoPttSessionId session_id)
{
    if (!FindAuthorizedSession(
            session_id,
            neo_admin::Permission::ManageBans))
        return false;

    const std::string catalog = g_bans.BuildCatalogJson();
    if (catalog.empty() || catalog.size() > 60000)
        return false;
    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageAdminBanCatalog,
        .sequence = ++g_server_sequence,
        .tick = static_cast<std::uint32_t>(g_bans.ActiveSize()),
        .player_slot = -1,
        .payload = std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(catalog.data()),
            catalog.size()),
    };
    return NeoPtt_SendVoicePacketTo(session_id, data);
}

bool NeoPtt_SendDisciplineCatalog(NeoPttSessionId session_id)
{
    if (!FindAuthorizedSession(
            session_id,
            neo_admin::Permission::ManageDiscipline))
        return false;
    const std::string catalog = g_discipline.BuildRestrictionCatalogJson();
    if (catalog.empty() || catalog.size() > 60000)
        return false;
    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageDisciplineCatalog,
        .sequence = ++g_server_sequence,
        .tick = static_cast<std::uint32_t>(g_discipline.ActiveSize()),
        .player_slot = -1,
        .payload = std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(catalog.data()), catalog.size()),
    };
    return NeoPtt_SendVoicePacketTo(session_id, data);
}

bool NeoPtt_SendDisciplineHistory(
    NeoPttSessionId session_id,
    std::string_view steam_id)
{
    if (!FindAuthorizedSession(
            session_id,
            neo_admin::Permission::ManageDiscipline))
        return false;
    const std::string catalog = g_discipline.BuildHistoryJson(steam_id);
    if (catalog.empty() || catalog.size() > 60000)
        return false;
    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageDisciplineHistory,
        .sequence = ++g_server_sequence,
        .player_slot = -1,
        .payload = std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(catalog.data()), catalog.size()),
    };
    return NeoPtt_SendVoicePacketTo(session_id, data);
}

bool NeoPtt_SendMapRotationCatalog(NeoPttSessionId session_id)
{
    if (!FindAuthorizedSession(
            session_id,
            neo_admin::Permission::ManageMapRotation))
        return false;
    const std::string catalog = g_operations.BuildRotationJson();
    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageMapRotationCatalog,
        .sequence = ++g_server_sequence,
        .player_slot = -1,
        .payload = std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(catalog.data()), catalog.size()),
    };
    return catalog.size() <= 60000 &&
        NeoPtt_SendVoicePacketTo(session_id, data);
}

bool NeoPtt_SendAnnouncementCatalog(NeoPttSessionId session_id)
{
    if (!FindAuthorizedSession(
            session_id,
            neo_admin::Permission::ManageAnnouncements))
        return false;
    const std::string catalog = g_operations.BuildAnnouncementsJson();
    const voicebridge::VoicePacketData data{
        .message_type = voicebridge::kMessageAnnouncementCatalog,
        .sequence = ++g_server_sequence,
        .player_slot = -1,
        .payload = std::span<const std::uint8_t>(
            reinterpret_cast<const std::uint8_t*>(catalog.data()), catalog.size()),
    };
    return catalog.size() <= 60000 &&
        NeoPtt_SendVoicePacketTo(session_id, data);
}

void NeoPtt_RecordAudit(
    std::string_view acting_account_id,
    std::string_view action,
    std::string_view target,
    bool success,
    std::string_view details)
{
    std::string error;
    if (!g_audit.Append(
            acting_account_id,
            action,
            target,
            success,
            details,
            error))
    {
        std::fprintf(stderr, "[NEO ADMIN] audit write failed: %s\n", error.c_str());
    }
}

bool NeoPtt_SaveBan(
    std::string_view request_json,
    std::string_view acting_account_id,
    std::uint64_t& steam_id,
    std::string& target,
    std::string& message)
{
    neo_admin::BanRecord saved{};
    if (!g_bans.Upsert(request_json, acting_account_id, saved, message))
        return false;
    std::string history_error;
    (void)g_discipline.Record(saved.steam_id, saved.player_name, "Ban",
        saved.reason, acting_account_id, saved.expires_unix, history_error);
    steam_id = saved.steam_id;
    target = saved.player_name + " (" + std::to_string(saved.steam_id) + ")";
    return true;
}

bool NeoPtt_DeleteBan(
    std::string_view steam_id,
    std::string_view acting_account_id,
    std::string& target,
    std::string& message)
{
    if (!g_bans.Remove(steam_id, target, message))
        return false;
    std::uint64_t parsed = 0;
    const auto result = std::from_chars(
        steam_id.data(), steam_id.data() + steam_id.size(), parsed);
    if (result.ec == std::errc{} && result.ptr == steam_id.data() + steam_id.size())
    {
        std::string history_error;
        (void)g_discipline.Record(parsed, target, "Unban", "Ban removed",
            acting_account_id, 0, history_error);
    }
    return true;
}

bool NeoPtt_SaveRestriction(
    std::string_view request_json,
    std::string_view acting_account_id,
    neo_admin::RestrictionRecord& saved,
    std::string& message)
{
    return g_discipline.UpsertRestriction(
        request_json, acting_account_id, saved, message);
}

bool NeoPtt_DeleteRestriction(
    std::string_view request_json,
    std::string_view acting_account_id,
    neo_admin::RestrictionRecord& removed,
    std::string& message)
{
    return g_discipline.RemoveRestriction(
        request_json, acting_account_id, removed, message);
}

bool NeoPtt_RecordDiscipline(
    std::uint64_t steam_id,
    std::string_view player_name,
    std::string_view action,
    std::string_view reason,
    std::string_view acting_account_id,
    std::uint64_t expires_unix)
{
    std::string error;
    const bool recorded = g_discipline.Record(steam_id, player_name, action,
        reason, acting_account_id, expires_unix, error);
    if (!recorded)
        std::fprintf(stderr, "[NEO ADMIN] discipline write failed: %s\n", error.c_str());
    return recorded;
}

bool NeoPtt_SaveMapRotation(std::string_view request_json,
    const std::vector<std::string>& allowed_maps,
    std::string_view acting_account_id, std::string& message)
{
    return g_operations.SaveRotation(
        request_json, allowed_maps, acting_account_id, message);
}

bool NeoPtt_SaveScheduledMap(std::string_view request_json,
    const std::vector<std::string>& allowed_maps,
    std::string_view acting_account_id, std::string& message)
{
    return g_operations.SaveScheduledMap(
        request_json, allowed_maps, acting_account_id, message);
}

bool NeoPtt_DeleteScheduledMap(std::string_view id, std::string& message)
{
    return g_operations.DeleteScheduledMap(id, message);
}

bool NeoPtt_RunNextMap(neo_admin::DueMapChange& due, std::string& message)
{
    return g_operations.RunNextMap(due, message);
}

bool NeoPtt_TakeDueMap(neo_admin::DueMapChange& due)
{
    return g_operations.TakeDueMap(due);
}

bool NeoPtt_SaveAnnouncement(std::string_view request_json,
    std::string_view acting_account_id, std::string& message)
{
    return g_operations.SaveAnnouncement(request_json, acting_account_id, message);
}

bool NeoPtt_DeleteAnnouncement(std::string_view id, std::string& message)
{
    return g_operations.DeleteAnnouncement(id, message);
}

bool NeoPtt_TakeDueAnnouncement(neo_admin::DueAnnouncement& due)
{
    return g_operations.TakeDueAnnouncement(due);
}

bool NeoPtt_IsSteamIdBanned(std::uint64_t steam_id, std::string& reason)
{
    return g_bans.IsBanned(steam_id, reason);
}

bool NeoPtt_GetInGameAdmin(
    std::uint64_t steam_id,
    std::string& account_id,
    std::string& display_name,
    std::uint64_t& permissions)
{
    const neo_admin::GameAdmin* admin = g_game_admins.FindBySteamId(steam_id);
    if (!admin)
        return false;
    account_id = "game:" + std::to_string(admin->steam_id);
    display_name = admin->display_name;
    permissions = admin->enabled ? admin->permissions : 0;
    return true;
}

bool NeoPtt_DeleteAdminAccount(
    std::string_view account_id,
    std::string_view acting_account_id,
    std::string& message)
{
    return g_accounts.Remove(account_id, acting_account_id, message);
}

NeoPttStats NeoPtt_GetStats()
{
    return g_stats;
}
