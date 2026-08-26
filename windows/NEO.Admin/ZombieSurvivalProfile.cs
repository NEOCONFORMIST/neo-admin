namespace NeoAdmin;

internal static class ZombieSurvivalProfile
{
    public static readonly bool Implemented = false;
    public const string NotImplementedText = "NOT IMPLEMENTED YET";
    public const ulong WorkshopId = 3484400725UL;
    public const string MapName = "zm_lila_panic_371";
    public const string MapToken = "workshop/3484400725/zm_lila_panic_371";
    public const string DisplayName = "Zombie Survival - Lila Panic";

    public static bool IsMapToken(string? value) =>
        string.Equals(
            value?.Trim().Replace('\\', '/'),
            MapToken,
            StringComparison.OrdinalIgnoreCase);
}
