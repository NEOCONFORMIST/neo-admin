using System.Text.Json;

namespace NeoAdmin;

internal sealed class AdminBanManagerForm : NeoForm
{
    private readonly UdpVoiceReceiver _receiver;
    private readonly AdminBanTarget? _initialTarget;
    private readonly DataGridView _grid = new();
    private readonly TextBox _search = new();
    private readonly Label _status = new();
    private readonly Button _add = new();
    private readonly Button _edit = new();
    private readonly Button _unban = new();
    private readonly Button _refresh = new();
    private readonly List<AdminBanRecord> _bans = new();

    public AdminBanManagerForm(
        UdpVoiceReceiver receiver,
        AdminBanTarget? initialTarget = null)
    {
        _receiver = receiver;
        _initialTarget = initialTarget;
        Text = "Ban Management";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 580);
        Size = new Size(1220, 720);
        ShowIcon = false;
        BuildUi();
        _receiver.PacketReceived += OnPacketReceived;
        Shown += async (_, _) =>
        {
            if (_initialTarget is not null)
                await ShowEditorAsync(_initialTarget, null);
            else
                await RefreshAsync();
        };
        FormClosed += (_, _) => _receiver.PacketReceived -= OnPacketReceived;
    }

    private AdminBanRecord? SelectedBan =>
        _grid.SelectedRows.Count == 1
            ? _grid.SelectedRows[0].Tag as AdminBanRecord
            : null;

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "ACTIVE BANS",
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        var searchRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.Controls.Add(new Label
        {
            Text = "Search",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "Player, SteamID64, reason, or administrator";
        _search.TextChanged += (_, _) => ApplyFilter();
        searchRow.Controls.Add(_search, 1, 0);
        root.Controls.Add(searchRow, 0, 1);

        ConfigureGrid();
        root.Controls.Add(_grid, 0, 2);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0),
        };
        ConfigureButton(_add, "ADD BAN", 115, async () => await ShowEditorAsync(null, null));
        ConfigureButton(_edit, "EDIT", 95, async () => await EditSelectedAsync());
        ConfigureButton(_unban, "UNBAN", 105, async () => await UnbanSelectedAsync());
        ConfigureButton(_refresh, "REFRESH", 110, async () => await RefreshAsync());
        toolbar.Controls.AddRange(new Control[] { _add, _edit, _unban, _refresh });
        root.Controls.Add(toolbar, 0, 3);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        _status.Dock = DockStyle.Fill;
        _status.Text = "Waiting for the server...";
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        footer.Controls.Add(_status, 0, 0);
        var close = new Button
        {
            Text = "CLOSE",
            Dock = DockStyle.Right,
            Width = 100,
            DialogResult = DialogResult.Cancel,
        };
        footer.Controls.Add(close, 1, 0);
        root.Controls.Add(footer, 0, 4);
        Controls.Add(root);
        CancelButton = close;
    }

    private void ConfigureGrid()
    {
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
        _grid.Columns.Add("Player", "Player");
        _grid.Columns.Add("SteamId", "SteamID64");
        _grid.Columns.Add("Reason", "Reason");
        _grid.Columns.Add("CreatedBy", "Created by");
        _grid.Columns.Add("Created", "Created");
        _grid.Columns.Add("Expires", "Expires");
        _grid.Columns[0].FillWeight = 105;
        _grid.Columns[1].FillWeight = 115;
        _grid.Columns[2].FillWeight = 165;
        _grid.Columns[3].FillWeight = 85;
        _grid.Columns[4].FillWeight = 100;
        _grid.Columns[5].FillWeight = 100;
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0)
                await EditSelectedAsync();
        };
    }

    private static void ConfigureButton(
        Button button,
        string text,
        int width,
        Func<Task> action)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 32;
        button.Click += async (_, _) => await action();
    }

    private async Task RefreshAsync()
    {
        SetBusy(true, "Refreshing active bans...");
        bool sent = await _receiver.SendAdminActionAsync(
            AdminActionCode.RequestBanCatalog,
            -1);
        if (!sent)
            SetBusy(false, "The ban-list request could not be sent.", true);
    }

    private async Task EditSelectedAsync()
    {
        AdminBanRecord? selected = SelectedBan;
        if (selected is not null)
            await ShowEditorAsync(null, selected);
    }

    private async Task ShowEditorAsync(
        AdminBanTarget? target,
        AdminBanRecord? existing)
    {
        using var editor = new AdminBanEditorForm(target, existing);
        if (editor.ShowDialog(this) != DialogResult.OK)
            return;

        string json = JsonSerializer.Serialize(new
        {
            steamId = editor.SteamId,
            playerName = editor.PlayerName,
            reason = editor.Reason,
            durationMinutes = editor.DurationMinutes,
        });
        SetBusy(true, existing is null ? "Saving ban..." : "Updating ban...");
        bool sent = await _receiver.SendAdminActionAsync(
            AdminActionCode.SaveBan,
            target?.PlayerSlot ?? -1,
            0,
            json);
        if (!sent)
            SetBusy(false, "The ban request could not be sent.", true);
    }

    private async Task UnbanSelectedAsync()
    {
        AdminBanRecord? selected = SelectedBan;
        if (selected is null)
            return;
        DialogResult confirmation = MessageBox.Show(
            this,
            $"Remove the ban for {selected.PlayerName} ({selected.SteamId})?",
            "Unban Player",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
            return;

        SetBusy(true, "Removing ban...");
        bool sent = await _receiver.SendAdminActionAsync(
            AdminActionCode.DeleteBan,
            -1,
            0,
            selected.SteamId);
        if (!sent)
            SetBusy(false, "The unban request could not be sent.", true);
    }

    private void OnPacketReceived(VoicePacket packet, System.Net.IPEndPoint _)
    {
        if (IsDisposed || Disposing)
            return;
        if (packet.MessageType == BridgeMessageType.AdminBanCatalog)
        {
            try
            {
                AdminBanCatalog catalog =
                    AdminBanCatalog.Parse(packet.AdminBanCatalogJson);
                BeginInvoke(() => LoadCatalog(catalog));
            }
            catch (Exception exception)
            {
                BeginInvoke(() => SetBusy(false, exception.Message, true));
            }
        }
        else if (packet.MessageType == BridgeMessageType.AdminActionResult &&
                 packet.AdminActionCode is >= 110 and <= 112)
        {
            BeginInvoke(() => SetBusy(
                false,
                packet.AdminActionMessage,
                !packet.AdminActionSucceeded));
        }
    }

    private void LoadCatalog(AdminBanCatalog catalog)
    {
        string? selectedSteamId = SelectedBan?.SteamId;
        _bans.Clear();
        _bans.AddRange(catalog.Bans.OrderBy(item => item.PlayerName));
        ApplyFilter();
        if (selectedSteamId is not null)
        {
            DataGridViewRow? row = _grid.Rows
                .Cast<DataGridViewRow>()
                .FirstOrDefault(item =>
                    (item.Tag as AdminBanRecord)?.SteamId == selectedSteamId);
            if (row is not null)
                row.Selected = true;
        }
        SetBusy(false, $"{_bans.Count} active ban(s).");
    }

    private void ApplyFilter()
    {
        string search = _search.Text.Trim();
        IEnumerable<AdminBanRecord> filtered = _bans.Where(item =>
            search.Length == 0 ||
            item.PlayerName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.SteamId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.Reason.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            item.CreatedBy.Contains(search, StringComparison.OrdinalIgnoreCase));
        _grid.Rows.Clear();
        foreach (AdminBanRecord item in filtered)
        {
            int rowIndex = _grid.Rows.Add(
                item.PlayerName,
                item.SteamId,
                item.Reason,
                item.CreatedBy,
                FormatUtc(item.CreatedUtc),
                FormatExpiration(item.ExpiresUnix));
            _grid.Rows[rowIndex].Tag = item;
        }
        _status.Text = $"Showing {_grid.Rows.Count} of {_bans.Count} active ban(s).";
        UpdateButtons();
    }

    private void SetBusy(bool busy, string message, bool error = false)
    {
        _grid.Enabled = !busy;
        _search.Enabled = !busy;
        _status.Text = message;
        _status.ForeColor = error ? NeoTheme.Danger : NeoTheme.MutedText;
        if (busy)
        {
            _add.Enabled = false;
            _edit.Enabled = false;
            _unban.Enabled = false;
            _refresh.Enabled = false;
        }
        else
        {
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        bool available = _receiver.Can(AdminPermission.ManageBans);
        _add.Enabled = available;
        _refresh.Enabled = available;
        _edit.Enabled = available && SelectedBan is not null;
        _unban.Enabled = available && SelectedBan is not null;
    }

    private static string FormatUtc(string value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : value;

    private static string FormatExpiration(ulong value)
    {
        if (value == 0)
            return "Permanent";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(checked((long)value))
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm");
        }
        catch (ArgumentOutOfRangeException)
        {
            return value.ToString();
        }
    }
}

internal sealed class AdminBanEditorForm : NeoForm
{
    private readonly TextBox _steamId = new();
    private readonly TextBox _playerName = new();
    private readonly TextBox _reason = new();
    private readonly ComboBox _duration = new();
    private readonly NumericUpDown _customAmount = new();
    private readonly ComboBox _customUnit = new();

    public string SteamId { get; private set; } = string.Empty;
    public string PlayerName { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public long DurationMinutes { get; private set; }

    public AdminBanEditorForm(AdminBanTarget? target, AdminBanRecord? existing)
    {
        Text = existing is null ? "Ban Player" : "Edit Ban";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(650, 330);
        MinimumSize = new Size(650, 330);
        MaximumSize = new Size(780, 420);
        ShowIcon = false;
        BuildUi(target, existing);
    }

    private void BuildUi(AdminBanTarget? target, AdminBanRecord? existing)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 4; ++row)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _steamId.Dock = DockStyle.Fill;
        _steamId.Text = existing?.SteamId ?? target?.SteamId ?? string.Empty;
        _steamId.ReadOnly = existing is not null || target is not null;
        _playerName.Dock = DockStyle.Fill;
        _playerName.Text = existing?.PlayerName ?? target?.PlayerName ?? string.Empty;
        _reason.Dock = DockStyle.Fill;
        _reason.Text = existing?.Reason ?? string.Empty;
        _reason.MaxLength = 160;

        _duration.Dock = DockStyle.Fill;
        _duration.DropDownStyle = ComboBoxStyle.DropDownList;
        _duration.Items.AddRange(new object[]
        {
            new DurationChoice("Permanent", 0),
            new DurationChoice("30 minutes", 30),
            new DurationChoice("1 hour", 60),
            new DurationChoice("1 day", 1440),
            new DurationChoice("1 week", 10080),
            new DurationChoice("30 days", 43200),
            new DurationChoice("Custom duration", -1),
        });
        _duration.SelectedIndex = existing?.ExpiresUnix == 0 ? 0 : 2;
        _duration.SelectedIndexChanged += (_, _) => UpdateCustomDuration();

        var custom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        _customAmount.Minimum = 1;
        _customAmount.Maximum = 1825;
        _customAmount.Value = 1;
        _customAmount.Width = 120;
        _customUnit.DropDownStyle = ComboBoxStyle.DropDownList;
        _customUnit.Items.AddRange(new object[] { "Minutes", "Hours", "Days" });
        _customUnit.SelectedIndex = 2;
        _customUnit.Width = 130;
        custom.Controls.Add(_customAmount);
        custom.Controls.Add(_customUnit);

        AddRow(root, 0, "SteamID64", _steamId);
        AddRow(root, 1, "Player name", _playerName);
        AddRow(root, 2, "Reason", _reason);
        AddRow(root, 3, "Duration", _duration);
        AddRow(root, 4, "Custom", custom);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var save = new Button { Text = "SAVE BAN", Width = 115, Height = 32 };
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
        UpdateCustomDuration();
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

    private void UpdateCustomDuration()
    {
        bool enabled = (_duration.SelectedItem as DurationChoice)?.Minutes == -1;
        _customAmount.Enabled = enabled;
        _customUnit.Enabled = enabled;
    }

    private void SaveAndClose()
    {
        string steamId = _steamId.Text.Trim();
        string playerName = _playerName.Text.Trim();
        string reason = _reason.Text.Trim();
        if (!ulong.TryParse(steamId, out ulong parsed) ||
            parsed < 76561197960265728UL)
        {
            MessageBox.Show(this, "Enter a valid SteamID64.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _steamId.Focus();
            return;
        }
        if (playerName.Length > 64 || playerName.Any(char.IsControl))
        {
            MessageBox.Show(this, "Player name must be 64 characters or fewer.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _playerName.Focus();
            return;
        }
        if (reason.Length is < 1 or > 160 || reason.Any(char.IsControl))
        {
            MessageBox.Show(this, "Enter a one-line ban reason (160 characters maximum).", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _reason.Focus();
            return;
        }

        long minutes = (_duration.SelectedItem as DurationChoice)?.Minutes ?? 0;
        if (minutes == -1)
        {
            long multiplier = _customUnit.SelectedIndex switch
            {
                1 => 60,
                2 => 1440,
                _ => 1,
            };
            minutes = checked((long)_customAmount.Value * multiplier);
        }
        SteamId = steamId;
        PlayerName = playerName.Length == 0 ? steamId : playerName;
        Reason = reason;
        DurationMinutes = minutes;
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record DurationChoice(string Label, long Minutes)
    {
        public override string ToString() => Label;
    }
}
