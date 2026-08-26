#include "neo_admin_discipline.h"
#include "neo_admin_operations.h"

#include <ctime>
#include <filesystem>
#include <iostream>
#include <string>
#include <vector>

int main()
{
    const std::filesystem::path directory =
        std::filesystem::temp_directory_path() / "neo-admin-operations-test";
    std::error_code error_code;
    std::filesystem::remove_all(directory, error_code);
    std::filesystem::create_directories(directory, error_code);
    if (error_code)
        return 2;

    std::string error;
    std::string message;
    neo_admin::DisciplineStore discipline;
    const std::filesystem::path discipline_path = directory / "discipline.json";
    if (!discipline.Load(discipline_path.string(), error))
        return 3;
    neo_admin::RestrictionRecord restriction{};
    const std::string mute =
        "{\"steamId\":\"76561198000000003\",\"playerName\":\"Voice Player\","
        "\"type\":\"Mute\",\"reason\":\"Mic spam\",\"durationMinutes\":30}";
    if (!discipline.UpsertRestriction(mute, "moderator", restriction, message) ||
        restriction.type != "Mute" || restriction.expires_unix == 0 ||
        discipline.ActiveSize() != 1)
        return 4;
    if (discipline.BuildRestrictionCatalogJson().find("Mic spam") == std::string::npos ||
        discipline.BuildHistoryJson("76561198000000003").find("Mute") == std::string::npos)
        return 5;
    neo_admin::RestrictionRecord removed{};
    if (!discipline.RemoveRestriction(
            "{\"steamId\":\"76561198000000003\",\"type\":\"Mute\"}",
            "owner", removed, message) || discipline.ActiveSize() != 0 ||
        discipline.BuildHistoryJson("76561198000000003").find("Unmute") == std::string::npos)
        return 6;
    if (!discipline.Record(76561198000000003ULL, "Voice Player", "Kick",
            "Team damage", "owner", 0, error))
        return 7;
    neo_admin::DisciplineStore reloaded_discipline;
    if (!reloaded_discipline.Load(discipline_path.string(), error) ||
        reloaded_discipline.BuildHistoryJson("76561198000000003").find("Team damage") == std::string::npos)
        return 8;

    neo_admin::OperationsStore operations;
    const std::filesystem::path operations_path = directory / "operations.json";
    if (!operations.Load(operations_path.string(), error))
        return 9;
    const std::vector<std::string> allowed{ "de_dust2", "de_nuke", "workshop/123/test" };
    if (!operations.SaveRotation(
            "{\"enabled\":true,\"maps\":[\"de_nuke\",\"de_dust2\"]}",
            allowed, "owner", message) ||
        operations.BuildRotationJson().find("de_nuke") == std::string::npos)
        return 10;
    if (operations.SaveRotation(
            "{\"enabled\":true,\"maps\":[\"prefabs/de_nuke_skybox\"]}",
            allowed, "owner", message))
        return 11;
    neo_admin::DueMapChange next{};
    if (!operations.RunNextMap(next, message) || next.map != "de_nuke")
        return 12;

    const std::uint64_t now = static_cast<std::uint64_t>(std::time(nullptr));
    const std::string scheduled_map =
        "{\"map\":\"workshop/123/test\",\"scheduledUnix\":" +
        std::to_string(now) + "}";
    if (!operations.SaveScheduledMap(scheduled_map, allowed, "owner", message))
        return 13;
    neo_admin::DueMapChange due_map{};
    if (!operations.TakeDueMap(due_map) || due_map.map != "workshop/123/test" ||
        operations.TakeDueMap(due_map))
        return 14;

    const std::string one_time =
        "{\"message\":\"Match begins soon\",\"scheduledUnix\":" +
        std::to_string(now) + ",\"repeatMinutes\":0}";
    if (!operations.SaveAnnouncement(one_time, "owner", message))
        return 15;
    neo_admin::DueAnnouncement due_announcement{};
    if (!operations.TakeDueAnnouncement(due_announcement) ||
        due_announcement.message != "Match begins soon" ||
        operations.TakeDueAnnouncement(due_announcement))
        return 16;

    const std::string repeating =
        "{\"message\":\"Remember the rules\",\"scheduledUnix\":" +
        std::to_string(now) + ",\"repeatMinutes\":5}";
    if (!operations.SaveAnnouncement(repeating, "owner", message) ||
        !operations.TakeDueAnnouncement(due_announcement) ||
        operations.BuildAnnouncementsJson().find("Remember the rules") == std::string::npos)
        return 17;

    neo_admin::OperationsStore reloaded_operations;
    if (!reloaded_operations.Load(operations_path.string(), error) ||
        reloaded_operations.BuildRotationJson().find("de_dust2") == std::string::npos ||
        reloaded_operations.BuildAnnouncementsJson().find("Remember the rules") == std::string::npos)
        return 18;

    std::filesystem::remove_all(directory, error_code);
    return 0;
}
