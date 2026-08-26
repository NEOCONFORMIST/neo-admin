using System.Text.Json;

namespace NeoAdmin;

internal sealed class InGameAdminManagerForm : NeoForm
{
    private readonly UdpVoiceReceiver _receiver;
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();
    private readonly Button _addButton = new();
    private readonly Button _editButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _refreshButton = new();
    private readonly List<InGameAdminRecord> _admins = new();

    public InGameAdminManagerForm(UdpVoiceReceiver receiver)
    {
        _receiver = receiver;
        Text = "In-Game Administrators";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 540);
        Size = new Size(1100, 650);
        ShowIcon = false;

        BuildUi();
        _receiver.PacketReceived += OnPacketReceived;
        Shown += async (_, _) => await RefreshAsync();
        FormClosed += (_, _) => _receiver.PacketReceived -= OnPacketReceived;
    }

    private InGameAdminRecord? SelectedAdmin
    {
        get
        {
            if (_grid.SelectedRows.Count != 1)
                return null;
            int index = _grid.SelectedRows[0].Index;
            return index >= 0 && index < _admins.Count ? _admins[index] : null;
        }
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

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "IN-GAME ADMINISTRATORS",
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

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
        _grid.Columns.Add("SteamId", "SteamID64");
        _grid.Columns.Add("Role", "In-game role");
        _grid.Columns.Add("Permissions", "Menu access");
        _grid.Columns.Add("Status", "Status");
        _grid.Columns[0].FillWeight = 120;
        _grid.Columns[1].FillWeight = 120;
        _grid.Columns[2].FillWeight = 90;
        _grid.Columns[3].FillWeight = 220;
        _grid.Columns[4].FillWeight = 65;
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                EditSelected();
        };
        root.Controls.Add(_grid, 0, 1);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0),
        };
        ConfigureButton(_addButton, "ADD IN-GAME ADMIN", 185, AddAdmin);
        ConfigureButton(_editButton, "EDIT PERMISSIONS", 170, EditSelected);
        ConfigureButton(_deleteButton, "DELETE", 100, DeleteSelected);
        ConfigureButton(_refreshButton, "REFRESH", 110, () => _ = RefreshAsync());
        toolbar.Controls.AddRange(new Control[]
        {
            _addButton, _editButton, _deleteButton, _refreshButton,
        });
        root.Controls.Add(toolbar, 0, 2);

        _status.Dock = DockStyle.Fill;
        _status.Text = "Waiting for the server...";
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        var close = new Button
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
        footer.Controls.Add(close, 1, 0);
        root.Controls.Add(footer, 0, 3);

        Controls.Add(root);
        CancelButton = close;
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

    private void UpdateButtons()
    {
        bool available = _receiver.Can(AdminPermission.ManageGameAdmins);
        bool selected = SelectedAdmin is not null;
        _addButton.Enabled = available;
        _refreshButton.Enabled = available;
        _editButton.Enabled = available && selected;
        _deleteButton.Enabled = available && selected;
    }

    private async Task RefreshAsync()
    {
        SetBusy(true, "Refreshing in-game administrators...");
        bool sent = await _receiver.SendAdminActionAsync(
            AdminActionCode.RequestGameAdmins,
            -1);
        if (!sent)
            SetBusy(false, "The in-game administrator request could not be sent.", true);
    }

    private void AddAdmin()
    {
        using var editor = new InGameAdminEditorForm(null);
        if (editor.ShowDialog(this) == DialogResult.OK)
            _ = SaveAsync(editor.Admin!);
    }

    private void EditSelected()
    {
        InGameAdminRecord? selected = SelectedAdmin;
        if (selected is null)
            return;
        using var editor = new InGameAdminEditorForm(selected);
        if (editor.ShowDialog(this) == DialogResult.OK)
            _ = SaveAsync(editor.Admin!);
    }

    private async Task SaveAsync(InGameAdminRecord admin)
    {
        string json = JsonSerializer.Serialize(new
        {
            steamId = admin.SteamId,
            displayName = admin.DisplayName,
            role = admin.Role,
            permissions = (ulong)admin.Permissions,
            enabled = admin.Enabled,
        });
        SetBusy(true, "Saving in-game administrator...");
        bool sent = await _receiver.SendAdminActionAsync(
            AdminActionCode.SaveGameAdmin,
            -1,
            0,
            json);
        if (!sent)
            SetBusy(false, "The in-game administrator update could not be sent.", true);
    }

    private void DeleteSelected()
    {
        InGameAdminRecord? selected = SelectedAdmin;
        if (selected is null)
            return;
        DialogResult confirmation = MessageBox.Show(
            this,
            $"Remove in-game menu access for {selected.DisplayName} ({selected.SteamId})?",
            "Delete In-Game Administrator",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
            return;
        _ = DeleteAsync(selected.SteamId);
    }

    private async Task DeleteAsync(string steamId)
    {
        SetBusy(true, "Deleting in-game administrator...");
        bool sent = await _receiver.SendAdminActionAsync(
            AdminActionCode.DeleteGameAdmin,
            -1,
            0,
            steamId);
        if (!sent)
            SetBusy(false, "The delete request could not be sent.", true);
    }

    private void OnPacketReceived(VoicePacket packet, System.Net.IPEndPoint _)
    {
        if (IsDisposed || Disposing)
            return;
        if (packet.MessageType == BridgeMessageType.GameAdminCatalog)
        {
            try
            {
                InGameAdminCatalog catalog =
                    InGameAdminCatalog.Parse(packet.GameAdminCatalogJson);
                BeginInvoke(() => LoadCatalog(catalog));
            }
            catch (Exception exception)
            {
                BeginInvoke(() => SetBusy(false, exception.Message, true));
            }
        }
        else if (packet.MessageType == BridgeMessageType.AdminActionResult &&
                 packet.AdminActionCode is >= 104 and <= 106)
        {
            BeginInvoke(() => SetBusy(
                false,
                packet.AdminActionMessage,
                !packet.AdminActionSucceeded));
        }
    }

    private void LoadCatalog(InGameAdminCatalog catalog)
    {
        string? selectedSteamId = SelectedAdmin?.SteamId;
        _admins.Clear();
        _admins.AddRange(catalog.Admins.OrderBy(admin => admin.DisplayName));
        _grid.Rows.Clear();
        foreach (InGameAdminRecord admin in _admins)
        {
            int row = _grid.Rows.Add(
                admin.DisplayName,
                admin.SteamId,
                admin.Role,
                DescribePermissions(admin.Permissions),
                admin.Enabled ? "Enabled" : "Disabled");
            if (!admin.Enabled)
                _grid.Rows[row].DefaultCellStyle.ForeColor = NeoTheme.MutedText;
        }
        if (selectedSteamId is not null)
        {
            int index = _admins.FindIndex(admin => admin.SteamId == selectedSteamId);
            if (index >= 0)
                _grid.Rows[index].Selected = true;
        }
        SetBusy(false, $"{_admins.Count} in-game administrator(s).");
    }

    private static string DescribePermissions(InGamePermission permissions)
    {
        if (permissions == InGameRoles.Administrator)
            return "Full in-game access";
        if (permissions == InGameRoles.Moderator)
            return "Player discipline";
        if (permissions == InGamePermission.None)
            return "No menu permissions";
        int count = Enum.GetValues<InGamePermission>()
            .Count(value => value != InGamePermission.None && permissions.HasFlag(value));
        return $"{count} custom permission(s)";
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
            _deleteButton.Enabled = false;
            _refreshButton.Enabled = false;
        }
        else
        {
            UpdateButtons();
        }
    }
}

