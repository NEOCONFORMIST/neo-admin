using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NeoAdmin;

internal sealed record SteamProfileInfo(
    string PersonaName,
    string ProfileUrl,
    byte[] AvatarBytes,
    bool CommunityBanned,
    bool VacBanned,
    int VacBanCount,
    int GameBanCount,
    int DaysSinceLastBan,
    string EconomyBan,
    string DataSource);

internal static class SteamProfileClient
{
    private static readonly HttpClient Client = CreateClient();

    public static async Task<SteamProfileInfo> GetAsync(
        string apiKey,
        ulong steamId64,
        CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(apiKey)
            ? await GetFromSteamGptAsync(steamId64, cancellationToken)
            : await GetFromValveAsync(apiKey, steamId64, cancellationToken);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "NEO-ADMIN/1.0 (CS2 server administration client)");
        return client;
    }

    private static async Task<SteamProfileInfo> GetFromValveAsync(
        string apiKey,
        ulong steamId64,
        CancellationToken cancellationToken)
    {
        string key = Uri.EscapeDataString(apiKey.Trim());
        string steamId = steamId64.ToString();
        string summariesUrl =
            $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={key}&steamids={steamId}";
        string bansUrl =
            $"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={key}&steamids={steamId}";

        Task<SteamSummaryEnvelope?> summaryTask =
            Client.GetFromJsonAsync<SteamSummaryEnvelope>(
                summariesUrl,
                cancellationToken);
        Task<SteamBanEnvelope?> bansTask =
            Client.GetFromJsonAsync<SteamBanEnvelope>(
                bansUrl,
                cancellationToken);

        await Task.WhenAll(summaryTask, bansTask);
        SteamPlayerSummary? summary =
            (await summaryTask)?.Response.Players.FirstOrDefault();
        SteamPlayerBan? bans =
            (await bansTask)?.Players.FirstOrDefault();
        if (summary is null || bans is null)
            throw new InvalidDataException("Steam returned no profile for this player.");

        byte[] avatar = Array.Empty<byte>();
        if (Uri.TryCreate(summary.AvatarFull, UriKind.Absolute, out Uri? avatarUri))
            avatar = await Client.GetByteArrayAsync(avatarUri, cancellationToken);

        return new SteamProfileInfo(
            summary.PersonaName,
            summary.ProfileUrl,
            avatar,
            bans.CommunityBanned,
            bans.VacBanned,
            bans.NumberOfVacBans,
            bans.NumberOfGameBans,
            bans.DaysSinceLastBan,
            bans.EconomyBan,
            "Valve Steam Web API");
    }

    private static async Task<SteamProfileInfo> GetFromSteamGptAsync(
        ulong steamId64,
        CancellationToken cancellationToken)
    {
        string steamId = steamId64.ToString();
        Task<SteamGptProfileEnvelope?> profileTask =
            Client.GetFromJsonAsync<SteamGptProfileEnvelope>(
                $"https://steamgpt.net/profile/{steamId}.json",
                cancellationToken);
        Task<SteamGptBanEnvelope?> bansTask =
            Client.GetFromJsonAsync<SteamGptBanEnvelope>(
                $"https://steamgpt.net/bans/{steamId}.json",
                cancellationToken);

        await Task.WhenAll(profileTask, bansTask);
        SteamGptProfileEnvelope? profileEnvelope = await profileTask;
        SteamGptBanEnvelope? banEnvelope = await bansTask;
        SteamGptProfile? profile = profileEnvelope?.Data.Steam;
        SteamPlayerBan? bans = banEnvelope?.Data.Bans;
        if (!string.Equals(profileEnvelope?.Result, "success", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(banEnvelope?.Result, "success", StringComparison.OrdinalIgnoreCase) ||
            profile is null || bans is null ||
            profile.SteamId != steamId || bans.SteamId != steamId)
        {
            throw new InvalidDataException(
                "The keyless Steam profile service returned no matching player.");
        }

        byte[] avatar = Array.Empty<byte>();
        if (Uri.TryCreate(profile.AvatarFull, UriKind.Absolute, out Uri? avatarUri))
            avatar = await Client.GetByteArrayAsync(avatarUri, cancellationToken);

        return new SteamProfileInfo(
            profile.PersonaName,
            profile.ProfileUrl,
            avatar,
            bans.CommunityBanned,
            bans.VacBanned,
            bans.NumberOfVacBans,
            bans.NumberOfGameBans,
            bans.DaysSinceLastBan,
            bans.EconomyBan,
            "SteamGPT keyless API");
    }

    private sealed class SteamSummaryEnvelope
    {
        [JsonPropertyName("response")]
        public SteamSummaryResponse Response { get; init; } = new();
    }

    private sealed class SteamSummaryResponse
    {
        [JsonPropertyName("players")]
        public List<SteamPlayerSummary> Players { get; init; } = new();
    }

    private sealed class SteamPlayerSummary
    {
        [JsonPropertyName("personaname")]
        public string PersonaName { get; init; } = string.Empty;

        [JsonPropertyName("profileurl")]
        public string ProfileUrl { get; init; } = string.Empty;

        [JsonPropertyName("avatarfull")]
        public string AvatarFull { get; init; } = string.Empty;
    }

    private sealed class SteamBanEnvelope
    {
        [JsonPropertyName("players")]
        public List<SteamPlayerBan> Players { get; init; } = new();
    }

    private sealed class SteamPlayerBan
    {
        [JsonPropertyName("SteamId")]
        public string SteamId { get; init; } = string.Empty;

        [JsonPropertyName("CommunityBanned")]
        public bool CommunityBanned { get; init; }

        [JsonPropertyName("VACBanned")]
        public bool VacBanned { get; init; }

        [JsonPropertyName("NumberOfVACBans")]
        public int NumberOfVacBans { get; init; }

        [JsonPropertyName("NumberOfGameBans")]
        public int NumberOfGameBans { get; init; }

        [JsonPropertyName("DaysSinceLastBan")]
        public int DaysSinceLastBan { get; init; }

        [JsonPropertyName("EconomyBan")]
        public string EconomyBan { get; init; } = "none";
    }

    private sealed class SteamGptProfileEnvelope
    {
        [JsonPropertyName("result")]
        public string Result { get; init; } = string.Empty;

        [JsonPropertyName("data")]
        public SteamGptProfileData Data { get; init; } = new();
    }

    private sealed class SteamGptProfileData
    {
        [JsonPropertyName("steam")]
        public SteamGptProfile Steam { get; init; } = new();
    }

    private sealed class SteamGptProfile
    {
        [JsonPropertyName("steamid")]
        public string SteamId { get; init; } = string.Empty;

        [JsonPropertyName("personaname")]
        public string PersonaName { get; init; } = string.Empty;

        [JsonPropertyName("profileurl")]
        public string ProfileUrl { get; init; } = string.Empty;

        [JsonPropertyName("avatarfull")]
        public string AvatarFull { get; init; } = string.Empty;
    }

    private sealed class SteamGptBanEnvelope
    {
        [JsonPropertyName("result")]
        public string Result { get; init; } = string.Empty;

        [JsonPropertyName("data")]
        public SteamGptBanData Data { get; init; } = new();
    }

    private sealed class SteamGptBanData
    {
        [JsonPropertyName("bans")]
        public SteamPlayerBan Bans { get; init; } = new();
    }
}
