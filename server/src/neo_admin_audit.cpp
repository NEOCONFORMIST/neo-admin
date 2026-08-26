#include "neo_admin_audit.h"
#include "neo_admin_persistence.h"

#include "vendor/nlohmann/json.hpp"

#include <algorithm>
#include <chrono>
#include <ctime>
#include <iomanip>
#include <sstream>

namespace neo_admin
{
namespace
{
using json = nlohmann::json;
constexpr std::size_t kMaximumStoredEvents = 2000;
constexpr std::size_t kMaximumCatalogBytes = 60000;

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

std::string CleanText(std::string_view value, std::size_t maximum)
{
    std::string result;
    result.reserve(std::min(value.size(), maximum));
    for (char ch : value)
    {
        if (result.size() >= maximum)
            break;
        if (ch == '\r' || ch == '\n' || ch == '\0')
            result.push_back(' ');
        else if (static_cast<unsigned char>(ch) >= 0x20U)
            result.push_back(ch);
    }
    return result;
}

json ToJson(const AuditEvent& event)
{
    return {
        {"id", event.id},
        {"createdUtc", event.created_utc},
        {"accountId", event.account_id},
        {"action", event.action},
        {"target", event.target},
        {"success", event.success},
        {"details", event.details},
    };
}
} // namespace

bool AuditStore::Load(const std::string& path, std::string& error)
{
    path_ = path;
    events_.clear();
    next_id_ = 1;
    if (path_.empty())
    {
        error = "Audit storage path is invalid.";
        return false;
    }

    std::string stored_json;
    bool exists = false;
    if (!ReadJsonDocument("audit", path_, stored_json, exists, error))
        return false;
    if (!exists)
        return Save(error);
    const json document = json::parse(stored_json, nullptr, false, true);
    if (document.is_discarded() || !document.is_object() ||
        !document.contains("events") || !document["events"].is_array())
    {
        error = "The audit log file has invalid JSON or no events array.";
        return false;
    }

    for (const json& item : document["events"])
    {
        if (!item.is_object() ||
            !item.contains("id") || !item["id"].is_number_unsigned() ||
            !item.contains("createdUtc") || !item["createdUtc"].is_string() ||
            !item.contains("accountId") || !item["accountId"].is_string() ||
            !item.contains("action") || !item["action"].is_string() ||
            !item.contains("target") || !item["target"].is_string() ||
            !item.contains("success") || !item["success"].is_boolean() ||
            !item.contains("details") || !item["details"].is_string())
        {
            error = "The audit log contains an invalid event.";
            events_.clear();
            return false;
        }

        AuditEvent event{
            .id = item["id"].get<std::uint64_t>(),
            .created_utc = item["createdUtc"].get<std::string>(),
            .account_id = item["accountId"].get<std::string>(),
            .action = item["action"].get<std::string>(),
            .target = item["target"].get<std::string>(),
            .success = item["success"].get<bool>(),
            .details = item["details"].get<std::string>(),
        };
        if (event.created_utc.size() > 32 || event.account_id.size() > 32 ||
            event.action.empty() || event.action.size() > 64 ||
            event.target.size() > 128 || event.details.size() > 256)
        {
            error = "The audit log contains an invalid event value.";
            events_.clear();
            return false;
        }
        next_id_ = std::max(next_id_, event.id + 1);
        events_.push_back(std::move(event));
    }

    if (events_.size() > kMaximumStoredEvents)
        events_.erase(events_.begin(), events_.end() - kMaximumStoredEvents);
    return true;
}

bool AuditStore::Append(
    std::string_view account_id,
    std::string_view action,
    std::string_view target,
    bool success,
    std::string_view details,
    std::string& error)
{
    const std::vector<AuditEvent> previous = events_;
    const std::uint64_t previous_next_id = next_id_;
    AuditEvent event{
        .id = next_id_++,
        .created_utc = UtcNow(),
        .account_id = CleanText(account_id.empty() ? "unknown" : account_id, 32),
        .action = CleanText(action.empty() ? "Unknown action" : action, 64),
        .target = CleanText(target, 128),
        .success = success,
        .details = CleanText(details, 256),
    };
    events_.push_back(std::move(event));
    if (events_.size() > kMaximumStoredEvents)
        events_.erase(events_.begin());

    if (Save(error))
        return true;
    events_ = previous;
    next_id_ = previous_next_id;
    return false;
}

std::string AuditStore::BuildCatalogJson(std::size_t limit) const
{
    json document;
    document["version"] = 1;
    document["events"] = json::array();
    limit = std::min(limit, events_.size());
    for (std::size_t offset = 0; offset < limit; ++offset)
    {
        document["events"].push_back(
            ToJson(events_[events_.size() - 1 - offset]));
        if (document.dump().size() > kMaximumCatalogBytes)
        {
            document["events"].erase(document["events"].end() - 1);
            break;
        }
    }
    return document.dump();
}

bool AuditStore::Save(std::string& error) const
{
    json document;
    document["version"] = 1;
    document["events"] = json::array();
    for (const AuditEvent& event : events_)
        document["events"].push_back(ToJson(event));

    return WriteJsonDocument(
        "audit", path_, document.dump(2) + '\n', error);
}
} // namespace neo_admin
