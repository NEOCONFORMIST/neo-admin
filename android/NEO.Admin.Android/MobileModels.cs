using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using NeoAdmin;
using System.Text;

namespace NeoAdmin.AndroidApp;

internal sealed class MobileAdminProfile
{
    public string ServerName { get; set; } = "CS2 Server";
    public string ServerAddress { get; set; } = string.Empty;
    public int ServerPort { get; set; } = 27122;
    public string OperatorName { get; set; } = string.Empty;
    public string AdminId { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public bool AutoConnect { get; set; } = true;
    public bool MutePlayerAudio { get; set; }
    public int MicrophoneGainPercent { get; set; } = 100;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ServerAddress) &&
        ServerPort is >= 1 and <= 65535 &&
        OperatorNameIsValid(OperatorName) &&
        AccessKey.Trim().Length >= 16;

    private static bool OperatorNameIsValid(string value)
    {
        string normalized = value.Trim();
        int byteCount = Encoding.UTF8.GetByteCount(normalized);
        return byteCount is >= 1 and <= 32 &&
            !normalized.Any(char.IsControl);
    }
}

internal static class MobileProfileStore
{
    private const string PreferencesName = "neo_admin_mobile_profile";

    public static MobileAdminProfile Load(Context context)
    {
        var preferences = context.GetSharedPreferences(
            PreferencesName,
            FileCreationMode.Private);

        string adminId =
            preferences?.GetString("admin_id", string.Empty) ?? string.Empty;
        string operatorName =
            preferences?.GetString("operator_name", string.Empty) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(operatorName) &&
            !adminId.StartsWith("key_", StringComparison.Ordinal))
        {
            operatorName = adminId;
        }

        return new MobileAdminProfile
        {
            ServerName = preferences?.GetString("server_name", "CS2 Server") ?? "CS2 Server",
            ServerAddress = preferences?.GetString("server_address", string.Empty) ?? string.Empty,
            ServerPort = preferences?.GetInt("server_port", 27122) ?? 27122,
            OperatorName = operatorName,
            AdminId = adminId,
            AccessKey = preferences?.GetString("access_key", string.Empty) ?? string.Empty,
            AutoConnect = preferences?.GetBoolean("auto_connect", true) ?? true,
            MutePlayerAudio = preferences?.GetBoolean("mute_player_audio", false) ?? false,
            MicrophoneGainPercent = Math.Clamp(
                preferences?.GetInt("microphone_gain_percent", 100) ?? 100,
                50,
                300),
        };
    }

    public static void Save(Context context, MobileAdminProfile profile)
    {
        var preferences = context.GetSharedPreferences(
            PreferencesName,
            FileCreationMode.Private);
        using var editor = preferences?.Edit();
        editor?.PutString("server_name", profile.ServerName.Trim());
        editor?.PutString("server_address", profile.ServerAddress.Trim());
        editor?.PutInt("server_port", profile.ServerPort);
        editor?.PutString("operator_name", profile.OperatorName.Trim());
        editor?.PutString("admin_id", profile.AdminId.Trim());
        editor?.PutString("access_key", profile.AccessKey.Trim());
        editor?.PutBoolean("auto_connect", profile.AutoConnect);
        editor?.PutBoolean("mute_player_audio", profile.MutePlayerAudio);
        editor?.PutInt("microphone_gain_percent", profile.MicrophoneGainPercent);
        editor?.Apply();
    }
}

internal sealed class MobilePlayer
{
    public string Key { get; init; } = string.Empty;
    public ulong SteamId { get; set; }
    public int Slot { get; set; }
    public string Name { get; set; } = "Unknown";
    public int Team { get; set; }
    public int Health { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public bool Alive { get; set; }
    public bool Bot { get; set; }
    public bool SourceTv { get; set; }
    public bool Speaking { get; set; }
    public DateTime LastVoiceUtc { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public string TeamName => Team switch
    {
        2 => "T",
        3 => "CT",
        1 => "SPEC",
        _ => "--",
    };

    public string Identity => Bot
        ? "BOT"
        : SteamId == 0
            ? "SteamID unavailable"
            : SteamId.ToString();
}

internal sealed class PlayerListAdapter : BaseAdapter<MobilePlayer>
{
    private readonly MainActivity _activity;
    private readonly List<MobilePlayer> _players = new();

    public PlayerListAdapter(MainActivity activity) => _activity = activity;

    public override int Count => _players.Count;
    public override MobilePlayer this[int position] => _players[position];
    public override long GetItemId(int position) => _players[position].Slot;

    public void Replace(IEnumerable<MobilePlayer> players)
    {
        _players.Clear();
        _players.AddRange(players);
        NotifyDataSetChanged();
    }

    public override View GetView(int position, View? convertView, ViewGroup? parent)
    {
        MobilePlayer player = _players[position];
        var row = convertView as LinearLayout ?? BuildRow();
        var name = (TextView)row.GetChildAt(0)!;
        var details = (TextView)row.GetChildAt(1)!;

        string state = player.SourceTv
            ? "SourceTV"
            : player.Alive
                ? $"HP {player.Health}"
                : "DEAD";
        string speaking = player.Speaking ? " | VOICE" : string.Empty;

        name.Text = player.Name;
        name.SetTextColor(player.Team switch
        {
            2 => Color.Rgb(239, 184, 75),
            3 => Color.Rgb(93, 174, 226),
            _ => Color.Rgb(230, 234, 237),
        });
        details.Text =
            $"{player.TeamName} | {state} | Slot {player.Slot} | {player.Identity}{speaking}";
        return row;
    }

    private LinearLayout BuildRow()
    {
        int pad = _activity.Dp(12);
        var row = new LinearLayout(_activity)
        {
            Orientation = Orientation.Vertical,
        };
        row.SetMinimumHeight(_activity.Dp(72));
        row.SetPadding(pad, _activity.Dp(10), pad, _activity.Dp(10));
        row.SetBackgroundColor(Color.Rgb(31, 36, 41));

        var name = new TextView(_activity)
        {
            TextSize = 16,
            Typeface = Typeface.DefaultBold,
        };
        name.SetMaxLines(1);
        var details = new TextView(_activity)
        {
            TextSize = 12,
        };
        details.SetMaxLines(2);
        details.SetTextColor(Color.Rgb(164, 174, 184));
        row.AddView(name);
        row.AddView(details);
        return row;
    }
}
