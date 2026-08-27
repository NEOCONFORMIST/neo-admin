#include "neo_admin_accounts.h"
#include "neo_admin_persistence.h"
#include "neo_admin_permissions.h"
#include "voicebridge_protocol.h"

#include "vendor/nlohmann/json.hpp"

#include <algorithm>
#include <charconv>
#include <chrono>
#include <cctype>
#include <ctime>
#include <iomanip>
#include <sstream>

namespace neo_admin
{
namespace
{
using json = nlohmann::json;

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

bool IsValidId(std::string_view id)
{
    if (id.size() < 3 || id.size() > 32)
        return false;

    return std::all_of(
        id.begin(),
        id.end(),
        [](unsigned char ch)
        {
            return std::isalnum(ch) || ch == '.' || ch == '_' || ch == '-';
        });
}

bool IsValidRole(std::string_view role)
{
    return role == "Viewer" || role == "Moderator" ||
        role == "Event Admin" || role == "Administrator" ||
        role == "Senior Admin" || role == "Owner" || role == "Custom";
}

bool ParseOptionalSteamId(const json& value, std::uint64_t& steam_id)
{
    steam_id = 0;
    if (value.is_null())
        return true;
    if (!value.is_string())
        return false;
    const std::string text = value.get<std::string>();
    if (text.empty())
        return true;
    const auto result = std::from_chars(
        text.data(), text.data() + text.size(), steam_id);
    return result.ec == std::errc{} && result.ptr == text.data() + text.size() &&
        steam_id >= 76561197960265728ULL;
}
} // namespace

bool AccountStore::Load(
    const std::string& path,
    const std::vector<std::uint8_t>& server_secret,
    std::string& error)
{
    path_ = path;
    server_secret_ = server_secret;
    accounts_.clear();

    if (path_.empty())
    {
        error = "Account storage path is invalid.";
        return false;
    }

    std::string stored_json;
    bool account_file_exists = false;
    if (!ReadJsonDocument(
            "accounts", path_, stored_json, account_file_exists, error))
        return false;

    if (!account_file_exists)
    {
        // Preserve the existing configured-secret bootstrap. A truly fresh
        // installation with no server secret remains empty until its one-time
        // setup code is claimed from the Windows app.
        if (server_secret_.size() >= 16)
        {
            accounts_.push_back(Account{
                .id = "owner",
                .display_name = "Neo",
                .role = "Owner",
                .permissions = kOwnerPermissions,
                .enabled = true,
                .uses_server_secret = true,
                .secret = {},
                .access_selector = {},
                .created_utc = UtcNow(),
            });
            accounts_.back().access_selector =
                voicebridge::BuildAdminAccessSelector(
                    ResolveSecret(accounts_.back()));
        }

        if (!Save(error))
            return false;

        return true;
    }

    const json document = json::parse(stored_json, nullptr, false, true);
    if (document.is_discarded() || !document.is_object() ||
        !document.contains("accounts") || !document["accounts"].is_array())
    {
        error = "The administrator account file has invalid JSON or no accounts array.";
        return false;
    }

    for (const json& item : document["accounts"])
    {
        if (!item.is_object() ||
            !item.contains("id") || !item["id"].is_string() ||
            !item.contains("displayName") || !item["displayName"].is_string() ||
            !item.contains("role") || !item["role"].is_string() ||
            !item.contains("permissions") || !item["permissions"].is_number_unsigned() ||
            !item.contains("enabled") || !item["enabled"].is_boolean() ||
            !item.contains("usesServerSecret") || !item["usesServerSecret"].is_boolean() ||
            !item.contains("secret") || !item["secret"].is_string())
        {
            error = "The administrator account file contains an invalid account record.";
            accounts_.clear();
            return false;
        }

        Account account{};
        account.id = item["id"].get<std::string>();
        account.display_name = item["displayName"].get<std::string>();
        account.role = item["role"].get<std::string>();
        account.permissions = item["permissions"].get<std::uint64_t>();
        if (!ParseOptionalSteamId(
                item.contains("steamId") ? item["steamId"] : json(nullptr),
                account.steam_id))
        {
            error = "The administrator account file contains an invalid SteamID64.";
            accounts_.clear();
            return false;
        }
        account.enabled = item["enabled"].get<bool>();
        account.uses_server_secret = item["usesServerSecret"].get<bool>();
        account.secret = item["secret"].get<std::string>();
        account.created_utc =
            item.contains("createdUtc") && item["createdUtc"].is_string()
                ? item["createdUtc"].get<std::string>()
                : "";
        if (item.contains("expiresUnix") &&
            !item["expiresUnix"].is_number_unsigned())
        {
            error = "The administrator account file contains an invalid expiration.";
            accounts_.clear();
            return false;
        }
        account.expires_unix =
            item.contains("expiresUnix")
                ? item["expiresUnix"].get<std::uint64_t>()
                : 0;

        // Named roles receive newly introduced role capabilities without
        // replacing any deliberate per-account permission customizations.
        if (account.role == "Owner" || account.role == "Senior Admin" ||
            account.role == "Administrator")
            account.permissions |= ToMask(Permission::ViewAuditLog);
        if (account.role == "Owner" || account.role == "Senior Admin" ||
            account.role == "Administrator" || account.role == "Moderator")
        {
            account.permissions |= ToMask(Permission::ManageBans);
            account.permissions |= ToMask(Permission::ManageDiscipline);
        }
        if (account.role == "Owner" || account.role == "Senior Admin" ||
            account.role == "Administrator")
        {
            account.permissions |= ToMask(Permission::ManageMapRotation);
            account.permissions |= ToMask(Permission::ManageAnnouncements);
            account.permissions |= ToMask(Permission::ManageZombieMode);
            account.permissions |= ToMask(Permission::ManageWorkshopMaps);
        }
        if (account.role == "Event Admin")
            account.permissions |= ToMask(Permission::ManageWorkshopMaps);
        if (account.role == "Owner" || account.role == "Senior Admin")
            account.permissions |= ToMask(Permission::ManageGameAdmins);
        if (account.role == "Owner")
        {
            account.permissions |= ToMask(Permission::RunServerConsole);
        }

        if (!IsValidId(account.id) || account.display_name.empty() ||
            account.display_name.size() > 64 || !IsValidRole(account.role) ||
            (account.uses_server_secret && server_secret_.size() < 16) ||
            (!account.uses_server_secret && account.secret.size() < 16))
        {
            error = "The administrator account file contains an invalid account.";
            accounts_.clear();
            return false;
        }

        if (Find(account.id))
        {
            error = "The administrator account file contains duplicate IDs.";
            accounts_.clear();
            return false;
        }

        const bool duplicate_steam_id = account.steam_id != 0 &&
            std::any_of(accounts_.begin(), accounts_.end(),
                [&](const Account& existing)
                {
                    return existing.steam_id == account.steam_id;
                });
        if (duplicate_steam_id)
        {
            error = "The administrator account file contains a duplicate SteamID64 link.";
            accounts_.clear();
            return false;
        }

        account.access_selector = voicebridge::BuildAdminAccessSelector(
            ResolveSecret(account));
        accounts_.push_back(std::move(account));
    }

    const bool has_manager = std::any_of(
        accounts_.begin(),
        accounts_.end(),
        [](const Account& account)
        {
            return account.enabled &&
                account.expires_unix == 0 &&
                HasPermission(account.permissions, Permission::ManageAccounts);
        });

    if (!accounts_.empty() && !has_manager)
    {
        error = "At least one enabled account must be able to manage accounts.";
        accounts_.clear();
        return false;
    }

    return true;
}

const Account* AccountStore::Find(std::string_view id) const
{
    const auto found = std::find_if(
        accounts_.begin(),
        accounts_.end(),
        [&](const Account& account) { return account.id == id; });
    return found == accounts_.end() ? nullptr : &*found;
}

const Account* AccountStore::FindByAccessSelector(
    std::string_view selector) const
{
    if (selector.size() != 32 || !selector.starts_with("key_"))
        return nullptr;

    const Account* match = nullptr;
    for (const Account& account : accounts_)
    {
        if (account.access_selector != selector)
            continue;
        if (match)
            return nullptr;
        match = &account;
    }
    return match;
}

const Account* AccountStore::FindBySteamId(std::uint64_t steam_id) const
{
    if (steam_id == 0)
        return nullptr;
    const auto found = std::find_if(
        accounts_.begin(), accounts_.end(),
        [&](const Account& account)
        {
            return account.steam_id == steam_id;
        });
    return found == accounts_.end() ? nullptr : &*found;
}

bool AccountStore::IsExpired(const Account& account) const
{
    if (account.expires_unix == 0)
        return false;
    const std::time_t now = std::time(nullptr);
    return now >= 0 && account.expires_unix <= static_cast<std::uint64_t>(now);
}

std::vector<std::uint8_t> AccountStore::ResolveSecret(const Account& account) const
{
    if (account.uses_server_secret)
        return server_secret_;

    return std::vector<std::uint8_t>(account.secret.begin(), account.secret.end());
}

std::string AccountStore::BuildCatalogJson() const
{
    json document;
    document["version"] = 1;
    document["accounts"] = json::array();

    for (const Account& account : accounts_)
    {
        document["accounts"].push_back({
            {"id", account.id},
            {"displayName", account.display_name},
            {"role", account.role},
            {"permissions", account.permissions},
            {"enabled", account.enabled},
            {"expiresUnix", account.expires_unix},
            {"createdUtc", account.created_utc},
            {"credential", account.uses_server_secret ? "Server owner key" : "Individual key"},
        });
    }

    return document.dump();
}

std::vector<LegacySteamLink> AccountStore::LegacySteamLinks() const
{
    std::vector<LegacySteamLink> links;
    for (const Account& account : accounts_)
    {
        if (account.steam_id == 0)
            continue;
        links.push_back(LegacySteamLink{
            .steam_id = account.steam_id,
            .display_name = account.display_name,
            .role = account.role,
            .permissions = account.permissions,
            .enabled = account.enabled,
            .created_utc = account.created_utc,
        });
    }
    return links;
}

bool AccountStore::ClearLegacySteamLinks(std::string& error)
{
    const std::vector<Account> previous = accounts_;
    bool changed = false;
    for (Account& account : accounts_)
    {
        changed = changed || account.steam_id != 0;
        account.steam_id = 0;
    }
    if (!changed)
        return true;
    if (Save(error))
        return true;
    accounts_ = previous;
    return false;
}

bool AccountStore::BootstrapOwner(
    std::string_view account_id,
    std::string_view display_name,
    std::string_view access_key,
    std::string& message)
{
    if (!accounts_.empty())
    {
        message = "Initial setup is already complete.";
        return false;
    }
    if (!IsValidId(account_id))
    {
        message = "Account ID must be 3-32 letters, numbers, dots, dashes, or underscores.";
        return false;
    }
    if (display_name.empty() || display_name.size() > 64)
    {
        message = "Display name must be 1-64 characters.";
        return false;
    }
    if (access_key.size() < 32 || access_key.size() > 128)
    {
        message = "The first owner access key must be 32-128 characters.";
        return false;
    }

    accounts_.push_back(Account{
        .id = std::string(account_id),
        .display_name = std::string(display_name),
        .role = "Owner",
        .permissions = kOwnerPermissions,
        .enabled = true,
        .uses_server_secret = false,
        .secret = std::string(access_key),
        .access_selector = {},
        .created_utc = UtcNow(),
    });
    accounts_.back().access_selector =
        voicebridge::BuildAdminAccessSelector(
            ResolveSecret(accounts_.back()));

    std::string save_error;
    if (!Save(save_error))
    {
        accounts_.clear();
        message = save_error;
        return false;
    }

    message = "First owner account created.";
    return true;
}

bool AccountStore::Upsert(
    std::string_view request_json,
    std::string_view acting_account_id,
    std::string& message)
{
    const json request = json::parse(request_json, nullptr, false);
    if (request.is_discarded() || !request.is_object() ||
        !request.contains("id") || !request["id"].is_string() ||
        !request.contains("displayName") || !request["displayName"].is_string() ||
        !request.contains("role") || !request["role"].is_string() ||
        !request.contains("permissions") || !request["permissions"].is_number_unsigned() ||
        !request.contains("enabled") || !request["enabled"].is_boolean())
    {
        message = "Account request JSON is invalid.";
        return false;
    }

    const std::string id = request["id"].get<std::string>();
    const std::string display_name = request["displayName"].get<std::string>();
    const std::string role = request["role"].get<std::string>();
    const std::uint64_t permissions = request["permissions"].get<std::uint64_t>();
    const bool enabled = request["enabled"].get<bool>();
    if (request.contains("expiresUnix") &&
        !request["expiresUnix"].is_number_unsigned())
    {
        message = "The account expiration is invalid.";
        return false;
    }
    const std::uint64_t expires_unix =
        request.contains("expiresUnix")
            ? request["expiresUnix"].get<std::uint64_t>()
            : 0;
    const std::string secret =
        request.contains("secret") && request["secret"].is_string()
            ? request["secret"].get<std::string>()
            : "";

        if (!IsValidId(id))
        {
            message = "Account ID must be 3-32 letters, numbers, dots, dashes, or underscores.";
            return false;
        }
        if (display_name.empty() || display_name.size() > 64)
        {
            message = "Display name must be 1-64 characters.";
            return false;
        }
        if (!IsValidRole(role))
        {
            message = "The selected role is invalid.";
            return false;
        }

        auto found = std::find_if(
            accounts_.begin(),
            accounts_.end(),
            [&](const Account& account) { return account.id == id; });

        const bool creating = found == accounts_.end();
        if (creating && accounts_.size() >= 128)
        {
            message = "The administrator account limit has been reached.";
            return false;
        }
        if (creating && secret.size() < 32)
        {
            message = "New accounts require a generated access key of at least 32 characters.";
            return false;
        }
        if (!secret.empty() && secret.size() < 32)
        {
            message = "Replacement access keys must be at least 32 characters.";
            return false;
        }
        const std::time_t now = std::time(nullptr);
        if (expires_unix != 0 && now >= 0 &&
            expires_unix <= static_cast<std::uint64_t>(now))
        {
            message = "Access expiration must be in the future.";
            return false;
        }

        if (!creating && found->id == acting_account_id &&
            (!enabled || expires_unix != 0 ||
                !HasPermission(permissions, Permission::ManageAccounts)))
        {
            message = "You cannot disable, expire, or remove your own account-management access.";
            return false;
        }

        if (!creating && found->enabled && found->expires_unix == 0 &&
            HasPermission(found->permissions, Permission::ManageAccounts) &&
            (!enabled || expires_unix != 0 ||
                !HasPermission(permissions, Permission::ManageAccounts)) &&
            !HasAnotherEnabledManager(id))
        {
            message = "The last permanent account manager cannot be disabled, expired, or demoted.";
            return false;
        }

        const std::vector<Account> previous = accounts_;
        if (creating)
        {
            accounts_.push_back(Account{
                .id = id,
                .display_name = display_name,
                .role = role,
                .permissions = permissions,
                .enabled = enabled,
                .uses_server_secret = false,
                .secret = secret,
                .access_selector = {},
                .created_utc = UtcNow(),
                .expires_unix = expires_unix,
            });
            accounts_.back().access_selector =
                voicebridge::BuildAdminAccessSelector(
                    ResolveSecret(accounts_.back()));
        }
        else
        {
            found->display_name = display_name;
            found->role = role;
            found->permissions = permissions;
            found->enabled = enabled;
            found->expires_unix = expires_unix;
            if (!secret.empty())
            {
                found->uses_server_secret = false;
                found->secret = secret;
            }
            found->access_selector =
                voicebridge::BuildAdminAccessSelector(
                    ResolveSecret(*found));
        }

        std::string save_error;
        if (!Save(save_error))
        {
            accounts_ = previous;
            message = save_error;
            return false;
        }

    message = creating ? "Administrator account created." : "Administrator account updated.";
    return true;
}

bool AccountStore::Remove(
    std::string_view account_id,
    std::string_view acting_account_id,
    std::string& message)
{
    if (account_id == acting_account_id)
    {
        message = "You cannot delete the account you are currently using.";
        return false;
    }

    const auto found = std::find_if(
        accounts_.begin(),
        accounts_.end(),
        [&](const Account& account) { return account.id == account_id; });

    if (found == accounts_.end())
    {
        message = "Administrator account was not found.";
        return false;
    }

    if (found->enabled && found->expires_unix == 0 &&
        HasPermission(found->permissions, Permission::ManageAccounts) &&
        !HasAnotherEnabledManager(account_id))
    {
        message = "The last enabled account manager cannot be deleted.";
        return false;
    }

    const std::vector<Account> previous = accounts_;
    accounts_.erase(found);

    std::string save_error;
    if (!Save(save_error))
    {
        accounts_ = previous;
        message = save_error;
        return false;
    }

    message = "Administrator account deleted.";
    return true;
}

bool AccountStore::Save(std::string& error) const
{
    json document;
    document["version"] = 1;
    document["accounts"] = json::array();
    for (const Account& account : accounts_)
    {
        document["accounts"].push_back({
            {"id", account.id},
            {"displayName", account.display_name},
            {"role", account.role},
            {"permissions", account.permissions},
            {"enabled", account.enabled},
            {"expiresUnix", account.expires_unix},
            {"usesServerSecret", account.uses_server_secret},
            {"secret", account.uses_server_secret ? "" : account.secret},
            {"createdUtc", account.created_utc},
        });
    }
    return WriteJsonDocument(
        "accounts", path_, document.dump(2) + '\n', error);
}

bool AccountStore::HasAnotherEnabledManager(std::string_view excluded_id) const
{
    return std::any_of(
        accounts_.begin(),
        accounts_.end(),
        [&](const Account& account)
        {
            return account.id != excluded_id && account.enabled &&
                account.expires_unix == 0 &&
                HasPermission(account.permissions, Permission::ManageAccounts);
        });
}
} // namespace neo_admin
