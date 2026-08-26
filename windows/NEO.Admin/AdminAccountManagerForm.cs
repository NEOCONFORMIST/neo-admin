using System.Security.Cryptography;
using System.Text.Json;

namespace NeoAdmin;

internal sealed class AdminAccountManagerForm : NeoForm
{
    private readonly UdpVoiceReceiver _receiver;
    private readonly string _serverAddress;
    private readonly int _serverPort;
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();
    private readonly Button _addButton = new();
    private readonly Button _editButton = new();
    private readonly Button _rotateButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _refreshButton = new();
    private readonly List<AdminAccountRecord> _accounts = new();
    private PendingCredential? _pendingCredential;

    public AdminAccountManagerForm(
        UdpVoiceReceiver receiver,
        string serverAddress,
        int serverPort)
    {
        _receiver = receiver;
        _serverAddress = serverAddress;
        _serverPort = serverPort;

        Text = "Administrator Accounts";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 560);
        Size = new Size(1100, 680);
        ShowIcon = false;

        BuildUi();
        _receiver.PacketReceived += OnPacketReceived;
        Shown += async (_, _) => await RefreshAccountsAsync();
        FormClosed += (_, _) => _receiver.PacketReceived -= OnPacketReceived;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var heading = new Label
        {
            Dock = DockStyle.Fill,
            Text = "ADMINISTRATOR ACCOUNTS",
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.Columns.Add("DisplayName", "Name");
        _grid.Columns.Add("Id", "Account ID");
        _grid.Columns.Add("Role", "Role");
        _grid.Columns.Add("Status", "Status");
        _grid.Columns.Add("Expires", "Access expires");
        _grid.Columns.Add("Credential", "Credential");
        _grid.Columns[0].FillWeight = 130;
        _grid.Columns[1].FillWeight = 110;
        _grid.Columns[2].FillWeight = 95;
        _grid.Columns[3].FillWeight = 65;
        _grid.Columns[4].FillWeight = 105;
        _grid.Columns[5].FillWeight = 95;
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                EditSelected();
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0),
        };

        ConfigureButton(_addButton, "ADD ACCOUNT", 150, async () => await AddAccountAsync());
        ConfigureButton(_editButton, "EDIT", 96, EditSelected);
        ConfigureButton(_rotateButton, "NEW ACCESS KEY", 178, async () => await RotateSelectedAsync());
        ConfigureButton(_deleteButton, "DELETE", 100, async () => await DeleteSelectedAsync());
        ConfigureButton(_refreshButton, "REFRESH", 110, async () => await RefreshAccountsAsync());
        toolbar.Controls.AddRange(new Control[]
        {
            _addButton, _editButton, _rotateButton, _deleteButton, _refreshButton,
        });

        _status.Dock = DockStyle.Fill;
        _status.Text = "Waiting for the server...";
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;

        var closeButton = new Button
        {
            Text = "CLOSE",
            Dock = DockStyle.Right,
            Width = 100,
            DialogResult = DialogResult.Cancel,
        };
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(closeButton, 1, 0);

        root.Controls.Add(heading, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(toolbar, 0, 2);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);
        CancelButton = closeButton;
        UpdateButtons();
    }

    private static void ConfigureButton(
        Button button,
        string text,
        int width,
        Action action)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 30;
        button.Click += (_, _) => action();
    }

    private AdminAccountRecord? SelectedAccount
    {
        get
        {
            if (_grid.SelectedRows.Count != 1)
                return null;
            int index = _grid.SelectedRows[0].Index;
            return index >= 0 && index < _accounts.Count ? _accounts[index] : null;
        }
    }

    private void UpdateButtons()
    {
        bool available = _receiver.Can(AdminPermission.ManageAccounts);
        bool selected = SelectedAccount is not null;
        _addButton.Enabled = available;
        _refreshButton.Enabled = available;
        _editButton.Enabled = available && selected;
        _rotateButton.Enabled = available && selected;
        _deleteButton.Enabled = available && selected &&
            !string.Equals(
                SelectedAccount!.Id,
                _receiver.CurrentSession?.AccountId,
                StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshAccountsAsync()
    {
        SetBusy(true, "Refreshing administrator accounts...");
        bool sent = await _receiver.SendAdminActionAsync(
            AdminActionCode.RequestAdminAccounts,
            -1);
        if (!sent)
            SetBusy(false, "The account-list request could not be sent.", true);
    }

    private async Task AddAccountAsync()
    {
        using var editor = new AdminAccountEditorForm(null);
        if (editor.ShowDialog(this) != DialogResult.OK)
            return;

        string accessKey = GenerateAccessKey();
        await SaveAccountAsync(editor.Account!, accessKey, true);
    }

    private void EditSelected()
    {
        AdminAccountRecord? selected = SelectedAccount;
        if (selected is null)
            return;

        using var editor = new AdminAccountEditorForm(selected);
        if (editor.ShowDialog(this) != DialogResult.OK)
            return;

        _ = SaveAccountAsync(editor.Account!, string.Empty, false);
    }

    private async Task RotateSelectedAsync()
    {
        AdminAccountRecord? selected = SelectedAccount;
        if (selected is null)
            return;

        DialogResult confirmation = MessageBox.Show(
            this,
            $"Generate a new access key for {selected.DisplayName}?\n\nThe old key will stop working after the account reconnects.",
            "Replace Access Key",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
            return;

        await SaveAccountAsync(selected, GenerateAccessKey(), true);
    }

    private async Task SaveAccountAsync(
        AdminAccountRecord account,
        string accessKey,
        bool showCredential)
    {
        string json = JsonSerializer.Serialize(new
        {
            id = account.Id,
            displayName = account.DisplayName,
            role = account.Role,
            permissions = (ulong)account.Permissions,
            enabled = account.Enabled,
            expiresUnix = account.ExpiresUnix,
            secret = accessKey,
        });

        _pendingCredential = showCredential
            ? new PendingCredential(account.DisplayName, account.Id, accessKey)
            : null;

        SetBusy(true, "Saving administrator account...");
        bool sent = await _receiver.SendAdminActionAsync(
            AdminActionCode.SaveAdminAccount,
            -1,
            0,
            json);
        if (!sent)
        {
            _pendingCredential = null;
            SetBusy(false, "The account update could not be sent.", true);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        AdminAccountRecord? selected = SelectedAccount;
        if (selected is null)
            return;

        DialogResult confirmation = MessageBox.Show(
            this,
            $"Delete the administrator account '{selected.DisplayName}'?\n\nThat access key will stop working immediately.",
            "Delete Administrator Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
            return;

        SetBusy(true, "Deleting administrator account...");
        bool sent = await _receiver.SendAdminActionAsync(
            AdminActionCode.DeleteAdminAccount,
            -1,
            0,
            selected.Id);
        if (!sent)
            SetBusy(false, "The delete request could not be sent.", true);
    }

    private void OnPacketReceived(VoicePacket packet, System.Net.IPEndPoint _)
    {
        if (IsDisposed || Disposing)
            return;

        if (packet.MessageType == BridgeMessageType.AdminAccountCatalog)
        {
            try
            {
                AdminAccountCatalog catalog =
                    AdminAccountCatalog.Parse(packet.AdminAccountCatalogJson);
                BeginInvoke(() => LoadCatalog(catalog));
            }
            catch (Exception exception)
            {
                BeginInvoke(() => SetBusy(false, exception.Message, true));
            }
        }
        else if (packet.MessageType == BridgeMessageType.AdminActionResult &&
                 packet.AdminActionCode is >= 100 and <= 102)
        {
            BeginInvoke(() => HandleActionResult(packet));
        }
    }

    private void LoadCatalog(AdminAccountCatalog catalog)
    {
        string? selectedId = SelectedAccount?.Id;
        _accounts.Clear();
        _accounts.AddRange(catalog.Accounts.OrderBy(account => account.DisplayName));
        _grid.Rows.Clear();

        foreach (AdminAccountRecord account in _accounts)
        {
            int row = _grid.Rows.Add(
                account.DisplayName,
                account.Id,
                account.Role,
                account.IsExpired
                    ? "Expired"
                    : account.Enabled ? "Enabled" : "Disabled",
                FormatAccountExpiry(account.ExpiresUnix),
                account.Credential);
            if (!account.Enabled || account.IsExpired)
                _grid.Rows[row].DefaultCellStyle.ForeColor = NeoTheme.MutedText;
        }

        if (selectedId is not null)
        {
            int index = _accounts.FindIndex(account => account.Id == selectedId);
            if (index >= 0)
                _grid.Rows[index].Selected = true;
        }

        SetBusy(false, $"{_accounts.Count} administrator account(s).", false);
    }

    private static string FormatAccountExpiry(ulong expiresUnix)
    {
        if (expiresUnix == 0)
            return "Never";
        return DateTimeOffset.FromUnixTimeSeconds((long)expiresUnix)
            .LocalDateTime
            .ToString("yyyy-MM-dd HH:mm");
    }

    private void HandleActionResult(VoicePacket packet)
    {
        SetBusy(false, packet.AdminActionMessage, !packet.AdminActionSucceeded);
        if (packet.AdminActionCode == (uint)AdminActionCode.SaveAdminAccount &&
            packet.AdminActionSucceeded && _pendingCredential is not null)
        {
            PendingCredential credential = _pendingCredential;
            _pendingCredential = null;
            using var reveal = new AdminAccessKeyForm(
                credential,
                _serverAddress,
                _serverPort);
            reveal.ShowDialog(this);
        }
        else if (!packet.AdminActionSucceeded)
        {
            _pendingCredential = null;
        }
    }

    private void SetBusy(bool busy, string message, bool error = false)
    {
        _status.Text = message;
        _status.ForeColor = error ? NeoTheme.Danger : NeoTheme.MutedText;
        _grid.Enabled = !busy;
        if (busy)
        {
            _addButton.Enabled = false;
            _editButton.Enabled = false;
            _rotateButton.Enabled = false;
            _deleteButton.Enabled = false;
            _refreshButton.Enabled = false;
        }
        else
        {
            UpdateButtons();
        }
    }

    private static string GenerateAccessKey()
    {
        string value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return value.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal sealed record PendingCredential(
        string DisplayName,
        string AccountId,
        string AccessKey);
}

internal sealed class AdminAccountEditorForm : NeoForm
{
    private readonly TextBox _displayName = new();
    private readonly TextBox _accountId = new();
    private readonly ComboBox _role = new();
    private readonly CheckBox _enabled = new();
    private readonly DateTimePicker _expires = new();
    private readonly CheckedListBox _permissions = new();
    private bool _updatingPermissions;

    private static readonly (AdminPermission Permission, string Label)[] PermissionOptions =
    {
        (AdminPermission.ViewDashboard, "View dashboard, players, maps, and server status"),
        (AdminPermission.ViewSteamIds, "View detailed Steam IDs"),
        (AdminPermission.SendChat, "Send server chat messages"),
        (AdminPermission.BroadcastVoice, "Use server broadcast microphone"),
        (AdminPermission.ModeratePlayers, "Kick, slay, respawn, and move players"),
        (AdminPermission.ControlBots, "Add and remove bots"),
        (AdminPermission.ControlMatch, "Restart, pause, and control matches"),
        (AdminPermission.ChangeMap, "Change the current map"),
        (AdminPermission.TeleportPlayers, "Drag players on the live map"),
        (AdminPermission.ManageAccounts, "Create and manage administrator accounts"),
        (AdminPermission.ManageGameAdmins, "Manage the separate in-game administrator list"),
        (AdminPermission.RestartServer, "Restart the game server"),
        (AdminPermission.DeployPlugin, "Deploy server plugin updates"),
        (AdminPermission.ViewAuditLog, "View the administrator audit log"),
        (AdminPermission.ManageBans, "Create, search, edit, and remove bans"),
        (AdminPermission.ManageDiscipline, "Manage mutes, gags, and player discipline history"),
        (AdminPermission.ManageMapRotation, "Manage map rotations and scheduled map changes"),
        (AdminPermission.ManageAnnouncements, "Send and schedule server announcements"),
        (AdminPermission.RunServerConsole, "Run commands in the CS2 server console"),
        (AdminPermission.ManageWorkshopMaps, "Install and update CS2 Workshop maps"),
        (AdminPermission.ManageZombieMode, "Zombie Survival mode (not implemented yet)"),
    };

    public AdminAccountRecord? Account { get; private set; }

    public AdminAccountEditorForm(AdminAccountRecord? account)
    {
        Text = account is null ? "Add Administrator" : "Edit Administrator";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(650, 625);
        MinimumSize = new Size(650, 625);
        MaximumSize = new Size(800, 760);
        ShowIcon = false;
        BuildUi(account);
    }

    private void BuildUi(AdminAccountRecord? account)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            Padding = new Padding(16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        _displayName.Dock = DockStyle.Fill;
        _accountId.Dock = DockStyle.Fill;
        _accountId.Enabled = account is null;
        _role.Dock = DockStyle.Fill;
        _role.DropDownStyle = ComboBoxStyle.DropDownList;
        _role.Items.AddRange(new object[]
        {
            "Viewer", "Moderator", "Event Admin", "Administrator",
            "Senior Admin", "Owner", "Custom",
        });
        _enabled.Text = "Account enabled";
        _enabled.Checked = account?.Enabled ?? true;
        _enabled.Dock = DockStyle.Fill;

        _expires.Dock = DockStyle.Left;
        _expires.Width = 210;
        _expires.Format = DateTimePickerFormat.Custom;
        _expires.CustomFormat = "yyyy-MM-dd  HH:mm";
        _expires.ShowCheckBox = true;
        _expires.Checked = account?.ExpiresUnix > 0;
        _expires.MinDate = new DateTime(2020, 1, 1);
        _expires.Value = account?.ExpiresUnix > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)account.ExpiresUnix).LocalDateTime
            : DateTime.Now.AddDays(7);

        _permissions.Dock = DockStyle.Fill;
        _permissions.CheckOnClick = true;
        foreach ((_, string label) in PermissionOptions)
            _permissions.Items.Add(label);

        _displayName.Text = account?.DisplayName ?? string.Empty;
        _accountId.Text = account?.Id ?? string.Empty;
        _role.SelectedItem = account?.Role ?? "Moderator";
        ApplyPermissionMask(account?.Permissions ?? AdminRoles.Moderator);

        _role.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingPermissions && _role.SelectedItem is string role && role != "Custom")
                ApplyPermissionMask(AdminRoles.ForName(role));
        };
        _permissions.ItemCheck += (_, _) =>
        {
            if (_updatingPermissions)
                return;
            BeginInvoke(() =>
            {
                AdminPermission selected = GetPermissionMask();
                string matchingRole = new[]
                    {
                        "Viewer", "Moderator", "Event Admin", "Administrator",
                        "Senior Admin", "Owner",
                    }
                    .FirstOrDefault(role => AdminRoles.ForName(role) == selected)
                    ?? "Custom";
                _updatingPermissions = true;
                _role.SelectedItem = matchingRole;
                _updatingPermissions = false;
            });
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var save = new Button { Text = "SAVE", Width = 100, Height = 32 };
        var cancel = new Button
        {
            Text = "CANCEL",
            Width = 100,
            Height = 32,
            DialogResult = DialogResult.Cancel,
        };
        save.Click += (_, _) => SaveAndClose();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);

        AddRow(root, 0, "Display name", _displayName);
        AddRow(root, 1, "Account ID", _accountId);
        AddRow(root, 2, "Role", _role);
        AddRow(root, 3, "Status", _enabled);
        AddRow(root, 4, "Access expires", _expires);
        root.Controls.Add(new Label
        {
            Text = "Permissions",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 6, 0, 0),
        }, 0, 5);
        root.Controls.Add(_permissions, 1, 5);
        root.Controls.Add(buttons, 0, 6);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static void AddRow(
        TableLayoutPanel panel,
        int row,
        string label,
        Control control)
    {
        panel.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private void ApplyPermissionMask(AdminPermission permissions)
    {
        _updatingPermissions = true;
        for (int index = 0; index < PermissionOptions.Length; ++index)
        {
            AdminPermission value = PermissionOptions[index].Permission;
            _permissions.SetItemChecked(index, (permissions & value) == value);
        }
        _updatingPermissions = false;
    }

    private AdminPermission GetPermissionMask()
    {
        AdminPermission result = AdminPermission.None;
        for (int index = 0; index < PermissionOptions.Length; ++index)
        {
            if (_permissions.GetItemChecked(index))
                result |= PermissionOptions[index].Permission;
        }
        return result;
    }

    private void SaveAndClose()
    {
        string displayName = _displayName.Text.Trim();
        string id = _accountId.Text.Trim();
        if (displayName.Length is < 1 or > 64)
        {
            MessageBox.Show(this, "Enter a display name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _displayName.Focus();
            return;
        }
        if (id.Length is < 3 or > 32 ||
            id.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '.' and not '_' and not '-'))
        {
            MessageBox.Show(
                this,
                "Account ID must be 3-32 letters, numbers, dots, dashes, or underscores.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _accountId.Focus();
            return;
        }
        AdminPermission permissions = GetPermissionMask();
        if ((permissions & AdminPermission.ViewDashboard) == 0)
        {
            MessageBox.Show(
                this,
                "Every account must be able to view the dashboard.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        ulong expiresUnix = _expires.Checked
            ? (ulong)new DateTimeOffset(_expires.Value).ToUnixTimeSeconds()
            : 0;
        if (expiresUnix != 0 &&
            expiresUnix <= (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            MessageBox.Show(
                this,
                "The access expiration must be in the future.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        Account = new AdminAccountRecord
        {
            Id = id,
            DisplayName = displayName,
            Role = _role.SelectedItem?.ToString() ?? "Custom",
            Permissions = permissions,
            Enabled = _enabled.Checked,
            ExpiresUnix = expiresUnix,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class AdminAccessKeyForm : NeoForm
{
    public AdminAccessKeyForm(
        AdminAccountManagerForm.PendingCredential credential,
        string serverAddress,
        int serverPort)
    {
        Text = "Administrator Access Created";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(740, 350);
        MinimumSize = Size;
        MaximumSize = Size;
        ShowIcon = false;

        var keyBox = new TextBox
        {
            Text = credential.AccessKey,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10F),
        };
        var accountBox = new TextBox
        {
            Text = credential.AccountId,
            ReadOnly = true,
            Dock = DockStyle.Fill,
        };
        var copy = new Button { Text = "COPY KEY", Width = 115, Height = 32 };
        var export = new Button { Text = "SAVE ACCESS PROFILE", Width = 210, Height = 32 };
        var close = new Button
        {
            Text = "CLOSE",
            Width = 110,
            Height = 32,
            DialogResult = DialogResult.OK,
        };
        copy.Click += (_, _) => Clipboard.SetText(credential.AccessKey);
        export.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save NEO ADMIN access profile",
                Filter = "NEO ADMIN profile (*.neo-admin-profile.json)|*.neo-admin-profile.json|JSON files (*.json)|*.json",
                FileName = $"{credential.AccountId}.neo-admin-profile.json",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            new AdminAccessProfile
            {
                ServerAddress = serverAddress,
                ServerPttPort = serverPort,
                AdminId = credential.AccountId,
                AccessKey = credential.AccessKey,
            }.Save(dialog.FileName);
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(16),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new Label
        {
            Text = $"Access created for {credential.DisplayName}",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);
        AddValueRow(layout, 1, "Account ID", accountBox);
        AddValueRow(layout, 2, "Access key", keyBox);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        buttons.Controls.Add(copy);
        buttons.Controls.Add(export);
        buttons.Controls.Add(close);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        var warning = new Label
        {
            Text = "This key is shown once. Save the access profile before closing.",
            Dock = DockStyle.Fill,
            ForeColor = NeoTheme.Danger,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        layout.Controls.Add(warning, 0, 4);
        layout.SetColumnSpan(warning, 2);

        var placement = new Label
        {
            Text =
                "Where to use the saved file:\r\n" +
                "Keep it in a private folder on the administrator's Windows PC. " +
                "In NEO ADMIN, choose Settings > Import Access Profile. " +
                "Do not copy it to the CS2 server.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
        };
        layout.Controls.Add(placement, 0, 5);
        layout.SetColumnSpan(placement, 2);
        Controls.Add(layout);
        AcceptButton = close;
    }

    private static void AddValueRow(
        TableLayoutPanel layout,
        int row,
        string label,
        Control value)
    {
        layout.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, row);
        layout.Controls.Add(value, 1, row);
    }
}