internal sealed class InGameAdminEditorForm : NeoForm
{
    private readonly TextBox _displayName = new();
    private readonly TextBox _steamId = new();
    private readonly ComboBox _role = new();
    private readonly CheckBox _enabled = new();
    private readonly CheckedListBox _permissions = new();
    private bool _updatingPermissions;

    private static readonly (InGamePermission Permission, string Label)[] PermissionOptions =
    {
        (InGamePermission.ModeratePlayers, "Kick, slay, respawn, and move players"),
        (InGamePermission.ManageBans, "Create temporary and permanent bans"),
        (InGamePermission.ManageDiscipline, "Mute and gag players"),
        (InGamePermission.ControlBots, "Add and remove bots"),
        (InGamePermission.ControlMatch, "Restart, pause, and control matches"),
        (InGamePermission.ChangeMap, "Change maps from the in-game menu"),
        (InGamePermission.ManageMapRotation, "Run the saved map rotation"),
        (InGamePermission.ManageAnnouncements, "Send in-game announcements"),
    };

    public InGameAdminRecord? Admin { get; private set; }

    public InGameAdminEditorForm(InGameAdminRecord? admin)
    {
        Text = admin is null
            ? "Add In-Game Administrator"
            : "Edit In-Game Administrator";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(660, 500);
        MinimumSize = Size;
        MaximumSize = new Size(820, 650);
        ShowIcon = false;
        BuildUi(admin);
    }

