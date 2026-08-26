#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace neo_admin
{
struct BanRecord
{
    std::uint64_t steam_id = 0;
    std::string player_name;
    std::string reason;
    std::string created_by;
    std::string created_utc;
    std::uint64_t expires_unix = 0;
};

class BanStore
{
public:
    bool Load(const std::string& path, std::string& error);
    bool Upsert(
        std::string_view request_json,
        std::string_view acting_account_id,
        BanRecord& saved,
        std::string& message);
    bool Remove(
        std::string_view steam_id,
        std::string& removed_target,
        std::string& message);
    bool IsBanned(std::uint64_t steam_id, std::string& reason) const;
    std::string BuildCatalogJson() const;
    std::size_t ActiveSize() const;

private:
    bool Save(std::string& error) const;

    std::string path_;
    std::vector<BanRecord> bans_;
};
} // namespace neo_admin
