#include "neo_admin_game_admins.h"
#include "neo_admin_game_permissions.h"
#include "neo_admin_permissions.h"

#include <filesystem>
#include <iostream>
#include <string>
#include <vector>

int main()
{
    const std::filesystem::path directory =
        std::filesystem::temp_directory_path() / "neo-game-admin-test";
    std::error_code error_code;
    std::filesystem::remove_all(directory, error_code);
    std::filesystem::create_directories(directory, error_code);
    if (error_code)
        return 2;

    const std::filesystem::path path = directory / "game-admins.json";
    const std::uint64_t legacy_permissions =
        neo_admin::ToMask(neo_admin::Permission::ModeratePlayers) |
        neo_admin::ToMask(neo_admin::Permission::ManageBans) |
        neo_admin::ToMask(neo_admin::Permission::ManageDiscipline) |
        neo_admin::ToMask(neo_admin::Permission::ControlBots);
    const std::vector<neo_admin::LegacySteamLink> legacy{
        neo_admin::LegacySteamLink{
            .steam_id = 76561198012345678ULL,
            .display_name = "Migrated Admin",
            .role = "Custom",
            .permissions = legacy_permissions,
            .enabled = true,
            .created_utc = "2026-08-24T00:00:00Z",
        },
    };

    neo_admin::GameAdminStore store;
    std::string error;
    if (!store.Load(path.string(), legacy, error) || store.Size() != 1)
    {
        std::cerr << error << '\n';
        return 3;
    }
    const neo_admin::GameAdmin* migrated =
        store.FindBySteamId(76561198012345678ULL);
    if (!migrated || !migrated->enabled ||
        !neo_admin::HasGamePermission(
            migrated->permissions,
            neo_admin::GamePermission::ModeratePlayers) ||
        !neo_admin::HasGamePermission(
            migrated->permissions,
            neo_admin::GamePermission::ControlBots) ||
        neo_admin::HasGamePermission(
            migrated->permissions,
            neo_admin::GamePermission::ControlMatch))
    {
        return 4;
    }

    const std::string request =
        "{\"steamId\":\"76561198087654321\"," 
        "\"displayName\":\"Game Owner\",\"role\":\"Owner\"," 
        "\"permissions\":" +
        std::to_string(neo_admin::kAllGamePermissions) +
        ",\"enabled\":true}";
    std::string message;
    if (!store.Upsert(request, message) || store.Size() != 2)
        return 5;
    const neo_admin::GameAdmin* owner =
        store.FindBySteamId(76561198087654321ULL);
    if (!owner || owner->permissions != neo_admin::kAllGamePermissions)
        return 6;

    const std::string invalid =
        "{\"steamId\":\"76561198099999999\"," 
        "\"displayName\":\"Invalid\",\"role\":\"Custom\"," 
        "\"permissions\":18446744073709551615,\"enabled\":true}";
    if (store.Upsert(invalid, message))
        return 7;

    neo_admin::GameAdminStore reloaded;
    if (!reloaded.Load(path.string(), {}, error) || reloaded.Size() != 2 ||
        !reloaded.FindBySteamId(76561198012345678ULL) ||
        !reloaded.FindBySteamId(76561198087654321ULL))
    {
        std::cerr << error << '\n';
        return 8;
    }
    const std::string catalog = reloaded.BuildCatalogJson();
    if (catalog.find("Migrated Admin") == std::string::npos ||
        catalog.find("76561198087654321") == std::string::npos)
        return 9;

    if (!reloaded.Remove("76561198012345678", message) ||
        reloaded.FindBySteamId(76561198012345678ULL))
        return 10;

    std::filesystem::remove_all(directory, error_code);
    return 0;
}
