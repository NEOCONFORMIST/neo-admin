#pragma once

#include "neo_admin_accounts.h"

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace neo_admin
{
struct GameAdmin
{
    std::uint64_t steam_id = 0;
    std::string display_name;
    std::string role;
    std::uint64_t permissions = 0;
    bool enabled = true;
    std::string created_utc;
};

class GameAdminStore
{
public:
    bool Load(
        const std::string& path,
        const std::vector<LegacySteamLink>& legacy_links,
        std::string& error);

    const GameAdmin* FindBySteamId(std::uint64_t steam_id) const;
    std::string BuildCatalogJson() const;
    bool Upsert(std::string_view request_json, std::string& message);
    bool Remove(std::string_view steam_id, std::string& message);
    std::size_t Size() const { return admins_.size(); }

private:
    bool Save(std::string& error) const;

    std::string path_;
    std::vector<GameAdmin> admins_;
};
} // namespace neo_admin
