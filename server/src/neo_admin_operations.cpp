#include "neo_admin_operations.h"
#include "neo_admin_persistence.h"

#include "vendor/nlohmann/json.hpp"

#include <algorithm>
#include <charconv>
#include <cctype>
#include <ctime>
namespace neo_admin
{
namespace
{
using json = nlohmann::json;
constexpr std::size_t kMaximumRotationMaps = 40;
constexpr std::size_t kMaximumSchedules = 100;
constexpr std::uint64_t kMaximumFutureSeconds = 315360000;

std::uint64_t UnixNow()
{
    const std::time_t now = std::time(nullptr);
    return now > 0 ? static_cast<std::uint64_t>(now) : 0;
}

bool InvalidText(std::string_view value)
{
    return std::any_of(value.begin(), value.end(), [](unsigned char ch)
        { return ch == 0 || ch == '\r' || ch == '\n' || ch < 0x20U; });
}

bool ParseId(std::string_view text, std::uint64_t& id)
{
    id = 0;
    const auto result = std::from_chars(text.data(), text.data() + text.size(), id);
    return !text.empty() && result.ec == std::errc{} &&
        result.ptr == text.data() + text.size() && id != 0;
}

const std::string* MatchAllowed(
    std::string_view requested,
    const std::vector<std::string>& allowed)
{
    auto equal_ascii = [](char left, char right)
        { return std::tolower(static_cast<unsigned char>(left)) ==
            std::tolower(static_cast<unsigned char>(right)); };
    for (const std::string& map : allowed)
    {
        if (map.size() == requested.size() &&
            std::equal(map.begin(), map.end(), requested.begin(), equal_ascii))
            return &map;
    }
    return nullptr;
}
} // namespace

bool OperationsStore::Load(const std::string& path, std::string& error)
{
    path_ = path;
    rotation_enabled_ = false;
    current_index_ = 0;
    rotation_maps_.clear();
    scheduled_maps_.clear();
    announcements_.clear();
    next_id_ = 1;
    std::string stored_json;
    bool exists = false;
    if (!ReadJsonDocument(
            "operations", path_, stored_json, exists, error))
        return false;
    if (!exists)
        return Save(error);
    const json document = json::parse(stored_json, nullptr, false, true);
    if (document.is_discarded() || !document.is_object())
    {
        error = "The operations file has invalid JSON.";
        return false;
    }
    rotation_enabled_ = document.value("rotationEnabled", false);
    current_index_ = document.value("currentIndex", std::size_t{0});
    next_id_ = document.value("nextId", std::uint64_t{1});
    if (document.contains("rotationMaps") && document["rotationMaps"].is_array())
    {
        if (document["rotationMaps"].size() > kMaximumRotationMaps)
        {
            error = "The operations file contains too many rotation maps.";
            return false;
        }
        for (const json& item : document["rotationMaps"])
        {
            if (!item.is_string() || item.get_ref<const std::string&>().empty() ||
                item.get_ref<const std::string&>().size() > 260 ||
                InvalidText(item.get_ref<const std::string&>()))
            {
                error = "The operations file contains an invalid rotation map.";
                return false;
            }
            rotation_maps_.push_back(item.get<std::string>());
        }
    }
    if (document.contains("scheduledMaps") && document["scheduledMaps"].is_array())
    {
        if (document["scheduledMaps"].size() > kMaximumSchedules)
        {
            error = "The operations file contains too many scheduled maps.";
            return false;
        }
        for (const json& item : document["scheduledMaps"])
        {
            if (!item.is_object() || !item.value("id", json()).is_number_unsigned() ||
                !item.value("map", json()).is_string() ||
                !item.value("scheduledUnix", json()).is_number_unsigned() ||
                !item.value("createdBy", json()).is_string())
            {
                error = "The operations file contains an invalid map schedule.";
                return false;
            }
            scheduled_maps_.push_back({ item["id"].get<std::uint64_t>(),
                item["map"].get<std::string>(), item["scheduledUnix"].get<std::uint64_t>(),
                item["createdBy"].get<std::string>() });
            next_id_ = std::max(next_id_, scheduled_maps_.back().id + 1);
        }
    }
    if (document.contains("announcements") && document["announcements"].is_array())
    {
        if (document["announcements"].size() > kMaximumSchedules)
        {
            error = "The operations file contains too many announcements.";
            return false;
        }
        for (const json& item : document["announcements"])
        {
            if (!item.is_object() || !item.value("id", json()).is_number_unsigned() ||
                !item.value("message", json()).is_string() ||
                !item.value("scheduledUnix", json()).is_number_unsigned() ||
                !item.value("repeatMinutes", json()).is_number_unsigned() ||
                !item.value("createdBy", json()).is_string())
            {
                error = "The operations file contains an invalid announcement.";
                return false;
            }
            announcements_.push_back({ item["id"].get<std::uint64_t>(),
                item["message"].get<std::string>(), item["scheduledUnix"].get<std::uint64_t>(),
                item["repeatMinutes"].get<std::uint64_t>(), item["createdBy"].get<std::string>() });
            if (announcements_.back().message.empty() || announcements_.back().message.size() > 220 ||
                InvalidText(announcements_.back().message))
            {
                error = "The operations file contains invalid announcement text.";
                return false;
            }
            next_id_ = std::max(next_id_, announcements_.back().id + 1);
        }
    }
    if (rotation_maps_.empty() || current_index_ >= rotation_maps_.size())
        current_index_ = 0;
    return true;
}

std::string OperationsStore::BuildRotationJson() const
{
    json document{{"version", 1}, {"enabled", rotation_enabled_},
        {"currentIndex", current_index_}, {"maps", rotation_maps_}, {"schedules", json::array()}};
    for (const ScheduledMap& value : scheduled_maps_)
        document["schedules"].push_back({ {"id", value.id}, {"map", value.map},
            {"scheduledUnix", value.scheduled_unix}, {"createdBy", value.created_by} });
    return document.dump();
}

std::string OperationsStore::BuildAnnouncementsJson() const
{
    json document{{"version", 1}, {"announcements", json::array()}};
    for (const Announcement& value : announcements_)
        document["announcements"].push_back({ {"id", value.id}, {"message", value.message},
            {"scheduledUnix", value.scheduled_unix}, {"repeatMinutes", value.repeat_minutes},
            {"createdBy", value.created_by} });
    return document.dump();
}

bool OperationsStore::SaveRotation(std::string_view request_json,
    const std::vector<std::string>& allowed_maps, std::string_view, std::string& message)
{
    const json request = json::parse(request_json, nullptr, false);
    if (request.is_discarded() || !request.is_object() ||
        !request.value("enabled", json()).is_boolean() ||
        !request.value("maps", json()).is_array() ||
        request["maps"].size() > kMaximumRotationMaps)
    {
        message = "Map rotation request JSON is invalid.";
        return false;
    }
    std::vector<std::string> maps;
    for (const json& item : request["maps"])
    {
        if (!item.is_string())
        {
            message = "A rotation map is invalid.";
            return false;
        }
        const std::string requested = item.get<std::string>();
        const std::string* allowed = MatchAllowed(requested, allowed_maps);
        if (!allowed)
        {
            message = "A rotation map was not found in the server map catalog.";
            return false;
        }
        if (std::find(maps.begin(), maps.end(), *allowed) == maps.end())
            maps.push_back(*allowed);
    }
    const bool previous_enabled = rotation_enabled_;
    const std::size_t previous_index = current_index_;
    const std::vector<std::string> previous_maps = rotation_maps_;
    rotation_enabled_ = request["enabled"].get<bool>();
    rotation_maps_ = std::move(maps);
    current_index_ = rotation_maps_.empty() ? 0 : std::min(current_index_, rotation_maps_.size() - 1);
    std::string error;
    if (!Save(error))
    {
        rotation_enabled_ = previous_enabled;
        current_index_ = previous_index;
        rotation_maps_ = previous_maps;
        message = error;
        return false;
    }
    message = "Map rotation saved.";
    return true;
}

bool OperationsStore::SaveScheduledMap(std::string_view request_json,
    const std::vector<std::string>& allowed_maps, std::string_view actor, std::string& message)
{
    const json request = json::parse(request_json, nullptr, false);
    if (request.is_discarded() || !request.is_object() ||
        !request.value("map", json()).is_string() ||
        !request.value("scheduledUnix", json()).is_number_unsigned())
    {
        message = "Scheduled map request JSON is invalid.";
        return false;
    }
    const std::string requested = request["map"].get<std::string>();
    const std::string* map = MatchAllowed(requested, allowed_maps);
    const std::uint64_t when = request["scheduledUnix"].get<std::uint64_t>();
    const std::uint64_t now = UnixNow();
    if (!map || when + 5 < now || when > now + kMaximumFutureSeconds ||
        scheduled_maps_.size() >= kMaximumSchedules)
    {
        message = "Scheduled map, time, or schedule limit is invalid.";
        return false;
    }
    scheduled_maps_.push_back({ next_id_++, *map, when, std::string(actor) });
    std::string error;
    if (!Save(error))
    {
        scheduled_maps_.pop_back();
        --next_id_;
        message = error;
        return false;
    }
    message = "Map change scheduled.";
    return true;
}

bool OperationsStore::DeleteScheduledMap(std::string_view text, std::string& message)
{
    std::uint64_t id = 0;
    if (!ParseId(text, id))
    {
        message = "Scheduled map ID is invalid.";
        return false;
    }
    auto found = std::find_if(scheduled_maps_.begin(), scheduled_maps_.end(),
        [&](const ScheduledMap& value) { return value.id == id; });
    if (found == scheduled_maps_.end())
    {
        message = "Scheduled map was not found.";
        return false;
    }
    const ScheduledMap previous = *found;
    const std::size_t index = static_cast<std::size_t>(found - scheduled_maps_.begin());
    scheduled_maps_.erase(found);
    std::string error;
    if (!Save(error))
    {
        scheduled_maps_.insert(scheduled_maps_.begin() + index, previous);
        message = error;
        return false;
    }
    message = "Scheduled map removed.";
    return true;
}

bool OperationsStore::RunNextMap(DueMapChange& due, std::string& message)
{
    if (rotation_maps_.empty())
    {
        message = "The map rotation is empty.";
        return false;
    }
    due = { rotation_maps_[current_index_], {}, "rotation" };
    current_index_ = (current_index_ + 1) % rotation_maps_.size();
    std::string error;
    if (!Save(error))
    {
        current_index_ = current_index_ == 0 ? rotation_maps_.size() - 1 : current_index_ - 1;
        message = error;
        return false;
    }
    message = "Changing to the next rotation map.";
    return true;
}

bool OperationsStore::TakeDueMap(DueMapChange& due)
{
    const std::uint64_t now = UnixNow();
    auto found = std::min_element(scheduled_maps_.begin(), scheduled_maps_.end(),
        [](const ScheduledMap& left, const ScheduledMap& right)
            { return left.scheduled_unix < right.scheduled_unix; });
    if (found == scheduled_maps_.end() || found->scheduled_unix > now)
        return false;
    due = { found->map, found->created_by, "schedule" };
    scheduled_maps_.erase(found);
    std::string ignored;
    (void)Save(ignored);
    return true;
}

bool OperationsStore::SaveAnnouncement(
    std::string_view request_json, std::string_view actor, std::string& message)
{
    const json request = json::parse(request_json, nullptr, false);
    if (request.is_discarded() || !request.is_object() ||
        !request.value("message", json()).is_string() ||
        !request.value("scheduledUnix", json()).is_number_unsigned() ||
        !request.value("repeatMinutes", json()).is_number_unsigned())
    {
        message = "Announcement request JSON is invalid.";
        return false;
    }
    const std::string text = request["message"].get<std::string>();
    const std::uint64_t when = request["scheduledUnix"].get<std::uint64_t>();
    const std::uint64_t repeat = request["repeatMinutes"].get<std::uint64_t>();
    const std::uint64_t now = UnixNow();
    if (text.empty() || text.size() > 220 || InvalidText(text) || when + 5 < now ||
        when > now + kMaximumFutureSeconds || repeat > 525600 ||
        announcements_.size() >= kMaximumSchedules)
    {
        message = "Announcement text, time, repeat, or schedule limit is invalid.";
        return false;
    }
    announcements_.push_back({ next_id_++, text, when, repeat, std::string(actor) });
    std::string error;
    if (!Save(error))
    {
        announcements_.pop_back();
        --next_id_;
        message = error;
        return false;
    }
    message = "Announcement scheduled.";
    return true;
}

bool OperationsStore::DeleteAnnouncement(std::string_view text, std::string& message)
{
    std::uint64_t id = 0;
    if (!ParseId(text, id))
    {
        message = "Announcement ID is invalid.";
        return false;
    }
    auto found = std::find_if(announcements_.begin(), announcements_.end(),
        [&](const Announcement& value) { return value.id == id; });
    if (found == announcements_.end())
    {
        message = "Announcement was not found.";
        return false;
    }
    const Announcement previous = *found;
    const std::size_t index = static_cast<std::size_t>(found - announcements_.begin());
    announcements_.erase(found);
    std::string error;
    if (!Save(error))
    {
        announcements_.insert(announcements_.begin() + index, previous);
        message = error;
        return false;
    }
    message = "Announcement removed.";
    return true;
}

bool OperationsStore::TakeDueAnnouncement(DueAnnouncement& due)
{
    const std::uint64_t now = UnixNow();
    auto found = std::min_element(announcements_.begin(), announcements_.end(),
        [](const Announcement& left, const Announcement& right)
            { return left.scheduled_unix < right.scheduled_unix; });
    if (found == announcements_.end() || found->scheduled_unix > now)
        return false;
    due = { found->message, found->created_by };
    if (found->repeat_minutes == 0)
        announcements_.erase(found);
    else
    {
        const std::uint64_t interval = found->repeat_minutes * 60U;
        do { found->scheduled_unix += interval; } while (found->scheduled_unix <= now);
    }
    std::string ignored;
    (void)Save(ignored);
    return true;
}

bool OperationsStore::Save(std::string& error) const
{
    json document{{"version", 1}, {"nextId", next_id_},
        {"rotationEnabled", rotation_enabled_}, {"currentIndex", current_index_},
        {"rotationMaps", rotation_maps_}, {"scheduledMaps", json::array()},
        {"announcements", json::array()}};
    for (const ScheduledMap& value : scheduled_maps_)
        document["scheduledMaps"].push_back({ {"id", value.id}, {"map", value.map},
            {"scheduledUnix", value.scheduled_unix}, {"createdBy", value.created_by} });
    for (const Announcement& value : announcements_)
        document["announcements"].push_back({ {"id", value.id}, {"message", value.message},
            {"scheduledUnix", value.scheduled_unix}, {"repeatMinutes", value.repeat_minutes},
            {"createdBy", value.created_by} });
    return WriteJsonDocument(
        "operations", path_, document.dump(2) + '\n', error);
}
} // namespace neo_admin
