#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace neo_admin
{
struct Account
{
    std::string id;
    std::string display_name;
    std::string role;
    std::uint64_t permissions = 0;
    std::uint64_t steam_id = 0;
    bool enabled = true;
    bool uses_server_secret = false;
    std::string secret;
    std::string access_selector;
    std::string created_utc;
    std::uint64_t expires_unix = 0;
};

struct LegacySteamLink
{
    std::uint64_t steam_id = 0;
    std::string display_name;
    std::string role;
    std::uint64_t permissions = 0;
    bool enabled = true;
    std::string created_utc;
};

class AccountStore
{
public:
    bool Load(
        const std::string& path,
        const std::vector<std::uint8_t>& server_secret,
        std::string& error);

    const Account* Find(std::string_view id) const;
    const Account* FindByAccessSelector(std::string_view selector) const;
    const Account* FindBySteamId(std::uint64_t steam_id) const;
    bool IsExpired(const Account& account) const;
    std::vector<std::uint8_t> ResolveSecret(const Account& account) const;
    std::string BuildCatalogJson() const;
    std::vector<LegacySteamLink> LegacySteamLinks() const;
    bool ClearLegacySteamLinks(std::string& error);

    bool BootstrapOwner(
        std::string_view account_id,
        std::string_view display_name,
        std::string_view access_key,
        std::string& message);

    bool Upsert(
        std::string_view request_json,
        std::string_view acting_account_id,
        std::string& message);

    bool Remove(
        std::string_view account_id,
        std::string_view acting_account_id,
        std::string& message);

    std::size_t Size() const { return accounts_.size(); }

private:
    bool Save(std::string& error) const;
    bool HasAnotherEnabledManager(std::string_view excluded_id) const;

    std::string path_;
    std::vector<std::uint8_t> server_secret_;
    std::vector<Account> accounts_;
};
} // namespace neo_admin
