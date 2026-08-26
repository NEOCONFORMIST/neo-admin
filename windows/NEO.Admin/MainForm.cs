using System.Diagnostics;
using System.Net;

namespace NeoAdmin;

internal sealed class MainForm : NeoForm
{
    private static readonly TimeSpan SpeakingHoldTime =
        TimeSpan.FromMilliseconds(550);

    // Position/state packets are continuous while a game player exists.
    // If one disappears without a disconnect packet (for example during
    // bot churn or a level transition), remove its stale row automatically.
    private static readonly TimeSpan PlayerStaleTimeout =
        TimeSpan.FromSeconds(5);

    // A requested map can take several seconds to start loading. Once the
    // server reports the new level, its presence snapshot follows immediately.
    private static readonly TimeSpan RosterCommandHoldTime =
        TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RosterSettlingTime =
        TimeSpan.FromSeconds(3);

    private enum SteamIdDisplayFormat
    {
        Steam2,
        SteamId64,
    }

    private readonly AppConfig _config;
    private readonly UdpVoiceReceiver _receiver;
    private readonly AudioMixer _audio;
    private readonly Label _statusLabel = new();
    private readonly Label _packetLabel = new();
    private readonly Label _playerCountLabel = new();
    private readonly Label _recordingLabel = new();
    private readonly Label _healthQualityLabel = new();
    private readonly Label _healthTickRateLabel = new();
    private readonly Label _healthPlayersLabel = new();
    private readonly Label _healthMapUptimeLabel = new();
    private readonly Label _healthPingLabel = new();
    private readonly Label _healthPacketLossLabel = new();
    private readonly Label _healthCpuLabel = new();
    private readonly Label _healthMemoryLabel = new();
    private readonly Label _healthVersionLabel = new();
    private readonly DataGridView _playersGrid = new();
    private readonly MapOverviewControl _mapOverview = new();
    private readonly TrackBar _volume = new();
    private readonly Button _startRecordingButton = new();
    private readonly Button _stopRecordingButton = new();
    private readonly Button _pushToTalkButton = new(); // NEO ADMIN PTT v2 SERVER BROADCAST
    private readonly Label _pttTargetLabel = new();
    private readonly CheckBox _pttToggleCheckBox = new();
    private readonly ComboBox _microphoneDeviceBox = new();
    private readonly RichTextBox _serverChatHistory = new();
    private readonly TextBox _serverChatInput = new();
    private readonly Button _serverChatSendButton = new();
    private readonly RichTextBox _pluginConsoleHistory = new();
    private readonly RichTextBox _serverConsoleHistory = new();
    private readonly TextBox _serverConsoleInput = new();
    private readonly Button _serverConsoleExecuteButton = new();
    private readonly Button _serverConsoleClearButton = new();
    private readonly List<string> _serverConsoleCommandHistory = new();
    private int _serverConsoleHistoryIndex = -1;
    private readonly ContextMenuStrip _playerAdminMenu = new();
    private readonly ToolStripMenuItem _playerAdminHeaderItem =
        new("PLAYER ADMIN");
    private readonly ToolStripMenuItem _playerInspectorMenuItem =
        new("Inspect Steam ID...");
    private readonly ToolStripMenuItem _giveItemMenuItem =
        new("Give weapon or item");
    private readonly ToolStripMenuItem _banPlayerMenuItem =
        new("Ban...");
    private readonly ToolStripMenuItem _mutePlayerMenuItem = new("Mute...");
    private readonly ToolStripMenuItem _gagPlayerMenuItem = new("Gag...");
    private readonly ToolStripMenuItem _disciplineHistoryMenuItem =
        new("Discipline History...");

    // NEO SERVER CONTROL STAGE 3U
    private readonly Label _serverMapDisplay = new();
    private readonly Button _changeMapButton = new();
    private readonly Button _restartRoundButton = new();
    private readonly Button _restartMatchButton = new();
    private readonly Button _endWarmupButton = new();
    private readonly Button _pauseMatchButton = new();
    private readonly Button _unpauseMatchButton = new();
    private readonly Button _swapTeamsButton = new();
    private readonly Button _addTBotButton = new();
    private readonly Button _addCtBotButton = new();
    private readonly Button _kickBotsButton = new();
    private readonly Label _zombieModeStatusLabel = new();
    private readonly CheckBox _zombieModeToggle = new();
    private bool _zombieModeEnabled;
    private bool _zombieModeStateKnown;
    private bool _zombieModeRequestInFlight;
    private bool _updatingZombieModeToggle;

    // NEO MAP CATALOG STAGE 3V
    private readonly List<string> _serverMapCatalog = new();
    private bool _openMapWindowWhenCatalogArrives;

    // User-selectable server connection.
    private readonly ToolStripTextBox _serverAddressBox = new();
    private readonly ToolStripTextBox _serverPttPortBox = new();
    private readonly ToolStripComboBox _serverProfileBox = new();
    private readonly ToolStripButton _connectServerButton = new();
    private readonly ToolStripLabel _serverConnectionLabel = new();
    private readonly ToolStripLabel _adminSessionLabel = new();
    private readonly ToolStripMenuItem _adminAccountsMenuItem =
        new("Administrator &Accounts...");
    private readonly ToolStripMenuItem _inGameAdminsMenuItem =
        new("&In-Game Administrators...");
    private readonly ToolStripMenuItem _auditLogMenuItem =
        new("Administrator Audit &Log...");
    private readonly ToolStripMenuItem _banManagementMenuItem =
        new("&Ban Management...");
    private readonly ToolStripMenuItem _disciplineManagementMenuItem =
        new("&Mute and Gag Management...");
    private readonly ToolStripMenuItem _mapRotationMenuItem =
        new("Map &Rotation Manager...");
    private readonly ToolStripMenuItem _announcementsMenuItem =
        new("Admin &Announcements...");
    private readonly ToolStripMenuItem _workshopMapsMenuItem =
        new("&Workshop Map Manager...");
    private bool _updatingServerProfileBox;

    private readonly ServerConnectionSettings
        _serverConnectionSettings =
            ServerConnectionSettings.Load();

    private readonly AdminPttCapture _pttCapture = new();
    private readonly Dictionary<string, PlayerState> _players = new();
    private readonly System.Windows.Forms.Timer _activityTimer = new();
    private readonly System.Windows.Forms.Timer _serverHealthTimer = new();

    private readonly ToolStripMenuItem _steam2MenuItem =
        new("Steam2 (Old: STEAM_0:X:Y)");
    private readonly ToolStripMenuItem _steamId64MenuItem =
        new("SteamID64 (New: 7656...)");
    private readonly ToolStripMenuItem _startRecordingMenuItem =
        new("Start Recording...");
    private readonly ToolStripMenuItem _stopRecordingMenuItem =
        new("Stop Recording");

    private SteamIdDisplayFormat _steamIdDisplayFormat =
        SteamIdDisplayFormat.Steam2;
    private long _packetCount;
    private bool _closing;
    private DateTime _rosterTransitionUntilUtc = DateTime.MinValue;
    private bool _rosterAwaitingMapStart;
    private bool _rosterSettling;
    private bool _healthProbeInFlight;
    private DateTime _lastServerHealthUtc = DateTime.MinValue;

    public MainForm(AppConfig config)
    {
        _config = config;
        _receiver = new UdpVoiceReceiver(config);
        _audio = new AudioMixer(config.MasterVolume);

        if (!string.IsNullOrWhiteSpace(_serverConnectionSettings.AdminId) &&
            _serverConnectionSettings.AccessKey.Length >= 16)
        {
            _receiver.SetAdminCredentials(
                _serverConnectionSettings.AdminId,
                _serverConnectionSettings.AccessKey);
        }

        Text = "NEO ADMIN";
        Width = 1500;
        Height = 900;
        MinimumSize = new Size(1100, 650);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;

        BuildUi();
        PopulateMicrophoneDeviceSelector();
        _microphoneDeviceBox.SelectedIndexChanged +=
            OnMicrophoneDeviceChanged;

        _receiver.StatusChanged += OnReceiverStatus;
        _receiver.PacketReceived += OnPacketReceived;
        _receiver.AdminSessionChanged += OnAdminSessionChanged;
        _mapOverview.PlayerDragTeleport += OnMapPlayerDragTeleport;
        _mapOverview.InteractionStatus += message =>
            _statusLabel.Text = message;
        _audio.DecodeError += OnDecodeError;
        _audio.RecordingError += OnRecordingError;
        _pttCapture.OpusFrameReady += OnPttOpusFrameReady;
        _pttCapture.CaptureError += OnPttCaptureError;

        _activityTimer.Interval = 100;
        _activityTimer.Tick += (_, _) =>
        {
            RefreshSpeakingIndicators();
            PruneStalePlayers();
            RefreshServerHealthFreshness();
        };

        _serverHealthTimer.Interval = 2000;
        _serverHealthTimer.Tick += async (_, _) =>
            await RequestServerHealthAsync();

        Shown += async (_, _) =>
        {
            BeginInvoke((Action)(() =>
                NeoTheme.RefreshToolStripComboBox(_serverProfileBox)));
            _activityTimer.Start();
            if (_config.EnableServerHealthPanel)
                _serverHealthTimer.Start();
            _receiver.Start();

            _connectServerButton.Text =
                "CONNECT";

            _serverConnectionLabel.Text =
                "Server: disconnected";

            _serverConnectionLabel.ForeColor =
                NeoTheme.Danger;

            _statusLabel.Text = _receiver.HasAdminCredentials
                ? "Disconnected - press CONNECT to connect."
                : "No administrator access is configured. " +
                    "Use Settings > Initial Server Setup for a fresh server, " +
                    "or import an access profile.";

            UpdatePttTargetLabel();
            UpdateServerChatUi();
            ApplyAdminSession(null);
            ResetServerHealthDisplay("DISCONNECTED");

            if (!string.IsNullOrWhiteSpace(
                    _serverConnectionSettings.ServerAddress) &&
                _receiver.HasAdminCredentials)
            {
                await ConnectServerFromUiAsync(false);
            }
        };

        Deactivate += (_, _) =>
        {
            // HOLD mode stops if the window loses focus.
            // TOGGLE mode deliberately remains transmitting
            // while the admin uses CS2 or another window.
            if (!_pttToggleCheckBox.Checked)
                StopPushToTalk();
        };
        FormClosing += OnFormClosingAsync;
    }

    private void ConfigureServerConnectionUi(
        ToolStrip connectionBar)
    {
        _serverProfileBox.AutoSize = false;
        _serverProfileBox.Width = 150;
        _serverProfileBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _serverProfileBox.ToolTipText = "Active CS2 server";
        _serverProfileBox.SelectedIndexChanged += async (_, _) =>
        {
            if (!_updatingServerProfileBox &&
                _serverProfileBox.SelectedItem is ServerProfile profile)
            {
                await SelectServerProfileAsync(profile.Id, true);
            }
        };
        RefreshServerProfileSelector();

        _serverAddressBox.AutoSize = false;
        _serverAddressBox.Width = 220;
        _serverAddressBox.Text =
            _serverConnectionSettings.ServerAddress;
        _serverAddressBox.ToolTipText =
            "Server LAN IP, public IP, or DNS hostname";

        _serverPttPortBox.AutoSize = false;
        _serverPttPortBox.Width = 60;
        _serverPttPortBox.Text =
            _serverConnectionSettings.ServerPttPort
                .ToString();
        _serverPttPortBox.ToolTipText =
            "NEO PTT UDP port";

        _connectServerButton.Text = "CONNECT";
        _connectServerButton.ToolTipText =
            "Set this server as the active NEO ADMIN target";

        _serverConnectionLabel.Text =
            "Server: not configured";

        _serverConnectionLabel.ForeColor =
            NeoTheme.Danger;

        _adminSessionLabel.Text =
            $"Account: {_receiver.CurrentAdminId} (not signed in)";
        _adminSessionLabel.ForeColor = NeoTheme.Danger;

        _connectServerButton.Click +=
            async (_, _) =>
                await ToggleServerConnectionAsync();

        _serverAddressBox.KeyDown +=
            async (_, e) =>
            {
                if (e.KeyCode != Keys.Enter)
                    return;

                e.SuppressKeyPress = true;
                await ConnectServerFromUiAsync(true);
            };

        _serverPttPortBox.KeyDown +=
            async (_, e) =>
            {
                if (e.KeyCode != Keys.Enter)
                    return;

                e.SuppressKeyPress = true;
                await ConnectServerFromUiAsync(true);
            };

        connectionBar.Items.Add(new ToolStripLabel("SERVER"));
        connectionBar.Items.Add(_serverProfileBox);
        connectionBar.Items.Add(new ToolStripSeparator());
        connectionBar.Items.Add(new ToolStripLabel("ADDRESS"));
        connectionBar.Items.Add(_serverAddressBox);
        connectionBar.Items.Add(new ToolStripLabel("PORT"));
        connectionBar.Items.Add(_serverPttPortBox);
        connectionBar.Items.Add(_connectServerButton);
        connectionBar.Items.Add(new ToolStripSeparator());
        connectionBar.Items.Add(_serverConnectionLabel);
        connectionBar.Items.Add(new ToolStripSeparator());
        connectionBar.Items.Add(_adminSessionLabel);
    }

    private void RefreshServerProfileSelector()
    {
        _updatingServerProfileBox = true;
        try
        {
            _serverProfileBox.Items.Clear();
            foreach (ServerProfile profile in _serverConnectionSettings.Servers)
                _serverProfileBox.Items.Add(profile);

            ServerProfile active = _serverConnectionSettings.ActiveServer;
            _serverProfileBox.SelectedItem = _serverProfileBox.Items
                .Cast<ServerProfile>()
                .FirstOrDefault(profile => profile.Id == active.Id);
        }
        finally
        {
            _updatingServerProfileBox = false;
        }
    }

    private async Task SelectServerProfileAsync(string profileId, bool connect)
    {
        if (_pttCapture.IsRunning)
            StopPushToTalk();
        if (_receiver.HasServerTarget)
            _receiver.DisconnectServer();

        _serverConnectionSettings.SetActive(profileId);
        ServerProfile profile = _serverConnectionSettings.ActiveServer;
        _serverConnectionSettings.Save();
        _receiver.SetAdminCredentials(profile.AdminId, profile.AccessKey);
        _serverAddressBox.Text = profile.ServerAddress;
        _serverPttPortBox.Text = profile.ServerPttPort.ToString();
        _connectServerButton.Text = "CONNECT";
        ClearPlayerRoster();
        _serverMapCatalog.Clear();
        _serverMapDisplay.Text = string.Empty;
        ApplyAdminSession(null);
        RefreshServerProfileSelector();

        if (connect && !string.IsNullOrWhiteSpace(profile.ServerAddress))
            await ConnectServerFromUiAsync(true);
    }

    private async Task ShowServerProfileManagerAsync()
    {
        using var manager = new ServerProfileManagerForm(
            _serverConnectionSettings.Servers,
            _serverConnectionSettings.ActiveServerId);
        if (manager.ShowDialog(this) != DialogResult.OK)
            return;

        _serverConnectionSettings.ReplaceServers(
            manager.Profiles,
            manager.ActiveServerId);
        await SelectServerProfileAsync(
            _serverConnectionSettings.ActiveServerId,
            true);
    }

    private async Task ToggleServerConnectionAsync()
    {
        if (_receiver.HasServerTarget)
        {
            _receiver.DisconnectServer();

            _connectServerButton.Text =
                "CONNECT";

            _serverConnectionLabel.Text =
                "Server: disconnected";

            _serverConnectionLabel.ForeColor =
                NeoTheme.Danger;

            _statusLabel.Text =
                "Disconnected - press CONNECT to reconnect.";

            ClearPlayerRoster();

            _serverMapCatalog.Clear();
            _serverMapDisplay.Text = string.Empty;
            _openMapWindowWhenCatalogArrives = false;
            ResetServerHealthDisplay("DISCONNECTED");

            ApplyAdminSession(null);

            UpdateServerChatUi();
            UpdatePttTargetLabel();
            return;
        }

        await ConnectServerFromUiAsync(true);
    }

