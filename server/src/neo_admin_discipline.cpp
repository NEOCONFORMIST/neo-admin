#include "neo_admin_discipline.h"
#include "neo_admin_persistence.h"

#include "vendor/nlohmann/json.hpp"

#include <algorithm>
#include <charconv>
#include <ctime>
#include <iomanip>
#include <sstream>

namespace neo_admin
{
namespace
{
using json = nlohmann::json;
constexpr std::uint64_t kMaximumDurationMinutes = 2628000;
constexpr std::size_t kMaximumRestrictions = 160;
constexpr std::size_t kMaximumHistory = 2000;
constexpr std::size_t kMaximumCatalogBytes = 60000;

std::uint64_t UnixNow()
{
    const std::time_t value = std::time(nullptr);
    return value > 0 ? static_cast<std::uint64_t>(value) : 0;
}

std::string UtcNow()
{
    const std::time_t value = std::time(nullptr);
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

bool ParseSteamId(std::string_view text, std::uint64_t& value)
{
    value = 0;
    if (text.empty() || text.size() > 20)
        return false;
    const auto parsed = std::from_chars(text.data(), text.data() + text.size(), value);
    return parsed.ec == std::errc{} && parsed.ptr == text.data() + text.size() &&
        value >= 76561197960265728ULL;
}

bool InvalidText(std::string_view value)
{
    return std::any_of(value.begin(), value.end(), [](unsigned char ch)
        { return ch == 0 || ch == '\r' || ch == '\n' || ch < 0x20U; });
}

bool ValidType(std::string_view type)
{
    return type == "Mute" || type == "Gag";
}

json RestrictionJson(const RestrictionRecord& value)
{
    return {
        {"steamId", std::to_string(value.steam_id)},
        {"playerName", value.player_name},
        {"type", value.type},
        {"reason", value.reason},
        {"createdBy", value.created_by},
        {"createdUtc", value.created_utc},
        {"expiresUnix", value.expires_unix},
    };
}

json HistoryJson(const DisciplineRecord& value)
{
    return {
        {"steamId", std::to_string(value.steam_id)},
        {"playerName", value.player_name},
        {"action", value.action},
        {"reason", value.reason},
        {"createdBy", value.created_by},
        {"createdUtc", value.created_utc},
        {"expiresUnix", value.expires_unix},
    };
}
} // namespace

bool DisciplineStore::Load(const std::string& path, std::string& error)
{
    path_ = path;
    restrictions_.clear();
    history_.clear();
    if (path_.empty())
    {
        error = "Discipline storage path is invalid.";
        return false;
    }
    std::string stored_json;
    bool exists = false;
    if (!ReadJsonDocument(
            "discipline", path_, stored_json, exists, error))
        return false;
    if (!exists)
        return Save(error);
    const json document = json::parse(stored_json, nullptr, false, true);
    if (document.is_discarded() || !document.is_object())
    {
        error = "The discipline file has invalid JSON.";
        return false;
    }

    auto read_common = [&](const json& item, std::uint64_t& steam_id,
        std::string& player_name, std::string& reason, std::string& created_by,
        std::string& created_utc, std::uint64_t& expires_unix) -> bool
    {
        if (!item.is_object() || !item.value("steamId", json()).is_string() ||
            !item.value("playerName", json()).is_string() ||
            !item.value("reason", json()).is_string() ||
            !item.value("createdBy", json()).is_string() ||
            !item.value("createdUtc", json()).is_string() ||
            !item.value("expiresUnix", json()).is_number_unsigned())
            return false;
        const std::string steam_text = item["steamId"].get<std::string>();
        player_name = item["playerName"].get<std::string>();
        reason = item["reason"].get<std::string>();
        created_by = item["createdBy"].get<std::string>();
        created_utc = item["createdUtc"].get<std::string>();
        expires_unix = item["expiresUnix"].get<std::uint64_t>();
        return ParseSteamId(steam_text, steam_id) && player_name.size() <= 64 &&
            reason.size() <= 160 && created_by.size() <= 32 && created_utc.size() <= 32 &&
            !InvalidText(player_name) && !InvalidText(reason) && !InvalidText(created_by);
    };

    if (document.contains("restrictions") && document["restrictions"].is_array())
    {
        for (const json& item : document["restrictions"])
        {
            RestrictionRecord value{};
            if (!read_common(item, value.steam_id, value.player_name, value.reason,
                    value.created_by, value.created_utc, value.expires_unix) ||
                !item.value("type", json()).is_string())
            {
                error = "The discipline file contains an invalid restriction.";
                return false;
            }
            value.type = item["type"].get<std::string>();
            if (!ValidType(value.type))
            {
                error = "The discipline file contains an invalid restriction type.";
                return false;
            }
            restrictions_.push_back(std::move(value));
        }
    }
    if (document.contains("history") && document["history"].is_array())
    {
        for (const json& item : document["history"])
        {
            DisciplineRecord value{};
            if (!read_common(item, value.steam_id, value.player_name, value.reason,
                    value.created_by, value.created_utc, value.expires_unix) ||
                !item.value("action", json()).is_string())
            {
                error = "The discipline file contains an invalid history record.";
                return false;
            }
            value.action = item["action"].get<std::string>();
            if (value.action.empty() || value.action.size() > 32 || InvalidText(value.action))
            {
                error = "The discipline file contains an invalid history action.";
                return false;
            }
            history_.push_back(std::move(value));
        }
    }
    PruneExpired();
    return true;
}

bool DisciplineStore::UpsertRestriction(
    std::string_view request_json,
    std::string_view acting_account_id,
    RestrictionRecord& saved,
    std::string& message)
{
    const json request = json::parse(request_json, nullptr, false);
    if (request.is_discarded() || !request.is_object() ||
        !request.value("steamId", json()).is_string() ||
        !request.value("playerName", json()).is_string() ||
        !request.value("type", json()).is_string() ||
        !request.value("reason", json()).is_string() ||
        !request.value("durationMinutes", json()).is_number_integer())
    {
        message = "Restriction request JSON is invalid.";
        return false;
    }
    const std::string steam_text = request["steamId"].get<std::string>();
    const std::string player_name = request["playerName"].get<std::string>();
    const std::string type = request["type"].get<std::string>();
    const std::string reason = request["reason"].get<std::string>();
    const std::int64_t duration = request["durationMinutes"].get<std::int64_t>();
    std::uint64_t steam_id = 0;
    if (!ParseSteamId(steam_text, steam_id))
        message = "Enter a valid SteamID64.";
    else if (!ValidType(type))
        message = "Restriction type must be Mute or Gag.";
    else if (player_name.size() > 64 || InvalidText(player_name))
        message = "Player name must be 64 characters or fewer.";
    else if (reason.empty() || reason.size() > 160 || InvalidText(reason))
        message = "Reason must be 1-160 characters on one line.";
    else if (duration < 0 || static_cast<std::uint64_t>(duration) > kMaximumDurationMinutes)
        message = "Restriction duration is outside the supported range.";
    else if (acting_account_id.empty() || acting_account_id.size() > 32)
        message = "The acting administrator account is invalid.";
    else
        message.clear();
    if (!message.empty())
        return false;

    PruneExpired();
    const std::vector<RestrictionRecord> previous_restrictions = restrictions_;
    const std::vector<DisciplineRecord> previous_history = history_;
    const std::uint64_t now = UnixNow();
    const std::uint64_t expires = duration == 0 ? 0 : now + static_cast<std::uint64_t>(duration) * 60U;
    RestrictionRecord replacement{
        .steam_id = steam_id,
        .player_name = player_name.empty() ? steam_text : player_name,
        .type = type,
        .reason = reason,
        .created_by = std::string(acting_account_id),
        .created_utc = UtcNow(),
        .expires_unix = expires,
        .duration_minutes = static_cast<std::uint64_t>(duration),
    };
    auto found = std::find_if(restrictions_.begin(), restrictions_.end(), [&](const RestrictionRecord& value)
        { return value.steam_id == steam_id && value.type == type; });
    if (found == restrictions_.end() && restrictions_.size() >= kMaximumRestrictions)
    {
        message = "The active restriction limit has been reached.";
        return false;
    }
    if (found == restrictions_.end())
        restrictions_.push_back(replacement);
    else
        *found = replacement;
    history_.push_back({ steam_id, replacement.player_name, type, reason,
        std::string(acting_account_id), replacement.created_utc, expires });
    if (history_.size() > kMaximumHistory)
        history_.erase(history_.begin(), history_.begin() + (history_.size() - kMaximumHistory));
    std::string error;
    if (!Save(error))
    {
        restrictions_ = previous_restrictions;
        history_ = previous_history;
        message = error;
        return false;
    }
    saved = replacement;
    message = type == "Mute" ? "Player muted." : "Player gagged.";
    return true;
}

bool DisciplineStore::RemoveRestriction(
    std::string_view request_json,
    std::string_view acting_account_id,
    RestrictionRecord& removed,
    std::string& message)
{
    const json request = json::parse(request_json, nullptr, false);
    if (request.is_discarded() || !request.is_object() ||
        !request.value("steamId", json()).is_string() ||
        !request.value("type", json()).is_string())
    {
        message = "Restriction removal JSON is invalid.";
        return false;
    }
    std::uint64_t steam_id = 0;
    const std::string steam_text = request["steamId"].get<std::string>();
    const std::string type = request["type"].get<std::string>();
    if (!ParseSteamId(steam_text, steam_id) || !ValidType(type))
    {
        message = "Restriction identity is invalid.";
        return false;
    }
    auto found = std::find_if(restrictions_.begin(), restrictions_.end(), [&](const RestrictionRecord& value)
        { return value.steam_id == steam_id && value.type == type; });
    if (found == restrictions_.end())
    {
        message = "Restriction was not found.";
        return false;
    }
    const std::vector<RestrictionRecord> previous_restrictions = restrictions_;
    const std::vector<DisciplineRecord> previous_history = history_;
    removed = *found;
    restrictions_.erase(found);
    history_.push_back({ steam_id, removed.player_name, type == "Mute" ? "Unmute" : "Ungag",
        "Restriction removed", std::string(acting_account_id), UtcNow(), 0 });
    if (history_.size() > kMaximumHistory)
        history_.erase(history_.begin(), history_.begin() + (history_.size() - kMaximumHistory));
    std::string error;
    if (!Save(error))
    {
        restrictions_ = previous_restrictions;
        history_ = previous_history;
        message = error;
        return false;
    }
    message = type == "Mute" ? "Player unmuted." : "Player ungagged.";
    return true;
}

bool DisciplineStore::Record(
    std::uint64_t steam_id,
    std::string_view player_name,
    std::string_view action,
    std::string_view reason,
    std::string_view acting_account_id,
    std::uint64_t expires_unix,
    std::string& error)
{
    if (steam_id < 76561197960265728ULL || action.empty() || action.size() > 32 ||
        player_name.size() > 64 || reason.size() > 160 || acting_account_id.size() > 32 ||
        InvalidText(player_name) || InvalidText(action) || InvalidText(reason) || InvalidText(acting_account_id))
    {
        error = "Discipline history record is invalid.";
        return false;
    }
    const std::vector<DisciplineRecord> previous = history_;
    history_.push_back({ steam_id, std::string(player_name), std::string(action),
        std::string(reason), std::string(acting_account_id), UtcNow(), expires_unix });
    if (history_.size() > kMaximumHistory)
        history_.erase(history_.begin(), history_.begin() + (history_.size() - kMaximumHistory));
    if (!Save(error))
    {
        history_ = previous;
        return false;
    }
    return true;
}

std::string DisciplineStore::BuildRestrictionCatalogJson() const
{
    json document{{"version", 1}, {"restrictions", json::array()}};
    const std::uint64_t now = UnixNow();
    for (const RestrictionRecord& value : restrictions_)
    {
        if (value.expires_unix != 0 && value.expires_unix <= now)
            continue;
        document["restrictions"].push_back(RestrictionJson(value));
        if (document.dump().size() > kMaximumCatalogBytes)
        {
            document["restrictions"].erase(document["restrictions"].end() - 1);
            break;
        }
    }
    return document.dump();
}

std::string DisciplineStore::BuildHistoryJson(std::string_view steam_text) const
{
    std::uint64_t steam_id = 0;
    if (!steam_text.empty() && !ParseSteamId(steam_text, steam_id))
        return {};
    json document{{"version", 1}, {"steamId", std::string(steam_text)}, {"history", json::array()}};
    for (auto iterator = history_.rbegin(); iterator != history_.rend(); ++iterator)
    {
        if (steam_id != 0 && iterator->steam_id != steam_id)
            continue;
        document["history"].push_back(HistoryJson(*iterator));
        if (document.dump().size() > kMaximumCatalogBytes)
        {
            document["history"].erase(document["history"].end() - 1);
            break;
        }
    }
    return document.dump();
}

std::size_t DisciplineStore::ActiveSize() const
{
    const std::uint64_t now = UnixNow();
    return static_cast<std::size_t>(std::count_if(restrictions_.begin(), restrictions_.end(),
        [&](const RestrictionRecord& value) { return value.expires_unix == 0 || value.expires_unix > now; }));
}

void DisciplineStore::PruneExpired()
{
    const std::uint64_t now = UnixNow();
    restrictions_.erase(std::remove_if(restrictions_.begin(), restrictions_.end(),
        [&](const RestrictionRecord& value) { return value.expires_unix != 0 && value.expires_unix <= now; }),
        restrictions_.end());
}

bool DisciplineStore::Save(std::string& error) const
{
    json document{{"version", 1}, {"restrictions", json::array()}, {"history", json::array()}};
    for (const RestrictionRecord& value : restrictions_)
        document["restrictions"].push_back(RestrictionJson(value));
    for (const DisciplineRecord& value : history_)
        document["history"].push_back(HistoryJson(value));
    return WriteJsonDocument(
        "discipline", path_, document.dump(2) + '\n', error);
}
} // namespace neo_admin
