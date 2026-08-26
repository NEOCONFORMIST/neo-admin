#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace neo_admin
{
struct DueMapChange
{
    std::string map;
    std::string created_by;
    std::string source;
};

struct DueAnnouncement
{
    std::string message;
    std::string created_by;
};

class OperationsStore
{
public:
    bool Load(const std::string& path, std::string& error);
    std::string BuildRotationJson() const;
    std::string BuildAnnouncementsJson() const;
    bool SaveRotation(std::string_view request_json,
        const std::vector<std::string>& allowed_maps,
        std::string_view actor, std::string& message);
    bool SaveScheduledMap(std::string_view request_json,
        const std::vector<std::string>& allowed_maps,
        std::string_view actor, std::string& message);
    bool DeleteScheduledMap(std::string_view id, std::string& message);
    bool RunNextMap(DueMapChange& due, std::string& message);
    bool TakeDueMap(DueMapChange& due);
    bool SaveAnnouncement(std::string_view request_json,
        std::string_view actor, std::string& message);
    bool DeleteAnnouncement(std::string_view id, std::string& message);
    bool TakeDueAnnouncement(DueAnnouncement& due);

private:
    struct ScheduledMap
    {
        std::uint64_t id = 0;
        std::string map;
        std::uint64_t scheduled_unix = 0;
        std::string created_by;
    };
    struct Announcement
    {
        std::uint64_t id = 0;
        std::string message;
        std::uint64_t scheduled_unix = 0;
        std::uint64_t repeat_minutes = 0;
        std::string created_by;
    };

    bool Save(std::string& error) const;
    std::string path_;
    bool rotation_enabled_ = false;
    std::size_t current_index_ = 0;
    std::vector<std::string> rotation_maps_;
    std::vector<ScheduledMap> scheduled_maps_;
    std::vector<Announcement> announcements_;
    std::uint64_t next_id_ = 1;
};
} // namespace neo_admin
