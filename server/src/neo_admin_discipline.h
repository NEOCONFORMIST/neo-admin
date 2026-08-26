#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace neo_admin
{
struct RestrictionRecord
{
    std::uint64_t steam_id = 0;
    std::string player_name;
    std::string type;
    std::string reason;
    std::string created_by;
    std::string created_utc;
    std::uint64_t expires_unix = 0;
    std::uint64_t duration_minutes = 0;
};

struct DisciplineRecord
{
    std::uint64_t steam_id = 0;
    std::string player_name;
    std::string action;
    std::string reason;
    std::string created_by;
    std::string created_utc;
    std::uint64_t expires_unix = 0;
};

class DisciplineStore
{
public:
    bool Load(const std::string& path, std::string& error);
    bool UpsertRestriction(
        std::string_view request_json,
        std::string_view acting_account_id,
        RestrictionRecord& saved,
        std::string& message);
    bool RemoveRestriction(
        std::string_view request_json,
        std::string_view acting_account_id,
        RestrictionRecord& removed,
        std::string& message);
    bool Record(
        std::uint64_t steam_id,
        std::string_view player_name,
        std::string_view action,
        std::string_view reason,
        std::string_view acting_account_id,
        std::uint64_t expires_unix,
        std::string& error);
    std::string BuildRestrictionCatalogJson() const;
    std::string BuildHistoryJson(std::string_view steam_id) const;
    std::size_t ActiveSize() const;

private:
    void PruneExpired();
    bool Save(std::string& error) const;

    std::string path_;
    std::vector<RestrictionRecord> restrictions_;
    std::vector<DisciplineRecord> history_;
};
} // namespace neo_admin