    private async Task ConnectServerFromUiAsync(
        bool showErrorDialog)
    {
        string address =
            _serverAddressBox.Text.Trim();

        if (address.Length == 0)
        {
            _serverConnectionLabel.Text =
                "Server: enter an address";

            _serverConnectionLabel.ForeColor =
                NeoTheme.Danger;

            UpdatePttTargetLabel();
            return;
        }

        if (!int.TryParse(
                _serverPttPortBox.Text.Trim(),
                out int port) ||
            port is < 1 or > 65535)
        {
            const string message =
                "PTT port must be between 1 and 65535.";

            _serverConnectionLabel.Text =
                "Server: invalid port";

            _serverConnectionLabel.ForeColor =
                NeoTheme.Danger;

            if (showErrorDialog)
            {
                MessageBox.Show(
                    this,
                    message,
                    "Server Connection",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            UpdatePttTargetLabel();
            return;
        }

        _connectServerButton.Enabled = false;

        _serverConnectionLabel.Text =
            "Server: resolving...";
        ResetServerHealthDisplay("CONNECTING");

        _serverConnectionLabel.ForeColor =
            NeoTheme.MutedText;

        try
        {
            IPEndPoint endpoint =
                await _receiver.ConfigureServerAsync(
                    address,
                    port);

            _serverConnectionSettings.UpdateActiveConnection(address, port);

            try
            {
                _serverConnectionSettings.Save();
            }
            catch (Exception saveException)
            {
                _statusLabel.Text =
                    $"Server selected, but settings could not be saved: " +
                    saveException.Message;
            }

            _serverConnectionLabel.Text =
                $"Target: {endpoint.Address}:{endpoint.Port}";

            _serverConnectionLabel.ForeColor =
                NeoTheme.Success;

            _connectServerButton.Text =
                "DISCONNECT";

            _statusLabel.Text =
                $"Login sent: {address} -> " +
                $"{endpoint.Address}:{endpoint.Port}/UDP; " +
                "waiting for server reply";

            ClearPlayerRoster();

            _serverMapCatalog.Clear();
            _serverMapDisplay.Text = string.Empty;
            _openMapWindowWhenCatalogArrives = false;

            UpdateServerChatUi();
            ResetServerHealthDisplay("WAITING FOR SERVER");
        }
        catch (Exception exception)
        {
            _connectServerButton.Text =
                "CONNECT";

            _serverConnectionLabel.Text =
                "Server: connection setup failed";

            _serverConnectionLabel.ForeColor =
                NeoTheme.Danger;

            _statusLabel.Text =
                $"Server target error: {exception.Message}";
            ResetServerHealthDisplay("CONNECTION FAILED");

            if (showErrorDialog)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    "Server Connection",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            _connectServerButton.Enabled = true;
            UpdateServerChatUi();
            UpdatePttTargetLabel();
        }
    }

    private void BuildUi()
    {
        MenuStrip menu = BuildMenu();
        ToolStrip connectionBar = BuildConnectionBar();
        Control serverHealthPanel = BuildServerHealthPanel();
        serverHealthPanel.Visible =
            _config.EnableServerHealthPanel;

        ConfigureServerConnectionUi(connectionBar);

        ConfigureGrid();

        var mapTitle = new Label
        {
            Text = "LIVE MAP OVERVIEW",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0),
            BackColor = Color.FromArgb(31, 36, 41),
            ForeColor = Color.WhiteSmoke,
        };

        var mapPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.FromArgb(22, 25, 29),
        };
        mapPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mapPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        mapPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mapPanel.Controls.Add(mapTitle, 0, 0);
        mapPanel.Controls.Add(_mapOverview, 0, 1);

        var sideTitle = new Label
        {
            Text = "OPERATIONS",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
            BackColor = Color.FromArgb(31, 35, 41),
            ForeColor = Color.WhiteSmoke,
        };

        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(12, 6, 12, 6),
            BackColor = Color.FromArgb(27, 30, 35),
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21));
        statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _statusLabel.AutoSize = true;
        _statusLabel.Text = "Starting...";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        _packetLabel.AutoSize = true;
        _packetLabel.Text = "Packets: 0";
        _packetLabel.Dock = DockStyle.Fill;
        _packetLabel.TextAlign = ContentAlignment.MiddleLeft;

        _playerCountLabel.AutoSize = true;
        _playerCountLabel.Text = "Players: 0";
        _playerCountLabel.Dock = DockStyle.Fill;
        _playerCountLabel.TextAlign = ContentAlignment.MiddleLeft;

        _statusLabel.AutoEllipsis = true;
        _statusLabel.ForeColor = NeoTheme.MutedText;
        _packetLabel.ForeColor = NeoTheme.MutedText;
        _playerCountLabel.ForeColor = NeoTheme.Text;

        statusPanel.Controls.Add(_statusLabel, 0, 0);
        statusPanel.Controls.Add(_packetLabel, 1, 0);
        statusPanel.Controls.Add(_playerCountLabel, 2, 0);

        var voiceControls = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(4),
            BackColor = NeoTheme.Canvas,
            ForeColor = NeoTheme.Text,
        };

        var voiceLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        voiceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        voiceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        voiceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        voiceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        voiceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        voiceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        voiceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var volumeLabel = new Label
        {
            Text = "Master volume",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 8, 0),
        };

        _volume.Minimum = 0;
        _volume.Maximum = 100;
        _volume.TickFrequency = 10;
        _volume.Value = (int)Math.Round(_config.MasterVolume * 100);
        _volume.Dock = DockStyle.Fill;
        _volume.ValueChanged += (_, _) =>
            _audio.MasterVolume = _volume.Value / 100f;

        _recordingLabel.Text = "REC: off";
        _recordingLabel.AutoSize = true;
        _recordingLabel.Dock = DockStyle.None;
        _recordingLabel.Anchor = AnchorStyles.Left;
        _recordingLabel.Margin = new Padding(0, 8, 6, 0);
        _recordingLabel.TextAlign = ContentAlignment.MiddleLeft;

        var recordingButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            MinimumSize = new Size(250, 38),
        };

        _startRecordingButton.Text = "REC";
        _startRecordingButton.AutoSize = false;
        _startRecordingButton.Width = 64;
        _startRecordingButton.Click += (_, _) => StartRecording();

        _stopRecordingButton.Text = "Stop";
        _stopRecordingButton.AutoSize = false;
        _stopRecordingButton.Width = 68;
        _stopRecordingButton.Click += (_, _) => StopRecording(true);

        recordingButtons.Controls.Add(_recordingLabel);
        recordingButtons.Controls.Add(_startRecordingButton);
        recordingButtons.Controls.Add(_stopRecordingButton);

        var microphoneLabel = new Label
        {
            Text = "Microphone",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _microphoneDeviceBox.DropDownStyle =
            ComboBoxStyle.DropDownList;

        _microphoneDeviceBox.Dock =
            DockStyle.Fill;

        _microphoneDeviceBox.IntegralHeight =
            false;

        _microphoneDeviceBox.DropDownHeight =
            220;

        _microphoneDeviceBox.Enabled =
            true;

        _pttTargetLabel.Text = "Talk target: SERVER BROADCAST (all players)";
        _pttTargetLabel.Dock = DockStyle.Fill;
        _pttTargetLabel.AutoEllipsis = true;
        _pttTargetLabel.TextAlign = ContentAlignment.MiddleLeft;

        _pushToTalkButton.Text = "HOLD TO TALK";
        _pushToTalkButton.Dock = DockStyle.Fill;

        // Keep PTT readable even with Windows DPI scaling.
        _pushToTalkButton.MinimumSize =
            new Size(0, 38);

        _pushToTalkButton.Margin =
            new Padding(3, 4, 3, 4);
        _pushToTalkButton.Font = new Font(
            Font.FontFamily,
            10,
            FontStyle.Bold);
        _pushToTalkButton.MouseDown += OnPushToTalkMouseDown;

        _pushToTalkButton.MouseUp += (_, _) =>
        {
            if (!_pttToggleCheckBox.Checked)
                StopPushToTalk();
        };

        _pushToTalkButton.MouseLeave += (_, _) =>
        {
            if (!_pttToggleCheckBox.Checked)
                StopPushToTalk();
        };

        _pushToTalkButton.Click +=
            OnPushToTalkClick;

        _pttToggleCheckBox.Text = "Toggle";
        _pttToggleCheckBox.Checked = false;
        _pttToggleCheckBox.AutoSize = true;
        _pttToggleCheckBox.Anchor = AnchorStyles.Left;

        _pttToggleCheckBox.CheckedChanged +=
            OnPttToggleModeChanged;

        var pttRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        pttRow.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                82F));

        pttRow.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                18F));

        pttRow.RowStyles.Add(
            new RowStyle(
                SizeType.Percent,
                100F));

        pttRow.Controls.Add(
            _pushToTalkButton,
            0,
            0);

        pttRow.Controls.Add(
            _pttToggleCheckBox,
            1,
            0);

        var voiceStatusRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            MinimumSize = new Size(0, 38),
            MaximumSize = new Size(int.MaxValue, 38),
        };
        voiceStatusRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100F));
        voiceStatusRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));
        voiceStatusRow.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 38F));
        _pttTargetLabel.AutoSize = false;
        _pttTargetLabel.MaximumSize = new Size(int.MaxValue, 34);
        voiceStatusRow.Controls.Add(_pttTargetLabel, 0, 0);
        voiceStatusRow.Controls.Add(recordingButtons, 1, 0);

        voiceLayout.RowCount = 5;
        voiceLayout.RowStyles.Clear();
        voiceLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 52F));
        voiceLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 34F));
        voiceLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 38F));
        voiceLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 54F));
        voiceLayout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100F));

        voiceLayout.Controls.Add(volumeLabel, 0, 0);
        voiceLayout.Controls.Add(_volume, 1, 0);
        voiceLayout.Controls.Add(microphoneLabel, 0, 1);
        voiceLayout.Controls.Add(_microphoneDeviceBox, 1, 1);
        voiceLayout.Controls.Add(voiceStatusRow, 0, 2);
        voiceLayout.SetColumnSpan(voiceStatusRow, 2);
        voiceLayout.Controls.Add(pttRow, 0, 3);
        voiceLayout.SetColumnSpan(pttRow, 2);
        var voiceSpacer = new Panel { Dock = DockStyle.Fill };
        voiceLayout.Controls.Add(voiceSpacer, 0, 4);
        voiceLayout.SetColumnSpan(voiceSpacer, 2);
        voiceControls.Controls.Add(voiceLayout);

        // ============================================================
        // NEO SERVER CONTROL STAGE 3U
        // ============================================================

        var serverControlControls = new GroupBox
        {
            Text = "SERVER CONTROL",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 9),
            BackColor = Color.FromArgb(226, 230, 234),
            ForeColor = Color.FromArgb(28, 32, 38),
        };

        var serverControlLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        serverControlLayout.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100F));

        for (int serverControlRow = 0;
             serverControlRow < 6;
             serverControlRow++)
        {
            serverControlLayout.RowStyles.Add(
                new RowStyle(SizeType.Absolute, 52F));
        }
        serverControlLayout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100F));

        var serverMapRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        serverMapRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));

        serverMapRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100F));

        serverMapRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 125F));

        var serverMapLabel = new Label
        {
            Text = "Map:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 8, 0),
        };

        _serverMapDisplay.Dock = DockStyle.Fill;
        _serverMapDisplay.BackColor = NeoTheme.Input;
        _serverMapDisplay.ForeColor = NeoTheme.Text;
        _serverMapDisplay.BorderStyle = BorderStyle.FixedSingle;
        _serverMapDisplay.TextAlign = ContentAlignment.MiddleLeft;
        _serverMapDisplay.Padding = new Padding(4, 0, 0, 0);

        _changeMapButton.Text = "MAP LIST...";
        _changeMapButton.Dock = DockStyle.Fill;
        _changeMapButton.Font = new Font(
            Font.FontFamily,
            8.5F,
            FontStyle.Bold);

        _changeMapButton.Click +=
            async (_, _) =>
                await RequestAndOpenMapListAsync();

        serverMapRow.Controls.Add(
            serverMapLabel,
            0,
            0);

        serverMapRow.Controls.Add(
            _serverMapDisplay,
            1,
            0);

        serverMapRow.Controls.Add(
            _changeMapButton,
            2,
            0);


        TableLayoutPanel CreateTwoButtonRow(
            Button left,
            string leftText,
            AdminActionCode leftAction,
            Button right,
            string rightText,
            AdminActionCode rightAction)
        {
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };

            row.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 50F));

            row.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 50F));

            left.Text = leftText;
            left.Dock = DockStyle.Fill;
            left.Margin = new Padding(0, 2, 3, 2);
            left.Click +=
                async (_, _) =>
                    await SendServerControlActionAsync(
                        leftAction);

            right.Text = rightText;
            right.Dock = DockStyle.Fill;
            right.Margin = new Padding(3, 2, 0, 2);
            right.Click +=
                async (_, _) =>
                    await SendServerControlActionAsync(
                        rightAction);

            row.Controls.Add(left, 0, 0);
            row.Controls.Add(right, 1, 0);

            return row;
        }

        TableLayoutPanel restartRow =
            CreateTwoButtonRow(
                _restartRoundButton,
                "Restart round",
                AdminActionCode.RestartRound,
                _restartMatchButton,
                "Restart match",
                AdminActionCode.RestartMatch);

        TableLayoutPanel warmupSwapRow =
            CreateTwoButtonRow(
                _endWarmupButton,
                "End warmup",
                AdminActionCode.EndWarmup,
                _swapTeamsButton,
                "Swap teams",
                AdminActionCode.SwapTeams);

        TableLayoutPanel pauseRow =
            CreateTwoButtonRow(
                _pauseMatchButton,
                "Pause",
                AdminActionCode.PauseMatch,
                _unpauseMatchButton,
                "Unpause",
                AdminActionCode.UnpauseMatch);

        var botRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        botRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 33.333F));

        botRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 33.333F));

        botRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 33.334F));

        _addTBotButton.Text = "Add T bot";
        _addTBotButton.Dock = DockStyle.Fill;
        _addTBotButton.Margin = new Padding(0, 2, 3, 0);
        _addTBotButton.Click +=
            async (_, _) =>
                await SendServerControlActionAsync(
                    AdminActionCode.AddBot,
                    value: 2);

        _addCtBotButton.Text = "Add CT bot";
        _addCtBotButton.Dock = DockStyle.Fill;
        _addCtBotButton.Margin = new Padding(3, 2, 3, 0);
        _addCtBotButton.Click +=
            async (_, _) =>
                await SendServerControlActionAsync(
                    AdminActionCode.AddBot,
                    value: 3);

        _kickBotsButton.Text = "Kick bots";
        _kickBotsButton.Dock = DockStyle.Fill;
        _kickBotsButton.Margin = new Padding(3, 2, 0, 0);
        _kickBotsButton.Click +=
            async (_, _) =>
                await SendServerControlActionAsync(
                    AdminActionCode.RemoveBots);

        botRow.Controls.Add(
            _addTBotButton,
            0,
            0);

        botRow.Controls.Add(
            _addCtBotButton,
            1,
            0);

        botRow.Controls.Add(
            _kickBotsButton,
            2,
            0);

        var zombieModeRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        zombieModeRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 150F));
        zombieModeRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100F));
        zombieModeRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.AutoSize));

        var zombieModeLabel = new Label
        {
            Text = "Zombie Survival",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
        };

        _zombieModeStatusLabel.Text = ZombieSurvivalProfile.NotImplementedText;
        _zombieModeStatusLabel.Dock = DockStyle.Fill;
        _zombieModeStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _zombieModeStatusLabel.ForeColor = NeoTheme.MutedText;

        _zombieModeToggle.Text = "Enabled";
        _zombieModeToggle.AutoSize = true;
        _zombieModeToggle.Anchor = AnchorStyles.Right;
        _zombieModeToggle.Enabled = false;
        _zombieModeToggle.Visible = ZombieSurvivalProfile.Implemented;
        _zombieModeToggle.Margin = new Padding(8, 0, 4, 0);
        _zombieModeToggle.CheckedChanged += async (_, _) =>
        {
            if (!_updatingZombieModeToggle)
                await SetZombieModeAsync(_zombieModeToggle.Checked);
        };

        zombieModeRow.Controls.Add(zombieModeLabel, 0, 0);
        zombieModeRow.Controls.Add(_zombieModeStatusLabel, 1, 0);
        zombieModeRow.Controls.Add(_zombieModeToggle, 2, 0);

        serverControlLayout.Controls.Add(
            serverMapRow,
            0,
            0);

        serverControlLayout.Controls.Add(
            restartRow,
            0,
            1);

        serverControlLayout.Controls.Add(
            warmupSwapRow,
            0,
            2);

        serverControlLayout.Controls.Add(
            pauseRow,
            0,
            3);

        serverControlLayout.Controls.Add(
            botRow,
            0,
            4);

        serverControlLayout.Controls.Add(
            zombieModeRow,
            0,
            5);

        serverControlLayout.Controls.Add(
            new Panel { Dock = DockStyle.Fill },
            0,
            6);

        serverControlControls.Controls.Add(
            serverControlLayout);


        var serverChatControls = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(4),
            BackColor = NeoTheme.Canvas,
            ForeColor = NeoTheme.Text,
        };

        var serverChatLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        serverChatLayout.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100F));

        serverChatLayout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100F));

        serverChatLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 38F));

        _serverChatHistory.Dock = DockStyle.Fill;
        _serverChatHistory.ReadOnly = true;
        _serverChatHistory.DetectUrls = false;
        _serverChatHistory.BorderStyle = BorderStyle.FixedSingle;
        _serverChatHistory.BackColor = Color.White;
        _serverChatHistory.ForeColor = Color.FromArgb(25, 29, 34);
        _serverChatHistory.Font = new Font(
            Font.FontFamily,
            9F,
            FontStyle.Regular);
        _serverChatHistory.TabStop = false;

        var serverChatInputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 5, 0, 0),
            Padding = Padding.Empty,
        };

        serverChatInputRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100F));

        serverChatInputRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 92F));

        _serverChatInput.Dock = DockStyle.Fill;
        _serverChatInput.PlaceholderText =
            "Type a message to all players...";
        _serverChatInput.MaxLength = 220;
        _serverChatInput.Enabled = false;

        _serverChatInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter ||
                e.Modifiers != Keys.None)
            {
                return;
            }

            e.SuppressKeyPress = true;
            _ = SendServerChatFromUiAsync();
        };

        _serverChatSendButton.Text = "SEND";
        _serverChatSendButton.Dock = DockStyle.Fill;
        _serverChatSendButton.Enabled = false;
        _serverChatSendButton.Font = new Font(
            Font.FontFamily,
            9F,
            FontStyle.Bold);
        _serverChatSendButton.Click +=
            async (_, _) => await SendServerChatFromUiAsync();

        serverChatInputRow.Controls.Add(
            _serverChatInput,
            0,
            0);

        serverChatInputRow.Controls.Add(
            _serverChatSendButton,
            1,
            0);

        serverChatLayout.Controls.Add(
            _serverChatHistory,
            0,
            0);

        serverChatLayout.Controls.Add(
            serverChatInputRow,
            0,
            1);

        serverChatControls.Controls.Add(
            serverChatLayout);

        var pluginConsoleControls = new GroupBox
        {
            Text = "PLUGIN CONSOLE",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 9),
        };

        _pluginConsoleHistory.Dock = DockStyle.Fill;
        _pluginConsoleHistory.ReadOnly = true;
        _pluginConsoleHistory.DetectUrls = false;
        _pluginConsoleHistory.BorderStyle = BorderStyle.FixedSingle;
        _pluginConsoleHistory.Font = new Font(
            "Consolas",
            9F,
            FontStyle.Regular);
        _pluginConsoleHistory.TabStop = false;
        pluginConsoleControls.Controls.Add(_pluginConsoleHistory);

        var serverConsoleControls = new GroupBox
        {
            Text = "SERVER CONSOLE",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 9),
        };

        var serverConsoleLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        serverConsoleLayout.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100F));
        serverConsoleLayout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100F));
        serverConsoleLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 40F));

        _serverConsoleHistory.Dock = DockStyle.Fill;
        _serverConsoleHistory.ReadOnly = true;
        _serverConsoleHistory.DetectUrls = false;
        _serverConsoleHistory.BorderStyle = BorderStyle.FixedSingle;
        _serverConsoleHistory.BackColor = NeoTheme.Input;
        _serverConsoleHistory.ForeColor = NeoTheme.Text;
        _serverConsoleHistory.Font = new Font(
            "Consolas",
            9.5F,
            FontStyle.Regular);
        _serverConsoleHistory.WordWrap = false;
        _serverConsoleHistory.ScrollBars = RichTextBoxScrollBars.Both;
        _serverConsoleHistory.TabStop = false;

        var serverConsoleInputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 6, 0, 0),
            Padding = Padding.Empty,
        };
        serverConsoleInputRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 82F));
        serverConsoleInputRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100F));
        serverConsoleInputRow.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 116F));

        _serverConsoleClearButton.Text = "CLEAR";
        _serverConsoleClearButton.Dock = DockStyle.Fill;
        _serverConsoleClearButton.Enabled = false;
        _serverConsoleClearButton.Margin = new Padding(0, 0, 6, 0);
        _serverConsoleClearButton.Click += (_, _) =>
        {
            _serverConsoleHistory.Clear();
            _serverConsoleClearButton.Enabled = false;
            _serverConsoleInput.Focus();
        };

        _serverConsoleInput.Dock = DockStyle.Fill;
        _serverConsoleInput.PlaceholderText = "Enter a server command...";
        _serverConsoleInput.MaxLength = 2048;
        _serverConsoleInput.Enabled = false;
        _serverConsoleInput.Font = new Font(
            "Consolas",
            9.5F,
            FontStyle.Regular);
        _serverConsoleInput.KeyDown += OnServerConsoleInputKeyDown;

        _serverConsoleExecuteButton.Text = "EXECUTE";
        _serverConsoleExecuteButton.Dock = DockStyle.Fill;
        _serverConsoleExecuteButton.Enabled = false;
        _serverConsoleExecuteButton.Margin = new Padding(6, 0, 0, 0);
        _serverConsoleExecuteButton.Font = new Font(
            Font.FontFamily,
            9F,
            FontStyle.Bold);
        _serverConsoleExecuteButton.Click += async (_, _) =>
            await SendServerConsoleCommandAsync();

        serverConsoleInputRow.Controls.Add(
            _serverConsoleClearButton,
            0,
            0);
        serverConsoleInputRow.Controls.Add(
            _serverConsoleInput,
            1,
            0);
        serverConsoleInputRow.Controls.Add(
            _serverConsoleExecuteButton,
            2,
            0);
        serverConsoleLayout.Controls.Add(
            _serverConsoleHistory,
            0,
            0);
        serverConsoleLayout.Controls.Add(
            serverConsoleInputRow,
            0,
            1);
        serverConsoleControls.Controls.Add(serverConsoleLayout);

        var communicationTabs = new NeoTabControl
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };

        var operationsTabs = new NeoTabControl
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            TabWidth = 210,
        };

        TabPage CreateWorkspacePage(string title, Control content)
        {
            var page = new TabPage(title)
            {
                BackColor = NeoTheme.Canvas,
                Padding = new Padding(12),
            };
            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            return page;
        }

        communicationTabs.TabPages.Add(
            CreateWorkspacePage("Chat", serverChatControls));
        communicationTabs.TabPages.Add(
            CreateWorkspacePage("Voice", voiceControls));

        var playersWorkspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        playersWorkspace.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100F));
        playersWorkspace.RowStyles.Add(
            new RowStyle(SizeType.Percent, 55F));
        playersWorkspace.RowStyles.Add(
            new RowStyle(SizeType.Percent, 45F));
        playersWorkspace.Controls.Add(_playersGrid, 0, 0);
        playersWorkspace.Controls.Add(communicationTabs, 0, 1);

        void ResizePlayerCommunicationArea()
        {
            bool compact = playersWorkspace.Height < 700;
            playersWorkspace.RowStyles[0].Height = compact ? 35F : 55F;
            playersWorkspace.RowStyles[1].Height = compact ? 65F : 45F;
        }

        playersWorkspace.SizeChanged += (_, _) =>
            ResizePlayerCommunicationArea();

        operationsTabs.TabPages.Add(
            CreateWorkspacePage("Players", playersWorkspace));
        operationsTabs.TabPages.Add(
            CreateWorkspacePage("Server", serverControlControls));
        operationsTabs.TabPages.Add(
            CreateWorkspacePage("Server Console", serverConsoleControls));
        operationsTabs.TabPages.Add(
            CreateWorkspacePage("Console", pluginConsoleControls));

        var sidePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = NeoTheme.Canvas,
        };
        sidePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        sidePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        sidePanel.Controls.Add(sideTitle, 0, 0);
        sidePanel.Controls.Add(operationsTabs, 0, 1);
        sidePanel.Controls.Add(statusPanel, 0, 2);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = 7,
            BackColor = Color.FromArgb(70, 75, 82),
        };
        split.Panel1.Controls.Add(mapPanel);
        split.Panel2.Controls.Add(sidePanel);

        void ResizeDashboardPanels()
        {
            if (split.IsDisposed || split.Width <= split.SplitterWidth + 100)
                return;

            int minimum = split.Panel1MinSize;
            int maximum = split.Width - split.Panel2MinSize - split.SplitterWidth;
            if (maximum < minimum)
                return;

            int operationsWidth = Math.Clamp(
                (int)Math.Round(split.Width * 0.30F),
                520,
                1100);
            int desired = Math.Clamp(
                split.Width - operationsWidth,
                minimum,
                maximum);
            if (split.SplitterDistance != desired)
                split.SplitterDistance = desired;
        }

        split.SizeChanged += (_, _) => ResizeDashboardPanels();
        split.HandleCreated += (_, _) =>
            split.BeginInvoke((Action)ResizeDashboardPanels);

        Shown += (_, _) =>
            BeginInvoke((Action)ResizeDashboardPanels);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
        layout.RowStyles.Add(
            new RowStyle(
                SizeType.Absolute,
                _config.EnableServerHealthPanel ? 104F : 0F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(menu, 0, 0);
        layout.Controls.Add(connectionBar, 0, 1);
        layout.Controls.Add(serverHealthPanel, 0, 2);
        layout.Controls.Add(split, 0, 3);

        MainMenuStrip = menu;
        Controls.Add(layout);

        SetSteamIdDisplayFormat(SteamIdDisplayFormat.Steam2);
        SetRecordingUi(false);
        UpdatePlayerCount();
    }

    private Control BuildServerHealthPanel()
    {
        Color background = Color.FromArgb(37, 42, 47);
        Color secondaryText = Color.FromArgb(172, 180, 188);

        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(14, 0, 14, 0),
            BackColor = background,
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var title = new Label
        {
            Text = "SERVER HEALTH",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            ForeColor = Color.WhiteSmoke,
        };

        _healthQualityLabel.Text = "CONNECTION: WAITING";
        _healthQualityLabel.Dock = DockStyle.Fill;
        _healthQualityLabel.TextAlign = ContentAlignment.MiddleRight;
        _healthQualityLabel.Font = new Font(
            Font.FontFamily,
            9F,
            FontStyle.Bold);
        _healthQualityLabel.ForeColor = secondaryText;

        titleRow.Controls.Add(title, 0, 0);
        titleRow.Controls.Add(_healthQualityLabel, 1, 0);

        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(8, 0, 8, 7),
            BackColor = background,
        };

        for (int column = 0; column < metrics.ColumnCount; ++column)
        {
            metrics.ColumnStyles.Add(
                new ColumnStyle(SizeType.Percent, 12.5F));
        }

        metrics.Controls.Add(
            BuildHealthMetric("TICK RATE", _healthTickRateLabel), 0, 0);
        metrics.Controls.Add(
            BuildHealthMetric("PLAYERS", _healthPlayersLabel), 1, 0);
        metrics.Controls.Add(
            BuildHealthMetric("MAP UPTIME", _healthMapUptimeLabel), 2, 0);
        metrics.Controls.Add(
            BuildHealthMetric("PING", _healthPingLabel), 3, 0);
        metrics.Controls.Add(
            BuildHealthMetric("PACKET LOSS", _healthPacketLossLabel), 4, 0);
        metrics.Controls.Add(
            BuildHealthMetric("HOST CPU", _healthCpuLabel), 5, 0);
        metrics.Controls.Add(
            BuildHealthMetric("HOST RAM", _healthMemoryLabel), 6, 0);
        metrics.Controls.Add(
            BuildHealthMetric("PLUGIN", _healthVersionLabel), 7, 0);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = background,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(titleRow, 0, 0);
        panel.Controls.Add(metrics, 0, 1);
        return panel;
    }

    private Control BuildHealthMetric(
        string caption,
        Label valueLabel)
    {
        var metric = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(6, 0, 6, 0),
            Padding = Padding.Empty,
        };
        metric.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        metric.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        metric.RowStyles.Add(new RowStyle(SizeType.Percent, 58));

        var captionLabel = new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = Color.FromArgb(172, 180, 188),
            Font = new Font(Font.FontFamily, 8F, FontStyle.Regular),
            AutoEllipsis = true,
        };

        valueLabel.Text = "--";
        valueLabel.Dock = DockStyle.Fill;
        valueLabel.TextAlign = ContentAlignment.TopLeft;
        valueLabel.ForeColor = Color.WhiteSmoke;
        valueLabel.Font = new Font(
            Font.FontFamily,
            10F,
            FontStyle.Bold);
        valueLabel.AutoEllipsis = true;

        metric.Controls.Add(captionLabel, 0, 0);
        metric.Controls.Add(valueLabel, 0, 1);
        return metric;
    }

    private ToolStrip BuildConnectionBar()
    {
        return new ToolStrip
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 46,
            CanOverflow = true,
            Stretch = true,
            Padding = new Padding(12, 6, 12, 6),
        };
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip
        {
            Dock = DockStyle.Fill,
        };

        var fileMenu = new ToolStripMenuItem("&File");

        _startRecordingMenuItem.ShortcutKeys = Keys.Control | Keys.R;
        _startRecordingMenuItem.Click += (_, _) => StartRecording();

        _stopRecordingMenuItem.ShortcutKeys =
            Keys.Control | Keys.Shift | Keys.R;
        _stopRecordingMenuItem.Click += (_, _) => StopRecording(true);

        var exitMenuItem = new ToolStripMenuItem("E&xit");
        exitMenuItem.Click += (_, _) => Close();

        fileMenu.DropDownItems.Add(_startRecordingMenuItem);
        fileMenu.DropDownItems.Add(_stopRecordingMenuItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(exitMenuItem);

        var settingsMenu = new ToolStripMenuItem("&Settings");
        var serverProfilesMenuItem = new ToolStripMenuItem("CS2 &Servers...");
        serverProfilesMenuItem.Click += async (_, _) =>
            await ShowServerProfileManagerAsync();
        var steamIdMenu = new ToolStripMenuItem("&Steam ID");

        _steam2MenuItem.Click += (_, _) =>
            SetSteamIdDisplayFormat(SteamIdDisplayFormat.Steam2);
        _steamId64MenuItem.Click += (_, _) =>
            SetSteamIdDisplayFormat(SteamIdDisplayFormat.SteamId64);

        steamIdMenu.DropDownItems.Add(_steam2MenuItem);
        steamIdMenu.DropDownItems.Add(_steamId64MenuItem);
        settingsMenu.DropDownItems.Add(steamIdMenu);
        var steamApiMenuItem = new ToolStripMenuItem(
            "Steam Profile &Integration...");
        steamApiMenuItem.Click += (_, _) => ShowSteamApiSettings();
        settingsMenu.DropDownItems.Add(steamApiMenuItem);

        var importAccessProfileMenuItem = new ToolStripMenuItem(
            "&Import Access Profile...");
        importAccessProfileMenuItem.Click += async (_, _) =>
            await ImportAccessProfileAsync();

        var initialServerSetupMenuItem = new ToolStripMenuItem(
            "&Initial Server Setup...");
        initialServerSetupMenuItem.Click += async (_, _) =>
            await ShowInitialServerSetupAsync();

        _adminAccountsMenuItem.Enabled = false;
        _adminAccountsMenuItem.Click += (_, _) =>
            ShowAdminAccountManager();
        _inGameAdminsMenuItem.Enabled = false;
        _inGameAdminsMenuItem.Click += (_, _) =>
            ShowInGameAdminManager();
        _auditLogMenuItem.Enabled = false;
        _auditLogMenuItem.Click += (_, _) =>
            ShowAdminAuditLog();
        _banManagementMenuItem.Enabled = false;
        _banManagementMenuItem.Click += (_, _) =>
            ShowBanManager();
        _disciplineManagementMenuItem.Enabled = false;
        _disciplineManagementMenuItem.Click += (_, _) =>
            ShowDisciplineManager();
        _announcementsMenuItem.Enabled = false;
        _announcementsMenuItem.Click += (_, _) => ShowAnnouncements();

        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        settingsMenu.DropDownItems.Add(serverProfilesMenuItem);
        settingsMenu.DropDownItems.Add(initialServerSetupMenuItem);
        settingsMenu.DropDownItems.Add(importAccessProfileMenuItem);
        settingsMenu.DropDownItems.Add(_adminAccountsMenuItem);
        settingsMenu.DropDownItems.Add(_inGameAdminsMenuItem);
        settingsMenu.DropDownItems.Add(_auditLogMenuItem);
        settingsMenu.DropDownItems.Add(_banManagementMenuItem);
        settingsMenu.DropDownItems.Add(_disciplineManagementMenuItem);
        settingsMenu.DropDownItems.Add(_announcementsMenuItem);

        var mapMenu = new ToolStripMenuItem("&Map");
        var importMapMenuItem = new ToolStripMenuItem(
            "&Import Current Map Overview...");
        importMapMenuItem.Click += (_, _) =>
        {
            if (_mapOverview.ImportCurrentMapImage(this))
            {
                _statusLabel.Text =
                    $"Loaded map overview for {_mapOverview.CurrentMapName}";
            }
        };

        var configureMapMenuItem = new ToolStripMenuItem(
            "&Configure Current Map...");
        configureMapMenuItem.Click += (_, _) =>
            _mapOverview.ConfigureCurrentMap(this);

        var reloadMapMenuItem = new ToolStripMenuItem("&Reload Map Files");
        reloadMapMenuItem.Click += (_, _) =>
            _mapOverview.ReloadCurrentMap();

        var openMapFolderMenuItem = new ToolStripMenuItem(
            "Open Maps &Folder");
        openMapFolderMenuItem.Click += (_, _) =>
            _mapOverview.OpenMapsFolder();

        mapMenu.DropDownItems.Add(importMapMenuItem);
        mapMenu.DropDownItems.Add(configureMapMenuItem);
        mapMenu.DropDownItems.Add(reloadMapMenuItem);
        _mapRotationMenuItem.Enabled = false;
        _mapRotationMenuItem.Click += (_, _) => ShowMapRotationManager();
        mapMenu.DropDownItems.Add(_mapRotationMenuItem);
        _workshopMapsMenuItem.Enabled = false;
        _workshopMapsMenuItem.Click += (_, _) => ShowWorkshopMapManager();
        mapMenu.DropDownItems.Add(_workshopMapsMenuItem);
        mapMenu.DropDownItems.Add(new ToolStripSeparator());
        mapMenu.DropDownItems.Add(openMapFolderMenuItem);

        var helpMenu = new ToolStripMenuItem("&Help");
        var aboutMenuItem = new ToolStripMenuItem("&About");
        aboutMenuItem.Click += (_, _) =>
            MessageBox.Show(
                this,
                "NEO ADMIN\n\n" +
                "The player list follows server connect and disconnect events. " +
                "A green speaker symbol appears while a player is talking.\n\n" +
                "The mixed playback can be saved as a WAV file. " +
                "The live map dashboard shows server positions, " +
                "teams, facing direction, health, and speaking status.",
                "About NEO ADMIN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

        helpMenu.DropDownItems.Add(aboutMenuItem);

        var brand = new ToolStripLabel("NEO ADMIN")
        {
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = NeoTheme.Text,
            Margin = new Padding(8, 0, 14, 0),
        };

        menu.Items.Add(brand);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(fileMenu);
        menu.Items.Add(settingsMenu);
        menu.Items.Add(mapMenu);
        menu.Items.Add(helpMenu);

        return menu;
    }

    private void OnAdminSessionChanged(AdminSession? session)
    {
        if (IsDisposed || _closing)
            return;

        PostToUi(() => ApplyAdminSession(session));
    }

    private void ApplyAdminSession(AdminSession? session)
    {
        if (session?.Authenticated == true)
        {
            _adminSessionLabel.Text =
                $"Signed in: {session.DisplayName} ({session.Role})";
            _adminSessionLabel.ForeColor = NeoTheme.Success;
            _statusLabel.Text =
                $"Signed in as {session.DisplayName} with {session.Role} access.";
        }
        else
        {
            _adminSessionLabel.Text =
                $"Account: {_receiver.CurrentAdminId} (not signed in)";
            _adminSessionLabel.ForeColor = NeoTheme.Danger;
        }

        _adminAccountsMenuItem.Enabled =
            session?.Can(AdminPermission.ManageAccounts) == true;
        _inGameAdminsMenuItem.Enabled =
            session?.Can(AdminPermission.ManageGameAdmins) == true;
        _auditLogMenuItem.Enabled =
            session?.Can(AdminPermission.ViewAuditLog) == true;
        _banManagementMenuItem.Enabled =
            session?.Can(AdminPermission.ManageBans) == true;
        _disciplineManagementMenuItem.Enabled =
            session?.Can(AdminPermission.ManageDiscipline) == true;
        _mapRotationMenuItem.Enabled =
            session?.Can(AdminPermission.ManageMapRotation) == true;
        _workshopMapsMenuItem.Enabled =
            session?.Can(AdminPermission.ManageWorkshopMaps) == true;
        _announcementsMenuItem.Enabled =
            session?.Can(AdminPermission.ManageAnnouncements) == true;
        _steam2MenuItem.Enabled =
            session?.Can(AdminPermission.ViewSteamIds) == true;
        _steamId64MenuItem.Enabled = _steam2MenuItem.Enabled;

        UpdateServerChatUi();
        UpdatePttTargetLabel();
        UpdateServerControlUi();

        if (ZombieSurvivalProfile.Implemented &&
            session?.Can(AdminPermission.ManageZombieMode) == true)
            _ = RequestZombieModeStatusAsync();
        else
            ResetZombieModeState(
                ZombieSurvivalProfile.Implemented
                    ? "UNAVAILABLE"
                    : ZombieSurvivalProfile.NotImplementedText);
    }

    private async Task ImportAccessProfileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import NEO ADMIN access profile",
            Filter = "NEO ADMIN profile (*.neo-admin-profile.json)|*.neo-admin-profile.json|JSON files (*.json)|*.json",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            AdminAccessProfile profile = AdminAccessProfile.Load(dialog.FileName);

            if (_pttCapture.IsRunning)
                StopPushToTalk();
            if (_receiver.HasServerTarget)
                _receiver.DisconnectServer();

            ServerProfile selected = _serverConnectionSettings.AddOrSelect(
                profile.ServerAddress,
                profile.ServerAddress,
                profile.ServerPttPort,
                profile.AdminId,
                profile.AccessKey);
            _receiver.SetAdminCredentials(selected.AdminId, selected.AccessKey);
            _serverConnectionSettings.Save();
            RefreshServerProfileSelector();

            _serverAddressBox.Text = profile.ServerAddress;
            _serverPttPortBox.Text = profile.ServerPttPort.ToString();
            _connectServerButton.Text = "CONNECT";
            ApplyAdminSession(null);

            _statusLabel.Text =
                $"Access profile imported for {profile.AdminId}; connecting...";
            await ConnectServerFromUiAsync(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Import Access Profile",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task ShowInitialServerSetupAsync()
    {
        int port = int.TryParse(
            _serverPttPortBox.Text.Trim(),
            out int parsedPort)
            ? parsedPort
            : 27122;
        using var setup = new FirstOwnerSetupForm(
            _serverAddressBox.Text.Trim(),
            port);
        if (setup.ShowDialog(this) != DialogResult.OK ||
            setup.Result is not FirstOwnerSetupResult result)
        {
            return;
        }

        if (_pttCapture.IsRunning)
            StopPushToTalk();
        if (_receiver.HasServerTarget)
            _receiver.DisconnectServer();

        ServerProfile setupProfile = _serverConnectionSettings.AddOrSelect(
            result.ServerAddress,
            result.ServerAddress,
            result.ServerPort,
            result.AccountId,
            result.AccessKey);
        _receiver.SetAdminCredentials(setupProfile.AdminId, setupProfile.AccessKey);

        string? saveWarning = null;
        try
        {
            _serverConnectionSettings.Save();
        }
        catch (Exception exception)
        {
            saveWarning = exception.Message;
        }

        _serverAddressBox.Text = result.ServerAddress;
        _serverPttPortBox.Text = result.ServerPort.ToString();
        RefreshServerProfileSelector();
        _connectServerButton.Text = "CONNECT";
        ApplyAdminSession(null);

        using (var access = new AdminAccessKeyForm(
            new AdminAccountManagerForm.PendingCredential(
                result.DisplayName,
                result.AccountId,
                result.AccessKey),
            result.ServerAddress,
            result.ServerPort))
        {
            access.ShowDialog(this);
        }

        if (saveWarning is not null)
        {
            MessageBox.Show(
                this,
                "The Owner was created, but the local settings could not be saved: " +
                saveWarning + "\n\nSave the access profile before closing NEO ADMIN.",
                "Owner Created",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        _statusLabel.Text =
            $"Owner account {result.AccountId} created; connecting...";
        await ConnectServerFromUiAsync(true);
    }

    private void ShowAdminAccountManager()
    {
        if (!_receiver.Can(AdminPermission.ManageAccounts))
        {
            MessageBox.Show(
                this,
                "The Manage Accounts permission is required.",
                "Administrator Accounts",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        using var manager = new AdminAccountManagerForm(
            _receiver,
            _serverAddressBox.Text.Trim(),
            int.TryParse(_serverPttPortBox.Text, out int port) ? port : 27122);
        manager.ShowDialog(this);
    }

    private void ShowInGameAdminManager()
    {
        if (!_receiver.Can(AdminPermission.ManageGameAdmins))
        {
            MessageBox.Show(
                this,
                "The Manage In-Game Administrators permission is required.",
                "In-Game Administrators",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        using var manager = new InGameAdminManagerForm(_receiver);
        manager.ShowDialog(this);
    }

    private void ShowAdminAuditLog()
    {
        if (!_receiver.Can(AdminPermission.ViewAuditLog))
        {
            MessageBox.Show(
                this,
                "The View Audit Log permission is required.",
                "Administrator Audit Log",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        using var log = new AdminAuditLogForm(_receiver);
        log.ShowDialog(this);
    }

    private void ShowBanManager(AdminBanTarget? target = null)
    {
        if (!_receiver.Can(AdminPermission.ManageBans))
        {
            MessageBox.Show(
                this,
                "The Manage Bans permission is required.",
                "Ban Management",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        using var manager = new AdminBanManagerForm(_receiver, target);
        manager.ShowDialog(this);
    }

    private void ShowDisciplineManager(
        AdminBanTarget? target = null,
        string? restrictionType = null)
    {
        if (!_receiver.Can(AdminPermission.ManageDiscipline))
        {
            MessageBox.Show(this,
                "The Manage Discipline permission is required.",
                "Mute and Gag Management",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        using var manager = new AdminDisciplineForm(
            _receiver, target, restrictionType);
        manager.ShowDialog(this);
    }

    private void ShowMapRotationManager()
    {
        if (!_receiver.Can(AdminPermission.ManageMapRotation))
            return;
        using var manager = new MapRotationManagerForm(
            _receiver, _serverMapCatalog);
        manager.ShowDialog(this);
    }

    private void ShowAnnouncements()
    {
        if (!_receiver.Can(AdminPermission.ManageAnnouncements))
            return;
        using var manager = new AdminAnnouncementsForm(_receiver);
        manager.ShowDialog(this);
    }

    private void ConfigureGrid()
    {
        _playersGrid.Dock = DockStyle.Fill;
        _playersGrid.AllowUserToAddRows = false;
        _playersGrid.AllowUserToDeleteRows = false;
        _playersGrid.AllowUserToResizeRows = false;
        _playersGrid.RowHeadersVisible = false;
        _playersGrid.AutoSizeColumnsMode =
            DataGridViewAutoSizeColumnsMode.None;
        _playersGrid.AllowUserToResizeColumns = true;
        _playersGrid.SelectionMode =
            DataGridViewSelectionMode.FullRowSelect;
        _playersGrid.MultiSelect = false;
        _playersGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _playersGrid.BackgroundColor = Color.FromArgb(224, 227, 231);
        _playersGrid.BorderStyle = BorderStyle.None;
        _playersGrid.RowTemplate.Height = 28;

        _playersGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Muted",
            HeaderText = "Mute",
            Width = 48,
        });

        _playersGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Speaking",
            HeaderText = "Voice",
            Width = 72,
            ReadOnly = true,
        });

        _playersGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = "Player",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 110,
            FillWeight = 100,
            Resizable = DataGridViewTriState.True,
            ReadOnly = true,
        });

        _playersGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Slot",
            HeaderText = "#",
            Width = 36,
            ReadOnly = true,
        });

        _playersGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Team",
            HeaderText = "Team",
            Width = 48,
            ReadOnly = true,
        });

        _playersGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Health",
            HeaderText = "HP",
            Width = 44,
            ReadOnly = true,
        });

        _playersGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Life",
            HeaderText = "State",
            Width = 64,
            ReadOnly = true,
        });

        // Keep the technical fields available to the existing receiver logic,
        // but hide them so the right-hand panel stays easy to read.
        foreach ((string name, string header) in new[]
        {
            ("SteamId", "Steam ID"),
            ("Format", "Audio"),
            ("Level", "Voice level"),
            ("Packets", "Packets"),
            ("LastSeen", "Last packet"),
            ("Note", "Status"),
        })
        {
            _playersGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                Visible = false,
                ReadOnly = true,
            });
        }

        _playersGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_playersGrid.IsCurrentCellDirty)
            {
                _playersGrid.CommitEdit(
                    DataGridViewDataErrorContexts.Commit);
            }
        };

        _playersGrid.CellValueChanged += OnGridCellValueChanged;
        _playersGrid.CellMouseDown +=
            OnPlayerGridCellMouseDown;
        _playersGrid.CellDoubleClick +=
            OnPlayerGridCellDoubleClick;

        ConfigurePlayerAdminMenu();
    }

    private void ConfigurePlayerAdminMenu()
    {
        _playerAdminHeaderItem.Enabled = false;
        _playerAdminHeaderItem.Font = new Font(
            Font.FontFamily,
            Font.Size,
            FontStyle.Bold);

        ToolStripMenuItem ActionItem(
            string text,
            AdminActionCode action)
        {
            var item = new ToolStripMenuItem(text);

            item.Click += async (_, _) =>
                await SendSelectedPlayerAdminActionAsync(
                    action);

            return item;
        }

        _playerAdminMenu.Items.Add(
            _playerAdminHeaderItem);

        _playerAdminMenu.Items.Add(
            new ToolStripSeparator());

        _playerInspectorMenuItem.Click += (_, _) =>
            ShowSelectedPlayerInspector();

        _playerAdminMenu.Items.Add(
            _playerInspectorMenuItem);

        _playerAdminMenu.Items.Add(
            new ToolStripSeparator());

        foreach (AdminGiveItemCategory category in
                 AdminGiveItemCatalog.Categories)
        {
            var categoryItem = new ToolStripMenuItem(category.Name);

            foreach (AdminGiveItem giveItem in category.Items)
            {
                var item = new ToolStripMenuItem(giveItem.Name);
                item.Click += async (_, _) =>
                    await SendSelectedPlayerAdminActionAsync(
                        AdminActionCode.GiveItem,
                        giveItem.EntityClass,
                        $"Give {giveItem.Name}");
                categoryItem.DropDownItems.Add(item);
            }

            _giveItemMenuItem.DropDownItems.Add(categoryItem);
        }

        _playerAdminMenu.Items.Add(_giveItemMenuItem);

        _playerAdminMenu.Items.Add(
            new ToolStripSeparator());

        _playerAdminMenu.Items.Add(
            ActionItem(
                "Respawn",
                AdminActionCode.Respawn));

        _playerAdminMenu.Items.Add(
            ActionItem(
                "Slay",
                AdminActionCode.Slay));

        _playerAdminMenu.Items.Add(
            new ToolStripSeparator());

        _playerAdminMenu.Items.Add(
            ActionItem(
                "Move to Terrorists",
                AdminActionCode.MoveToT));

        _playerAdminMenu.Items.Add(
            ActionItem(
                "Move to Counter-Terrorists",
                AdminActionCode.MoveToCT));

        _playerAdminMenu.Items.Add(
            ActionItem(
                "Move to Spectator",
                AdminActionCode.MoveToSpectator));

        _playerAdminMenu.Items.Add(
            new ToolStripSeparator());

        _playerAdminMenu.Items.Add(
            ActionItem(
                "Kick",
                AdminActionCode.Kick));

        _banPlayerMenuItem.Click += (_, _) =>
        {
            PlayerState? state = GetSelectedPlayerState();
            if (state is null)
                return;
            ShowBanManager(new AdminBanTarget(
                state.SteamId.ToString(),
                state.Name,
                state.Slot));
        };
        _playerAdminMenu.Items.Add(_banPlayerMenuItem);

        void OpenRestriction(string type)
        {
            PlayerState? state = GetSelectedPlayerState();
            if (state is null)
                return;
            ShowDisciplineManager(new AdminBanTarget(
                state.SteamId.ToString(), state.Name, state.Slot), type);
        }
        _mutePlayerMenuItem.Click += (_, _) => OpenRestriction("Mute");
        _gagPlayerMenuItem.Click += (_, _) => OpenRestriction("Gag");
        _disciplineHistoryMenuItem.Click += (_, _) =>
        {
            PlayerState? state = GetSelectedPlayerState();
            if (state is not null)
            {
                using var history = new PlayerDisciplineHistoryForm(
                    _receiver, state.SteamId.ToString(), state.Name);
                history.ShowDialog(this);
            }
        };
        _playerAdminMenu.Items.Add(_mutePlayerMenuItem);
        _playerAdminMenu.Items.Add(_gagPlayerMenuItem);
        _playerAdminMenu.Items.Add(_disciplineHistoryMenuItem);

        NeoTheme.StyleToolStrip(_playerAdminMenu);

        _playerAdminMenu.Opening += (_, e) =>
        {
            PlayerState? state =
                GetSelectedPlayerState();

            if (state is null)
            {
                e.Cancel = true;
                return;
            }

            _playerAdminHeaderItem.Text =
                $"{state.Name}  (slot {state.Slot})";

            bool actionsEnabled =
                !_closing &&
                _receiver.HasServerTarget &&
                _receiver.Can(AdminPermission.ModeratePlayers) &&
                state.Slot >= 0 &&
                !string.Equals(
                    state.Name,
                    "SourceTV",
                    StringComparison.OrdinalIgnoreCase);

            foreach (ToolStripItem item in
                     _playerAdminMenu.Items)
            {
                if (ReferenceEquals(
                        item,
                        _playerAdminHeaderItem) ||
                    item is ToolStripSeparator)
                {
                    continue;
                }

                if (ReferenceEquals(
                        item,
                        _playerInspectorMenuItem))
                {
                    item.Enabled =
                        _receiver.Can(AdminPermission.ViewSteamIds);
                    continue;
                }

                if (ReferenceEquals(item, _banPlayerMenuItem))
                {
                    item.Enabled =
                        _receiver.Can(AdminPermission.ManageBans) &&
                        !state.Bot &&
                        state.SteamId >= 76561197960265728UL &&
                        !string.Equals(
                            state.Name,
                            "SourceTV",
                            StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (ReferenceEquals(item, _mutePlayerMenuItem) ||
                    ReferenceEquals(item, _gagPlayerMenuItem) ||
                    ReferenceEquals(item, _disciplineHistoryMenuItem))
                {
                    item.Enabled =
                        _receiver.Can(AdminPermission.ManageDiscipline) &&
                        !state.Bot &&
                        state.SteamId >= 76561197960265728UL &&
                        !string.Equals(state.Name, "SourceTV",
                            StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                item.Enabled = actionsEnabled;
            }
        };

        _playersGrid.ContextMenuStrip =
            _playerAdminMenu;
    }

    private void OnPlayerGridCellMouseDown(
        object? sender,
        DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right ||
            e.RowIndex < 0)
        {
            return;
        }

        _playersGrid.ClearSelection();

        DataGridViewRow row =
            _playersGrid.Rows[e.RowIndex];

        row.Selected = true;

        if (e.ColumnIndex >= 0)
        {
            _playersGrid.CurrentCell =
                row.Cells[e.ColumnIndex];
        }
    }

    private void OnPlayerGridCellDoubleClick(
        object? sender,
        DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.ColumnIndex < 0 ||
            _playersGrid.Columns[e.ColumnIndex].Name == "Muted")
        {
            return;
        }

        _playersGrid.ClearSelection();

        DataGridViewRow row =
            _playersGrid.Rows[e.RowIndex];

        row.Selected = true;
        _playersGrid.CurrentCell =
            row.Cells[e.ColumnIndex];

        ShowSelectedPlayerInspector();
    }

    private PlayerState? GetSelectedPlayerState()
    {
        if (_playersGrid.SelectedRows.Count != 1)
            return null;

        DataGridViewRow row =
            _playersGrid.SelectedRows[0];

        if (row.Tag is not string key)
            return null;

        return _players.TryGetValue(
            key,
            out PlayerState? state)
                ? state
                : null;
    }

    private void ShowSelectedPlayerInspector()
    {
        if (!_receiver.Can(AdminPermission.ViewSteamIds))
        {
            _statusLabel.Text =
                "Player identity unavailable: View Steam IDs permission is required.";
            return;
        }

        PlayerState? state =
            GetSelectedPlayerState();

        if (state is null || _closing)
            return;

        using var inspector = new PlayerIdentityForm(
            state.Name,
            state.Slot,
            state.SteamId,
            _serverConnectionSettings.SteamWebApiKey);

        inspector.ShowDialog(this);
    }

    private void ShowSteamApiSettings()
    {
        using var settings = new SteamApiSettingsForm(
            _serverConnectionSettings.SteamWebApiKey);
        if (settings.ShowDialog(this) != DialogResult.OK)
            return;

        _serverConnectionSettings.SteamWebApiKey = settings.ApiKey;
        _serverConnectionSettings.Save();
        _statusLabel.Text = settings.ApiKey.Length == 0
            ? "SteamGPT keyless profile lookup enabled."
            : "Direct Valve Steam profile lookup configured.";
    }

    private void ShowWorkshopMapManager()
    {
        if (!_receiver.Can(AdminPermission.ManageWorkshopMaps))
        {
            _statusLabel.Text =
                "Workshop Map Manager permission is required.";
            return;
        }

        using var manager = new WorkshopMapManagerForm(
            _receiver,
            _serverMapCatalog);
        manager.ShowDialog(this);
    }

    private async Task SendSelectedPlayerAdminActionAsync(
        AdminActionCode action,
        string? text = null,
        string? displayName = null)
    {
        if (_closing)
            return;

        PlayerState? state =
            GetSelectedPlayerState();

        if (state is null)
        {
            _statusLabel.Text =
                "Player admin: select a player first.";
            return;
        }

        if (!_receiver.HasServerTarget)
        {
            _statusLabel.Text =
                "Player admin unavailable: connect to a server first.";
            return;
        }

        string actionName = displayName ??
            GetAdminActionDisplayName((uint)action);

        try
        {
            bool sent =
                await _receiver.SendAdminActionAsync(
                    action,
                    state.Slot,
                    0,
                    text);

            if (sent)
            {
                _statusLabel.Text =
                    $"{actionName} request sent for {state.Name}; waiting for server result.";
            }
            else
            {
                _statusLabel.Text =
                    $"{actionName} request was not sent. Wait for authenticated server traffic and try again.";
            }
        }
        catch (Exception exception)
        {
            _statusLabel.Text =
                $"{actionName} failed to send: {exception.Message}";
        }
    }

    private void HandleAdminActionResult(
        VoicePacket packet)
    {
        if (packet.AdminActionCode ==
                (uint)AdminActionCode.RequestZombieModeStatus ||
            packet.AdminActionCode ==
                (uint)AdminActionCode.SetZombieMode)
        {
            if (!ZombieSurvivalProfile.Implemented)
            {
                ResetZombieModeState(
                    ZombieSurvivalProfile.NotImplementedText);
                return;
            }

            string zombieMessage = packet.AdminActionMessage
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            _zombieModeRequestInFlight = false;

            if (packet.AdminActionSucceeded)
            {
                bool? enabled = zombieMessage.Contains(
                    "disabled",
                    StringComparison.OrdinalIgnoreCase)
                        ? false
                        : zombieMessage.Contains(
                            "enabled",
                            StringComparison.OrdinalIgnoreCase)
                            ? true
                            : null;

                if (enabled.HasValue)
                    ApplyZombieModeState(enabled.Value);
                else
                    RestoreZombieModeStateDisplay();
            }
            else
            {
                RestoreZombieModeStateDisplay();
                _zombieModeStatusLabel.Text = "ERROR";
                _zombieModeStatusLabel.ForeColor = NeoTheme.Danger;
            }

            string zombiePrefix = packet.AdminActionSucceeded
                ? "ADMIN OK"
                : "ADMIN FAILED";
            string zombieActionName = GetAdminActionDisplayName(
                packet.AdminActionCode);
            _statusLabel.Text =
                $"{zombiePrefix}: {zombieActionName}: {zombieMessage}";
            AppendPluginConsole(
                $"{zombiePrefix}: {zombieActionName} / server: {zombieMessage}");
            UpdateServerControlUi();
            return;
        }

        if (packet.AdminActionCode ==
            (uint)AdminActionCode.RunServerConsole)
        {
            string output = packet.AdminActionMessage
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .TrimEnd();

            if (output.Length == 0)
            {
                output = packet.AdminActionSucceeded
                    ? "Command completed without console output."
                    : "The command failed without an error message.";
            }

            string status = packet.AdminActionSucceeded
                ? "SERVER CONSOLE OK"
                : "SERVER CONSOLE FAILED";
            string summary = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim() ?? output;

            _statusLabel.Text = $"{status}: {summary}";
            AppendServerConsoleResult(
                packet.AdminActionSucceeded,
                output);
            return;
        }

        string actionName =
            GetAdminActionDisplayName(
                packet.AdminActionCode);

        if (ZombieSurvivalProfile.Implemented &&
            packet.AdminActionCode == (uint)AdminActionCode.ChangeMap &&
            packet.AdminActionSucceeded &&
            packet.AdminActionMessage.Contains(
                "Zombie Survival enabled",
                StringComparison.OrdinalIgnoreCase))
        {
            ApplyZombieModeState(true);
        }

        bool serverAction =
            packet.AdminActionCode >=
                (uint)AdminActionCode.ChangeMap &&
            packet.AdminActionCode <=
                (uint)AdminActionCode.RemoveBots;

        string target =
            serverAction
                ? "server"
                : _players.Values
                    .FirstOrDefault(state =>
                        state.Slot == packet.PlayerSlot)
                    ?.Name
                    ?? $"slot {packet.PlayerSlot}";

        string message =
            packet.AdminActionMessage
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

        if (message.Length == 0)
        {
            message = packet.AdminActionSucceeded
                ? "Action completed."
                : "Action failed.";
        }

        string prefix =
            packet.AdminActionSucceeded
                ? "ADMIN OK"
                : "ADMIN FAILED";

        _statusLabel.Text =
            $"{prefix}: {actionName} / {target}: {message}";

        AppendPluginConsole(
            $"{prefix}: {actionName} / {target}: {message}");
    }

    private static string GetAdminActionDisplayName(
        uint action)
    {
        return (AdminActionCode)action switch
        {
            AdminActionCode.Kick => "Kick",
            AdminActionCode.Slay => "Slay",
            AdminActionCode.Respawn => "Respawn",
            AdminActionCode.MoveToT => "Move to Terrorists",
            AdminActionCode.MoveToCT => "Move to Counter-Terrorists",
            AdminActionCode.MoveToSpectator => "Move to Spectator",
            AdminActionCode.GiveItem => "Give Weapon or Item",
            AdminActionCode.ChangeMap => "Change Map",
            AdminActionCode.RestartRound => "Restart Round",
            AdminActionCode.RestartMatch => "Restart Match",
            AdminActionCode.EndWarmup => "End Warmup",
            AdminActionCode.PauseMatch => "Pause Match",
            AdminActionCode.UnpauseMatch => "Unpause Match",
            AdminActionCode.SwapTeams => "Swap Teams",
            AdminActionCode.AddBot => "Add Bot",
            AdminActionCode.RemoveBots => "Kick Bots",
            AdminActionCode.RequestMapCatalog => "Refresh Map List",
            AdminActionCode.RequestServerHealth => "Refresh Server Health",
            AdminActionCode.RequestAdminAccounts => "Refresh Administrator Accounts",
            AdminActionCode.RequestGameAdmins => "Refresh In-Game Administrators",
            AdminActionCode.SaveGameAdmin => "Save In-Game Administrator",
            AdminActionCode.DeleteGameAdmin => "Delete In-Game Administrator",
            AdminActionCode.SaveAdminAccount => "Save Administrator Account",
            AdminActionCode.DeleteAdminAccount => "Delete Administrator Account",
            AdminActionCode.RequestAuditLog => "Refresh Audit Log",
            AdminActionCode.RequestBanCatalog => "Refresh Ban List",
            AdminActionCode.SaveBan => "Ban Player",
            AdminActionCode.DeleteBan => "Unban Player",
            AdminActionCode.RequestDisciplineCatalog => "Refresh Mute and Gag List",
            AdminActionCode.SaveRestriction => "Save Player Restriction",
            AdminActionCode.DeleteRestriction => "Remove Player Restriction",
            AdminActionCode.RequestDisciplineHistory => "Refresh Discipline History",
            AdminActionCode.RequestMapRotation => "Refresh Map Rotation",
            AdminActionCode.SaveMapRotation => "Save Map Rotation",
            AdminActionCode.RunNextMap => "Run Next Map",
            AdminActionCode.SaveScheduledMap => "Schedule Map Change",
            AdminActionCode.DeleteScheduledMap => "Remove Scheduled Map Change",
            AdminActionCode.RequestAnnouncements => "Refresh Announcements",
            AdminActionCode.SendAnnouncementNow => "Send Announcement",
            AdminActionCode.SaveAnnouncement => "Schedule Announcement",
            AdminActionCode.DeleteAnnouncement => "Remove Announcement",
            AdminActionCode.RunServerConsole => "Run Server Console Command",
            AdminActionCode.RequestZombieModeStatus => "Refresh Zombie Survival Status",
            AdminActionCode.SetZombieMode => "Set Zombie Survival Mode",
            AdminActionCode.HostWorkshopMap => "Install or Switch Workshop Map",
            _ => $"Admin action {action}",
        };
    }

    private void StartRecording()
    {
        string recordingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NEO ADMIN Recordings");

        try
        {
            Directory.CreateDirectory(recordingsFolder);
        }
        catch
        {
            recordingsFolder =
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Save NEO ADMIN recording",
            Filter = "Wave audio (*.wav)|*.wav",
            DefaultExt = "wav",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = recordingsFolder,
            FileName =
                $"NEO-ADMIN-{DateTime.Now:yyyy-MM-dd-HHmmss}.wav",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _audio.StartRecording(dialog.FileName);
            SetRecordingUi(true, dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Recording could not be started:\n\n{exception.Message}",
                "Recording Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void StopRecording(bool showConfirmation)
    {
        string? path = _audio.StopRecording();
        SetRecordingUi(false, path);

        if (showConfirmation && !string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(
                this,
                $"Recording saved to:\n\n{path}",
                "Recording Saved",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private void SetRecordingUi(bool recording, string? path = null)
    {
        _startRecordingMenuItem.Enabled = !recording;
        _stopRecordingMenuItem.Enabled = recording;
        _startRecordingButton.Enabled = !recording;
        _stopRecordingButton.Enabled = recording;

        if (recording)
        {
            string fileName = string.IsNullOrWhiteSpace(path)
                ? "WAV file"
                : Path.GetFileName(path);

            _recordingLabel.Text = $"REC: {fileName}";
            _recordingLabel.ForeColor = NeoTheme.Danger;
            Text = "NEO ADMIN - RECORDING";
        }
        else
        {
            _recordingLabel.Text = string.IsNullOrWhiteSpace(path)
                ? "REC: off"
                : $"Saved: {Path.GetFileName(path)}";

            _recordingLabel.ForeColor = NeoTheme.Text;
            Text = "NEO ADMIN";
        }
    }

    private void SetSteamIdDisplayFormat(SteamIdDisplayFormat format)
    {
        _steamIdDisplayFormat = format;
        _steam2MenuItem.Checked =
            format == SteamIdDisplayFormat.Steam2;
        _steamId64MenuItem.Checked =
            format == SteamIdDisplayFormat.SteamId64;

        if (_playersGrid.Columns.Contains("SteamId"))
        {
            _playersGrid.Columns["SteamId"].HeaderText =
                format == SteamIdDisplayFormat.Steam2
                    ? "Steam ID (Steam2)"
                    : "Steam ID (SteamID64)";
        }

        foreach (PlayerState state in _players.Values)
        {
            state.Row.Cells["SteamId"].Value =
                FormatSteamId(state.SteamId);
        }
    }

    private string FormatSteamId(ulong steamId64)
    {
        return _steamIdDisplayFormat == SteamIdDisplayFormat.Steam2
            ? ToSteam2Id(steamId64)
            : steamId64 == 0
                ? "BOT"
                : steamId64.ToString();
    }

    private static string ToSteam2Id(ulong steamId64)
    {
        const ulong SteamId64Base = 76561197960265728UL;

        if (steamId64 < SteamId64Base)
        {
            return steamId64 == 0
                ? "BOT"
                : steamId64.ToString();
        }

        ulong accountId = steamId64 - SteamId64Base;
        ulong authenticationServer = accountId & 1UL;
        ulong accountNumber = accountId >> 1;

        return $"STEAM_0:{authenticationServer}:{accountNumber}";
    }

    private static string GetPlayerKey(ulong steamId, int slot)
    {
        return steamId != 0
            ? $"steam:{steamId}"
            : $"slot:{slot}";
    }

    private static bool IsSourceTv(string? playerName)
    {
        return string.Equals(
            playerName,
            "SourceTV",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceTv(PlayerState state) =>
        IsSourceTv(state.Name);

    private PlayerState UpsertPlayer(VoicePacket packet)
    {
        string key = GetPlayerKey(packet.SteamId, packet.PlayerSlot);

        _players.TryGetValue(
            key,
            out PlayerState? state);

        if (state is null)
        {
            state = _players.Values.FirstOrDefault(
                candidate =>
                    candidate.Slot == packet.PlayerSlot);

            if (state is not null)
            {
                string previousKey = state.Key;
                _players.Remove(state.Key);
                _mapOverview.RemovePlayer(previousKey);
                state.Key = key;
                state.Row.Tag = key;
                _players.Add(key, state);
            }
        }
        else
        {
            // A Steam identity can move into a slot still owned by a stale
            // entry. Keep the known identity and remove only the duplicate.
            PlayerState? slotOwner =
                _players.Values.FirstOrDefault(
                    candidate =>
                        candidate.Slot == packet.PlayerSlot &&
                        !ReferenceEquals(candidate, state));

            if (slotOwner is not null)
                RemovePlayerState(slotOwner);
        }

        if (state is not null)
        {
            state.SteamId = packet.SteamId;
            state.Slot = packet.PlayerSlot;
            state.LastActivityUtc = DateTime.UtcNow;
            state.DisconnectPendingSinceUtc = null;
            state.SeenDuringTransition = true;

            if (!string.IsNullOrWhiteSpace(packet.PlayerName))
                state.Name = packet.PlayerName;

            UpdateIdentityCells(state);
            _mapOverview.UpdateIdentity(
                state.Key, state.SteamId, state.Slot, state.Name);
            return state;
        }

        int rowIndex = _playersGrid.Rows.Add();
        DataGridViewRow row = _playersGrid.Rows[rowIndex];
        row.Cells["Muted"].Value = false;
        row.Cells["Speaking"].Value = "Online";
        row.Cells["Name"].Value = string.IsNullOrWhiteSpace(packet.PlayerName)
            ? $"Player {packet.PlayerSlot}"
            : packet.PlayerName;
        row.Cells["SteamId"].Value = FormatSteamId(packet.SteamId);
        row.Cells["Slot"].Value = packet.PlayerSlot;
        row.Cells["Team"].Value = "-";
        row.Cells["Health"].Value = "-";
        row.Cells["Life"].Value = "Online";
        row.Cells["Format"].Value = string.Empty;
        row.Cells["Level"].Value = string.Empty;
        row.Cells["Packets"].Value = 0L;
        row.Cells["LastSeen"].Value = string.Empty;
        row.Cells["Note"].Value = "Online";
        row.Tag = key;

        state = new PlayerState(
            key,
            row,
            packet.SteamId,
            packet.PlayerSlot,
            string.IsNullOrWhiteSpace(packet.PlayerName)
                ? $"Player {packet.PlayerSlot}"
                : packet.PlayerName);

        state.LastActivityUtc =
            DateTime.UtcNow;
        state.SeenDuringTransition = true;

        _players.Add(key, state);
        SetSpeakingCell(state, false);
        UpdatePlayerCount();
        return state;
    }

    private void UpdateIdentityCells(PlayerState state)
    {
        state.Row.Cells["Name"].Value = state.Name;
        state.Row.Cells["SteamId"].Value =
            FormatSteamId(state.SteamId);
        state.Row.Cells["Slot"].Value = state.Slot;
    }

    private void RemovePlayer(VoicePacket packet)
    {
        string key = GetPlayerKey(packet.SteamId, packet.PlayerSlot);

        if (!_players.TryGetValue(key, out PlayerState? state))
        {
            // A few server transitions can report a zero XUID. Match the
            // slot as a fallback so the stale row is still removed.
            state = _players.Values.FirstOrDefault(
                candidate => candidate.Slot == packet.PlayerSlot);

            if (state is null)
                return;
        }

        if (IsSourceTv(state))
            return;

        DateTime now = DateTime.UtcNow;

        if (now < _rosterTransitionUntilUtc)
        {
            state.DisconnectPendingSinceUtc = now;
            return;
        }

        RemovePlayerState(state);
    }

    private void BeginRosterTransition(
        TimeSpan holdTime,
        bool trackReconnections = false)
    {
        DateTime now = DateTime.UtcNow;
        _rosterTransitionUntilUtc = now + holdTime;
        _rosterSettling = trackReconnections;

        foreach (PlayerState state in _players.Values)
        {
            state.LastActivityUtc = now;

            if (trackReconnections)
            {
                state.SeenDuringTransition =
                    IsSourceTv(state);
            }
        }
    }

    private void RemovePlayerState(
        PlayerState state)
    {
        _players.Remove(state.Key);
        _mapOverview.RemovePlayer(state.Key);

        if (state.Row.DataGridView is not null)
            _playersGrid.Rows.Remove(state.Row);

        UpdatePlayerCount();
    }

    private void ClearPlayerRoster()
    {
        _rosterTransitionUntilUtc = DateTime.MinValue;
        _rosterAwaitingMapStart = false;
        _rosterSettling = false;

        PlayerState[] states =
            _players.Values.ToArray();

        foreach (PlayerState state in states)
            RemovePlayerState(state);

        _players.Clear();
        UpdatePlayerCount();
    }

    private void OnPacketReceived(VoicePacket packet, IPEndPoint sender)
    {
        if (packet.MessageType == BridgeMessageType.Voice)
            _audio.HandlePacket(packet);

        Interlocked.Increment(ref _packetCount);

        if (IsDisposed || _closing)
            return;

        PostToUi(() =>
        {
            _statusLabel.Text =
                $"Receiving from {sender.Address}:{sender.Port}";
            _packetLabel.Text =
                $"Packets: {Interlocked.Read(ref _packetCount):N0}";

            switch (packet.MessageType)
            {
                case BridgeMessageType.PlayerConnected:
                {
                    // The server's recurring presence cache can contain old
                    // slots after CS2 replaces bots during a level load. Live
                    // players arrive through position/voice packets; SourceTV
                    // is the only intentional presence-only roster entry.
                    if (!IsSourceTv(packet.PlayerName))
                        break;

                    PlayerState state = UpsertPlayer(packet);
                    if (state.Packets == 0)
                        state.Row.Cells["Note"].Value = "Online";
                    break;
                }

                case BridgeMessageType.PlayerDisconnected:
                    RemovePlayer(packet);
                    break;

                case BridgeMessageType.Voice:
                    UpdateVoicePacket(packet);
                    break;

                case BridgeMessageType.MapChanged:
                {
                    string reportedMap = packet.MapName.Trim();
                    bool changedMap =
                        !string.Equals(
                            _mapOverview.CurrentMapName,
                            reportedMap,
                            StringComparison.OrdinalIgnoreCase);

                    // MapChanged is also part of the recurring presence
                    // snapshot. Only a different map, or disconnects from a
                    // requested same-map reload, starts the settling window.
                    bool requestedMapStarted =
                        _rosterAwaitingMapStart &&
                        _players.Values.Any(state =>
                            state.DisconnectPendingSinceUtc.HasValue);

                    if (changedMap || requestedMapStarted)
                    {
                        _rosterAwaitingMapStart = false;
                        BeginRosterTransition(
                            RosterSettlingTime,
                            trackReconnections: true);
                        _ = RequestZombieModeStatusAsync();
                    }

                    _mapOverview.SetCurrentMap(packet.MapName);

                    if (!string.IsNullOrWhiteSpace(packet.MapName))
                    {
                        DisplayCurrentMap(
                            packet.MapName.Trim());
                    }
                    break;
                }

                case BridgeMessageType.PlayerPosition:
                    UpdatePlayerPosition(packet);
                    break;

                case BridgeMessageType.ChatEvent:
                    AppendServerChatMessage(packet);
                    break;

                case BridgeMessageType.AdminActionResult:
                    HandleAdminActionResult(packet);
                    break;

                case BridgeMessageType.MapCatalog:
                    HandleMapCatalog(packet);
                    break;

                case BridgeMessageType.ServerHealth:
                    UpdateServerHealth(packet);
                    break;
            }
        });
    }

    private async Task RequestServerHealthAsync()
    {
        if (_closing ||
            !_config.EnableServerHealthPanel ||
            _healthProbeInFlight ||
            !_receiver.HasServerTarget)
        {
            return;
        }

        _healthProbeInFlight = true;
        try
        {
            await _receiver.SendHealthProbeAsync();
        }
        catch (Exception exception)
        {
            CrashLog.Write(
                "Server health probe failed.",
                exception);
        }
        finally
        {
            _healthProbeInFlight = false;
        }
    }

    private void UpdateServerHealth(VoicePacket packet)
    {
        _lastServerHealthUtc = DateTime.UtcNow;

        _healthTickRateLabel.Text =
            float.IsFinite(packet.TickRate) &&
            packet.TickRate >= 0F &&
            packet.TickRate <= 1024F
                ? $"{packet.TickRate:F1} Hz"
                : "--";

        int connectedPlayers = Math.Max(0, packet.ConnectedPlayers);
        uint maxPlayers = Math.Min(packet.MaxPlayers, 1024U);
        _healthPlayersLabel.Text =
            maxPlayers > 0
                ? $"{connectedPlayers} / {maxPlayers}"
                : connectedPlayers.ToString();

        _healthMapUptimeLabel.Text =
            FormatHealthDuration(packet.MapUptimeSeconds);

        double roundTrip = packet.RoundTripMilliseconds;
        _healthPingLabel.Text =
            double.IsFinite(roundTrip) && roundTrip >= 0.0
                ? $"{roundTrip:F0} ms"
                : "--";

        double packetLoss = packet.PacketLossPercent;
        _healthPacketLossLabel.Text =
            double.IsFinite(packetLoss) && packetLoss >= 0.0
                ? $"{Math.Min(packetLoss, 100.0):F1}%"
                : "--";

        _healthCpuLabel.Text =
            FormatHealthPercent(packet.CpuUsagePercent);
        _healthMemoryLabel.Text =
            FormatHealthPercent(packet.MemoryUsagePercent);
        _healthVersionLabel.Text =
            string.IsNullOrWhiteSpace(packet.PluginVersion)
                ? "Unknown"
                : packet.PluginVersion.Trim();

        string quality;
        Color qualityColor;

        if (!double.IsFinite(roundTrip) ||
            !double.IsFinite(packetLoss))
        {
            quality = "LIVE";
            qualityColor = Color.FromArgb(112, 197, 255);
        }
        else if (roundTrip > 200.0 || packetLoss >= 8.0)
        {
            quality = "POOR";
            qualityColor = Color.FromArgb(255, 112, 112);
        }
        else if (roundTrip > 100.0 || packetLoss >= 3.0)
        {
            quality = "FAIR";
            qualityColor = Color.FromArgb(255, 193, 87);
        }
        else if (roundTrip > 50.0 || packetLoss >= 1.0)
        {
            quality = "GOOD";
            qualityColor = Color.FromArgb(149, 214, 120);
        }
        else
        {
            quality = "EXCELLENT";
            qualityColor = Color.FromArgb(91, 214, 142);
        }

        _healthQualityLabel.Text = $"CONNECTION: {quality}";
        _healthQualityLabel.ForeColor = qualityColor;
    }

    private void RefreshServerHealthFreshness()
    {
        if (!_receiver.HasServerTarget ||
            _lastServerHealthUtc == DateTime.MinValue)
        {
            return;
        }

        if (DateTime.UtcNow - _lastServerHealthUtc <=
            TimeSpan.FromSeconds(6))
        {
            return;
        }

        _healthQualityLabel.Text = "CONNECTION: STALE";
        _healthQualityLabel.ForeColor =
            Color.FromArgb(255, 112, 112);
    }

    private void ResetServerHealthDisplay(string status)
    {
        _lastServerHealthUtc = DateTime.MinValue;
        _healthQualityLabel.Text = $"CONNECTION: {status}";
        _healthQualityLabel.ForeColor =
            Color.FromArgb(172, 180, 188);
        _healthTickRateLabel.Text = "--";
        _healthPlayersLabel.Text = "--";
        _healthMapUptimeLabel.Text = "--";
        _healthPingLabel.Text = "--";
        _healthPacketLossLabel.Text = "--";
        _healthCpuLabel.Text = "--";
        _healthMemoryLabel.Text = "--";
        _healthVersionLabel.Text = "--";
    }

    private static string FormatHealthPercent(float value) =>
        float.IsFinite(value) && value >= 0F && value <= 100F
            ? $"{value:F1}%"
            : "--";

    private static string FormatHealthDuration(ulong seconds)
    {
        const ulong SecondsPerDay = 24UL * 60UL * 60UL;
        ulong days = seconds / SecondsPerDay;
        ulong remaining = seconds % SecondsPerDay;
        ulong hours = remaining / 3600UL;
        ulong minutes = remaining % 3600UL / 60UL;
        ulong secs = remaining % 60UL;

        return days > 0
            ? $"{days}d {hours:00}:{minutes:00}:{secs:00}"
            : $"{hours:00}:{minutes:00}:{secs:00}";
    }

    private async Task RequestAndOpenMapListAsync()
    {
        if (_closing)
            return;

        if (!_receiver.HasServerTarget)
        {
            _statusLabel.Text =
                "Map list unavailable: connect to a server first.";

            return;
        }

        // Always request a fresh scan so newly installed/removed maps are
        // reflected without restarting NEO ADMIN.
        _openMapWindowWhenCatalogArrives = true;

        try
        {
            bool sent =
                await _receiver.SendAdminActionAsync(
                    AdminActionCode.RequestMapCatalog,
                    -1);

            if (sent)
            {
                _statusLabel.Text =
                    "Requesting map list from server...";
            }
            else
            {
                    _openMapWindowWhenCatalogArrives = false;

                _statusLabel.Text =
                    "Map list request was not sent. Wait for authenticated server traffic and try again.";
            }
        }
        catch (Exception exception)
        {
            _openMapWindowWhenCatalogArrives = false;

            _statusLabel.Text =
                $"Map list request failed: {exception.Message}";
        }
    }

    private void HandleMapCatalog(
        VoicePacket packet)
    {
        string[] maps =
            packet.MapCatalogText
                .Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Where(name =>
                    !string.IsNullOrWhiteSpace(name))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    name => name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();


        _serverMapCatalog.Clear();
        _serverMapCatalog.AddRange(maps);

        _statusLabel.Text =
            maps.Length == 0
                ? "Server returned an empty map list."
                : $"Server map list: {maps.Length} map(s).";

        if (_openMapWindowWhenCatalogArrives)
        {
            _openMapWindowWhenCatalogArrives = false;

            _ = OpenMapSelectionWindowAsync();
        }
    }

    private void DisplayCurrentMap(
        string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return;

        _serverMapDisplay.Text = mapName.Trim();
    }

    private async Task OpenMapSelectionWindowAsync()
    {
        if (_serverMapCatalog.Count == 0)
        {
            _statusLabel.Text =
                "No maps were returned by the server.";

            return;
        }

        using var dialog =
            new MapSelectionForm(
                _serverMapCatalog);

        DialogResult result =
            dialog.ShowDialog(this);

        if (result != DialogResult.OK ||
            string.IsNullOrWhiteSpace(
                dialog.SelectedMap))
        {
            return;
        }

        string selected =
            dialog.SelectedMap.Trim();

        bool zombieSurvivalProfile =
            ZombieSurvivalProfile.IsMapToken(selected);
        if (zombieSurvivalProfile && !ZombieSurvivalProfile.Implemented)
        {
            ResetZombieModeState(
                ZombieSurvivalProfile.NotImplementedText);
            _statusLabel.Text =
                "Zombie Survival is not implemented yet.";
            return;
        }

        DisplayCurrentMap(
            zombieSurvivalProfile
                ? ZombieSurvivalProfile.MapName
                : selected);

        if (zombieSurvivalProfile)
        {
            _zombieModeStatusLabel.Text = "STARTING";
            _zombieModeStatusLabel.ForeColor = NeoTheme.Warning;
        }

        // The selection window has already closed at this point.
        await SendServerControlActionAsync(
            AdminActionCode.ChangeMap,
            text: selected);
    }

    private void UpdateServerChatUi()
    {
        bool enabled =
            !_closing &&
            _receiver.HasServerTarget &&
            _receiver.Can(AdminPermission.SendChat);

        _serverChatInput.Enabled = enabled;
        _serverChatSendButton.Enabled = enabled;

        UpdateServerControlUi();
        UpdateServerConsoleUi();
    }

    private void UpdateServerConsoleUi()
    {
        bool enabled =
            !_closing &&
            _receiver.HasServerTarget &&
            _receiver.Can(AdminPermission.RunServerConsole);

        _serverConsoleInput.Enabled = enabled;
        _serverConsoleExecuteButton.Enabled = enabled;
    }

    private void OnServerConsoleInputKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None)
        {
            e.SuppressKeyPress = true;
            _ = SendServerConsoleCommandAsync();
            return;
        }

        if (e.KeyCode is not (Keys.Up or Keys.Down) ||
            _serverConsoleCommandHistory.Count == 0)
        {
            return;
        }

        if (e.KeyCode == Keys.Up)
        {
            _serverConsoleHistoryIndex =
                _serverConsoleHistoryIndex < 0
                    ? _serverConsoleCommandHistory.Count - 1
                    : Math.Max(0, _serverConsoleHistoryIndex - 1);
            _serverConsoleInput.Text =
                _serverConsoleCommandHistory[_serverConsoleHistoryIndex];
        }
        else
        {
            if (_serverConsoleHistoryIndex < 0 ||
                _serverConsoleHistoryIndex >=
                    _serverConsoleCommandHistory.Count - 1)
            {
                _serverConsoleHistoryIndex = -1;
                _serverConsoleInput.Clear();
            }
            else
            {
                ++_serverConsoleHistoryIndex;
                _serverConsoleInput.Text =
                    _serverConsoleCommandHistory[_serverConsoleHistoryIndex];
            }
        }

        _serverConsoleInput.SelectionStart =
            _serverConsoleInput.TextLength;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private async Task SendServerConsoleCommandAsync()
    {
        if (_closing)
            return;

        string command = _serverConsoleInput.Text.Trim();
        if (command.Length == 0)
        {
            _serverConsoleInput.Focus();
            return;
        }

        if (!_receiver.HasServerTarget ||
            !_receiver.Can(AdminPermission.RunServerConsole))
        {
            _statusLabel.Text =
                "Server Console unavailable: connect with an account that has Server Console permission.";
            UpdateServerConsoleUi();
            return;
        }

        if (_serverConsoleCommandHistory.Count == 0 ||
            !string.Equals(
                _serverConsoleCommandHistory[^1],
                command,
                StringComparison.Ordinal))
        {
            _serverConsoleCommandHistory.Add(command);
            if (_serverConsoleCommandHistory.Count > 100)
                _serverConsoleCommandHistory.RemoveAt(0);
        }
        _serverConsoleHistoryIndex = -1;
        AppendServerConsoleCommand(command);

        try
        {
            _serverConsoleInput.Focus();
            _serverConsoleExecuteButton.Enabled = false;
            bool sent = await _receiver.SendAdminActionAsync(
                AdminActionCode.RunServerConsole,
                -1,
                0,
                command);

            if (sent)
            {
                _serverConsoleInput.Clear();
                _statusLabel.Text =
                    "Server Console command sent; waiting for console output.";
            }
            else
            {
                const string message =
                    "Command was not sent. Wait for authenticated server traffic and try again.";
                _statusLabel.Text = $"Server Console: {message}";
                AppendServerConsoleResult(false, message);
            }
        }
        catch (Exception exception)
        {
            _statusLabel.Text =
                $"Server Console failed to send: {exception.Message}";
            AppendServerConsoleResult(false, exception.Message);
        }
        finally
        {
            UpdateServerConsoleUi();
            _serverConsoleInput.Focus();
        }
    }

    private void AppendServerConsoleCommand(string command)
    {
        _serverConsoleHistory.SelectionStart =
            _serverConsoleHistory.TextLength;
        _serverConsoleHistory.SelectionColor = NeoTheme.Accent;
        _serverConsoleHistory.AppendText(
            $"[{DateTime.Now:HH:mm:ss}] > {command}\r\n");
        FinishServerConsoleAppend();
    }

    private void AppendServerConsoleResult(
        bool success,
        string output)
    {
        string normalized = output
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace('\0', ' ')
            .TrimEnd();

        _serverConsoleHistory.SelectionStart =
            _serverConsoleHistory.TextLength;
        _serverConsoleHistory.SelectionColor =
            success ? NeoTheme.Success : NeoTheme.Danger;
        _serverConsoleHistory.AppendText(
            $"[{DateTime.Now:HH:mm:ss}] {(success ? "OK" : "ERROR")}\r\n");
        _serverConsoleHistory.SelectionColor = NeoTheme.Text;
        _serverConsoleHistory.AppendText(
            normalized.Replace("\n", "\r\n") + "\r\n\r\n");
        FinishServerConsoleAppend();
    }

    private void FinishServerConsoleAppend()
    {
        _serverConsoleHistory.SelectionColor = NeoTheme.Text;
        _serverConsoleHistory.SelectionStart =
            _serverConsoleHistory.TextLength;
        _serverConsoleHistory.ScrollToCaret();
        _serverConsoleClearButton.Enabled =
            _serverConsoleHistory.TextLength > 0;
    }

    private void ResetZombieModeState(string status)
    {
        _zombieModeEnabled = false;
        _zombieModeStateKnown = false;
        _zombieModeRequestInFlight = false;
        _updatingZombieModeToggle = true;
        _zombieModeToggle.Checked = false;
        _updatingZombieModeToggle = false;
        _zombieModeStatusLabel.Text = status;
        _zombieModeStatusLabel.ForeColor = NeoTheme.MutedText;
        UpdateServerControlUi();
    }

    private void ApplyZombieModeState(bool enabled)
    {
        _zombieModeEnabled = enabled;
        _zombieModeStateKnown = true;
        _zombieModeRequestInFlight = false;
        _updatingZombieModeToggle = true;
        _zombieModeToggle.Checked = enabled;
        _updatingZombieModeToggle = false;
        _zombieModeStatusLabel.Text = enabled ? "ON" : "OFF";
        _zombieModeStatusLabel.ForeColor = enabled
            ? NeoTheme.Success
            : NeoTheme.MutedText;
        UpdateServerControlUi();
    }

    private void RestoreZombieModeStateDisplay()
    {
        _updatingZombieModeToggle = true;
        _zombieModeToggle.Checked =
            _zombieModeStateKnown && _zombieModeEnabled;
        _updatingZombieModeToggle = false;
        _zombieModeStatusLabel.Text = _zombieModeStateKnown
            ? _zombieModeEnabled ? "ON" : "OFF"
            : "UNKNOWN";
        _zombieModeStatusLabel.ForeColor =
            _zombieModeStateKnown && _zombieModeEnabled
                ? NeoTheme.Success
                : NeoTheme.MutedText;
    }

    private async Task RequestZombieModeStatusAsync()
    {
        if (!ZombieSurvivalProfile.Implemented)
        {
            ResetZombieModeState(
                ZombieSurvivalProfile.NotImplementedText);
            return;
        }

        if (_closing ||
            _zombieModeRequestInFlight ||
            !_receiver.HasServerTarget ||
            !_receiver.Can(AdminPermission.ManageZombieMode))
        {
            return;
        }

        _zombieModeRequestInFlight = true;
        _zombieModeStatusLabel.Text = "CHECKING...";
        _zombieModeStatusLabel.ForeColor = NeoTheme.Warning;
        UpdateServerControlUi();

        try
        {
            bool sent = await _receiver.SendAdminActionAsync(
                AdminActionCode.RequestZombieModeStatus,
                -1,
                0,
                string.Empty);
            if (sent)
                return;

            _zombieModeRequestInFlight = false;
            RestoreZombieModeStateDisplay();
        }
        catch (Exception exception)
        {
            _zombieModeRequestInFlight = false;
            RestoreZombieModeStateDisplay();
            CrashLog.Write(
                "Zombie Survival status request failed.",
                exception);
        }
        finally
        {
            UpdateServerControlUi();
        }
    }

    private async Task SetZombieModeAsync(bool enabled)
    {
        if (!ZombieSurvivalProfile.Implemented)
        {
            ResetZombieModeState(
                ZombieSurvivalProfile.NotImplementedText);
            _statusLabel.Text =
                "Zombie Survival is not implemented yet.";
            return;
        }

        if (_closing ||
            _zombieModeRequestInFlight ||
            !_receiver.HasServerTarget ||
            !_receiver.Can(AdminPermission.ManageZombieMode))
        {
            RestoreZombieModeStateDisplay();
            UpdateServerControlUi();
            return;
        }

        _zombieModeRequestInFlight = true;
        _zombieModeStatusLabel.Text = enabled
            ? "ENABLING..."
            : "DISABLING...";
        _zombieModeStatusLabel.ForeColor = NeoTheme.Warning;
        UpdateServerControlUi();

        try
        {
            bool sent = await _receiver.SendAdminActionAsync(
                AdminActionCode.SetZombieMode,
                -1,
                enabled ? 1 : 0,
                string.Empty);
            if (sent)
            {
                _rosterAwaitingMapStart = true;
                BeginRosterTransition(RosterCommandHoldTime);
                _statusLabel.Text = enabled
                    ? "Enabling Zombie Survival; waiting for the map reload."
                    : "Disabling Zombie Survival; waiting for the map reload.";
                return;
            }

            _zombieModeRequestInFlight = false;
            RestoreZombieModeStateDisplay();
            _statusLabel.Text =
                "Zombie Survival request was not sent. Wait for authenticated server traffic and try again.";
        }
        catch (Exception exception)
        {
            _zombieModeRequestInFlight = false;
            RestoreZombieModeStateDisplay();
            _statusLabel.Text =
                $"Zombie Survival failed to send: {exception.Message}";
        }
        finally
        {
            UpdateServerControlUi();
        }
    }

    private void UpdateServerControlUi()
    {
        bool connected =
            !_closing &&
            _receiver.HasServerTarget;

        _changeMapButton.Enabled = connected &&
            _receiver.Can(AdminPermission.ChangeMap);

        bool match = connected &&
            _receiver.Can(AdminPermission.ControlMatch);
        _restartRoundButton.Enabled = match;
        _restartMatchButton.Enabled = match;
        _endWarmupButton.Enabled = match;
        _pauseMatchButton.Enabled = match;
        _unpauseMatchButton.Enabled = match;
        _swapTeamsButton.Enabled = match;

        bool bots = connected &&
            _receiver.Can(AdminPermission.ControlBots);
        _addTBotButton.Enabled = bots;
        _addCtBotButton.Enabled = bots;
        _kickBotsButton.Enabled = bots;

        _zombieModeToggle.Enabled = ZombieSurvivalProfile.Implemented &&
            connected &&
            _zombieModeStateKnown &&
            !_zombieModeRequestInFlight &&
            _receiver.Can(AdminPermission.ManageZombieMode);
    }

    private async Task SendServerControlActionAsync(
        AdminActionCode action,
        int value = 0,
        string? text = null)
    {
        if (_closing)
            return;

        if (!_receiver.HasServerTarget)
        {
            _statusLabel.Text =
                "Server control unavailable: connect to a server first.";

            UpdateServerControlUi();
            return;
        }

        string normalizedText =
            text?.Trim() ?? string.Empty;

        if (action == AdminActionCode.ChangeMap &&
            normalizedText.Length == 0)
        {
            _statusLabel.Text =
                "Change Map: enter a map name first.";

            _changeMapButton.Focus();
            return;
        }

        string actionName =
            GetAdminActionDisplayName(
                (uint)action);

        try
        {
            SetServerControlBusy(true);

            bool sent =
                await _receiver.SendAdminActionAsync(
                    action,
                    -1,
                    value,
                    normalizedText);

            if (sent)
            {
                if (action == AdminActionCode.ChangeMap)
                {
                    _rosterAwaitingMapStart = true;
                    BeginRosterTransition(RosterCommandHoldTime);
                }

                _statusLabel.Text =
                    $"{actionName} request sent; waiting for server result.";
            }
            else
            {
                _statusLabel.Text =
                    $"{actionName} request was not sent. Wait for authenticated server traffic and try again.";
            }
        }
        catch (Exception exception)
        {
            _statusLabel.Text =
                $"{actionName} failed to send: {exception.Message}";
        }
        finally
        {
            SetServerControlBusy(false);
            UpdateServerControlUi();
        }
    }

    private void SetServerControlBusy(
        bool busy)
    {
        if (!busy)
        {
            UpdateServerControlUi();
            return;
        }

        _changeMapButton.Enabled = false;
        _restartRoundButton.Enabled = false;
        _restartMatchButton.Enabled = false;
        _endWarmupButton.Enabled = false;
        _pauseMatchButton.Enabled = false;
        _unpauseMatchButton.Enabled = false;
        _swapTeamsButton.Enabled = false;
        _addTBotButton.Enabled = false;
        _addCtBotButton.Enabled = false;
        _kickBotsButton.Enabled = false;
        _zombieModeToggle.Enabled = false;
    }

    private void AppendServerChatMessage(
        VoicePacket packet)
    {
        string message =
            packet.ChatMessage
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

        if (message.Length == 0)
            return;

        string sender =
            string.IsNullOrWhiteSpace(packet.PlayerName)
                ? "SERVER"
                : packet.PlayerName.Trim();

        AppendServerChatLine(
            $"[{DateTime.Now:HH:mm:ss}] {sender}: {message}");
    }

    private void AppendPluginConsole(
        string message)
    {
        string normalized = message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (normalized.Length == 0)
            return;

        const int MaxConsoleCharacters = 100_000;
        if (_pluginConsoleHistory.TextLength > MaxConsoleCharacters)
        {
            _pluginConsoleHistory.Clear();
            _pluginConsoleHistory.AppendText(
                "[Older plugin messages cleared]" +
                Environment.NewLine);
        }

        _pluginConsoleHistory.AppendText(
            $"[{DateTime.Now:HH:mm:ss}] {normalized}" +
            Environment.NewLine);
        _pluginConsoleHistory.SelectionStart =
            _pluginConsoleHistory.TextLength;
        _pluginConsoleHistory.ScrollToCaret();
    }

    private void AppendServerChatLine(
        string line)
    {
        const int MaxChatCharacters = 100_000;

        if (_serverChatHistory.TextLength >
            MaxChatCharacters)
        {
            _serverChatHistory.Clear();
            _serverChatHistory.AppendText(
                "[Older chat history cleared]" +
                Environment.NewLine);
        }

        _serverChatHistory.AppendText(
            line + Environment.NewLine);

        _serverChatHistory.SelectionStart =
            _serverChatHistory.TextLength;

        _serverChatHistory.ScrollToCaret();
    }

    private async Task SendServerChatFromUiAsync()
    {
        if (_closing)
            return;

        string message =
            _serverChatInput.Text.Trim();

        if (message.Length == 0)
            return;

        if (!_receiver.HasServerTarget)
        {
            _statusLabel.Text =
                "Server chat unavailable: connect to a server first.";

            UpdateServerChatUi();
            return;
        }

        _serverChatSendButton.Enabled = false;

        try
        {
            bool sent =
                await _receiver.SendAdminChatAsync(
                    message);

            if (sent)
            {
                _serverChatInput.Clear();
                _statusLabel.Text =
                    "Server chat message sent; waiting for server echo.";
            }
            else
            {
                _statusLabel.Text =
                    "Server chat was not sent. Wait for the authenticated server reply and try again.";
            }
        }
        catch (Exception exception)
        {
            _statusLabel.Text =
                $"Server chat send failed: {exception.Message}";
        }
        finally
        {
            UpdateServerChatUi();

            if (_serverChatInput.Enabled)
                _serverChatInput.Focus();
        }
    }

    private void PopulateMicrophoneDeviceSelector()
    {
        _microphoneDeviceBox.Items.Clear();

        string[] microphones =
            AdminPttCapture.GetMicrophoneNames();

        foreach (string microphone in microphones)
            _microphoneDeviceBox.Items.Add(microphone);

        if (microphones.Length == 0)
        {
            _microphoneDeviceBox.Items.Add(
                "No microphone devices found");

            _microphoneDeviceBox.SelectedIndex = 0;
            _microphoneDeviceBox.Enabled = false;
            return;
        }

        int selectedIndex = -1;

        string savedName =
            _serverConnectionSettings.MicrophoneDeviceName;

        if (!string.IsNullOrWhiteSpace(savedName))
        {
            for (int i = 0; i < microphones.Length; i++)
            {
                if (string.Equals(
                        microphones[i],
                        savedName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }
        }

        if (selectedIndex < 0)
            selectedIndex = _pttCapture.DeviceNumber;

        if (selectedIndex < 0 ||
            selectedIndex >= microphones.Length)
        {
            selectedIndex = 0;
        }

        _pttCapture.DeviceNumber =
            selectedIndex;

        _microphoneDeviceBox.SelectedIndex =
            selectedIndex;

        _microphoneDeviceBox.Enabled =
            true;
    }

    private void OnMicrophoneDeviceChanged(
        object? sender,
        EventArgs e)
    {
        int selectedIndex =
            _microphoneDeviceBox.SelectedIndex;

        if (selectedIndex < 0)
            return;

        if (_pttCapture.IsRunning)
            return;

        try
        {
            string[] microphones =
                AdminPttCapture.GetMicrophoneNames();

            if (selectedIndex >= microphones.Length)
                return;

            _pttCapture.DeviceNumber =
                selectedIndex;

            string selectedName =
                microphones[selectedIndex];

            _serverConnectionSettings.MicrophoneDeviceName =
                selectedName;

            try
            {
                _serverConnectionSettings.Save();
            }
            catch (Exception saveException)
            {
                _statusLabel.Text =
                    "Microphone selected, but the setting " +
                    "could not be saved: " +
                    saveException.Message;

                return;
            }

            _statusLabel.Text =
                $"Microphone selected: {selectedName}";
        }
        catch (Exception exception)
        {
            _statusLabel.Text =
                $"Microphone selection failed: {exception.Message}";
        }
    }

    private void UpdatePttTargetLabel()
    {
        if (_pttCapture.IsRunning)
            return;

        if (!_receiver.HasServerTarget)
        {
            _pttTargetLabel.Text =
                "Talk target: NO SERVER - enter Server + PTT Port above";

            _pushToTalkButton.Enabled = false;
            return;
        }

        if (!_receiver.Can(AdminPermission.BroadcastVoice))
        {
            _pttTargetLabel.Text =
                "Talk target: BROADCAST VOICE PERMISSION REQUIRED";
            _pushToTalkButton.Enabled = false;
            return;
        }

        _pttTargetLabel.Text =
            $"Talk target: SERVER BROADCAST via " +
            _receiver.ServerTargetDisplay;

        _pushToTalkButton.Enabled = true;
    }

    private void OnPushToTalkMouseDown(
        object? sender,
        MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left ||
            _closing ||
            _pttToggleCheckBox.Checked)
        {
            return;
        }

        StartPushToTalk();
    }

    private void OnPushToTalkClick(
        object? sender,
        EventArgs e)
    {
        if (_closing ||
            !_pttToggleCheckBox.Checked)
        {
            return;
        }

        if (_pttCapture.IsRunning)
        {
            StopPushToTalk();
            return;
        }

        StartPushToTalk();
    }

    private void OnPttToggleModeChanged(
        object? sender,
        EventArgs e)
    {
        // Never allow the microphone to remain stuck on
        // when changing between HOLD and TOGGLE modes.
        if (_pttCapture.IsRunning)
            StopPushToTalk();

        _pushToTalkButton.Text =
            _pttToggleCheckBox.Checked
                ? "CLICK TO TALK"
                : "HOLD TO TALK";

        _statusLabel.Text =
            _pttToggleCheckBox.Checked
                ? "Push-to-talk mode: TOGGLE."
                : "Push-to-talk mode: HOLD.";

        UpdatePttTargetLabel();
    }

    private void StartPushToTalk()
    {
        if (_closing ||
            _pttCapture.IsRunning)
        {
            return;
        }

        if (!_receiver.HasServerTarget)
        {
            _statusLabel.Text =
                "Push-to-talk unavailable: connect to a server first.";

            return;
        }

        if (!_receiver.Can(AdminPermission.BroadcastVoice))
        {
            _statusLabel.Text =
                "Push-to-talk unavailable: Broadcast Voice permission is required.";
            return;
        }

        try
        {
            _pttCapture.Start();
            _microphoneDeviceBox.Enabled = false;

            _pushToTalkButton.Text =
                _pttToggleCheckBox.Checked
                    ? "TRANSMITTING - CLICK TO STOP"
                    : "TRANSMITTING - RELEASE TO STOP";

            _pushToTalkButton.BackColor =
                Color.LightSalmon;

            _statusLabel.Text =
                "Push-to-talk: SERVER BROADCAST transmitting to all players.";
        }
        catch (Exception exception)
        {
            _statusLabel.Text =
                $"Microphone could not start: {exception.Message}";
        }
    }

    private void StopPushToTalk()
    {
        if (!_pttCapture.IsRunning)
            return;

        _pttCapture.Stop();
        if (!_closing && !IsDisposed)
            _microphoneDeviceBox.Enabled = true;

        if (!_closing && !IsDisposed)
        {
            _pushToTalkButton.Text = "HOLD TO TALK";
            _pushToTalkButton.UseVisualStyleBackColor = true;
            _statusLabel.Text = "Server broadcast push-to-talk stopped.";
            UpdatePttTargetLabel();
        }
    }

    private void OnPttOpusFrameReady(
        byte[] opusPayload,
        int sequenceBytes,
        uint sectionNumber,
        uint uncompressedSampleOffset,
        float voiceLevel)
    {
        if (!_pttCapture.IsRunning)
            return;

        _ = SendPttFrameAsync(
            opusPayload,
            sequenceBytes,
            sectionNumber,
            uncompressedSampleOffset,
            voiceLevel);
    }

    private async Task SendPttFrameAsync(
        byte[] opusPayload,
        int sequenceBytes,
        uint sectionNumber,
        uint uncompressedSampleOffset,
        float voiceLevel)
    {
        try
        {
            bool sent = await _receiver.SendPushToTalkAsync(
                opusPayload,
                sequenceBytes,
                sectionNumber,
                uncompressedSampleOffset,
                voiceLevel);

            if (!sent &&
                _pttCapture.IsRunning &&
                !IsDisposed &&
                !_closing)
            {
                PostToUi(() =>
                    _statusLabel.Text =
                        "Server broadcast PTT packet was not sent.");
            }
        }
        catch (Exception exception)
        {
            if (!IsDisposed && !_closing)
            {
                PostToUi(() =>
                    _statusLabel.Text =
                        $"Server broadcast PTT send failed: {exception.Message}");
            }
        }
    }

    private void OnPttCaptureError(string message)
    {
        if (IsDisposed || _closing)
            return;

        PostToUi(() =>
        {
            _pushToTalkButton.Text = "HOLD TO TALK";
            _pushToTalkButton.UseVisualStyleBackColor = true;
            _statusLabel.Text = message;
            UpdatePttTargetLabel();
        });
    }
    private async void OnMapPlayerDragTeleport(
        MapPlayerSnapshot player,
        float x,
        float y,
        float z)
    {
        if (!_receiver.Can(AdminPermission.TeleportPlayers))
        {
            _statusLabel.Text =
                "Player drag unavailable: Teleport Players permission is required.";
            return;
        }

        try
        {
            bool sent = await _receiver.SendTeleportAsync(
                player.SteamId,
                player.Slot,
                x,
                y,
                z);

            if (sent && !IsDisposed && !_closing)
            {
                _statusLabel.Text =
                    $"Dragging {player.Name} to " +
                    $"{x:0.0}, {y:0.0}, {z:0.0}";
            }
        }
        catch (Exception exception)
        {
            if (!IsDisposed && !_closing)
            {
                _statusLabel.Text =
                    $"Teleport failed: {exception.Message}";
            }
        }
    }

    private void UpdateVoicePacket(VoicePacket packet)
    {
        PlayerState state = UpsertPlayer(packet);
        state.Packets++;
        state.LastVoiceUtc = DateTime.UtcNow;
        state.LastActivityUtc = DateTime.UtcNow;
        state.LastError = string.Empty;

        DataGridViewRow row = state.Row;
        row.Cells["Format"].Value = packet.AudioFormat.ToString();
        row.Cells["Level"].Value =
            packet.VoiceLevel.ToString("0.000");
        row.Cells["Packets"].Value = state.Packets;
        row.Cells["LastSeen"].Value =
            DateTime.Now.ToString("HH:mm:ss.fff");
        row.Cells["Note"].Value = DecoderStatus(packet.AudioFormat);

        SetSpeakingCell(state, true);
    }

    private void UpdatePlayerPosition(VoicePacket packet)
    {
        PlayerState state = UpsertPlayer(packet);
        state.Team = packet.TeamNumber;
        state.Health = packet.Health;
        state.Alive = packet.IsAlive;
        state.Bot = packet.IsBot;
        state.X = packet.PositionX;
        state.Y = packet.PositionY;
        state.Z = packet.PositionZ;
        state.Yaw = packet.ViewYaw;
        state.LastPositionUtc = DateTime.UtcNow;
        state.LastActivityUtc = DateTime.UtcNow;

        state.Row.Cells["Team"].Value = state.Team switch
        {
            2 => "T",
            3 => "CT",
            _ => "-",
        };
        state.Row.Cells["Health"].Value = state.Health;
        state.Row.Cells["Life"].Value = state.Alive ? "Alive" : "Dead";
        state.Row.Cells["Name"].Style.ForeColor = state.Team switch
        {
            2 => NeoTheme.Warning,
            3 => Color.FromArgb(107, 168, 255),
            _ => NeoTheme.Text,
        };

        _mapOverview.UpsertPlayer(new MapPlayerSnapshot(
            state.Key,
            state.SteamId,
            state.Slot,
            state.Name,
            state.X,
            state.Y,
            state.Z,
            state.Yaw,
            state.Team,
            state.Health,
            state.Alive,
            state.Bot,
            state.IsSpeaking,
            state.LastPositionUtc));
    }

    private void RefreshSpeakingIndicators()
    {
        DateTime now = DateTime.UtcNow;

        foreach (PlayerState state in _players.Values)
        {
            bool speaking =
                state.LastVoiceUtc != DateTime.MinValue &&
                now - state.LastVoiceUtc <= SpeakingHoldTime;

            SetSpeakingCell(state, speaking);
        }
    }

    private void PruneStalePlayers()
    {
        if (_players.Count == 0)
            return;

        DateTime now =
            DateTime.UtcNow;

        if (now < _rosterTransitionUntilUtc)
            return;

        if (_rosterSettling)
        {
            _rosterSettling = false;

            PlayerState[] notReconnected =
                _players.Values
                    .Where(state =>
                        !IsSourceTv(state) &&
                        !state.SeenDuringTransition)
                    .ToArray();

            foreach (PlayerState state in notReconnected)
                RemovePlayerState(state);
        }

        PlayerState[] stale =
            _players.Values
                .Where(state =>
                    !IsSourceTv(state) &&
                    (state.DisconnectPendingSinceUtc.HasValue ||
                     state.LastActivityUtc != DateTime.MinValue &&
                     now - state.LastActivityUtc > PlayerStaleTimeout))
                .ToArray();

        foreach (PlayerState state in stale)
            RemovePlayerState(state);
    }

    private void SetSpeakingCell(
        PlayerState state,
        bool speaking)
    {
        if (state.IsSpeaking == speaking)
            return;

        state.IsSpeaking = speaking;
        _mapOverview.SetSpeaking(state.Key, speaking);
        DataGridViewCell cell = state.Row.Cells["Speaking"];

        if (speaking)
        {
            cell.Value = "Speaking";
            cell.Style.ForeColor = NeoTheme.Success;
        }
        else
        {
            cell.Value = "Online";
            cell.Style.ForeColor = NeoTheme.MutedText;
        }
    }

    private void UpdatePlayerCount()
    {
        _playerCountLabel.Text = $"Players: {_players.Count}";
    }

    private static string DecoderStatus(VoiceAudioFormat format)
    {
        return format switch
        {
            VoiceAudioFormat.Opus => "Playing Opus",
            VoiceAudioFormat.Pcm16Test => "Playing PCM test",
            VoiceAudioFormat.Steam =>
                "Captured; Steam codec unsupported",
            VoiceAudioFormat.Engine =>
                "Captured; engine codec unsupported",
            _ => "Unsupported format",
        };
    }

    private void OnDecodeError(ulong steamId, string message)
    {
        if (IsDisposed || _closing)
            return;

        PostToUi(() =>
        {
            PlayerState? state = _players.Values.FirstOrDefault(
                candidate => candidate.SteamId == steamId);

            if (state is not null)
            {
                state.LastError = message;
                state.Row.Cells["Note"].Value = message;
            }
        });
    }

    private void OnRecordingError(string message)
    {
        if (IsDisposed || _closing)
            return;

        PostToUi(() =>
        {
            SetRecordingUi(false);

            MessageBox.Show(
                this,
                $"Recording stopped because of an error:\n\n{message}",
                "Recording Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        });
    }

    private void OnReceiverStatus(string message)
    {
        if (IsDisposed || _closing)
            return;

        PostToUi(() =>
        {
            _statusLabel.Text = message;
            AppendPluginConsole(message);
        });
    }

    private void PostToUi(Action action)
    {
        if (_closing || IsDisposed || Disposing || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke((Action)(() =>
            {
                if (!_closing && !IsDisposed && !Disposing)
                    action();
            }));
        }
        catch (ObjectDisposedException)
        {
            // A close can dispose the native handle between the checks above.
        }
        catch (InvalidOperationException exception)
        {
            if (!_closing && !IsDisposed && !Disposing)
                CrashLog.Write("UI callback could not be scheduled.", exception);
        }
    }

    private void OnGridCellValueChanged(
        object? sender,
        DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.ColumnIndex != _playersGrid.Columns["Muted"].Index)
        {
            return;
        }

        DataGridViewRow row = _playersGrid.Rows[e.RowIndex];
        if (row.Tag is not string key ||
            !_players.TryGetValue(key, out PlayerState? state) ||
            row.Cells["Muted"].Value is not bool muted)
        {
            return;
        }

        _audio.SetPlayerMuted(state.SteamId, muted);
    }

    private async void OnFormClosingAsync(
        object? sender,
        FormClosingEventArgs e)
    {
        if (_closing)
            return;

        _closing = true;
        e.Cancel = true;
        _activityTimer.Stop();
        _serverHealthTimer.Stop();
        StopPushToTalk();

        if (_audio.IsRecording)
            StopRecording(false);

        await _receiver.DisposeAsync();
        _pttCapture.Dispose();
        _audio.Dispose();
        _activityTimer.Dispose();
        _serverHealthTimer.Dispose();
        _mapOverview.Dispose();

        FormClosing -= OnFormClosingAsync;
        Close();
    }

    private sealed class PlayerState
    {
        public PlayerState(
            string key,
            DataGridViewRow row,
            ulong steamId,
            int slot,
            string name)
        {
            Key = key;
            Row = row;
            SteamId = steamId;
            Slot = slot;
            Name = name;
        }

        public string Key { get; set; }
        public DataGridViewRow Row { get; }
        public ulong SteamId { get; set; }
        public int Slot { get; set; }
        public string Name { get; set; }
        public long Packets { get; set; }
        public DateTime LastVoiceUtc { get; set; } = DateTime.MinValue;
        public bool IsSpeaking { get; set; }
        public string LastError { get; set; } = string.Empty;
        public int Team { get; set; }
        public int Health { get; set; }
        public bool Alive { get; set; }
        public bool Bot { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public DateTime LastPositionUtc { get; set; } = DateTime.MinValue;
        public DateTime LastActivityUtc { get; set; } = DateTime.MinValue;
        public DateTime? DisconnectPendingSinceUtc { get; set; }
        public bool SeenDuringTransition { get; set; }
    }

    // A deliberately simple single-click map picker. Clicking any map name
    // immediately selects it and closes the window.
    private sealed class MapSelectionForm : NeoForm
    {
        private readonly ListBox _maps = new();

        public MapSelectionForm(
            IReadOnlyList<string> maps)
        {
            Text = "Select Server Map";
            StartPosition =
                FormStartPosition.CenterParent;
            Width = 560;
            Height = 720;
            MinimumSize =
                new Size(420, 480);

            FormBorderStyle =
                FormBorderStyle.SizableToolWindow;

            var title = new Label
            {
                Text =
                    $"SERVER MAPS ({maps.Count}) - click a map to change",
                Dock = DockStyle.Top,
                Height = 34,
                TextAlign =
                    ContentAlignment.MiddleLeft,
                Padding =
                    new Padding(10, 0, 0, 0),
                Font = new Font(
                    SystemFonts.MessageBoxFont?.FontFamily
                        ?? FontFamily.GenericSansSerif,
                    9F,
                    FontStyle.Bold),
            };

            _maps.Dock = DockStyle.Fill;
            _maps.IntegralHeight = false;
            _maps.HorizontalScrollbar = true;
            _maps.Font = new Font(
                FontFamily.GenericMonospace,
                10F);

            foreach (string map in maps)
                _maps.Items.Add(new MapSelectionItem(
                    map,
                    ZombieSurvivalProfile.IsMapToken(map)
                        ? ZombieSurvivalProfile.Implemented
                            ? $"{ZombieSurvivalProfile.DisplayName}  [WORKSHOP]"
                            : $"{ZombieSurvivalProfile.DisplayName}  [NOT IMPLEMENTED YET]"
                        : map));

            _maps.Click += (_, _) =>
            {
                if (_maps.SelectedItem is not MapSelectionItem selected)
                {
                    return;
                }

                if (!ZombieSurvivalProfile.Implemented &&
                    ZombieSurvivalProfile.IsMapToken(selected.Token))
                {
                    return;
                }

                SelectedMap = selected.Token;
                DialogResult = DialogResult.OK;
                Close();
            };

            var cancel = new Button
            {
                Text = "CANCEL",
                Dock = DockStyle.Bottom,
                Height = 38,
            };

            cancel.Click += (_, _) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            Controls.Add(_maps);
            Controls.Add(cancel);
            Controls.Add(title);

            Shown += (_, _) =>
            {
                if (_maps.Items.Count > 0)
                {
                    _maps.SelectedIndex = -1;
                    _maps.Focus();
                }
            };
        }

        public string SelectedMap { get; private set; } =
            string.Empty;

        private sealed record MapSelectionItem(string Token, string Label)
        {
            public override string ToString() => Label;
        }
    }
}









