#include "neo_admin_game_admins.h"

#include "neo_admin_game_permissions.h"
#include "neo_admin_persistence.h"
#include "neo_admin_permissions.h"
#include "vendor/nlohmann/json.hpp"

#include <algorithm>
#include <charconv>
#include <chrono>
#include <ctime>
#include <iomanip>
#include <sstream>

namespace neo_admin
{
namespace
{
using json = nlohmann::json;

std::string UtcNow()
{
    const auto now = std::chrono::system_clock::now();
    const std::time_t value = std::chrono::system_clock::to_time_t(now);
    std::tm utc{};
#if defined(_WIN32)
    gmtime_s(&utc, &value);
#else
    gmtime_r(&value, &utc);
#endif
    std::ostringstream output;
    output << std::put_time(&utc, "%Y-%m-%dT%H:%M:%SZ");
    return output.str();
}

bool ParseSteamId(std::string_view text, std::uint64_t& steam_id)
{
    steam_id = 0;
    const auto result = std::from_chars(
        text.data(), text.data() + text.size(), steam_id);
    return result.ec == std::errc{} && result.ptr == text.data() + text.size() &&
        steam_id >= 76561197960265728ULL;
}

bool ParseSteamId(const json& value, std::uint64_t& steam_id)
{
    if (!value.is_string())
        return false;
    const std::string text = value.get<std::string>();
    return ParseSteamId(std::string_view(text), steam_id);
}

bool IsValidRole(std::string_view role)
{
    return role == "Moderator" || role == "Administrator" ||
        role == "Owner" || role == "Custom";
}

std::uint64_t MigratePermissions(std::uint64_t desktop_permissions)
{
    std::uint64_t result = 0;
    if (HasPermission(desktop_permissions, Permission::ModeratePlayers))
        result |= ToGameMask(GamePermission::ModeratePlayers);
    if (HasPermission(desktop_permissions, Permission::ManageBans))
        result |= ToGameMask(GamePermission::ManageBans);
    if (HasPermission(desktop_permissions, Permission::ManageDiscipline))
        result |= ToGameMask(GamePermission::ManageDiscipline);
    if (HasPermission(desktop_permissions, Permission::ControlBots))
        result |= ToGameMask(GamePermission::ControlBots);
    if (HasPermission(desktop_permissions, Permission::ControlMatch))
        result |= ToGameMask(GamePermission::ControlMatch);
    if (HasPermission(desktop_permissions, Permission::ChangeMap))
        result |= ToGameMask(GamePermission::ChangeMap);
    if (HasPermission(desktop_permissions, Permission::ManageMapRotation))
        result |= ToGameMask(GamePermission::ManageMapRotation);
    if (HasPermission(desktop_permissions, Permission::ManageAnnouncements))
        result |= ToGameMask(GamePermission::ManageAnnouncements);
    return result;
}
} // namespace

bool GameAdminStore::Load(
    const std::string& path,
    const std::vector<LegacySteamLink>& legacy_links,
    std::string& error)
{
    path_ = path;
    admins_.clear();
    if (path_.empty())
    {
        error = "In-game administrator storage path is invalid.";
        return false;
    }

    std::string stored_json;
    bool exists = false;
    if (!ReadJsonDocument(
            "game_admins", path_, stored_json, exists, error))
        return false;

    if (exists)
    {
        const json document = json::parse(stored_json, nullptr, false, true);
        if (document.is_discarded() || !document.is_object() ||
            !document.contains("admins") || !document["admins"].is_array())
        {
            error = "The in-game administrator file has invalid JSON or no admins array.";
            return false;
        }

        for (const json& item : document["admins"])
        {
            if (!item.is_object() ||
                !item.contains("steamId") ||
                !item.contains("displayName") || !item["displayName"].is_string() ||
                !item.contains("role") || !item["role"].is_string() ||
                !item.contains("permissions") || !item["permissions"].is_number_unsigned() ||
                !item.contains("enabled") || !item["enabled"].is_boolean())
            {
                error = "The in-game administrator file contains an invalid record.";
                admins_.clear();
                return false;
            }

            GameAdmin admin{};
            if (!ParseSteamId(item["steamId"], admin.steam_id))
            {
                error = "The in-game administrator file contains an invalid SteamID64.";
                admins_.clear();
                return false;
            }
            admin.display_name = item["displayName"].get<std::string>();
            admin.role = item["role"].get<std::string>();
            admin.permissions = item["permissions"].get<std::uint64_t>();
            admin.enabled = item["enabled"].get<bool>();
            admin.created_utc =
                item.contains("createdUtc") && item["createdUtc"].is_string()
                    ? item["createdUtc"].get<std::string>()
                    : "";

            if (admin.display_name.empty() || admin.display_name.size() > 64 ||
                !IsValidRole(admin.role) ||
                (admin.permissions & ~kAllGamePermissions) != 0 ||
                FindBySteamId(admin.steam_id))
            {
                error = "The in-game administrator file contains an invalid or duplicate administrator.";
                admins_.clear();
                return false;
            }
            admins_.push_back(std::move(admin));
        }
    }

    bool changed = !exists;
    for (const LegacySteamLink& link : legacy_links)
    {
        if (link.steam_id == 0 || FindBySteamId(link.steam_id))
            continue;
        admins_.push_back(GameAdmin{
            .steam_id = link.steam_id,
            .display_name = link.display_name,
            .role = IsValidRole(link.role) ? link.role : "Custom",
            .permissions = MigratePermissions(link.permissions),
            .enabled = link.enabled,
            .created_utc = link.created_utc,
        });
        changed = true;
    }

    return !changed || Save(error);
}

const GameAdmin* GameAdminStore::FindBySteamId(std::uint64_t steam_id) const
{
    if (steam_id == 0)
        return nullptr;
    const auto found = std::find_if(
        admins_.begin(), admins_.end(),
        [&](const GameAdmin& admin) { return admin.steam_id == steam_id; });
    return found == admins_.end() ? nullptr : &*found;
}

std::string GameAdminStore::BuildCatalogJson() const
{
    json document{{"version", 1}, {"admins", json::array()}};
    for (const GameAdmin& admin : admins_)
    {
        document["admins"].push_back({
            {"steamId", std::to_string(admin.steam_id)},
            {"displayName", admin.display_name},
            {"role", admin.role},
            {"permissions", admin.permissions},
            {"enabled", admin.enabled},
            {"createdUtc", admin.created_utc},
        });
    }
    return document.dump();
}

bool GameAdminStore::Upsert(std::string_view request_json, std::string& message)
{
    const json request = json::parse(request_json, nullptr, false);
    if (request.is_discarded() || !request.is_object() ||
        !request.contains("steamId") ||
        !request.contains("displayName") || !request["displayName"].is_string() ||
        !request.contains("role") || !request["role"].is_string() ||
        !request.contains("permissions") || !request["permissions"].is_number_unsigned() ||
        !request.contains("enabled") || !request["enabled"].is_boolean())
    {
        message = "In-game administrator request JSON is invalid.";
        return false;
    }

    std::uint64_t steam_id = 0;
    if (!ParseSteamId(request["steamId"], steam_id))
    {
        message = "SteamID64 must be a valid 64-bit Steam account ID.";
        return false;
    }
    const std::string display_name = request["displayName"].get<std::string>();
    const std::string role = request["role"].get<std::string>();
    const std::uint64_t permissions = request["permissions"].get<std::uint64_t>();
    const bool enabled = request["enabled"].get<bool>();
    if (display_name.empty() || display_name.size() > 64)
    {
        message = "Display name must be 1-64 characters.";
        return false;
    }
    if (!IsValidRole(role))
    {
        message = "The selected in-game role is invalid.";
        return false;
    }
    if ((permissions & ~kAllGamePermissions) != 0)
    {
        message = "The in-game permission mask contains unsupported permissions.";
        return false;
    }

    auto found = std::find_if(
        admins_.begin(), admins_.end(),
        [&](const GameAdmin& admin) { return admin.steam_id == steam_id; });
    const bool creating = found == admins_.end();
    if (creating && admins_.size() >= 256)
    {
        message = "The in-game administrator limit has been reached.";
        return false;
    }

    const std::vector<GameAdmin> previous = admins_;
    if (creating)
    {
        admins_.push_back(GameAdmin{
            .steam_id = steam_id,
            .display_name = display_name,
            .role = role,
            .permissions = permissions,
            .enabled = enabled,
            .created_utc = UtcNow(),
        });
    }
    else
    {
        found->display_name = display_name;
        found->role = role;
        found->permissions = permissions;
        found->enabled = enabled;
    }

    std::string save_error;
    if (!Save(save_error))
    {
        admins_ = previous;
        message = save_error;
        return false;
    }
    message = creating
        ? "In-game administrator created."
        : "In-game administrator updated.";
    return true;
}

bool GameAdminStore::Remove(std::string_view steam_id_text, std::string& message)
{
    std::uint64_t steam_id = 0;
    if (!ParseSteamId(steam_id_text, steam_id))
    {
        message = "SteamID64 is invalid.";
        return false;
    }
    const auto found = std::find_if(
        admins_.begin(), admins_.end(),
        [&](const GameAdmin& admin) { return admin.steam_id == steam_id; });
    if (found == admins_.end())
    {
        message = "In-game administrator was not found.";
        return false;
    }

    const std::vector<GameAdmin> previous = admins_;
    admins_.erase(found);
    std::string save_error;
    if (!Save(save_error))
    {
        admins_ = previous;
        message = save_error;
        return false;
    }
    message = "In-game administrator deleted.";
    return true;
}

bool GameAdminStore::Save(std::string& error) const
{
    json document{{"version", 1}, {"admins", json::array()}};
    for (const GameAdmin& admin : admins_)
    {
        document["admins"].push_back({
            {"steamId", std::to_string(admin.steam_id)},
            {"displayName", admin.display_name},
            {"role", admin.role},
            {"permissions", admin.permissions},
            {"enabled", admin.enabled},
            {"createdUtc", admin.created_utc},
        });
    }

    return WriteJsonDocument(
        "game_admins", path_, document.dump(2) + '\n', error);
}
} // namespace neo_admin
