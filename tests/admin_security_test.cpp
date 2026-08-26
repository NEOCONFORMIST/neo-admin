#include "neo_admin_audit.h"
#include "neo_admin_bans.h"

#include <filesystem>
#include <iostream>
#include <string>

int main()
{
    const std::filesystem::path directory =
        std::filesystem::temp_directory_path() / "neo-admin-security-test";
    std::error_code error_code;
    std::filesystem::remove_all(directory, error_code);
    std::filesystem::create_directories(directory, error_code);
    if (error_code)
        return 2;

    std::string error;
    neo_admin::AuditStore audit;
    const std::filesystem::path audit_path = directory / "audit.json";
    if (!audit.Load(audit_path.string(), error) ||
        !std::filesystem::exists(audit_path))
    {
        std::cerr << error << '\n';
        return 3;
    }
    if (!audit.Append(
            "owner",
            "Change map",
            "de_dust2",
            true,
            "Changing map.",
            error))
    {
        std::cerr << error << '\n';
        return 4;
    }
    const std::string audit_catalog = audit.BuildCatalogJson();
    if (audit_catalog.find("owner") == std::string::npos ||
        audit_catalog.find("de_dust2") == std::string::npos)
    {
        return 5;
    }
    neo_admin::AuditStore reloaded_audit;
    if (!reloaded_audit.Load(audit_path.string(), error) ||
        reloaded_audit.Size() != 1)
    {
        std::cerr << error << '\n';
        return 6;
    }

    neo_admin::BanStore bans;
    const std::filesystem::path bans_path = directory / "bans.json";
    if (!bans.Load(bans_path.string(), error) ||
        !std::filesystem::exists(bans_path))
    {
        std::cerr << error << '\n';
        return 7;
    }

    neo_admin::BanRecord saved{};
    std::string message;
    const std::string permanent_request =
        "{\"steamId\":\"76561198000000001\","
        "\"playerName\":\"Test Player\","
        "\"reason\":\"Repeated team damage\","
        "\"durationMinutes\":0}";
    if (!bans.Upsert(permanent_request, "moderator", saved, message) ||
        saved.steam_id != 76561198000000001ULL || saved.expires_unix != 0)
    {
        std::cerr << message << '\n';
        return 8;
    }

    std::string reason;
    if (!bans.IsBanned(saved.steam_id, reason) ||
        reason != "Repeated team damage")
    {
        return 9;
    }
    const std::string ban_catalog = bans.BuildCatalogJson();
    if (ban_catalog.find("\"76561198000000001\"") == std::string::npos ||
        ban_catalog.find("Repeated team damage") == std::string::npos)
    {
        return 10;
    }

    neo_admin::BanRecord temporary{};
    const std::string temporary_request =
        "{\"steamId\":\"76561198000000002\","
        "\"playerName\":\"Temporary Player\","
        "\"reason\":\"Mic spam\","
        "\"durationMinutes\":30}";
    if (!bans.Upsert(temporary_request, "moderator", temporary, message) ||
        temporary.expires_unix == 0 || bans.ActiveSize() != 2)
    {
        return 11;
    }

    std::string removed_target;
    if (!bans.Remove("76561198000000001", removed_target, message) ||
        removed_target.find("Test Player") == std::string::npos ||
        bans.IsBanned(76561198000000001ULL, reason))
    {
        return 12;
    }

    neo_admin::BanStore reloaded_bans;
    if (!reloaded_bans.Load(bans_path.string(), error) ||
        reloaded_bans.ActiveSize() != 1 ||
        !reloaded_bans.IsBanned(76561198000000002ULL, reason))
    {
        std::cerr << error << '\n';
        return 13;
    }

    neo_admin::BanRecord invalid{};
    if (bans.Upsert(
            "{\"steamId\":\"123\",\"playerName\":\"Bad\","
            "\"reason\":\"Invalid\",\"durationMinutes\":5}",
            "moderator",
            invalid,
            message))
    {
        return 14;
    }

    std::filesystem::remove_all(directory, error_code);
    return 0;
}