    private void BuildUi(InGameAdminRecord? admin)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int index = 0; index < 4; ++index)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        _displayName.Dock = DockStyle.Fill;
        _steamId.Dock = DockStyle.Fill;
        _steamId.Enabled = admin is null;
        _role.Dock = DockStyle.Fill;
        _role.DropDownStyle = ComboBoxStyle.DropDownList;
        _role.Items.AddRange(new object[]
        {
            "Moderator", "Administrator", "Owner", "Custom",
        });
        _enabled.Text = "In-game menu access enabled";
        _enabled.Dock = DockStyle.Fill;
        _permissions.Dock = DockStyle.Fill;
        _permissions.CheckOnClick = true;
        foreach ((_, string label) in PermissionOptions)
            _permissions.Items.Add(label);

        _displayName.Text = admin?.DisplayName ?? string.Empty;
        _steamId.Text = admin?.SteamId ?? string.Empty;
        _role.SelectedItem = admin?.Role ?? "Moderator";
        _enabled.Checked = admin?.Enabled ?? true;
        ApplyPermissionMask(admin?.Permissions ?? InGameRoles.Moderator);

        _role.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingPermissions &&
                _role.SelectedItem is string role && role != "Custom")
            {
                ApplyPermissionMask(InGameRoles.ForName(role));
            }
        };
        _permissions.ItemCheck += (_, _) =>
        {
            if (_updatingPermissions)
                return;
            BeginInvoke(() =>
            {
                InGamePermission selected = GetPermissionMask();
                string matchingRole =
                    new[] { "Moderator", "Administrator", "Owner" }
                        .FirstOrDefault(role => InGameRoles.ForName(role) == selected)
                    ?? "Custom";
                _updatingPermissions = true;
                _role.SelectedItem = matchingRole;
                _updatingPermissions = false;
            });
        };

        AddRow(root, 0, "Display name", _displayName);
        AddRow(root, 1, "SteamID64", _steamId);
        AddRow(root, 2, "In-game role", _role);
        AddRow(root, 3, "Status", _enabled);
        root.Controls.Add(new Label
        {
            Text = "Menu permissions",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 6, 0, 0),
        }, 0, 4);
        root.Controls.Add(_permissions, 1, 4);

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
        root.Controls.Add(buttons, 0, 5);
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

    private void ApplyPermissionMask(InGamePermission permissions)
    {
        _updatingPermissions = true;
        for (int index = 0; index < PermissionOptions.Length; ++index)
        {
            InGamePermission value = PermissionOptions[index].Permission;
            _permissions.SetItemChecked(index, (permissions & value) == value);
        }
        _updatingPermissions = false;
    }

    private InGamePermission GetPermissionMask()
    {
        InGamePermission result = InGamePermission.None;
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
        string steamId = _steamId.Text.Trim();
        if (displayName.Length is < 1 or > 64)
        {
            MessageBox.Show(
                this,
                "Enter a display name.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _displayName.Focus();
            return;
        }
        if (!ulong.TryParse(steamId, out ulong parsedSteamId) ||
            parsedSteamId < 76561197960265728UL)
        {
            MessageBox.Show(
                this,
                "Enter a valid SteamID64 beginning with 7656.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _steamId.Focus();
            return;
        }
        InGamePermission permissions = GetPermissionMask();
        if (_enabled.Checked && permissions == InGamePermission.None)
        {
            MessageBox.Show(
                this,
                "Select at least one in-game menu permission, or disable this administrator.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        Admin = new InGameAdminRecord
        {
            SteamId = steamId,
            DisplayName = displayName,
            Role = _role.SelectedItem?.ToString() ?? "Custom",
            Permissions = permissions,
            Enabled = _enabled.Checked,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
