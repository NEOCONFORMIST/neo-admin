using System.Text.Json;

namespace NeoAdmin;

internal sealed class AdminAccessProfile
{
    public int Version { get; init; } = 1;
    public string ServerAddress { get; init; } = string.Empty;
    public int ServerPttPort { get; init; } = 27122;
    public string AdminId { get; init; } = string.Empty;
    public string AccessKey { get; init; } = string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerAddress))
            throw new InvalidDataException("The access profile has no server address.");
        if (ServerPttPort is < 1 or > 65535)
            throw new InvalidDataException("The access profile has an invalid server port.");
        if (AdminId.Length is < 3 or > 32 ||
            AdminId.Any(ch =>
                !char.IsLetterOrDigit(ch) && ch is not '.' and not '_' and not '-'))
        {
            throw new InvalidDataException("The access profile has an invalid account ID.");
        }
        if (AccessKey.Length < 16)
            throw new InvalidDataException("The access profile has an invalid access key.");
    }

    public static AdminAccessProfile Load(string path)
    {
        AdminAccessProfile profile = JsonSerializer.Deserialize<AdminAccessProfile>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The access profile is empty.");
        profile.Validate();
        return profile;
    }

    public void Save(string path)
    {
        Validate();
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                this,
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
