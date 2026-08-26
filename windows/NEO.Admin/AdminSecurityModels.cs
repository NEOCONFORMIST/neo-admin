using System.Text.Json;

namespace NeoAdmin;

internal sealed class AdminAuditCatalog
{
    public int Version { get; init; }
    public List<AdminAuditRecord> Events { get; init; } = new();

    public static AdminAuditCatalog Parse(string json) =>
        JsonSerializer.Deserialize<AdminAuditCatalog>(json, JsonOptions.Value)
        ?? throw new InvalidDataException("The server returned an empty audit log.");
}

internal sealed class AdminAuditRecord
{
    public ulong Id { get; init; }
    public string CreatedUtc { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Details { get; init; } = string.Empty;
}

internal sealed class AdminBanCatalog
{
    public int Version { get; init; }
    public List<AdminBanRecord> Bans { get; init; } = new();

    public static AdminBanCatalog Parse(string json) =>
        JsonSerializer.Deserialize<AdminBanCatalog>(json, JsonOptions.Value)
        ?? throw new InvalidDataException("The server returned an empty ban list.");
}

internal sealed class AdminBanRecord
{
    public string SteamId { get; init; } = string.Empty;
    public string PlayerName { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string CreatedBy { get; init; } = string.Empty;
    public string CreatedUtc { get; init; } = string.Empty;
    public ulong ExpiresUnix { get; init; }
}

internal sealed record AdminBanTarget(
    string SteamId,
    string PlayerName,
    int PlayerSlot);

internal sealed class DisciplineCatalog
{
    public int Version { get; init; }
    public List<RestrictionRecord> Restrictions { get; init; } = new();
    public static DisciplineCatalog Parse(string json) =>
        JsonSerializer.Deserialize<DisciplineCatalog>(json, JsonOptions.Value)
        ?? throw new InvalidDataException("The server returned an empty restriction list.");
}

internal sealed class RestrictionRecord
{
    public string SteamId { get; init; } = string.Empty;
    public string PlayerName { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string CreatedBy { get; init; } = string.Empty;
    public string CreatedUtc { get; init; } = string.Empty;
    public ulong ExpiresUnix { get; init; }
}

internal sealed class DisciplineHistoryCatalog
{
    public int Version { get; init; }
    public string SteamId { get; init; } = string.Empty;
    public List<DisciplineHistoryRecord> History { get; init; } = new();
    public static DisciplineHistoryCatalog Parse(string json) =>
        JsonSerializer.Deserialize<DisciplineHistoryCatalog>(json, JsonOptions.Value)
        ?? throw new InvalidDataException("The server returned an empty discipline history.");
}

internal sealed class DisciplineHistoryRecord
{
    public string SteamId { get; init; } = string.Empty;
    public string PlayerName { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string CreatedBy { get; init; } = string.Empty;
    public string CreatedUtc { get; init; } = string.Empty;
    public ulong ExpiresUnix { get; init; }
}

internal sealed class MapRotationCatalog
{
    public int Version { get; init; }
    public bool Enabled { get; init; }
    public int CurrentIndex { get; init; }
    public List<string> Maps { get; init; } = new();
    public List<ScheduledMapRecord> Schedules { get; init; } = new();
    public static MapRotationCatalog Parse(string json) =>
        JsonSerializer.Deserialize<MapRotationCatalog>(json, JsonOptions.Value)
        ?? throw new InvalidDataException("The server returned an empty map rotation.");
}

internal sealed class ScheduledMapRecord
{
    public ulong Id { get; init; }
    public string Map { get; init; } = string.Empty;
    public ulong ScheduledUnix { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
}

internal sealed class AnnouncementCatalog
{
    public int Version { get; init; }
    public List<ScheduledAnnouncementRecord> Announcements { get; init; } = new();
    public static AnnouncementCatalog Parse(string json) =>
        JsonSerializer.Deserialize<AnnouncementCatalog>(json, JsonOptions.Value)
        ?? throw new InvalidDataException("The server returned an empty announcement list.");
}

internal sealed class ScheduledAnnouncementRecord
{
    public ulong Id { get; init; }
    public string Message { get; init; } = string.Empty;
    public ulong ScheduledUnix { get; init; }
    public ulong RepeatMinutes { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
}

internal static class JsonOptions
{
    public static JsonSerializerOptions Value { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
