#include "neo_admin_accounts.h"
#include "neo_admin_permissions.h"

#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <ctime>
#include <vector>

int main()
{
    const std::filesystem::path directory =
        std::filesystem::temp_directory_path() / "neo-admin-account-test";
    std::error_code error_code;
    std::filesystem::remove_all(directory, error_code);
    std::filesystem::create_directories(directory, error_code);
    if (error_code)
        return 2;

    const std::filesystem::path path = directory / "accounts.json";
    const std::string secret = "0123456789abcdef0123456789abcdef";
    const std::vector<std::uint8_t> secret_bytes(secret.begin(), secret.end());

    neo_admin::AccountStore store;
    std::string error;
    if (!store.Load(path.string(), secret_bytes, error) || store.Size() != 1)
    {
        std::cerr << error << '\n';
        return 3;
    }

    const neo_admin::Account* owner = store.Find("owner");
    if (!owner || !owner->enabled || owner->role != "Owner" ||
        !neo_admin::HasPermission(
            owner->permissions,
            neo_admin::Permission::ManageAccounts) ||
        !neo_admin::HasPermission(
            owner->permissions,
            neo_admin::Permission::ManageGameAdmins) ||
        !neo_admin::HasPermission(
            owner->permissions,
            neo_admin::Permission::RunServerConsole) ||
        !neo_admin::HasPermission(
            owner->permissions,
            neo_admin::Permission::ManageZombieMode) ||
        !neo_admin::HasPermission(
            owner->permissions,
            neo_admin::Permission::ManageWorkshopMaps) ||
        store.ResolveSecret(*owner) != secret_bytes)
    {
        return 4;
    }

    const std::string moderator_key =
        "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
    const std::string request =
        "{\"id\":\"alice\",\"displayName\":\"Alice\","
        "\"role\":\"Moderator\",\"permissions\":" +
        std::to_string(neo_admin::kModeratorPermissions) +
        ",\"steamId\":\"76561198012345678\",\"enabled\":true,\"secret\":\"" + moderator_key + "\"}";

    std::string message;
    if (!store.Upsert(request, "owner", message) || store.Size() != 2)
        return 5;
    const std::string disabled_request =
        "{\"id\":\"alice\",\"displayName\":\"Alice\","
        "\"role\":\"Moderator\",\"permissions\":" +
        std::to_string(neo_admin::kModeratorPermissions) +
        ",\"steamId\":\"76561198012345678\",\"enabled\":false}";
    if (!store.Upsert(disabled_request, "owner", message))
        return 15;
    const neo_admin::Account* disabled = store.Find("alice");
    if (!disabled || disabled->enabled || disabled->steam_id != 0)
        return 16;
    if (!store.Upsert(request, "owner", message))
        return 17;

    const std::uint64_t future_expiration =
        static_cast<std::uint64_t>(std::time(nullptr)) + 3600;
    const std::string event_admin_key =
        "event-admin-access-key-0123456789-ABCDEFG";
    const std::string event_admin_request =
        "{\"id\":\"event.admin\",\"displayName\":\"Event Admin\"," 
        "\"role\":\"Event Admin\",\"permissions\":" +
        std::to_string(neo_admin::kEventAdminPermissions) +
        ",\"enabled\":true,\"expiresUnix\":" +
        std::to_string(future_expiration) +
        ",\"secret\":\"" + event_admin_key + "\"}";
    if (!store.Upsert(event_admin_request, "owner", message))
        return 26;
    const neo_admin::Account* event_admin = store.Find("event.admin");
    if (!event_admin || event_admin->role != "Event Admin" ||
        event_admin->expires_unix != future_expiration ||
        event_admin->permissions != neo_admin::kEventAdminPermissions ||
        store.IsExpired(*event_admin))
    {
        return 27;
    }
    if (store.Upsert(
            "{\"id\":\"owner\",\"displayName\":\"Neo\","
            "\"role\":\"Owner\",\"permissions\":" +
                std::to_string(neo_admin::kOwnerPermissions) +
                ",\"enabled\":true,\"expiresUnix\":" +
                std::to_string(future_expiration) + "}",
            "owner",
            message))
    {
        return 28;
    }

    neo_admin::AccountStore linked_reloaded;
    if (!linked_reloaded.Load(path.string(), secret_bytes, error) ||
        linked_reloaded.FindBySteamId(76561198012345678ULL))
        return 18;

    const std::string catalog = store.BuildCatalogJson();
    if (catalog.find("Alice") == std::string::npos ||
        catalog.find("76561198012345678") != std::string::npos ||
        catalog.find(moderator_key) != std::string::npos)
    {
        return 6;
    }

    if (store.Remove("owner", "owner", message))
        return 7;
    if (!store.Remove("alice", "owner", message) ||
        !store.Remove("event.admin", "owner", message) || store.Size() != 1)
        return 8;

    neo_admin::AccountStore reloaded;
    if (!reloaded.Load(path.string(), secret_bytes, error) ||
        reloaded.Size() != 1 || !reloaded.Find("owner"))
    {
        return 9;
    }

    const std::filesystem::path fresh_path =
        directory / "fresh-accounts.json";
    neo_admin::AccountStore fresh;
    const std::vector<std::uint8_t> no_server_secret;
    if (!fresh.Load(fresh_path.string(), no_server_secret, error) ||
        fresh.Size() != 0 || !std::filesystem::exists(fresh_path))
    {
        std::cerr << error << '\n';
        return 10;
    }

    const std::string owner_key =
        "fresh-owner-access-key-0123456789-ABCDEFG";
    if (!fresh.BootstrapOwner(
            "first.owner",
            "First Owner",
            owner_key,
            message) ||
        fresh.Size() != 1)
    {
        std::cerr << message << '\n';
        return 11;
    }

    const neo_admin::Account* first_owner = fresh.Find("first.owner");
    const std::vector<std::uint8_t> owner_key_bytes(
        owner_key.begin(),
        owner_key.end());
    if (!first_owner || !first_owner->enabled ||
        first_owner->role != "Owner" ||
        first_owner->uses_server_secret ||
        first_owner->permissions != neo_admin::kOwnerPermissions ||
        fresh.ResolveSecret(*first_owner) != owner_key_bytes)
    {
        return 12;
    }

    if (fresh.BootstrapOwner(
            "second.owner",
            "Second Owner",
            owner_key,
            message))
    {
        return 13;
    }

    neo_admin::AccountStore fresh_reloaded;
    if (!fresh_reloaded.Load(
            fresh_path.string(),
            no_server_secret,
            error) ||
        fresh_reloaded.Size() != 1 ||
        !fresh_reloaded.Find("first.owner"))
    {
        std::cerr << error << '\n';
        return 14;
    }

    const std::filesystem::path legacy_path = directory / "legacy-accounts.json";
    const std::uint64_t legacy_owner_permissions =
        neo_admin::kOwnerPermissions &
        ~neo_admin::ToMask(neo_admin::Permission::RunServerConsole) &
        ~neo_admin::ToMask(neo_admin::Permission::ManageZombieMode);
    {
        std::ofstream legacy_output(legacy_path);
        legacy_output
            << "{\"version\":1,\"accounts\":[{"
            << "\"id\":\"legacy.owner\","
            << "\"displayName\":\"Legacy Owner\","
            << "\"role\":\"Owner\","
            << "\"permissions\":" << legacy_owner_permissions << ',';
        legacy_output
            << "\"steamId\":\"76561198033334444\","
            << "\"enabled\":true,"
            << "\"usesServerSecret\":true,"
            << "\"secret\":\"\","
            << "\"createdUtc\":\"2026-08-24T00:00:00Z\"}]}\n";
    }
    neo_admin::AccountStore legacy_store;
    if (!legacy_store.Load(legacy_path.string(), secret_bytes, error))
        return 21;
    const neo_admin::Account* legacy_owner =
        legacy_store.Find("legacy.owner");
    if (!legacy_owner || !neo_admin::HasPermission(
            legacy_owner->permissions,
            neo_admin::Permission::RunServerConsole) ||
        !neo_admin::HasPermission(
            legacy_owner->permissions,
            neo_admin::Permission::ManageZombieMode) ||
        !neo_admin::HasPermission(
            legacy_owner->permissions,
            neo_admin::Permission::ManageWorkshopMaps) ||
        legacy_owner->expires_unix != 0)
        return 25;
    const std::vector<neo_admin::LegacySteamLink> links =
        legacy_store.LegacySteamLinks();
    if (links.size() != 1 || links[0].steam_id != 76561198033334444ULL)
        return 22;
    if (!legacy_store.ClearLegacySteamLinks(error) ||
        !legacy_store.LegacySteamLinks().empty())
        return 23;
    neo_admin::AccountStore migrated_store;
    if (!migrated_store.Load(legacy_path.string(), secret_bytes, error) ||
        !migrated_store.LegacySteamLinks().empty() ||
        migrated_store.FindBySteamId(76561198033334444ULL))
        return 24;

    std::filesystem::remove_all(directory, error_code);
    return 0;
}
