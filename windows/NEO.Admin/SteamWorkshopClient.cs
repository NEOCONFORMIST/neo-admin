using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NeoAdmin;

internal sealed record SteamWorkshopMapInfo(
    ulong PublishedFileId,
    string Title,
    string Description,
    string FileName,
    string PreviewUrl,
    byte[] PreviewBytes);

internal static class SteamWorkshopClient
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    public static bool TryParseId(string? value, out ulong publishedFileId)
    {
        publishedFileId = 0;
        string text = value?.Trim() ?? string.Empty;
        if (ulong.TryParse(text, out publishedFileId) && publishedFileId > 0)
            return true;

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
            return false;
        string query = uri.Query.TrimStart('?');
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals("id", StringComparison.OrdinalIgnoreCase) &&
                ulong.TryParse(Uri.UnescapeDataString(pair[1]), out publishedFileId) &&
                publishedFileId > 0)
            {
                return true;
            }
        }
        return false;
    }

    public static async Task<SteamWorkshopMapInfo> GetMapAsync(
        ulong publishedFileId,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["itemcount"] = "1",
            ["publishedfileids[0]"] = publishedFileId.ToString(),
        });
        using HttpResponseMessage response = await Client.PostAsync(
            "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        WorkshopEnvelope? envelope = await response.Content.ReadFromJsonAsync<WorkshopEnvelope>(
            cancellationToken: cancellationToken);
        WorkshopDetail? detail = envelope?.Response.Details.FirstOrDefault();
        if (detail is null || detail.Result != 1)
            throw new InvalidDataException("Steam did not return a public Workshop item.");
        if (detail.ConsumerAppId != 730)
            throw new InvalidDataException("This Workshop item is not for Counter-Strike 2.");
        if (detail.FileType != 0)
            throw new InvalidDataException("This ID is a collection or unsupported item, not a CS2 map.");

        byte[] preview = Array.Empty<byte>();
        if (Uri.TryCreate(detail.PreviewUrl, UriKind.Absolute, out Uri? previewUri))
            preview = await Client.GetByteArrayAsync(previewUri, cancellationToken);

        return new SteamWorkshopMapInfo(
            publishedFileId,
            detail.Title,
            detail.Description,
            detail.FileName,
            detail.PreviewUrl,
            preview);
    }

    private sealed class WorkshopEnvelope
    {
        [JsonPropertyName("response")]
        public WorkshopResponse Response { get; init; } = new();
    }

    private sealed class WorkshopResponse
    {
        [JsonPropertyName("publishedfiledetails")]
        public List<WorkshopDetail> Details { get; init; } = new();
    }

    private sealed class WorkshopDetail
    {
        [JsonPropertyName("result")]
        public int Result { get; init; }

        [JsonPropertyName("consumer_app_id")]
        public int ConsumerAppId { get; init; }

        [JsonPropertyName("file_type")]
        public int FileType { get; init; }

        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        [JsonPropertyName("filename")]
        public string FileName { get; init; } = string.Empty;

        [JsonPropertyName("preview_url")]
        public string PreviewUrl { get; init; } = string.Empty;
    }
}
