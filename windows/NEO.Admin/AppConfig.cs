using System.Net;
using System.Text.Json;

namespace NeoAdmin;

internal sealed class AppConfig
{
    public string BindAddress { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 27120;
    public string SharedSecret { get; init; } = string.Empty;
    public string AdminId { get; init; } = "owner";
    public string AllowedServerIp { get; init; } = string.Empty;
    public float MasterVolume { get; init; } = 0.8f;
    public bool EnableServerHealthPanel { get; init; }

    public IPAddress GetBindAddress()
    {
        if (!IPAddress.TryParse(BindAddress, out IPAddress? address))
            throw new InvalidDataException($"Invalid BindAddress: {BindAddress}");
        return address;
    }

    public IPAddress? GetAllowedServerAddress()
    {
        if (string.IsNullOrWhiteSpace(AllowedServerIp))
            return null;
        if (!IPAddress.TryParse(AllowedServerIp, out IPAddress? address))
            throw new InvalidDataException($"Invalid AllowedServerIp: {AllowedServerIp}");
        return address;
    }

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Create appsettings.json from appsettings.example.json first.", path);

        AppConfig? config = JsonSerializer.Deserialize<AppConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (config is null)
            throw new InvalidDataException("appsettings.json is empty or invalid.");
        if (config.Port is < 1 or > 65535)
            throw new InvalidDataException("Port must be between 1 and 65535.");
        if (config.SharedSecret.Length is > 0 and < 16)
            throw new InvalidDataException(
                "SharedSecret must be empty or at least 16 characters.");
        if (config.AdminId.Length is < 3 or > 32 ||
            config.AdminId.Any(ch =>
                !char.IsLetterOrDigit(ch) && ch is not '.' and not '_' and not '-'))
        {
            throw new InvalidDataException(
                "AdminId must be 3-32 letters, numbers, dots, dashes, or underscores.");
        }
        if (config.MasterVolume is < 0 or > 1)
            throw new InvalidDataException("MasterVolume must be between 0 and 1.");

        _ = config.GetBindAddress();
        _ = config.GetAllowedServerAddress();
        return config;
    }
}

