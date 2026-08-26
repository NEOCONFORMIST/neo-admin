#pragma once

#include <cstdint>
#include <string_view>

namespace neo_admin
{
enum class Permission : std::uint64_t
{
    None = 0,
    ViewDashboard = 1ULL << 0,
    ViewSteamIds = 1ULL << 1,
    SendChat = 1ULL << 2,
    BroadcastVoice = 1ULL << 3,
    ModeratePlayers = 1ULL << 4,
    ControlBots = 1ULL << 5,
    ControlMatch = 1ULL << 6,
    ChangeMap = 1ULL << 7,
    TeleportPlayers = 1ULL << 8,
    ManageAccounts = 1ULL << 9,
    RestartServer = 1ULL << 10,
    DeployPlugin = 1ULL << 11,
    ViewAuditLog = 1ULL << 12,
    ManageBans = 1ULL << 13,
    ManageDiscipline = 1ULL << 14,
    ManageMapRotation = 1ULL << 15,
    ManageAnnouncements = 1ULL << 16,
    ManageGameAdmins = 1ULL << 17,
    RunServerConsole = 1ULL << 18,
    ManageZombieMode = 1ULL << 19,
    ManageWorkshopMaps = 1ULL << 20,
};

constexpr std::uint64_t ToMask(Permission permission)
{
    return static_cast<std::uint64_t>(permission);
}

constexpr std::uint64_t kViewerPermissions =
    ToMask(Permission::ViewDashboard) |
    ToMask(Permission::ViewSteamIds);

constexpr std::uint64_t kModeratorPermissions =
    kViewerPermissions |
    ToMask(Permission::SendChat) |
    ToMask(Permission::BroadcastVoice) |
    ToMask(Permission::ModeratePlayers) |
    ToMask(Permission::ManageBans) |
    ToMask(Permission::ManageDiscipline);

constexpr std::uint64_t kAdministratorPermissions =
    kModeratorPermissions |
    ToMask(Permission::ControlBots) |
    ToMask(Permission::ControlMatch) |
    ToMask(Permission::ChangeMap) |
    ToMask(Permission::TeleportPlayers) |
    ToMask(Permission::ViewAuditLog) |
    ToMask(Permission::ManageMapRotation) |
    ToMask(Permission::ManageAnnouncements) |
    ToMask(Permission::ManageWorkshopMaps) |
    ToMask(Permission::ManageZombieMode);

constexpr std::uint64_t kEventAdminPermissions =
    kViewerPermissions |
    ToMask(Permission::SendChat) |
    ToMask(Permission::BroadcastVoice) |
    ToMask(Permission::ModeratePlayers) |
    ToMask(Permission::ControlBots) |
    ToMask(Permission::ControlMatch) |
    ToMask(Permission::ChangeMap) |
    ToMask(Permission::ManageAnnouncements) |
    ToMask(Permission::ManageWorkshopMaps);

constexpr std::uint64_t kSeniorAdminPermissions =
    kAdministratorPermissions |
    ToMask(Permission::ManageAccounts) |
    ToMask(Permission::ManageGameAdmins);

constexpr std::uint64_t kOwnerPermissions =
    kSeniorAdminPermissions |
    ToMask(Permission::RunServerConsole) |
    ToMask(Permission::RestartServer) |
    ToMask(Permission::DeployPlugin);

constexpr bool HasPermission(
    std::uint64_t permissions,
    Permission required)
{
    const std::uint64_t mask = ToMask(required);
    return mask != 0 && (permissions & mask) == mask;
}

inline std::uint64_t PermissionsForRole(const char* role)
{
    if (!role)
        return 0;

    const std::string_view value(role);
    if (value == "Owner")
        return kOwnerPermissions;
    if (value == "Administrator")
        return kAdministratorPermissions;
    if (value == "Senior Admin")
        return kSeniorAdminPermissions;
    if (value == "Event Admin")
        return kEventAdminPermissions;
    if (value == "Moderator")
        return kModeratorPermissions;
    if (value == "Viewer")
        return kViewerPermissions;
    return 0;
}
} // namespace neo_admin
