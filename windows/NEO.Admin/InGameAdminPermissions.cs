using System.Text.Json;

namespace NeoAdmin;

[Flags]
internal enum InGamePermission : ulong
{
    None = 0,
    ModeratePlayers = 1UL << 0,
    ManageBans = 1UL << 1,
    ManageDiscipline = 1UL << 2,
    ControlBots = 1UL << 3,
    ControlMatch = 1UL << 4,
    ChangeMap = 1UL << 5,
    ManageMapRotation = 1UL << 6,
    ManageAnnouncements = 1UL << 7,
}

internal static class InGameRoles
{
    public const InGamePermission Moderator =
        InGamePermission.ModeratePlayers |
        InGamePermission.ManageBans |
        InGamePermission.ManageDiscipline;

    public const InGamePermission Administrator =
        Moderator |
        InGamePermission.ControlBots |
        InGamePermission.ControlMatch |
        InGamePermission.ChangeMap |
        InGamePermission.ManageMapRotation |
        InGamePermission.ManageAnnouncements;

    public static InGamePermission ForName(string role) => role switch
    {
        "Moderator" => Moderator,
        "Administrator" or "Owner" => Administrator,
        _ => InGamePermission.None,
    };
}

internal sealed class InGameAdminCatalog
{
    public int Version { get; init; }
    public List<InGameAdminRecord> Admins { get; init; } = new();

    public static InGameAdminCatalog Parse(string json) =>
        JsonSerializer.Deserialize<InGameAdminCatalog>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException(
            "The server returned an empty in-game administrator list.");
}

internal sealed class InGameAdminRecord
{
    public string SteamId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public InGamePermission Permissions { get; init; }
    public bool Enabled { get; init; }
    public string CreatedUtc { get; init; } = string.Empty;
}
