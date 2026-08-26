#pragma once

#include <cstdint>
#include <string_view>

namespace neo_admin
{
enum class GamePermission : std::uint64_t
{
    None = 0,
    ModeratePlayers = 1ULL << 0,
    ManageBans = 1ULL << 1,
    ManageDiscipline = 1ULL << 2,
    ControlBots = 1ULL << 3,
    ControlMatch = 1ULL << 4,
    ChangeMap = 1ULL << 5,
    ManageMapRotation = 1ULL << 6,
    ManageAnnouncements = 1ULL << 7,
};

constexpr std::uint64_t ToGameMask(GamePermission permission)
{
    return static_cast<std::uint64_t>(permission);
}

constexpr std::uint64_t kGameModeratorPermissions =
    ToGameMask(GamePermission::ModeratePlayers) |
    ToGameMask(GamePermission::ManageBans) |
    ToGameMask(GamePermission::ManageDiscipline);

constexpr std::uint64_t kGameAdministratorPermissions =
    kGameModeratorPermissions |
    ToGameMask(GamePermission::ControlBots) |
    ToGameMask(GamePermission::ControlMatch) |
    ToGameMask(GamePermission::ChangeMap) |
    ToGameMask(GamePermission::ManageMapRotation) |
    ToGameMask(GamePermission::ManageAnnouncements);

constexpr std::uint64_t kAllGamePermissions =
    kGameAdministratorPermissions;

constexpr bool HasGamePermission(
    std::uint64_t permissions,
    GamePermission required)
{
    const std::uint64_t mask = ToGameMask(required);
    return mask != 0 && (permissions & mask) == mask;
}

inline std::uint64_t GamePermissionsForRole(std::string_view role)
{
    if (role == "Moderator")
        return kGameModeratorPermissions;
    if (role == "Administrator" || role == "Owner")
        return kGameAdministratorPermissions;
    return 0;
}
} // namespace neo_admin
