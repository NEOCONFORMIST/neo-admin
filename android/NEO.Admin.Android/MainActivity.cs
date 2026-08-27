using System.Net;
using System.Buffers.Binary;
using System.Text;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Net;
using Android.OS;
using Android.Text;
using Android.Text.Method;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using NeoAdmin;
using Environment = System.Environment;
using OperationCanceledException = System.OperationCanceledException;
using Orientation = Android.Widget.Orientation;

namespace NeoAdmin.AndroidApp;

[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    ScreenOrientation = global::Android.Content.PM.ScreenOrientation.Unspecified,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation |
                           global::Android.Content.PM.ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity
{
    private const int RecordAudioPermissionRequest = 4201;

    private static readonly Color AppBackground = Color.Rgb(17, 20, 23);
    private static readonly Color Surface = Color.Rgb(27, 32, 37);
    private static readonly Color SurfaceRaised = Color.Rgb(35, 41, 47);
    private static readonly Color TextPrimary = Color.Rgb(238, 241, 243);
    private static readonly Color TextSecondary = Color.Rgb(166, 177, 187);
    private static readonly Color Teal = Color.Rgb(40, 185, 168);
    private static readonly Color Amber = Color.Rgb(239, 184, 75);
    private static readonly Color Red = Color.Rgb(219, 86, 80);

    private readonly Dictionary<string, MobilePlayer> _players = new();
    private readonly List<string> _maps = new();
    private readonly List<View> _pages = new();
    private readonly List<Button> _tabButtons = new();
    private readonly List<Button> _matchButtons = new();
    private readonly List<Button> _botButtons = new();
    private readonly CancellationTokenSource _lifetime = new();

    private MobileAdminProfile _profile = new();
    private UdpVoiceReceiver? _receiver;
    private Task? _healthTask;
    private CancellationTokenSource? _automaticReconnect;
    private MobileVoicePlayback _voicePlayback = null!;
    private MobilePttCapture _pttCapture = null!;
    private bool _connecting;
    private bool _isForeground;
    private bool _resumeConnectionRequested;
    private bool _mapsRequested;
    private bool _connectionHelpVisible;
    private bool _connectionEditorVisible;
    private string _networkRoute = string.Empty;
    private ConnectivityManager? _connectivityManager;
    private SystemBarInsetsListener? _systemBarInsetsListener;
    private string _lastReportedMap = string.Empty;
    private int _reportedConnectedPlayers = -1;
    private uint _reportedMaxPlayers;
    private string _lastConsoleStatus = string.Empty;
    private DateTime _lastConsoleStatusUtc;
    private DateTime _healthProbesPausedUntilUtc;
    private int _consecutiveHealthSendFailures;
    private int _healthRecoveryPending;
    private int _pttSendFailureReported;
    private long _lastServerPacketUtcTicks;
    private bool _transportStale;
    private string _connectionPrimaryStatus = "Not configured";
    private readonly object _overviewReplySync = new();
    private CancellationTokenSource? _overviewDownload;
    private TaskCompletionSource<VoicePacket>? _overviewChunkReply;
    private string _overviewReplyMap = string.Empty;
    private int _overviewReplyIndex = -1;

    private TextView _connectionStatus = null!;
    private TextView _sessionStatus = null!;
    private TextView _playerCount = null!;
    private TextView _currentMap = null!;
    private TextView _healthStatus = null!;
    private TextView _liveMapTitle = null!;
    private TextView _liveMapStatus = null!;
    private MobileMapOverviewView _liveMapView = null!;
    private Button _connectButton = null!;
    private Button _editConnectionButton = null!;
    private Button _mapButton = null!;
    private Button _chatSendButton = null!;
    private Button _pushToTalkButton = null!;
    private Button _consoleSendButton = null!;
    private CheckBox _mutePlayersCheckBox = null!;
    private TextView _microphoneGainLabel = null!;
    private SeekBar _microphoneGainSlider = null!;
    private ListView _playerList = null!;
    private PlayerListAdapter _playerAdapter = null!;
    private EditText _chatInput = null!;
    private EditText _consoleInput = null!;
    private TextView _chatHistory = null!;
    private TextView _voiceStatus = null!;
    private TextView _consoleHistory = null!;
    private ScrollView _chatScroll = null!;
    private ScrollView _consoleScroll = null!;
    private View _authenticatedShell = null!;
    private View _disconnectedShell = null!;
    private TextView _disconnectedServerName = null!;
    private TextView _disconnectedEndpoint = null!;
    private TextView _disconnectedStatus = null!;
    private Button _disconnectedConnectButton = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        Window?.SetStatusBarColor(Color.Rgb(23, 27, 31));
        Window?.SetNavigationBarColor(AppBackground);
        SetContentView(Resource.Layout.activity_main);

        var host = FindViewById<FrameLayout>(Resource.Id.app_root)
            ?? throw new InvalidOperationException("The Android root view is missing.");
        _systemBarInsetsListener = new SystemBarInsetsListener();
        host.SetOnApplyWindowInsetsListener(_systemBarInsetsListener);
        host.AddView(BuildApplicationShell());
        host.RequestApplyInsets();

        _voicePlayback = new MobileVoicePlayback();
        _voicePlayback.PlaybackError += OnVoicePlaybackError;
        _voicePlayback.VoiceActivity += OnVoiceActivity;
        _pttCapture = new MobilePttCapture();
        _pttCapture.OpusFrameReady += OnPttOpusFrameReady;
        _pttCapture.CaptureError += OnPttCaptureError;

        _profile = MobileProfileStore.Load(this);
        _mutePlayersCheckBox.Checked = _profile.MutePlayerAudio;
        _voicePlayback.SetUserMuted(_profile.MutePlayerAudio);
        _microphoneGainSlider.Progress = _profile.MicrophoneGainPercent - 50;
        _pttCapture.MicrophoneGain = _profile.MicrophoneGainPercent / 100f;
        _resumeConnectionRequested = _profile.AutoConnect;
        UpdateConnectionUi();
        UpdatePermissionUi();
        ShowPage(0);

        _healthTask = Task.Run(() => HealthLoopAsync(_lifetime.Token));
        if (!_profile.IsComplete)
            ShowConnectionSettings();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _isForeground = true;
        if (_resumeConnectionRequested && _profile.IsComplete)
            ScheduleAutomaticReconnect("phone resumed");
    }

    protected override void OnStop()
    {
        StopPushToTalk();
        _isForeground = false;
        CancelAutomaticReconnect();
        base.OnStop();
    }

    protected override void OnDestroy()
    {
        StopPushToTalk();
        CancelAutomaticReconnect();
        CancelMapOverviewDownload();
        _lifetime.Cancel();
        ReleaseNetworkBinding();
        UdpVoiceReceiver? receiver = _receiver;
        _receiver = null;
        if (receiver is not null)
            _ = receiver.DisposeAsync().AsTask();
        _voicePlayback.PlaybackError -= OnVoicePlaybackError;
        _voicePlayback.VoiceActivity -= OnVoiceActivity;
        _voicePlayback.Dispose();
        _pttCapture.OpusFrameReady -= OnPttOpusFrameReady;
        _pttCapture.CaptureError -= OnPttCaptureError;
        _pttCapture.Dispose();
        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != RecordAudioPermissionRequest)
            return;

        bool granted = grantResults.Length > 0 &&
            grantResults[0] == Permission.Granted;
        if (granted)
        {
            ToastMessage("Microphone ready. Hold the button to talk.");
            UpdateVoiceStatus();
        }
        else
        {
            _voiceStatus.Text =
                "Microphone permission is required for push-to-talk.";
            _voiceStatus.SetTextColor(Red);
        }
    }

    public int Dp(int value) =>
        (int)Math.Round(value * Resources!.DisplayMetrics!.Density);

    private View BuildApplicationShell()
    {
        var root = new FrameLayout(this)
        {
            LayoutParameters = MatchParent(),
        };
        root.SetBackgroundColor(Color.Black);

        var shell = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        shell.SetBackgroundColor(AppBackground);
        shell.LayoutParameters = MatchParent();

        shell.AddView(BuildTopBar());
        shell.AddView(BuildConnectionBar());

        var content = new FrameLayout(this)
        {
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                0,
                1f),
        };

        _pages.Add(BuildPlayersPage());
        _pages.Add(BuildLiveMapPage());
        _pages.Add(BuildChatPage());
        _pages.Add(BuildServerPage());
        _pages.Add(BuildConsolePage());
        foreach (View page in _pages)
            content.AddView(page, MatchParent());

        shell.AddView(content);
        shell.AddView(BuildTabBar());
        _authenticatedShell = shell;
        _disconnectedShell = BuildDisconnectedShell();
        root.AddView(_authenticatedShell, MatchParent());
        root.AddView(_disconnectedShell, MatchParent());
        return root;
    }

    private View BuildDisconnectedShell()
    {
        var scroll = new ScrollView(this)
        {
            FillViewport = true,
        };
        scroll.SetBackgroundColor(Color.Black);

        var outer = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        outer.SetGravity(GravityFlags.Center);
        outer.SetPadding(Dp(28), Dp(32), Dp(28), Dp(32));
        outer.SetBackgroundColor(Color.Black);

        var content = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        content.SetGravity(GravityFlags.CenterHorizontal);

        TextView brand = MakeText("NEO ADMIN", 14, TextSecondary, true);
        brand.Gravity = GravityFlags.Center;
        content.AddView(
            brand,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                Dp(40)));

        TextView heading = MakeText("CONNECT", 34, TextPrimary, true);
        heading.Gravity = GravityFlags.Center;
        content.AddView(
            heading,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                Dp(66)));

        _disconnectedServerName = MakeText("Server not configured", 18, TextPrimary, true);
        _disconnectedServerName.Gravity = GravityFlags.Center;
        content.AddView(
            _disconnectedServerName,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                Dp(42)));

        _disconnectedEndpoint = MakeText("Server information required", 15, TextSecondary);
        _disconnectedEndpoint.Gravity = GravityFlags.Center;
        content.AddView(
            _disconnectedEndpoint,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                Dp(40)));

        _disconnectedStatus = MakeText("Not connected", 13, TextSecondary);
        _disconnectedStatus.Gravity = GravityFlags.Center;
        _disconnectedStatus.SetMaxLines(3);
        content.AddView(
            _disconnectedStatus,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                Dp(68)));

        _disconnectedConnectButton = MakeButton(
            "CONNECT",
            Teal,
            Color.Rgb(7, 25, 23));
        _disconnectedConnectButton.TextSize = 15;
        _disconnectedConnectButton.Click += async (_, _) =>
        {
            _resumeConnectionRequested = true;
            CancelAutomaticReconnect();
            await ConnectAsync();
        };
        content.AddView(
            _disconnectedConnectButton,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                Dp(56))
            {
                TopMargin = Dp(10),
            });

        Button edit = MakeButton("EDIT SERVER", Color.Transparent, TextSecondary);
        edit.Click += (_, _) => ShowConnectionSettings();
        content.AddView(
            edit,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                Dp(50))
            {
                TopMargin = Dp(6),
            });

        outer.AddView(
            content,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent));
        scroll.AddView(outer, MatchParent());
        return scroll;
    }

    private View BuildTopBar()
    {
        var bar = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
        };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(Dp(16), Dp(8), Dp(8), Dp(8));
        bar.SetBackgroundColor(Color.Rgb(23, 27, 31));

        var titleBlock = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new LinearLayout.LayoutParams(0, Dp(52), 1f),
        };
        titleBlock.AddView(MakeText("NEO ADMIN", 20, TextPrimary, true));
        _sessionStatus = MakeText("No administrator session", 12, TextSecondary);
        titleBlock.AddView(_sessionStatus);
        bar.AddView(titleBlock);

        var settings = MakeButton("SETTINGS", SurfaceRaised, TextPrimary);
        settings.ContentDescription = "Connection settings";
        settings.Click += (_, _) => ShowConnectionSettings();
        bar.AddView(settings, new LinearLayout.LayoutParams(Dp(108), Dp(44)));
        return bar;
    }

    private View BuildConnectionBar()
    {
        var bar = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
        };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(Dp(12), Dp(8), Dp(8), Dp(8));
        bar.SetBackgroundColor(Surface);

        _connectionStatus = MakeText("Not configured", 13, TextSecondary, true);
        _connectionStatus.SetMaxLines(4);
        bar.AddView(
            _connectionStatus,
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        _editConnectionButton = MakeButton("EDIT", SurfaceRaised, TextPrimary);
        _editConnectionButton.ContentDescription = "Edit server connection";
        _editConnectionButton.Click += (_, _) => ShowConnectionSettings();
        bar.AddView(
            _editConnectionButton,
            new LinearLayout.LayoutParams(Dp(72), Dp(44)) { RightMargin = Dp(6) });

        _connectButton = MakeButton("CONNECT", Teal, Color.Rgb(7, 25, 23));
        _connectButton.Click += async (_, _) =>
        {
            if (_receiver?.HasServerTarget == true)
                await DisconnectAsync();
            else
            {
                _resumeConnectionRequested = true;
                CancelAutomaticReconnect();
                await ConnectAsync();
            }
        };
        bar.AddView(_connectButton, new LinearLayout.LayoutParams(Dp(112), Dp(44)));
        return bar;
    }

    private View BuildPlayersPage()
    {
        var page = PageContainer();
        var heading = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
        };
        heading.SetGravity(GravityFlags.CenterVertical);
        heading.SetPadding(Dp(14), Dp(12), Dp(14), Dp(8));
        heading.AddView(MakeText("PLAYERS", 16, TextPrimary, true),
            new LinearLayout.LayoutParams(0, Dp(36), 1f));
        _playerCount = MakeText("0 online", 13, TextSecondary, true);
        heading.AddView(_playerCount);
        page.AddView(heading);

        _playerAdapter = new PlayerListAdapter(this);
        _playerList = new ListView(this)
        {
            Adapter = _playerAdapter,
            DividerHeight = Dp(1),
        };
        _playerList.SetBackgroundColor(AppBackground);
        _playerList.ItemClick += (_, args) =>
            ShowPlayerActions(_playerAdapter[args.Position]);
        page.AddView(_playerList,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                0,
                1f));
        return page;
    }

    private View BuildChatPage()
    {
        var page = PageContainer(Dp(12));
        page.AddView(MakeSectionHeader("SERVER CHAT"));

        _voiceStatus = MakeText("Player voice: waiting for connection", 12, TextSecondary);
        _voiceStatus.SetPadding(Dp(4), 0, Dp(4), Dp(6));
        page.AddView(_voiceStatus);

        _pushToTalkButton = MakeButton(
            "HOLD TO TALK",
            Teal,
            Color.Rgb(7, 25, 23));
        _pushToTalkButton.Touch += OnPushToTalkTouch;
        page.AddView(
            _pushToTalkButton,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                Dp(58))
            {
                BottomMargin = Dp(2),
            });

        _microphoneGainLabel = MakeText(
            $"MICROPHONE GAIN  {_profile.MicrophoneGainPercent}%",
            12,
            TextSecondary,
            true);
        _microphoneGainLabel.SetPadding(Dp(4), Dp(8), Dp(4), 0);
        page.AddView(_microphoneGainLabel);

        _microphoneGainSlider = new SeekBar(this)
        {
            Max = 250,
            Progress = _profile.MicrophoneGainPercent - 50,
        };
        _microphoneGainSlider.ProgressTintList = ColorStateList.ValueOf(Teal);
        _microphoneGainSlider.ThumbTintList = ColorStateList.ValueOf(Teal);
        _microphoneGainSlider.ProgressChanged += (_, args) =>
        {
            int percent = args.Progress + 50;
            _profile.MicrophoneGainPercent = percent;
            _microphoneGainLabel.Text = $"MICROPHONE GAIN  {percent}%";
            if (_pttCapture is not null)
                _pttCapture.MicrophoneGain = percent / 100f;
            if (args.FromUser)
                MobileProfileStore.Save(this, _profile);
        };
        page.AddView(
            _microphoneGainSlider,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                Dp(42)));

        _mutePlayersCheckBox = new CheckBox(this)
        {
            Text = "Mute all player voice on this phone",
        };
        _mutePlayersCheckBox.SetTextColor(TextPrimary);
        _mutePlayersCheckBox.ButtonTintList = ColorStateList.ValueOf(Teal);
        _mutePlayersCheckBox.CheckedChange += (_, args) =>
        {
            _profile.MutePlayerAudio = args.IsChecked;
            _voicePlayback?.SetUserMuted(args.IsChecked);
            MobileProfileStore.Save(this, _profile);
            UpdateVoiceStatus();
        };
        page.AddView(
            _mutePlayersCheckBox,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                Dp(46)));

        _chatHistory = MakeText("", 13, TextPrimary);
        _chatHistory.SetTextIsSelectable(true);
        _chatHistory.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        _chatHistory.SetBackgroundColor(Surface);

        _chatScroll = new ScrollView(this) { FillViewport = true };
        _chatScroll.AddView(_chatHistory, MatchParent());
        page.AddView(_chatScroll,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                0,
                1f));

        var inputRow = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
        };
        inputRow.SetGravity(GravityFlags.CenterVertical);
        inputRow.SetPadding(0, Dp(8), 0, 0);
        _chatInput = MakeInput("Message players", false);
        _chatInput.ImeOptions = ImeAction.Send;
        _chatInput.EditorAction += async (_, args) =>
        {
            if (args.ActionId == ImeAction.Send)
                await SendChatAsync();
        };
        inputRow.AddView(_chatInput,
            new LinearLayout.LayoutParams(0, Dp(48), 1f));
        _chatSendButton = MakeButton("SEND", Teal, Color.Rgb(7, 25, 23));
        _chatSendButton.Click += async (_, _) => await SendChatAsync();
        inputRow.AddView(_chatSendButton,
            new LinearLayout.LayoutParams(Dp(88), Dp(48)) { LeftMargin = Dp(8) });
        page.AddView(inputRow);
        return page;
    }

    private View BuildLiveMapPage()
    {
        var page = PageContainer();
        _liveMapTitle = MakeText("LIVE MAP - --", 16, TextPrimary, true);
        _liveMapTitle.SetPadding(Dp(14), Dp(12), Dp(14), Dp(2));
        page.AddView(_liveMapTitle);

        _liveMapStatus = MakeText(
            "Waiting for map data from the server...",
            12,
            TextSecondary);
        _liveMapStatus.SetPadding(Dp(14), 0, Dp(14), Dp(6));
        page.AddView(_liveMapStatus);

        _liveMapView = new MobileMapOverviewView(this);
        _liveMapView.PlayerDragTeleport += OnMapPlayerDragTeleport;
        _liveMapView.InteractionStatus += status =>
        {
            _liveMapStatus.Text = status;
            AppendConsole($"MAP CONTROL: {status}");
        };
        page.AddView(
            _liveMapView,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                0,
                1f));
        return page;
    }

    private View BuildServerPage()
    {
        var scroll = new ScrollView(this) { FillViewport = true };
        var page = PageContainer(Dp(12));
        scroll.AddView(page, MatchParent());

        page.AddView(MakeSectionHeader("SERVER"));
        _currentMap = MakeText("Map: --", 17, TextPrimary, true);
        _currentMap.SetPadding(Dp(12), Dp(12), Dp(12), Dp(4));
        page.AddView(_currentMap);
        _healthStatus = MakeText("Health: waiting for connection", 13, TextSecondary);
        _healthStatus.SetPadding(Dp(12), 0, Dp(12), Dp(12));
        page.AddView(_healthStatus);

        _mapButton = MakeButton("SELECT MAP", Teal, Color.Rgb(7, 25, 23));
        _mapButton.Click += async (_, _) => await ShowMapSelectorAsync();
        page.AddView(_mapButton, FullWidthButton());

        page.AddView(MakeSectionHeader("MATCH CONTROL"));
        page.AddView(BuildButtonRow(
            MakeActionButton("RESTART ROUND", AdminActionCode.RestartRound, _matchButtons),
            MakeActionButton("RESTART MATCH", AdminActionCode.RestartMatch, _matchButtons)));
        page.AddView(BuildButtonRow(
            MakeActionButton("END WARMUP", AdminActionCode.EndWarmup, _matchButtons),
            MakeActionButton("SWAP TEAMS", AdminActionCode.SwapTeams, _matchButtons)));
        page.AddView(BuildButtonRow(
            MakeActionButton("PAUSE", AdminActionCode.PauseMatch, _matchButtons),
            MakeActionButton("UNPAUSE", AdminActionCode.UnpauseMatch, _matchButtons)));

        page.AddView(MakeSectionHeader("BOT CONTROL"));
        page.AddView(BuildButtonRow(
            MakeActionButton("ADD T BOT", AdminActionCode.AddBot, _botButtons, 2),
            MakeActionButton("ADD CT BOT", AdminActionCode.AddBot, _botButtons, 3)));
        Button removeBots = MakeActionButton(
            "KICK ALL BOTS",
            AdminActionCode.RemoveBots,
            _botButtons,
            color: Red);
        page.AddView(removeBots, FullWidthButton());
        return scroll;
    }

    private View BuildConsolePage()
    {
        var page = PageContainer(Dp(12));
        page.AddView(MakeSectionHeader("SERVER CONSOLE"));

        _consoleHistory = MakeText("", 12, TextPrimary);
        _consoleHistory.Typeface = Typeface.Monospace;
        _consoleHistory.SetTextIsSelectable(true);
        _consoleHistory.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        _consoleHistory.SetBackgroundColor(Color.Rgb(10, 13, 15));
        _consoleScroll = new ScrollView(this) { FillViewport = true };
        _consoleScroll.AddView(_consoleHistory, MatchParent());
        page.AddView(_consoleScroll,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                0,
                1f));

        var row = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
        };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(0, Dp(8), 0, 0);
        _consoleInput = MakeInput("Enter console command", false);
        _consoleInput.ImeOptions = ImeAction.Send;
        _consoleInput.EditorAction += async (_, args) =>
        {
            if (args.ActionId == ImeAction.Send)
                await SendConsoleCommandAsync();
        };
        row.AddView(_consoleInput,
            new LinearLayout.LayoutParams(0, Dp(48), 1f));
        _consoleSendButton = MakeButton("RUN", Amber, Color.Rgb(32, 25, 7));
        _consoleSendButton.Click += async (_, _) => await SendConsoleCommandAsync();
        row.AddView(_consoleSendButton,
            new LinearLayout.LayoutParams(Dp(82), Dp(48)) { LeftMargin = Dp(8) });
        page.AddView(row);
        return page;
    }

    private View BuildTabBar()
    {
        var scroll = new HorizontalScrollView(this)
        {
            HorizontalScrollBarEnabled = false,
            FillViewport = true,
        };
        scroll.SetBackgroundColor(Color.Rgb(23, 27, 31));
        var row = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
        };
        row.SetGravity(GravityFlags.Center);
        string[] labels = ["PLAYERS", "MAP", "CHAT", "SERVER", "CONSOLE"];
        for (int index = 0; index < labels.Length; index++)
        {
            int pageIndex = index;
            Button button = MakeButton(labels[index], Color.Transparent, TextSecondary);
            button.Click += (_, _) => ShowPage(pageIndex);
            row.AddView(button, new LinearLayout.LayoutParams(Dp(96), Dp(56)));
            _tabButtons.Add(button);
        }
        scroll.AddView(row);
        return scroll;
    }

    private void ShowPage(int index)
    {
        for (int page = 0; page < _pages.Count; page++)
            _pages[page].Visibility = page == index ? ViewStates.Visible : ViewStates.Gone;
        for (int tab = 0; tab < _tabButtons.Count; tab++)
        {
            _tabButtons[tab].SetTextColor(tab == index ? Teal : TextSecondary);
            _tabButtons[tab].Typeface = tab == index
                ? Typeface.DefaultBold
                : Typeface.Default;
        }
    }

    private async Task<bool> ConnectAsync(
        bool showErrors = true,
        bool waitForAuthentication = false,
        CancellationToken cancellationToken = default)
    {
        if (_connecting)
            return false;
        if (!_profile.IsComplete)
        {
            if (showErrors)
                ShowConnectionSettings();
            return false;
        }

        _connecting = true;
        _transportStale = true;
        UpdateConnectionUi("Connecting...");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DisposeReceiverAsync();
            if (!PrepareNetworkRoute(out string routeError))
                throw new InvalidOperationException(routeError);
            UpdateConnectionUi($"Connecting over {_networkRoute}...");
            string accessKey = _profile.AccessKey.Trim();
            byte[] accessKeyBytes = Encoding.UTF8.GetBytes(accessKey);
            var config = new AppConfig
            {
                BindAddress = "0.0.0.0",
                Port = 27120,
                SharedSecret = accessKey,
                AdminId = BridgeCommandPacket.BuildAdminAccessSelector(
                    accessKeyBytes),
                AdminDisplayName = _profile.OperatorName.Trim(),
            };
            var receiver = new UdpVoiceReceiver(config);
            receiver.StatusChanged += OnStatusChanged;
            receiver.AdminSessionChanged += OnAdminSessionChanged;
            receiver.PacketReceived += OnPacketReceived;
            _receiver = receiver;
            receiver.Start();
            await receiver.ConfigureServerAsync(
                _profile.ServerAddress,
                _profile.ServerPort);
            AppendConsole(
                $"CONNECT sent to {_profile.ServerAddress}:{_profile.ServerPort}/UDP");
            if (waitForAuthentication)
            {
                bool authenticated = await WaitForAuthenticationAsync(
                    receiver,
                    cancellationToken);
                if (!authenticated)
                {
                    string detail = receiver.CurrentSession?.Message ??
                        "The server did not answer the administrator login.";
                    throw new IOException(detail);
                }
            }
            else
            {
                _ = OfferConnectionHelpAfterTimeoutAsync(receiver);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeReceiverAsync();
            ReleaseNetworkBinding();
            return false;
        }
        catch (Exception exception)
        {
            if (showErrors)
            {
                AppendConsole($"CONNECT FAILED: {exception.Message}");
                ShowError("Connection failed", exception.Message);
            }
            await DisposeReceiverAsync();
            ReleaseNetworkBinding();
            return false;
        }
        finally
        {
            _connecting = false;
            Interlocked.Exchange(ref _consecutiveHealthSendFailures, 0);
            Interlocked.Exchange(ref _healthRecoveryPending, 0);
            UpdateConnectionUi();
            UpdatePermissionUi();
        }
    }

    private async Task DisconnectAsync()
    {
        StopPushToTalk();
        _resumeConnectionRequested = false;
        CancelAutomaticReconnect();
        CancelMapOverviewDownload();
        AppendConsole("Disconnected by administrator.");
        await DisposeReceiverAsync();
        ReleaseNetworkBinding();
        _players.Clear();
        RefreshPlayers();
        _maps.Clear();
        _mapsRequested = false;
        _lastReportedMap = string.Empty;
        _reportedConnectedPlayers = -1;
        _reportedMaxPlayers = 0;
        _healthProbesPausedUntilUtc = DateTime.MinValue;
        Interlocked.Exchange(ref _lastServerPacketUtcTicks, 0);
        _transportStale = false;
        Interlocked.Exchange(ref _consecutiveHealthSendFailures, 0);
        Interlocked.Exchange(ref _healthRecoveryPending, 0);
        _currentMap.Text = "Map: --";
        _healthStatus.Text = "Health: waiting for connection";
        _liveMapTitle.Text = "LIVE MAP - --";
        _liveMapStatus.Text = "Waiting for map data from the server...";
        _liveMapView.SetCurrentMap(string.Empty);
        UpdateServerContextStatus();
        UpdateConnectionUi();
        UpdatePermissionUi();
    }

    private static async Task<bool> WaitForAuthenticationAsync(
        UdpVoiceReceiver receiver,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            AdminSession? session = receiver.CurrentSession;
            if (session?.Authenticated == true)
                return true;
            if (session is not null)
                return false;
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        }
        return false;
    }

    private void ScheduleAutomaticReconnect(string reason)
    {
        CancelAutomaticReconnect();
        if (!_isForeground ||
            !_resumeConnectionRequested ||
            !_profile.IsComplete ||
            _lifetime.IsCancellationRequested)
        {
            return;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _automaticReconnect = source;
        _ = AutomaticReconnectLoopAsync(reason, source);
    }

    private void CancelAutomaticReconnect()
    {
        CancellationTokenSource? source = _automaticReconnect;
        _automaticReconnect = null;
        source?.Cancel();
    }

    private async Task AutomaticReconnectLoopAsync(
        string reason,
        CancellationTokenSource source)
    {
        CancellationToken cancellationToken = source.Token;
        int attempt = 0;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            AppendConsole($"CONNECTION: restoring after {reason}.");

            while (_isForeground &&
                   _resumeConnectionRequested &&
                   !cancellationToken.IsCancellationRequested)
            {
                if (attempt > 0)
                {
                    int retrySeconds = Math.Min(15, 1 << Math.Min(attempt - 1, 4));
                    UpdateConnectionUi($"Waiting for Wi-Fi or server; retrying in {retrySeconds}s...");
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken);
                }

                bool connected = await ConnectAsync(
                    showErrors: false,
                    waitForAuthentication: true,
                    cancellationToken);
                if (connected)
                {
                    AppendConsole("CONNECTION: administrator session restored.");
                    UpdateConnectionUi();
                    return;
                }

                attempt++;
            }
        }
        catch (OperationCanceledException)
        {
            // Going to the background is expected; OnResume starts a fresh attempt.
        }
        finally
        {
            if (ReferenceEquals(_automaticReconnect, source))
                _automaticReconnect = null;
            source.Dispose();
        }
    }

    private async Task DisposeReceiverAsync()
    {
        UdpVoiceReceiver? receiver = _receiver;
        _receiver = null;
        _voicePlayback.Reset();
        if (receiver is null)
            return;

        receiver.StatusChanged -= OnStatusChanged;
        receiver.AdminSessionChanged -= OnAdminSessionChanged;
        receiver.PacketReceived -= OnPacketReceived;
        await receiver.DisposeAsync();
    }

    private void OnStatusChanged(string status) =>
        RunOnUiThread(() =>
        {
            UpdateConnectionUi(status);
            DateTime now = DateTime.UtcNow;
            if (!string.Equals(status, _lastConsoleStatus, StringComparison.Ordinal) ||
                now - _lastConsoleStatusUtc >= TimeSpan.FromSeconds(30))
            {
                _lastConsoleStatus = status;
                _lastConsoleStatusUtc = now;
                AppendConsole(status);
            }
        });

    private void OnAdminSessionChanged(AdminSession? session) =>
        RunOnUiThread(() =>
        {
            if (session?.Authenticated == true)
            {
                _transportStale = false;
                Interlocked.Exchange(
                    ref _lastServerPacketUtcTicks,
                    DateTime.UtcNow.Ticks);
                _connectionHelpVisible = false;
                _sessionStatus.Text = $"{session.DisplayName} | {session.Role}";
                _sessionStatus.SetTextColor(Teal);
                AppendConsole($"AUTHENTICATED: {session.DisplayName} ({session.Role})");
                if (!_mapsRequested && session.Can(AdminPermission.ChangeMap))
                {
                    _mapsRequested = true;
                    _ = _receiver?.SendAdminActionAsync(
                        AdminActionCode.RequestMapCatalog,
                        -1);
                }
            }
            else
            {
                _transportStale = true;
                _sessionStatus.Text = session?.Message ?? "No administrator session";
                _sessionStatus.SetTextColor(session is null ? TextSecondary : Red);
            }
            UpdateConnectionUi(
                session?.Authenticated == true ? null : session?.Message);
            UpdatePermissionUi();
        });

    private async Task OfferConnectionHelpAfterTimeoutAsync(UdpVoiceReceiver receiver)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (!ReferenceEquals(_receiver, receiver) ||
                receiver.CurrentSession?.Authenticated == true ||
                _connectionHelpVisible ||
                _connectionEditorVisible)
            {
                return;
            }

            _connectionHelpVisible = true;
            new AlertDialog.Builder(this)
                .SetTitle("Connection not authenticated")
                .SetMessage(
                    "The server did not accept the connection. Check the server address, " +
                    "UDP port, and access key.")
                .SetCancelable(false)
                .SetNegativeButton("KEEP WAITING", (_, _) =>
                    _connectionHelpVisible = false)
                .SetPositiveButton("EDIT CONNECTION", (_, _) =>
                {
                    _connectionHelpVisible = false;
                    ShowConnectionSettings();
                })
                .Show();
        });
    }

    private void OnPacketReceived(VoicePacket packet, IPEndPoint endpoint)
    {
        if (packet.MessageType == BridgeMessageType.Voice)
            _voicePlayback.HandlePacket(packet);
        RunOnUiThread(() => HandlePacket(packet, endpoint));
    }

    private void HandlePacket(VoicePacket packet, IPEndPoint endpoint)
    {
        _transportStale = false;
        Interlocked.Exchange(
            ref _lastServerPacketUtcTicks,
            DateTime.UtcNow.Ticks);
        UpdateConnectionUi($"Receiving from {endpoint.Address}:{endpoint.Port}");
        switch (packet.MessageType)
        {
            case BridgeMessageType.PlayerConnected:
                if (IsSourceTv(packet.PlayerName))
                    UpsertPlayer(packet, sourceTv: true);
                break;
            case BridgeMessageType.PlayerDisconnected:
                RemovePlayer(packet);
                break;
            case BridgeMessageType.PlayerPosition:
                UpsertPlayer(packet);
                break;
            case BridgeMessageType.Voice:
                UpsertPlayer(packet, speaking: true);
                break;
            case BridgeMessageType.MapChanged:
            {
                string reportedMap = packet.MapName.Trim();
                if (reportedMap.Length == 0)
                    break;
                _currentMap.Text = $"Map: {reportedMap}";
                bool mapChanged = !string.Equals(
                        _lastReportedMap,
                        reportedMap,
                        StringComparison.OrdinalIgnoreCase);
                _lastReportedMap = reportedMap;
                UpdateServerContextStatus();
                if (mapChanged)
                {
                    Interlocked.Exchange(ref _consecutiveHealthSendFailures, 0);
                    AppendConsole($"MAP: {reportedMap}");
                    BeginMapOverviewSync(reportedMap);
                }
                break;
            }
            case BridgeMessageType.ChatEvent:
                AppendChat(packet.PlayerName, packet.ChatMessage);
                break;
            case BridgeMessageType.AdminActionResult:
                HandleMapOverviewActionResult(packet);
                HandleAdminActionResult(packet);
                break;
            case BridgeMessageType.MapCatalog:
                LoadMapCatalog(packet.MapCatalogText);
                break;
            case BridgeMessageType.ServerHealth:
                ShowHealth(packet);
                break;
            case BridgeMessageType.MapOverviewChunk:
                HandleMapOverviewChunk(packet);
                break;
        }
    }

    private void UpsertPlayer(
        VoicePacket packet,
        bool sourceTv = false,
        bool speaking = false)
    {
        string key = PlayerKey(packet);
        if (!_players.TryGetValue(key, out MobilePlayer? player))
        {
            player = new MobilePlayer { Key = key };
            _players.Add(key, player);
        }

        player.SteamId = packet.SteamId != 0 ? packet.SteamId : player.SteamId;
        player.Slot = packet.PlayerSlot;
        if (!string.IsNullOrWhiteSpace(packet.PlayerName))
            player.Name = packet.PlayerName.Trim();
        if (packet.MessageType == BridgeMessageType.PlayerPosition)
        {
            player.Team = packet.TeamNumber;
            player.Health = packet.Health;
            player.Alive = packet.IsAlive;
            player.Bot = packet.IsBot;
            player.X = packet.PositionX;
            player.Y = packet.PositionY;
            player.Z = packet.PositionZ;
            player.Yaw = packet.ViewYaw;
        }
        player.SourceTv |= sourceTv || IsSourceTv(player.Name);
        if (speaking)
        {
            player.Speaking = true;
            player.LastVoiceUtc = DateTime.UtcNow;
        }
        player.LastSeenUtc = DateTime.UtcNow;
        RefreshPlayers();
    }

    private void RemovePlayer(VoicePacket packet)
    {
        _players.Remove(PlayerKey(packet));
        if (packet.PlayerSlot >= 0)
        {
            foreach (string stale in _players
                .Where(pair => pair.Value.Slot == packet.PlayerSlot)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _players.Remove(stale);
            }
        }
        RefreshPlayers();
    }

    private void RefreshPlayers()
    {
        MobilePlayer[] ordered = _players.Values
            .OrderBy(player => player.SourceTv ? 1 : 0)
            .ThenBy(player => player.Team)
            .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _playerAdapter.Replace(ordered);
        _playerCount.Text = $"{ordered.Length} online";
        _liveMapView.UpdatePlayers(ordered);
        UpdateServerContextStatus();
    }

    private void PrunePlayers()
    {
        DateTime now = DateTime.UtcNow;
        bool changed = false;
        foreach (MobilePlayer player in _players.Values.ToArray())
        {
            if (player.Speaking && now - player.LastVoiceUtc > TimeSpan.FromSeconds(1))
            {
                player.Speaking = false;
                changed = true;
            }
            if (!player.SourceTv && now - player.LastSeenUtc > TimeSpan.FromSeconds(12))
            {
                _players.Remove(player.Key);
                changed = true;
            }
        }
        if (changed)
            RefreshPlayers();
    }

    private void HandleAdminActionResult(VoicePacket packet)
    {
        string prefix = packet.AdminActionSucceeded ? "OK" : "FAILED";
        string output = packet.AdminActionMessage
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();
        if (output.Length == 0)
            output = packet.AdminActionSucceeded ? "Action completed." : "Action failed.";

        string line = $"{prefix}: {ActionName(packet.AdminActionCode)}: {output}";
        AppendConsole(line);
        UpdateConnectionUi(line.Split('\n')[0]);
    }

    private void LoadMapCatalog(string text)
    {
        _maps.Clear();
        _maps.AddRange(text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(map => map.Trim())
            .Where(IsPlayableMap)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(map => map, StringComparer.OrdinalIgnoreCase));
        AppendConsole($"MAP CATALOG: {_maps.Count} playable maps");
    }

    private void ShowHealth(VoicePacket packet)
    {
        _reportedConnectedPlayers = Math.Max(0, packet.ConnectedPlayers);
        _reportedMaxPlayers = packet.MaxPlayers;
        string ping = double.IsFinite(packet.RoundTripMilliseconds)
            ? $"{packet.RoundTripMilliseconds:F0} ms"
            : "--";
        _healthStatus.Text =
            $"Players {_reportedConnectedPlayers}/{_reportedMaxPlayers} | " +
            $"Tick {packet.TickRate:F1} | Ping {ping} | Loss {packet.PacketLossPercent:F1}%";
        UpdateServerContextStatus();
    }

    private void UpdateServerContextStatus()
    {
        if (_connectionStatus is null)
            return;

        string map = string.IsNullOrWhiteSpace(_lastReportedMap)
            ? "--"
            : _lastReportedMap;
        int connectedPlayers = _reportedConnectedPlayers >= 0
            ? _reportedConnectedPlayers
            : _players.Count;
        string maxPlayers = _reportedMaxPlayers > 0
            ? _reportedMaxPlayers.ToString()
            : "--";
        _connectionStatus.Text =
            $"{_connectionPrimaryStatus}\n" +
            $"MAP: {map}  |  PLAYERS: {connectedPlayers}/{maxPlayers}";
    }

    private void BeginMapOverviewSync(string reportedMap)
    {
        string mapName = NormalizeOverviewMapName(reportedMap);
        CancelMapOverviewDownload();
        _liveMapView.SetCurrentMap(mapName);
        _liveMapTitle.Text = $"LIVE MAP - {mapName}";

        byte[]? cachedPackage = null;
        string cachePath = GetOverviewCachePath(mapName);
        try
        {
            if (File.Exists(cachePath))
            {
                cachedPackage = File.ReadAllBytes(cachePath);
                if (!_liveMapView.SetPackage(cachedPackage, out _))
                {
                    cachedPackage = null;
                    File.Delete(cachePath);
                }
            }
        }
        catch
        {
            cachedPackage = null;
        }

        _liveMapStatus.Text = cachedPackage is null
            ? "Syncing overview from server..."
            : "Checking the server for an updated overview...";

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        _overviewDownload = cancellation;
        _ = SyncMapOverviewAsync(
            mapName,
            cachePath,
            cachedPackage,
            cancellation);
    }

    private async Task SyncMapOverviewAsync(
        string mapName,
        string cachePath,
        byte[]? cachedPackage,
        CancellationTokenSource cancellation)
    {
        try
        {
            CancellationToken token = cancellation.Token;
            VoicePacket first = await RequestMapOverviewChunkAsync(
                mapName,
                0,
                token);
            int packageLength = checked((int)first.MapOverviewPackageLength);
            int chunkCount = checked((int)first.MapOverviewChunkCount);
            uint packageHash = first.MapOverviewPackageHash;

            if (cachedPackage is not null &&
                cachedPackage.Length == packageLength &&
                ComputeOverviewHash(cachedPackage) == packageHash)
            {
                SetMapOverviewStatus(mapName, "Overview is current with the server.");
                return;
            }

            var package = new byte[packageLength];
            int written = 0;
            for (int index = 0; index < chunkCount; index++)
            {
                token.ThrowIfCancellationRequested();
                VoicePacket chunk = index == 0
                    ? first
                    : await RequestMapOverviewChunkAsync(mapName, index, token);
                if (chunk.MapOverviewChunkIndex != index ||
                    chunk.MapOverviewChunkCount != first.MapOverviewChunkCount ||
                    chunk.MapOverviewPackageLength != first.MapOverviewPackageLength ||
                    chunk.MapOverviewPackageHash != packageHash ||
                    chunk.MapOverviewDefinitionLength != first.MapOverviewDefinitionLength ||
                    written + chunk.Payload.Length > package.Length)
                {
                    throw new InvalidDataException(
                        "The server returned inconsistent overview chunks.");
                }

                Buffer.BlockCopy(
                    chunk.Payload,
                    0,
                    package,
                    written,
                    chunk.Payload.Length);
                written += chunk.Payload.Length;

                if (index == 0 || index + 1 == chunkCount || index % 20 == 0)
                {
                    int percent = (int)Math.Round(
                        (index + 1) * 100.0 / chunkCount);
                    SetMapOverviewStatus(
                        mapName,
                        $"Syncing overview from server... {percent}%");
                }
            }

            if (written != package.Length ||
                ComputeOverviewHash(package) != packageHash ||
                BinaryPrimitives.ReadUInt32LittleEndian(package) !=
                    first.MapOverviewDefinitionLength)
            {
                throw new InvalidDataException(
                    "The completed overview failed its integrity check.");
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cachePath)!);
            string temporaryPath = cachePath + ".new";
            await File.WriteAllBytesAsync(temporaryPath, package, token);
            File.Move(temporaryPath, cachePath, true);

            RunOnUiThread(() =>
            {
                if (!string.Equals(
                        NormalizeOverviewMapName(_lastReportedMap),
                        mapName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (_liveMapView.SetPackage(package, out string error))
                    _liveMapStatus.Text = "Live overview synced from server.";
                else
                    _liveMapStatus.Text = error;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetMapOverviewStatus(
                mapName,
                $"Overview unavailable: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_overviewDownload, cancellation))
                _overviewDownload = null;
            cancellation.Dispose();
        }
    }

    private async Task<VoicePacket> RequestMapOverviewChunkAsync(
        string mapName,
        int chunkIndex,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UdpVoiceReceiver? receiver = _receiver;
            if (receiver?.CurrentSession?.Authenticated != true)
                throw new IOException("The administrator session is not connected.");

            var reply = new TaskCompletionSource<VoicePacket>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_overviewReplySync)
            {
                _overviewReplyMap = mapName;
                _overviewReplyIndex = chunkIndex;
                _overviewChunkReply = reply;
            }

            bool sent = await receiver.SendAdminActionAsync(
                AdminActionCode.RequestMapOverview,
                -1,
                chunkIndex,
                mapName);
            if (sent)
            {
                Task completed = await Task.WhenAny(
                    reply.Task,
                    Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
                if (completed == reply.Task)
                    return await reply.Task;
            }

            lock (_overviewReplySync)
            {
                if (ReferenceEquals(_overviewChunkReply, reply))
                    _overviewChunkReply = null;
            }
        }

        throw new IOException(
            $"The server did not return overview chunk {chunkIndex + 1}.");
    }

    private void HandleMapOverviewChunk(VoicePacket packet)
    {
        TaskCompletionSource<VoicePacket>? reply = null;
        lock (_overviewReplySync)
        {
            if (_overviewChunkReply is not null &&
                packet.MapOverviewChunkIndex == _overviewReplyIndex &&
                string.Equals(
                    NormalizeOverviewMapName(packet.MapOverviewName),
                    _overviewReplyMap,
                    StringComparison.OrdinalIgnoreCase))
            {
                reply = _overviewChunkReply;
                _overviewChunkReply = null;
            }
        }
        reply?.TrySetResult(packet);
    }

    private void HandleMapOverviewActionResult(VoicePacket packet)
    {
        if (packet.AdminActionCode != (uint)AdminActionCode.RequestMapOverview ||
            packet.AdminActionSucceeded)
        {
            return;
        }

        TaskCompletionSource<VoicePacket>? reply;
        lock (_overviewReplySync)
        {
            reply = _overviewChunkReply;
            _overviewChunkReply = null;
        }
        reply?.TrySetException(new IOException(packet.AdminActionMessage));
    }

    private void CancelMapOverviewDownload()
    {
        CancellationTokenSource? download = _overviewDownload;
        _overviewDownload = null;
        download?.Cancel();

        lock (_overviewReplySync)
        {
            _overviewChunkReply?.TrySetCanceled();
            _overviewChunkReply = null;
            _overviewReplyMap = string.Empty;
            _overviewReplyIndex = -1;
        }
    }

    private void SetMapOverviewStatus(string mapName, string status) =>
        RunOnUiThread(() =>
        {
            if (string.Equals(
                    NormalizeOverviewMapName(_lastReportedMap),
                    mapName,
                    StringComparison.OrdinalIgnoreCase))
            {
                _liveMapStatus.Text = status;
            }
        });

    private string GetOverviewCachePath(string mapName) =>
        System.IO.Path.Combine(
            FilesDir!.AbsolutePath,
            "map-overviews",
            mapName + ".neo-overview");

    private static uint ComputeOverviewHash(ReadOnlySpan<byte> bytes)
    {
        uint hash = 2166136261U;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= 16777619U;
        }
        return hash;
    }

    private static string NormalizeOverviewMapName(string value)
    {
        string normalized = value.Trim().Replace('\\', '/').ToLowerInvariant();
        int separator = normalized.LastIndexOf('/');
        if (separator >= 0)
            normalized = normalized[(separator + 1)..];
        return new string(normalized
            .Where(character => char.IsLetterOrDigit(character) ||
                character is '_' or '-')
            .Take(96)
            .ToArray());
    }

    private async Task HealthLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                if (!_isForeground)
                    continue;
                UdpVoiceReceiver? receiver = _receiver;
                if (receiver?.CurrentSession?.Authenticated == true &&
                    DateTime.UtcNow >= _healthProbesPausedUntilUtc)
                {
                    long lastServerPacketUtcTicks =
                        Interlocked.Read(ref _lastServerPacketUtcTicks);
                    if (lastServerPacketUtcTicks != 0 &&
                        DateTime.UtcNow.Ticks - lastServerPacketUtcTicks >
                            TimeSpan.FromSeconds(18).Ticks &&
                        Interlocked.CompareExchange(
                            ref _healthRecoveryPending,
                            1,
                            0) == 0)
                    {
                        RunOnUiThread(() =>
                        {
                            if (ReferenceEquals(_receiver, receiver) && !_connecting)
                            {
                                _transportStale = true;
                                UpdateConnectionUi(
                                    "Connection lost after the phone network changed; reconnecting...");
                                UpdatePermissionUi();
                                AppendConsole(
                                    "CONNECTION: server replies stopped; authenticating a new UDP endpoint.");
                                ScheduleAutomaticReconnect("the phone network changed");
                            }
                            else
                            {
                                Interlocked.Exchange(ref _healthRecoveryPending, 0);
                            }
                        });
                        continue;
                    }

                    bool sent = await receiver.SendHealthProbeAsync();
                    if (sent)
                    {
                        Interlocked.Exchange(ref _consecutiveHealthSendFailures, 0);
                    }
                    else if (Interlocked.Increment(
                                 ref _consecutiveHealthSendFailures) >= 3 &&
                             Interlocked.CompareExchange(
                                 ref _healthRecoveryPending,
                                 1,
                                 0) == 0)
                    {
                        RunOnUiThread(() =>
                        {
                            if (ReferenceEquals(_receiver, receiver) && !_connecting)
                            {
                                AppendConsole(
                                    "CONNECTION: refreshing the UDP session after repeated send failures.");
                                ScheduleAutomaticReconnect("a network interruption");
                            }
                            else
                            {
                                Interlocked.Exchange(ref _healthRecoveryPending, 0);
                            }
                        });
                    }
                }
                RunOnUiThread(PrunePlayers);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                RunOnUiThread(() => AppendConsole($"HEALTH: {exception.Message}"));
            }
        }
    }

    private async Task SendChatAsync()
    {
        string message = _chatInput.Text?.Trim() ?? string.Empty;
        if (message.Length == 0)
            return;
        if (_receiver?.Can(AdminPermission.SendChat) != true)
        {
            ToastMessage("Send Chat permission is required.");
            return;
        }

        try
        {
            if (await _receiver.SendAdminChatAsync(message))
            {
                _chatInput.Text = string.Empty;
                HideKeyboard(_chatInput);
            }
        }
        catch (Exception exception)
        {
            ShowError("Chat failed", exception.Message);
        }
    }

    private void OnPushToTalkTouch(object? sender, View.TouchEventArgs args)
    {
        MotionEventActions action = args.Event?.ActionMasked ?? MotionEventActions.Cancel;
        switch (action)
        {
            case MotionEventActions.Down:
                StartPushToTalk();
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
            case MotionEventActions.Outside:
                StopPushToTalk();
                break;
        }
        args.Handled = true;
    }

    private void StartPushToTalk()
    {
        UdpVoiceReceiver? receiver = _receiver;
        if (_transportStale ||
            receiver?.CurrentSession?.Authenticated != true ||
            !receiver.Can(AdminPermission.BroadcastVoice))
        {
            ToastMessage("Broadcast Voice permission and an active connection are required.");
            return;
        }

        if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) !=
            Permission.Granted)
        {
            _voiceStatus.Text = "Allow microphone access, then hold again to talk.";
            _voiceStatus.SetTextColor(Amber);
            RequestPermissions(
                new[] { global::Android.Manifest.Permission.RecordAudio },
                RecordAudioPermissionRequest);
            return;
        }

        try
        {
            Interlocked.Exchange(ref _pttSendFailureReported, 0);
            _pttCapture.Start();
            _voicePlayback.SetPttSuppressed(true);
            _pushToTalkButton.Text = "TRANSMITTING - RELEASE TO STOP";
            _pushToTalkButton.BackgroundTintList = ColorStateList.ValueOf(Amber);
            _pushToTalkButton.SetTextColor(Color.Rgb(32, 25, 7));
            _voiceStatus.Text = "Your microphone is live in the CS2 server.";
            _voiceStatus.SetTextColor(Amber);
        }
        catch (Exception exception)
        {
            _voicePlayback.SetPttSuppressed(false);
            _voiceStatus.Text = $"Microphone error: {exception.Message}";
            _voiceStatus.SetTextColor(Red);
        }
    }

    private void StopPushToTalk()
    {
        if (_pttCapture is null)
            return;

        _pttCapture.Stop();
        _voicePlayback.SetPttSuppressed(false);
        UpdateVoiceStatus();
    }

    private async void OnPttOpusFrameReady(
        byte[] payload,
        int sequenceBytes,
        uint sectionNumber,
        uint sampleOffset,
        float voiceLevel)
    {
        UdpVoiceReceiver? receiver = _receiver;
        if (!_pttCapture.IsRunning ||
            _transportStale ||
            receiver?.Can(AdminPermission.BroadcastVoice) != true)
        {
            return;
        }

        try
        {
            bool sent = await receiver.SendPushToTalkAsync(
                payload,
                sequenceBytes,
                sectionNumber,
                sampleOffset,
                voiceLevel);
            if (!sent &&
                Interlocked.Exchange(ref _pttSendFailureReported, 1) == 0)
            {
                RunOnUiThread(() =>
                {
                    _voiceStatus.Text =
                        "Push-to-talk could not reach the CS2 server.";
                    _voiceStatus.SetTextColor(Red);
                });
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _pttSendFailureReported, 1) == 0)
            {
                RunOnUiThread(() =>
                {
                    _voiceStatus.Text = $"Push-to-talk error: {exception.Message}";
                    _voiceStatus.SetTextColor(Red);
                });
            }
        }
    }

    private void OnPttCaptureError(string message) =>
        RunOnUiThread(() =>
        {
            _voicePlayback.SetPttSuppressed(false);
            _pushToTalkButton.Text = "HOLD TO TALK";
            _pushToTalkButton.BackgroundTintList = ColorStateList.ValueOf(Teal);
            _pushToTalkButton.SetTextColor(Color.Rgb(7, 25, 23));
            _voiceStatus.Text = message;
            _voiceStatus.SetTextColor(Red);
            AppendConsole($"VOICE: {message}");
        });

    private void OnVoicePlaybackError(string message) =>
        RunOnUiThread(() =>
        {
            _voiceStatus.Text = message;
            _voiceStatus.SetTextColor(Red);
            AppendConsole($"VOICE: {message}");
        });

    private void OnVoiceActivity(string playerName) =>
        RunOnUiThread(() =>
        {
            if (_profile.MutePlayerAudio || _pttCapture?.IsRunning == true)
                return;
            _voiceStatus.Text = $"Receiving player voice: {playerName}";
            _voiceStatus.SetTextColor(Teal);
        });

    private void UpdateVoiceStatus()
    {
        if (_voiceStatus is null || _pushToTalkButton is null)
            return;

        if (_pttCapture?.IsRunning == true)
            return;

        _pushToTalkButton.Text = "HOLD TO TALK";
        _pushToTalkButton.BackgroundTintList = ColorStateList.ValueOf(Teal);
        _pushToTalkButton.SetTextColor(Color.Rgb(7, 25, 23));

        if (_profile.MutePlayerAudio)
        {
            _voiceStatus.Text = "Player voice is muted on this phone.";
            _voiceStatus.SetTextColor(TextSecondary);
            return;
        }

        UdpVoiceReceiver? receiver = _receiver;
        if (_transportStale || receiver?.CurrentSession?.Authenticated != true)
        {
            _voiceStatus.Text = "Player voice: waiting for connection";
            _voiceStatus.SetTextColor(TextSecondary);
        }
        else if (!receiver.Can(AdminPermission.BroadcastVoice))
        {
            _voiceStatus.Text =
                "Listening is on. This account cannot broadcast voice.";
            _voiceStatus.SetTextColor(TextSecondary);
        }
        else
        {
            _voiceStatus.Text = "Listening to player voice. Hold the button to talk.";
            _voiceStatus.SetTextColor(Teal);
        }
    }

    private async Task SendConsoleCommandAsync()
    {
        string command = _consoleInput.Text?.Trim() ?? string.Empty;
        if (command.Length == 0)
            return;
        if (_receiver?.Can(AdminPermission.RunServerConsole) != true)
        {
            ToastMessage("Server Console permission is required.");
            return;
        }

        if (await SendActionAsync(
                AdminActionCode.RunServerConsole,
                -1,
                0,
                command,
                $"> {command}"))
        {
            _consoleInput.Text = string.Empty;
            HideKeyboard(_consoleInput);
        }
    }

    private async Task<bool> SendActionAsync(
        AdminActionCode action,
        int slot = -1,
        int value = 0,
        string? text = null,
        string? logLine = null)
    {
        UdpVoiceReceiver? receiver = _receiver;
        if (receiver?.CurrentSession?.Authenticated != true)
        {
            ToastMessage("Connect and authenticate first.");
            return false;
        }

        try
        {
            bool sent = await receiver.SendAdminActionAsync(
                action,
                slot,
                value,
                text);
            if (sent)
            {
                if (action == AdminActionCode.ChangeMap)
                {
                    _healthProbesPausedUntilUtc =
                        DateTime.UtcNow.AddSeconds(20);
                    Interlocked.Exchange(ref _consecutiveHealthSendFailures, 0);
                }
                AppendConsole(logLine ?? $"REQUEST: {ActionName((uint)action)}");
            }
            else
                ToastMessage("The server action was not sent.");
            return sent;
        }
        catch (Exception exception)
        {
            ShowError(ActionName((uint)action), exception.Message);
            return false;
        }
    }

    private void ShowPlayerActions(MobilePlayer player)
    {
        var labels = new List<string> { "View identity" };
        var actions = new List<Action> { () => ShowPlayerIdentity(player) };

        if (!player.SourceTv && _receiver?.Can(AdminPermission.ModeratePlayers) == true)
        {
            Add("Give weapon or item", () => ShowGiveItemSelector(player));
            Add("Respawn", () => _ = SendActionAsync(AdminActionCode.Respawn, player.Slot));
            Add("Slay", () => ConfirmPlayerAction(player, AdminActionCode.Slay, "Slay"));
            Add("Move to Terrorists", () => _ = SendActionAsync(AdminActionCode.MoveToT, player.Slot));
            Add("Move to Counter-Terrorists", () => _ = SendActionAsync(AdminActionCode.MoveToCT, player.Slot));
            Add("Move to Spectator", () => _ = SendActionAsync(AdminActionCode.MoveToSpectator, player.Slot));
            Add("Kick", () => ConfirmPlayerAction(player, AdminActionCode.Kick, "Kick"));
        }

        new AlertDialog.Builder(this)
            .SetTitle(player.Name)
            .SetItems(labels.ToArray(), (_, args) => actions[args.Which]())
            .SetNegativeButton("CLOSE", (_, _) => { })
            .Show();
        return;

        void Add(string label, Action action)
        {
            labels.Add(label);
            actions.Add(action);
        }
    }

    private void ShowPlayerIdentity(MobilePlayer player)
    {
        string details =
            $"SteamID64: {player.Identity}\n" +
            $"Slot: {player.Slot}\n" +
            $"Team: {player.TeamName}\n" +
            $"State: {(player.Alive ? $"Alive, HP {player.Health}" : "Not alive")}";
        new AlertDialog.Builder(this)
            .SetTitle(player.Name)
            .SetMessage(details)
            .SetPositiveButton("CLOSE", (_, _) => { })
            .Show();
    }

    private void ConfirmPlayerAction(
        MobilePlayer player,
        AdminActionCode action,
        string verb)
    {
        new AlertDialog.Builder(this)
            .SetTitle($"{verb} {player.Name}?")
            .SetMessage($"Target slot {player.Slot}.")
            .SetNegativeButton("CANCEL", (_, _) => { })
            .SetPositiveButton(verb.ToUpperInvariant(), (_, _) =>
                _ = SendActionAsync(action, player.Slot))
            .Show();
    }

    private void ShowGiveItemSelector(MobilePlayer player)
    {
        AdminGiveItem[] items = AdminGiveItemCatalog.Categories
            .SelectMany(category => category.Items)
            .ToArray();
        string[] labels = AdminGiveItemCatalog.Categories
            .SelectMany(category => category.Items.Select(item =>
                $"{category.Name} | {item.Name}"))
            .ToArray();

        new AlertDialog.Builder(this)
            .SetTitle($"Give item to {player.Name}")
            .SetItems(labels, (_, args) =>
            {
                AdminGiveItem item = items[args.Which];
                _ = SendActionAsync(
                    AdminActionCode.GiveItem,
                    player.Slot,
                    text: item.EntityClass,
                    logLine: $"GIVE: {item.Name} -> {player.Name}");
            })
            .SetNegativeButton("CANCEL", (_, _) => { })
            .Show();
    }

    private async Task ShowMapSelectorAsync()
    {
        if (_receiver?.Can(AdminPermission.ChangeMap) != true)
        {
            ToastMessage("Change Map permission is required.");
            return;
        }
        if (_maps.Count == 0)
        {
            _mapsRequested = true;
            await SendActionAsync(AdminActionCode.RequestMapCatalog);
            ToastMessage("Refreshing the server map list.");
            return;
        }

        string[] maps = _maps.ToArray();
        new AlertDialog.Builder(this)
            .SetTitle("Select map")
            .SetItems(maps, (_, args) =>
                _ = SendActionAsync(
                    AdminActionCode.ChangeMap,
                    -1,
                    text: maps[args.Which],
                    logLine: $"CHANGE MAP: {maps[args.Which]}"))
            .SetNeutralButton("REFRESH", (_, _) =>
                _ = SendActionAsync(AdminActionCode.RequestMapCatalog))
            .SetNegativeButton("CANCEL", (_, _) => { })
            .Show();
    }

    private void ShowConnectionSettings()
    {
        if (_connectionEditorVisible)
            return;
        _connectionEditorVisible = true;

        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(20), Dp(4), Dp(20), Dp(4));

        EditText operatorName = AddField(content, "YOUR NAME", _profile.OperatorName);
        EditText name = AddField(content, "SERVER NAME", _profile.ServerName);
        EditText address = AddField(
            content,
            "SERVER PUBLIC IPV4 OR DNS NAME",
            _profile.ServerAddress);
        EditText port = AddField(content, "UDP PORT", _profile.ServerPort.ToString(), numeric: true);
        EditText accessKey = AddField(content, "ACCESS KEY", _profile.AccessKey, password: true);
        var autoConnect = new CheckBox(this)
        {
            Text = "Connect automatically",
            Checked = _profile.AutoConnect,
        };
        autoConnect.SetTextColor(TextPrimary);
        content.AddView(autoConnect);

        var scroll = new ScrollView(this);
        scroll.AddView(content);
        AlertDialog dialog = new AlertDialog.Builder(this)
            .SetTitle("Server connection")
            .SetView(scroll)
            .SetNegativeButton("CANCEL", (_, _) => { })
            .SetNeutralButton("INITIAL SETUP", (_, _) => ShowInitialSetup())
            .SetPositiveButton("SAVE", (_, _) =>
            {
                try
                {
                    _profile = new MobileAdminProfile
                    {
                        ServerName = name.Text?.Trim() ?? "CS2 Server",
                        ServerAddress = NormalizeServerAddress(address.Text),
                        ServerPort = ParsePort(port.Text),
                        OperatorName = operatorName.Text?.Trim() ?? string.Empty,
                        AdminId = _profile.AdminId,
                        AccessKey = accessKey.Text?.Trim() ?? string.Empty,
                        AutoConnect = autoConnect.Checked,
                        MutePlayerAudio = _profile.MutePlayerAudio,
                        MicrophoneGainPercent = _profile.MicrophoneGainPercent,
                    };
                    if (!_profile.IsComplete)
                        throw new InvalidDataException("Enter your name, a valid server, UDP port, and access key.");
                    MobileProfileStore.Save(this, _profile);
                    _resumeConnectionRequested = true;
                    CancelAutomaticReconnect();
                    UpdateConnectionUi();
                    _ = ConnectAsync();
                }
                catch (Exception exception)
                {
                    ShowError("Invalid connection", exception.Message);
                }
            })
            .Create();
        dialog.DismissEvent += (_, _) => _connectionEditorVisible = false;
        dialog.Show();
    }

    private void ShowInitialSetup()
    {
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(20), Dp(4), Dp(20), Dp(4));
        EditText address = AddField(
            content,
            "SERVER PUBLIC IPV4 OR DNS NAME",
            _profile.ServerAddress);
        EditText port = AddField(content, "UDP PORT", _profile.ServerPort.ToString(), numeric: true);
        EditText displayName = AddField(content, "DISPLAY NAME", "Owner");
        EditText accountId = AddField(content, "ADMIN ID", string.IsNullOrWhiteSpace(_profile.AdminId) ? "owner" : _profile.AdminId);
        EditText setupCode = AddField(content, "24-CHARACTER SETUP CODE", string.Empty);

        var scroll = new ScrollView(this);
        scroll.AddView(content);
        new AlertDialog.Builder(this)
            .SetTitle("Initial Owner setup")
            .SetView(scroll)
            .SetNegativeButton("CANCEL", (_, _) => { })
            .SetPositiveButton("CREATE OWNER", async (_, _) =>
            {
                ProgressDialog? progress = null;
                try
                {
                    progress = ProgressDialog.Show(
                        this,
                        "Initial Owner setup",
                        "Waiting for the CS2 server...",
                        true,
                        false);
                    FirstOwnerSetupResult result = await FirstOwnerSetupClient.ClaimAsync(
                        address.Text ?? string.Empty,
                        ParsePort(port.Text),
                        displayName.Text ?? string.Empty,
                        accountId.Text ?? string.Empty,
                        setupCode.Text ?? string.Empty,
                        _lifetime.Token);
                    _profile = new MobileAdminProfile
                    {
                        ServerName = result.ServerAddress,
                        ServerAddress = result.ServerAddress,
                        ServerPort = result.ServerPort,
                        OperatorName = result.DisplayName,
                        AdminId = result.AccountId,
                        AccessKey = result.AccessKey,
                        AutoConnect = true,
                        MutePlayerAudio = _profile.MutePlayerAudio,
                        MicrophoneGainPercent = _profile.MicrophoneGainPercent,
                    };
                    MobileProfileStore.Save(this, _profile);
                    _resumeConnectionRequested = true;
                    CancelAutomaticReconnect();
                    await ConnectAsync();
                    ShowMessage("Owner created", "The Owner access profile was saved in this app.");
                }
                catch (Exception exception)
                {
                    ShowError("Initial setup failed", exception.Message);
                }
                finally
                {
                    progress?.Dismiss();
                }
            })
            .Show();
    }

    private void UpdateConnectionUi(string? detail = null)
    {
        bool target = _receiver?.HasServerTarget == true;
        bool authenticated =
            !_connecting &&
            !_transportStale &&
            _receiver?.CurrentSession?.Authenticated == true;
        string status = detail ?? (authenticated
            ? $"{_profile.ServerName} | Authenticated" +
              (string.IsNullOrWhiteSpace(_networkRoute) ? string.Empty : $" | {_networkRoute}")
            : target
                ? $"{_profile.ServerName} | Waiting for authentication"
                : _profile.IsComplete
                    ? $"{_profile.ServerName} | {_profile.ServerAddress}:{_profile.ServerPort}"
                    : "Server not configured");
        _connectionPrimaryStatus = status;
        UpdateServerContextStatus();
        _connectionStatus.SetTextColor(authenticated ? Teal : target ? Amber : TextSecondary);
        _connectButton.Text = authenticated ? "DISCONNECT" : "CONNECT";
        _connectButton.Enabled = !_connecting;

        _authenticatedShell.Visibility = authenticated
            ? ViewStates.Visible
            : ViewStates.Gone;
        _disconnectedShell.Visibility = authenticated
            ? ViewStates.Gone
            : ViewStates.Visible;

        _disconnectedServerName.Text = _profile.IsComplete
            ? _profile.ServerName
            : "Server not configured";
        _disconnectedEndpoint.Text = _profile.IsComplete
            ? $"{_profile.ServerAddress}:{_profile.ServerPort}"
            : "Server information required";
        _disconnectedStatus.Text = _connecting
            ? "Connecting and waiting for authentication..."
            : status;
        _disconnectedStatus.SetTextColor(target ? Amber : TextSecondary);
        _disconnectedConnectButton.Text = _connecting ? "CONNECTING..." : "CONNECT";
        _disconnectedConnectButton.Enabled = !_connecting;
    }

    private bool PrepareNetworkRoute(out string error)
    {
        error = string.Empty;
        ReleaseNetworkBinding();

        if (!IPAddress.TryParse(_profile.ServerAddress, out IPAddress? address) ||
            !IsPrivateAddress(address))
        {
            _networkRoute = "default network";
            return true;
        }

        var manager = GetSystemService(ConnectivityService) as ConnectivityManager;
        if (manager is null)
        {
            error = "Android could not access the network service.";
            return false;
        }
        _connectivityManager = manager;

        Android.Net.Network? wifi = manager.GetAllNetworks()
            .FirstOrDefault(network =>
                manager.GetNetworkCapabilities(network)?
                    .HasTransport(TransportType.Wifi) == true);
        if (wifi is null)
        {
            error =
                "This is a private LAN server. Connect the phone to the same Wi-Fi " +
                "as the CS2 server, then try again.";
            return false;
        }

        if (!manager.BindProcessToNetwork(wifi))
        {
            error = "Android could not route NEO ADMIN through Wi-Fi.";
            return false;
        }

        string? localAddress = manager.GetLinkProperties(wifi)?
            .LinkAddresses
            .Select(link => link.Address)
            .OfType<Java.Net.Inet4Address>()
            .Select(ip => ip.HostAddress)
            .FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip));
        _networkRoute = string.IsNullOrWhiteSpace(localAddress)
            ? "Wi-Fi"
            : $"Wi-Fi {localAddress}";
        AppendConsole($"NETWORK: {_networkRoute} -> {_profile.ServerAddress}");
        return true;
    }

    private void ReleaseNetworkBinding()
    {
        _connectivityManager?.BindProcessToNetwork(null);
        _connectivityManager = null;
        _networkRoute = string.Empty;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
            (bytes[0] == 10 ||
             bytes[0] == 127 ||
             bytes[0] == 192 && bytes[1] == 168 ||
             bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
             bytes[0] == 169 && bytes[1] == 254);
    }

    private void UpdatePermissionUi()
    {
        UdpVoiceReceiver? receiver = _receiver;
        bool authenticated =
            !_transportStale &&
            receiver?.CurrentSession?.Authenticated == true;
        bool canChat = authenticated && receiver!.Can(AdminPermission.SendChat);
        bool canUseConsole = authenticated && receiver!.Can(AdminPermission.RunServerConsole);
        bool canBroadcastVoice =
            authenticated && receiver!.Can(AdminPermission.BroadcastVoice);
        SetEnabledIfChanged(_chatSendButton, canChat);
        SetEnabledIfChanged(_chatInput, canChat);
        SetEnabledIfChanged(_consoleSendButton, canUseConsole);
        SetEnabledIfChanged(_consoleInput, canUseConsole);
        SetEnabledIfChanged(_pushToTalkButton, canBroadcastVoice);
        if (!canBroadcastVoice && _pttCapture.IsRunning)
            StopPushToTalk();
        SetEnabledIfChanged(
            _mapButton,
            authenticated && receiver!.Can(AdminPermission.ChangeMap));
        foreach (Button button in _matchButtons)
            SetEnabledIfChanged(
                button,
                authenticated && receiver!.Can(AdminPermission.ControlMatch));
        foreach (Button button in _botButtons)
            SetEnabledIfChanged(
                button,
                authenticated && receiver!.Can(AdminPermission.ControlBots));
        _liveMapView.SetTeleportEnabled(
            authenticated && receiver!.Can(AdminPermission.TeleportPlayers));
        UpdateVoiceStatus();
    }

    private async void OnMapPlayerDragTeleport(
        MobileMapMarker player,
        float x,
        float y,
        float z,
        bool final)
    {
        UdpVoiceReceiver? receiver = _receiver;
        if (_transportStale ||
            receiver?.Can(AdminPermission.TeleportPlayers) != true)
        {
            _liveMapView.SetTeleportEnabled(false);
            _liveMapStatus.Text =
                "Player drag requires an active session with Teleport Players permission.";
            return;
        }

        try
        {
            bool sent = await receiver.SendTeleportAsync(
                player.SteamId,
                player.Slot,
                x,
                y,
                z);
            if (!sent)
            {
                _liveMapStatus.Text = $"Could not move {player.Name}; the server did not accept the packet.";
            }
            else if (final)
            {
                _liveMapStatus.Text = $"Moved {player.Name} to a safe map position.";
                AppendConsole(
                    $"MOVE PLAYER: {player.Name} -> {x:0.0}, {y:0.0}, {z:0.0}");
            }
        }
        catch (Exception exception)
        {
            _liveMapStatus.Text = $"Could not move {player.Name}: {exception.Message}";
        }
    }

    private void AppendChat(string sender, string message)
    {
        string cleanSender = string.IsNullOrWhiteSpace(sender) ? "SERVER" : sender.Trim();
        string cleanMessage = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (cleanMessage.Length == 0)
            return;
        bool restoreComposerFocus = _chatInput.HasFocus;
        int cursor = _chatInput.SelectionStart;
        AppendBounded(_chatHistory, $"[{DateTime.Now:HH:mm:ss}] {cleanSender}: {cleanMessage}", 50_000);
        _chatScroll.Post(() =>
        {
            _chatScroll.ScrollTo(0, Math.Max(0, _chatHistory.Height - _chatScroll.Height));
            if (restoreComposerFocus && !_chatInput.HasFocus)
            {
                _chatInput.RequestFocus();
                _chatInput.SetSelection(Math.Clamp(cursor, 0, _chatInput.Text?.Length ?? 0));
            }
        });
    }

    private void AppendConsole(string message)
    {
        string clean = message.Replace('\r', ' ').Trim();
        if (clean.Length == 0)
            return;
        AppendBounded(_consoleHistory, $"[{DateTime.Now:HH:mm:ss}] {clean}", 80_000);
        _consoleScroll.Post(() =>
            _consoleScroll.ScrollTo(
                0,
                Math.Max(0, _consoleHistory.Height - _consoleScroll.Height)));
    }

    private static void SetEnabledIfChanged(View view, bool enabled)
    {
        if (view.Enabled != enabled)
            view.Enabled = enabled;
    }

    private static void AppendBounded(TextView view, string line, int maxCharacters)
    {
        string current = view.Text ?? string.Empty;
        if (current.Length > maxCharacters)
            current = current[^Math.Min(current.Length, maxCharacters / 2)..];
        view.Text = current.Length == 0 ? line : current + Environment.NewLine + line;
    }

    private Button MakeActionButton(
        string text,
        AdminActionCode action,
        List<Button> group,
        int value = 0,
        Color? color = null)
    {
        Button button = MakeButton(text, color ?? SurfaceRaised, TextPrimary);
        button.Click += async (_, _) => await SendActionAsync(action, -1, value);
        group.Add(button);
        return button;
    }

    private LinearLayout BuildButtonRow(Button left, Button right)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var leftLayout = new LinearLayout.LayoutParams(0, Dp(50), 1f)
        {
            RightMargin = Dp(4),
            TopMargin = Dp(4),
            BottomMargin = Dp(4),
        };
        var rightLayout = new LinearLayout.LayoutParams(0, Dp(50), 1f)
        {
            LeftMargin = Dp(4),
            TopMargin = Dp(4),
            BottomMargin = Dp(4),
        };
        row.AddView(left, leftLayout);
        row.AddView(right, rightLayout);
        return row;
    }

    private LinearLayout PageContainer(int padding = 0)
    {
        var page = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        page.SetPadding(padding, padding, padding, padding);
        page.SetBackgroundColor(AppBackground);
        return page;
    }

    private TextView MakeSectionHeader(string text)
    {
        TextView header = MakeText(text, 13, TextSecondary, true);
        header.SetPadding(0, Dp(14), 0, Dp(6));
        return header;
    }

    private TextView MakeText(
        string text,
        float size,
        Color color,
        bool bold = false)
    {
        var view = new TextView(this)
        {
            Text = text,
            TextSize = size,
            Gravity = GravityFlags.CenterVertical,
            Typeface = bold ? Typeface.DefaultBold : Typeface.Default,
        };
        view.SetTextColor(color);
        return view;
    }

    private Button MakeButton(string text, Color background, Color foreground)
    {
        var button = new Button(this)
        {
            Text = text,
            TextSize = 12,
            StateListAnimator = null,
        };
        button.SetMinWidth(0);
        button.SetMinHeight(0);
        button.SetTextColor(foreground);
        button.BackgroundTintList = ColorStateList.ValueOf(background);
        return button;
    }

    private EditText MakeInput(string hint, bool password)
    {
        var input = new EditText(this)
        {
            Hint = hint,
            TextSize = 15,
        };
        input.SetSingleLine(true);
        input.InputType = password
            ? InputTypes.ClassText | InputTypes.TextVariationPassword
            : InputTypes.ClassText;
        if (password)
            input.TransformationMethod = PasswordTransformationMethod.Instance;
        input.SetTextColor(TextPrimary);
        input.SetHintTextColor(TextSecondary);
        input.BackgroundTintList = ColorStateList.ValueOf(Teal);
        return input;
    }

    private EditText AddField(
        LinearLayout parent,
        string label,
        string value,
        bool password = false,
        bool numeric = false)
    {
        TextView caption = MakeText(label, 11, TextSecondary, true);
        caption.SetPadding(0, Dp(8), 0, 0);
        parent.AddView(caption);
        EditText input = MakeInput(string.Empty, password);
        input.Text = value;
        if (numeric)
            input.InputType = InputTypes.ClassNumber;
        parent.AddView(input, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            Dp(48)));
        return input;
    }

    private LinearLayout.LayoutParams FullWidthButton() =>
        new(ViewGroup.LayoutParams.MatchParent, Dp(50))
        {
            TopMargin = Dp(4),
            BottomMargin = Dp(4),
        };

    private static ViewGroup.LayoutParams MatchParent() =>
        new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent);

    private static string PlayerKey(VoicePacket packet) =>
        packet.SteamId != 0
            ? $"steam:{packet.SteamId}"
            : $"slot:{packet.PlayerSlot}";

    private static bool IsSourceTv(string name) =>
        name.Contains("SourceTV", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GOTV", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlayableMap(string map)
    {
        if (map.Length is < 4 or > 96 || map.Contains('/') || map.Contains('\\'))
            return false;
        return map.StartsWith("de_", StringComparison.OrdinalIgnoreCase) ||
               map.StartsWith("cs_", StringComparison.OrdinalIgnoreCase) ||
               map.StartsWith("ar_", StringComparison.OrdinalIgnoreCase) ||
               map.StartsWith("aim_", StringComparison.OrdinalIgnoreCase) ||
               map.StartsWith("awp_", StringComparison.OrdinalIgnoreCase) ||
               map.StartsWith("fy_", StringComparison.OrdinalIgnoreCase) ||
               map.StartsWith("ka_", StringComparison.OrdinalIgnoreCase) ||
               map.StartsWith("kz_", StringComparison.OrdinalIgnoreCase) ||
               map.StartsWith("surf_", StringComparison.OrdinalIgnoreCase) ||
               map.StartsWith("workshop_", StringComparison.OrdinalIgnoreCase) ||
               map.StartsWith("zm_", StringComparison.OrdinalIgnoreCase) ||
               map.StartsWith("ze_", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParsePort(string? text)
    {
        if (!int.TryParse(text, out int value) || value is < 1 or > 65535)
            throw new InvalidDataException("UDP port must be between 1 and 65535.");
        return value;
    }

    private static string NormalizeServerAddress(string? text)
    {
        string address = text?.Trim() ?? string.Empty;
        int separator = address.LastIndexOf(':');
        if (separator <= 0 || separator != address.IndexOf(':'))
            return address;

        string host = address[..separator].Trim();
        string port = address[(separator + 1)..].Trim();
        if (!int.TryParse(port, out int parsedPort) ||
            parsedPort is < 1 or > 65535)
        {
            return address;
        }

        return IPAddress.TryParse(host, out _) ||
               System.Uri.CheckHostName(host) == UriHostNameType.Dns
            ? host
            : address;
    }

    private static string ActionName(uint action) => (AdminActionCode)action switch
    {
        AdminActionCode.Kick => "Kick",
        AdminActionCode.Slay => "Slay",
        AdminActionCode.Respawn => "Respawn",
        AdminActionCode.MoveToT => "Move to Terrorists",
        AdminActionCode.MoveToCT => "Move to Counter-Terrorists",
        AdminActionCode.MoveToSpectator => "Move to Spectator",
        AdminActionCode.GiveItem => "Give item",
        AdminActionCode.ChangeMap => "Change map",
        AdminActionCode.RestartRound => "Restart round",
        AdminActionCode.RestartMatch => "Restart match",
        AdminActionCode.EndWarmup => "End warmup",
        AdminActionCode.PauseMatch => "Pause match",
        AdminActionCode.UnpauseMatch => "Unpause match",
        AdminActionCode.SwapTeams => "Swap teams",
        AdminActionCode.AddBot => "Add bot",
        AdminActionCode.RemoveBots => "Kick bots",
        AdminActionCode.RequestMapCatalog => "Refresh maps",
        AdminActionCode.RequestServerHealth => "Server health",
        AdminActionCode.RequestMapOverview => "Map overview",
        AdminActionCode.RunServerConsole => "Server console",
        _ => $"Admin action {action}",
    };

    private sealed class SystemBarInsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                Android.Graphics.Insets bars = insets.GetInsets(WindowInsets.Type.SystemBars());
                Android.Graphics.Insets keyboard = insets.GetInsets(WindowInsets.Type.Ime());
                view.SetPadding(
                    bars.Left,
                    bars.Top,
                    bars.Right,
                    Math.Max(bars.Bottom, keyboard.Bottom));
            }
            else
            {
#pragma warning disable CA1422
                view.SetPadding(
                    insets.SystemWindowInsetLeft,
                    insets.SystemWindowInsetTop,
                    insets.SystemWindowInsetRight,
                    insets.SystemWindowInsetBottom);
#pragma warning restore CA1422
            }

            return insets;
        }
    }

    private void HideKeyboard(View view)
    {
        var manager = (InputMethodManager?)GetSystemService(InputMethodService);
        manager?.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
        view.ClearFocus();
    }

    private void ToastMessage(string message) =>
        Toast.MakeText(this, message, ToastLength.Short)?.Show();

    private void ShowMessage(string title, string message) =>
        new AlertDialog.Builder(this)
            .SetTitle(title)
            .SetMessage(message)
            .SetPositiveButton("OK", (_, _) => { })
            .Show();

    private void ShowError(string title, string message) =>
        new AlertDialog.Builder(this)
            .SetTitle(title)
            .SetMessage(message)
            .SetPositiveButton("OK", (_, _) => { })
            .Show();
}
