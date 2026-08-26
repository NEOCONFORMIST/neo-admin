using System.Text.Json;

namespace NeoAdmin;

[Flags]
internal enum AdminPermission : ulong
{
    None = 0,
    ViewDashboard = 1UL << 0,
    ViewSteamIds = 1UL << 1,
    SendChat = 1UL << 2,
    BroadcastVoice = 1UL << 3,
    ModeratePlayers = 1UL << 4,
    ControlBots = 1UL << 5,
    ControlMatch = 1UL << 6,
    ChangeMap = 1UL << 7,
    TeleportPlayers = 1UL << 8,
    ManageAccounts = 1UL << 9,
    RestartServer = 1UL << 10,
    DeployPlugin = 1UL << 11,
    ViewAuditLog = 1UL << 12,
    ManageBans = 1UL << 13,
    ManageDiscipline = 1UL << 14,
    ManageMapRotation = 1UL << 15,
    ManageAnnouncements = 1UL << 16,
    ManageGameAdmins = 1UL << 17,
    RunServerConsole = 1UL << 18,
    ManageZombieMode = 1UL << 19,
    ManageWorkshopMaps = 1UL << 20,
}

internal static class AdminRoles
{
    public const AdminPermission Viewer =
        AdminPermission.ViewDashboard |
        AdminPermission.ViewSteamIds;

    public const AdminPermission Moderator =
        Viewer |
        AdminPermission.SendChat |
        AdminPermission.BroadcastVoice |
        AdminPermission.ModeratePlayers |
        AdminPermission.ManageBans |
        AdminPermission.ManageDiscipline;

    public const AdminPermission Administrator =
        Moderator |
        AdminPermission.ControlBots |
        AdminPermission.ControlMatch |
        AdminPermission.ChangeMap |
        AdminPermission.TeleportPlayers |
        AdminPermission.ViewAuditLog |
        AdminPermission.ManageMapRotation |
        AdminPermission.ManageAnnouncements |
        AdminPermission.ManageWorkshopMaps |
        AdminPermission.ManageZombieMode;

    public const AdminPermission EventAdmin =
        Viewer |
        AdminPermission.SendChat |
        AdminPermission.BroadcastVoice |
        AdminPermission.ModeratePlayers |
        AdminPermission.ControlBots |
        AdminPermission.ControlMatch |
        AdminPermission.ChangeMap |
        AdminPermission.ManageAnnouncements |
        AdminPermission.ManageWorkshopMaps;

    public const AdminPermission SeniorAdmin =
        Administrator |
        AdminPermission.ManageAccounts |
        AdminPermission.ManageGameAdmins;

    public const AdminPermission Owner =
        SeniorAdmin |
        AdminPermission.RunServerConsole |
        AdminPermission.RestartServer |
        AdminPermission.DeployPlugin;

    public static AdminPermission ForName(string role) => role switch
    {
        "Viewer" => Viewer,
        "Moderator" => Moderator,
        "Event Admin" => EventAdmin,
        "Administrator" => Administrator,
        "Senior Admin" => SeniorAdmin,
        "Owner" => Owner,
        _ => AdminPermission.None,
    };
}

internal sealed record AdminSession(
    string AccountId,
    string DisplayName,
    string Role,
    AdminPermission Permissions,
    bool Authenticated,
    string Message)
{
    public bool Can(AdminPermission permission) =>
        Authenticated && permission != AdminPermission.None &&
        (Permissions & permission) == permission;
}

internal sealed class AdminAccountCatalog
{
    public int Version { get; init; }
    public List<AdminAccountRecord> Accounts { get; init; } = new();

    public static AdminAccountCatalog Parse(string json) =>
        JsonSerializer.Deserialize<AdminAccountCatalog>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("The server returned an empty account list.");
}

internal sealed class AdminAccountRecord
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public AdminPermission Permissions { get; init; }
    public bool Enabled { get; init; }
    public string CreatedUtc { get; init; } = string.Empty;
    public ulong ExpiresUnix { get; init; }
    public string Credential { get; init; } = string.Empty;

    public bool IsExpired =>
        ExpiresUnix != 0 &&
        ExpiresUnix <= (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
