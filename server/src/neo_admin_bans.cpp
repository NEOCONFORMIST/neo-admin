#include "neo_admin_bans.h"
#include "neo_admin_persistence.h"

#include "vendor/nlohmann/json.hpp"

#include <algorithm>
#include <charconv>
#include <chrono>
#include <ctime>
#include <iomanip>
#include <limits>
#include <sstream>

namespace neo_admin
{
namespace
{
using json = nlohmann::json;
constexpr std::uint64_t kMaximumDurationMinutes = 2628000;
constexpr std::size_t kMaximumActiveBans = 80;
constexpr std::size_t kMaximumCatalogBytes = 60000;

std::uint64_t UnixNow()
{
    const std::time_t now = std::time(nullptr);
    return now > 0 ? static_cast<std::uint64_t>(now) : 0;
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
    const auto result = std::from_chars(text.data(), text.data() + text.size(), value);
    return result.ec == std::errc{} && result.ptr == text.data() + text.size() &&
        value >= 76561197960265728ULL;
}

bool HasInvalidText(std::string_view value)
{
    return std::any_of(value.begin(), value.end(), [](unsigned char ch)
    {
        return ch == 0 || ch == '\r' || ch == '\n' || ch < 0x20U;
    });
}

json ToJson(const BanRecord& ban)
{
    return {
        {"steamId", std::to_string(ban.steam_id)},
        {"playerName", ban.player_name},
        {"reason", ban.reason},
        {"createdBy", ban.created_by},
        {"createdUtc", ban.created_utc},
        {"expiresUnix", ban.expires_unix},
    };
}
} // namespace

bool BanStore::Load(const std::string& path, std::string& error)
{
    path_ = path;
    bans_.clear();
    if (path_.empty())
    {
        error = "Ban storage path is invalid.";
        return false;
    }

    std::string stored_json;
    bool exists = false;
    if (!ReadJsonDocument("bans", path_, stored_json, exists, error))
        return false;
    if (!exists)
        return Save(error);
    const json document = json::parse(stored_json, nullptr, false, true);
    if (document.is_discarded() || !document.is_object() ||
        !document.contains("bans") || !document["bans"].is_array())
    {
        error = "The ban file has invalid JSON or no bans array.";
        return false;
    }

    for (const json& item : document["bans"])
    {
        if (!item.is_object() ||
            !item.contains("steamId") || !item["steamId"].is_string() ||
            !item.contains("playerName") || !item["playerName"].is_string() ||
            !item.contains("reason") || !item["reason"].is_string() ||
            !item.contains("createdBy") || !item["createdBy"].is_string() ||
            !item.contains("createdUtc") || !item["createdUtc"].is_string() ||
            !item.contains("expiresUnix") || !item["expiresUnix"].is_number_unsigned())
        {
            error = "The ban file contains an invalid record.";
            bans_.clear();
            return false;
        }

        BanRecord ban{};
        const std::string steam_id = item["steamId"].get<std::string>();
        ban.player_name = item["playerName"].get<std::string>();
        ban.reason = item["reason"].get<std::string>();
        ban.created_by = item["createdBy"].get<std::string>();
        ban.created_utc = item["createdUtc"].get<std::string>();
        ban.expires_unix = item["expiresUnix"].get<std::uint64_t>();
        if (!ParseSteamId(steam_id, ban.steam_id) || ban.player_name.size() > 64 ||
            ban.reason.empty() || ban.reason.size() > 160 || ban.created_by.size() > 32 ||
            ban.created_utc.size() > 32 || HasInvalidText(ban.player_name) ||
            HasInvalidText(ban.reason) || HasInvalidText(ban.created_by))
        {
            error = "The ban file contains an invalid record value.";
            bans_.clear();
            return false;
        }
        if (std::any_of(bans_.begin(), bans_.end(), [&](const BanRecord& existing)
            { return existing.steam_id == ban.steam_id; }))
        {
            error = "The ban file contains duplicate Steam IDs.";
            bans_.clear();
            return false;
        }
        bans_.push_back(std::move(ban));
    }
    return true;
}

bool BanStore::Upsert(
    std::string_view request_json,
    std::string_view acting_account_id,
    BanRecord& saved,
    std::string& message)
{
    const json request = json::parse(request_json, nullptr, false);
    if (request.is_discarded() || !request.is_object() ||
        !request.contains("steamId") || !request["steamId"].is_string() ||
        !request.contains("playerName") || !request["playerName"].is_string() ||
        !request.contains("reason") || !request["reason"].is_string() ||
        !request.contains("durationMinutes") || !request["durationMinutes"].is_number_integer())
    {
        message = "Ban request JSON is invalid.";
        return false;
    }

    const std::string steam_id_text = request["steamId"].get<std::string>();
    const std::string player_name = request["playerName"].get<std::string>();
    const std::string reason = request["reason"].get<std::string>();
    const std::int64_t duration = request["durationMinutes"].get<std::int64_t>();
    std::uint64_t steam_id = 0;
    if (!ParseSteamId(steam_id_text, steam_id))
    {
        message = "Enter a valid SteamID64.";
        return false;
    }
    if (player_name.size() > 64 || HasInvalidText(player_name))
    {
        message = "Player name must be 64 characters or fewer.";
        return false;
    }
    if (reason.empty() || reason.size() > 160 || HasInvalidText(reason))
    {
        message = "Ban reason must be 1-160 characters on one line.";
        return false;
    }
    if (duration < 0 || static_cast<std::uint64_t>(duration) > kMaximumDurationMinutes)
    {
        message = "Ban duration is outside the supported range.";
        return false;
    }
    if (acting_account_id.empty() || acting_account_id.size() > 32)
    {
        message = "The acting administrator account is invalid.";
        return false;
    }

    const std::vector<BanRecord> previous = bans_;
    const std::uint64_t now = UnixNow();
    bans_.erase(
        std::remove_if(bans_.begin(), bans_.end(), [&](const BanRecord& ban)
        {
            return ban.expires_unix != 0 && ban.expires_unix <= now;
        }),
        bans_.end());
    const std::uint64_t expires = duration == 0
        ? 0
        : now + static_cast<std::uint64_t>(duration) * 60U;
    BanRecord replacement{
        .steam_id = steam_id,
        .player_name = player_name.empty() ? steam_id_text : player_name,
        .reason = reason,
        .created_by = std::string(acting_account_id),
        .created_utc = UtcNow(),
        .expires_unix = expires,
    };

    auto found = std::find_if(bans_.begin(), bans_.end(), [&](const BanRecord& ban)
        { return ban.steam_id == steam_id; });
    const bool updating = found != bans_.end();
    if (!updating && bans_.size() >= kMaximumActiveBans)
    {
        bans_ = previous;
        message = "The active ban limit has been reached.";
        return false;
    }
    if (updating)
        *found = replacement;
    else
        bans_.push_back(replacement);

    std::string save_error;
    if (!Save(save_error))
    {
        bans_ = previous;
        message = save_error;
        return false;
    }
    saved = replacement;
    message = updating ? "Ban updated." : "Player banned.";
    return true;
}

bool BanStore::Remove(
    std::string_view steam_id_text,
    std::string& removed_target,
    std::string& message)
{
    std::uint64_t steam_id = 0;
    if (!ParseSteamId(steam_id_text, steam_id))
    {
        message = "Enter a valid SteamID64.";
        return false;
    }
    const auto found = std::find_if(bans_.begin(), bans_.end(), [&](const BanRecord& ban)
        { return ban.steam_id == steam_id; });
    if (found == bans_.end())
    {
        message = "Ban was not found.";
        return false;
    }
    removed_target = found->player_name + " (" + std::to_string(found->steam_id) + ")";
    const std::vector<BanRecord> previous = bans_;
    bans_.erase(found);
    std::string save_error;
    if (!Save(save_error))
    {
        bans_ = previous;
        message = save_error;
        return false;
    }
    message = "Player unbanned.";
    return true;
}

bool BanStore::IsBanned(std::uint64_t steam_id, std::string& reason) const
{
    const std::uint64_t now = UnixNow();
    const auto found = std::find_if(bans_.begin(), bans_.end(), [&](const BanRecord& ban)
    {
        return ban.steam_id == steam_id &&
            (ban.expires_unix == 0 || ban.expires_unix > now);
    });
    if (found == bans_.end())
        return false;
    reason = found->reason;
    return true;
}

std::string BanStore::BuildCatalogJson() const
{
    json document;
    document["version"] = 1;
    document["bans"] = json::array();
    const std::uint64_t now = UnixNow();
    for (const BanRecord& ban : bans_)
    {
        if (ban.expires_unix == 0 || ban.expires_unix > now)
        {
            document["bans"].push_back(ToJson(ban));
            if (document.dump().size() > kMaximumCatalogBytes)
            {
                document["bans"].erase(document["bans"].end() - 1);
                break;
            }
        }
    }
    return document.dump();
}

std::size_t BanStore::ActiveSize() const
{
    const std::uint64_t now = UnixNow();
    return static_cast<std::size_t>(std::count_if(
        bans_.begin(), bans_.end(), [&](const BanRecord& ban)
        { return ban.expires_unix == 0 || ban.expires_unix > now; }));
}

bool BanStore::Save(std::string& error) const
{
    json document;
    document["version"] = 1;
    document["bans"] = json::array();
    for (const BanRecord& ban : bans_)
        document["bans"].push_back(ToJson(ban));

    return WriteJsonDocument(
        "bans", path_, document.dump(2) + '\n', error);
}
} // namespace neo_admin
