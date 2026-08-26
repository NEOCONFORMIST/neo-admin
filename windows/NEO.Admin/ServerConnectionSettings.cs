using System.Text.Json;

namespace NeoAdmin;

internal sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "CS2 Server";
    public string ServerAddress { get; set; } = string.Empty;
    public int ServerPttPort { get; set; } = 27122;
    public string AdminId { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;

    public ServerProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        ServerAddress = ServerAddress,
        ServerPttPort = ServerPttPort,
        AdminId = AdminId,
        AccessKey = AccessKey,
    };

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? "CS2 Server" : Name.Trim();
        ServerAddress = ServerAddress?.Trim() ?? string.Empty;
        ServerPttPort = ServerPttPort is >= 1 and <= 65535 ? ServerPttPort : 27122;
        AdminId = AdminId?.Trim() ?? string.Empty;
        AccessKey = AccessKey?.Trim() ?? string.Empty;
    }

    public override string ToString() => Name;
}

internal sealed class ServerConnectionSettings
{
    // These fields remain serialized for compatibility with previous versions.
    public string ServerAddress { get; set; } = string.Empty;
    public int ServerPttPort { get; set; } = 27122;
    public string MicrophoneDeviceName { get; set; } = string.Empty;
    public string SteamWebApiKey { get; set; } = string.Empty;
    public string AdminId { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string ActiveServerId { get; set; } = string.Empty;
    public List<ServerProfile> Servers { get; set; } = new();

    public ServerProfile ActiveServer
    {
        get
        {
            EnsureProfiles();
            return Servers.First(profile => profile.Id == ActiveServerId);
        }
    }

    private static string GetSettingsPath(string productName) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            productName,
            "server-connection.json");

    private static string SettingsPath => GetSettingsPath("NEO ADMIN");
    private static string LegacySettingsPath => GetSettingsPath("NEO ADMINISTRATION");

    public static ServerConnectionSettings Load()
    {
        try
        {
            string sourcePath = File.Exists(SettingsPath) ? SettingsPath : LegacySettingsPath;
            ServerConnectionSettings settings = File.Exists(sourcePath)
                ? JsonSerializer.Deserialize<ServerConnectionSettings>(
                    File.ReadAllText(sourcePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
                : new();
            settings.Normalize();
            return settings;
        }
        catch
        {
            var settings = new ServerConnectionSettings();
            settings.Normalize();
            return settings;
        }
    }

    public void SetActive(string profileId)
    {
        ServerProfile? profile = Servers.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
            throw new InvalidDataException("The selected server profile no longer exists.");

        ActiveServerId = profile.Id;
        SyncLegacyFields(profile);
    }

    public void UpdateActiveConnection(string address, int port)
    {
        ServerProfile profile = ActiveServer;
        profile.ServerAddress = address;
        profile.ServerPttPort = port;
        profile.Normalize();
        SyncLegacyFields(profile);
    }

    public ServerProfile AddOrSelect(
        string name,
        string address,
        int port,
        string adminId,
        string accessKey)
    {
        ServerProfile? profile = Servers.FirstOrDefault(item =>
            item.ServerAddress.Equals(address, StringComparison.OrdinalIgnoreCase) &&
            item.ServerPttPort == port &&
            item.AdminId.Equals(adminId, StringComparison.OrdinalIgnoreCase));

        profile ??= new ServerProfile();
        if (!Servers.Contains(profile))
            Servers.Add(profile);

        profile.Name = string.IsNullOrWhiteSpace(name) ? address : name;
        profile.ServerAddress = address;
        profile.ServerPttPort = port;
        profile.AdminId = adminId;
        profile.AccessKey = accessKey;
        profile.Normalize();
        SetActive(profile.Id);
        return profile;
    }

    public void ReplaceServers(IEnumerable<ServerProfile> profiles, string activeServerId)
    {
        Servers = profiles.Select(profile => profile.Clone()).ToList();
        ActiveServerId = activeServerId;
        Normalize();
    }

    public void Save()
    {
        Normalize();
        string? directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = SettingsPath + ".new";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, SettingsPath, true);
    }

    private void Normalize()
    {
        MicrophoneDeviceName = MicrophoneDeviceName?.Trim() ?? string.Empty;
        SteamWebApiKey = SteamWebApiKey?.Trim() ?? string.Empty;
        Servers ??= new();
        foreach (ServerProfile profile in Servers)
            profile.Normalize();

        Servers = Servers
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        EnsureProfiles();
        SyncLegacyFields(ActiveServer);
    }

    private void EnsureProfiles()
    {
        if (Servers.Count == 0)
        {
            var migrated = new ServerProfile
            {
                Name = string.IsNullOrWhiteSpace(ServerAddress) ? "CS2 Server" : ServerAddress.Trim(),
                ServerAddress = ServerAddress,
                ServerPttPort = ServerPttPort,
                AdminId = AdminId,
                AccessKey = AccessKey,
            };
            migrated.Normalize();
            Servers.Add(migrated);
        }

        if (!Servers.Any(profile => profile.Id == ActiveServerId))
            ActiveServerId = Servers[0].Id;
    }

    private void SyncLegacyFields(ServerProfile profile)
    {
        ServerAddress = profile.ServerAddress;
        ServerPttPort = profile.ServerPttPort;
        AdminId = profile.AdminId;
        AccessKey = profile.AccessKey;
    }
}
