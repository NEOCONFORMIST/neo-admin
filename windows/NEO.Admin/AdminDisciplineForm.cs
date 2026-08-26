using System.Text.Json;

namespace NeoAdmin;

internal sealed class AdminDisciplineForm : NeoForm
{
    private readonly UdpVoiceReceiver _receiver;
    private readonly AdminBanTarget? _initialTarget;
    private readonly string? _initialType;
    private readonly DataGridView _grid = new();
    private readonly TextBox _search = new();
    private readonly Label _status = new();
    private readonly List<RestrictionRecord> _records = new();

    public AdminDisciplineForm(
        UdpVoiceReceiver receiver,
        AdminBanTarget? initialTarget = null,
        string? initialType = null)
    {
        _receiver = receiver;
        _initialTarget = initialTarget;
        _initialType = initialType;
        Text = "Mute and Gag Management";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 560);
        Size = new Size(1200, 700);
        BuildUi();
        _receiver.PacketReceived += OnPacket;
        Shown += async (_, _) =>
        {
            if (_initialTarget is not null && _initialType is not null)
                await EditAsync(_initialTarget, _initialType, null);
            else
                await RefreshAsync();
        };
        FormClosed += (_, _) => _receiver.PacketReceived -= OnPacket;
    }

    private RestrictionRecord? Selected =>
        _grid.SelectedRows.Count == 1 ? _grid.SelectedRows[0].Tag as RestrictionRecord : null;

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(new Label
        {
            Text = "ACTIVE VOICE AND TEXT RESTRICTIONS", Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "Search player, SteamID64, type, reason, or administrator";
        _search.TextChanged += (_, _) => FillGrid();
        root.Controls.Add(_search, 0, 1);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (string column in new[] { "Player", "SteamID64", "Type", "Reason", "Created by", "Created", "Expires" })
            _grid.Columns.Add(column.Replace(" ", ""), column);
        root.Controls.Add(_grid, 0, 2);

        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 7, 0, 0) };
        AddButton(tools, "ADD MUTE", async () => await EditAsync(null, "Mute", null));
        AddButton(tools, "ADD GAG", async () => await EditAsync(null, "Gag", null));
        AddButton(tools, "EDIT", async () =>
        {
            if (Selected is RestrictionRecord value)
                await EditAsync(null, value.Type, value);
        });
        AddButton(tools, "REMOVE", RemoveAsync);
        AddButton(tools, "HISTORY", async () =>
        {
            if (Selected is RestrictionRecord value)
                new PlayerDisciplineHistoryForm(_receiver, value.SteamId, value.PlayerName).ShowDialog(this);
            await Task.CompletedTask;
        });
        AddButton(tools, "REFRESH", RefreshAsync);
        root.Controls.Add(tools, 0, 3);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        footer.Controls.Add(_status, 0, 0);
        var close = new Button { Text = "CLOSE", Dock = DockStyle.Fill, DialogResult = DialogResult.Cancel };
        footer.Controls.Add(close, 1, 0);
        root.Controls.Add(footer, 0, 4);
        Controls.Add(root);
        CancelButton = close;
    }

    private static void AddButton(Control parent, string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32 };
        button.Click += async (_, _) => await action();
        parent.Controls.Add(button);
    }

    private async Task RefreshAsync()
    {
        _status.Text = "Refreshing restrictions...";
        if (!await _receiver.SendAdminActionAsync(AdminActionCode.RequestDisciplineCatalog, -1))
            _status.Text = "The restriction-list request could not be sent.";
    }

    private async Task EditAsync(AdminBanTarget? target, string type, RestrictionRecord? existing)
    {
        using var editor = new RestrictionEditorForm(target, type, existing);
        if (editor.ShowDialog(this) != DialogResult.OK)
            return;
        string json = JsonSerializer.Serialize(new
        {
            steamId = editor.SteamId,
            playerName = editor.PlayerName,
            type = editor.RestrictionType,
            reason = editor.Reason,
            durationMinutes = editor.DurationMinutes,
        });
        _status.Text = "Saving restriction...";
        if (!await _receiver.SendAdminActionAsync(
                AdminActionCode.SaveRestriction, target?.PlayerSlot ?? -1, 0, json))
            _status.Text = "The restriction request could not be sent.";
    }

    private async Task RemoveAsync()
    {
        RestrictionRecord? selected = Selected;
        if (selected is null)
            return;
        if (MessageBox.Show(this,
                $"Remove the {selected.Type.ToLowerInvariant()} from {selected.PlayerName}?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        string json = JsonSerializer.Serialize(new { steamId = selected.SteamId, type = selected.Type });
        _status.Text = "Removing restriction...";
        if (!await _receiver.SendAdminActionAsync(AdminActionCode.DeleteRestriction, -1, 0, json))
            _status.Text = "The removal request could not be sent.";
    }

    private void OnPacket(VoicePacket packet, System.Net.IPEndPoint _)
    {
        if (IsDisposed || Disposing)
            return;
        if (packet.MessageType == BridgeMessageType.DisciplineCatalog)
        {
            try
            {
                DisciplineCatalog catalog = DisciplineCatalog.Parse(packet.CatalogJson);
                BeginInvoke(() =>
                {
                    _records.Clear();
                    _records.AddRange(catalog.Restrictions);
                    FillGrid();
                });
            }
            catch (Exception exception)
            {
                BeginInvoke(() => _status.Text = exception.Message);
            }
        }
        else if (packet.MessageType == BridgeMessageType.AdminActionResult &&
                 packet.AdminActionCode is >= 113 and <= 115)
        {
            BeginInvoke(() => _status.Text = packet.AdminActionMessage);
        }
    }

    private void FillGrid()
    {
        string search = _search.Text.Trim();
        _grid.Rows.Clear();
        foreach (RestrictionRecord value in _records.Where(value => search.Length == 0 ||
                     value.PlayerName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                     value.SteamId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                     value.Type.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                     value.Reason.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                     value.CreatedBy.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            int index = _grid.Rows.Add(value.PlayerName, value.SteamId, value.Type, value.Reason,
                value.CreatedBy, FormatDate(value.CreatedUtc), FormatExpiry(value.ExpiresUnix));
            _grid.Rows[index].Tag = value;
        }
        _status.Text = $"Showing {_grid.Rows.Count} of {_records.Count} active restriction(s).";
    }

    internal static string FormatDate(string value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset date)
            ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : value;

    internal static string FormatExpiry(ulong value) => value == 0 ? "Permanent" :
        DateTimeOffset.FromUnixTimeSeconds((long)value).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

internal sealed class RestrictionEditorForm : NeoForm
{
    private readonly TextBox _steam = new();
    private readonly TextBox _name = new();
    private readonly ComboBox _type = new();
    private readonly TextBox _reason = new();
    private readonly NumericUpDown _duration = new() { Minimum = 0, Maximum = 2628000 };
    public string SteamId { get; private set; } = string.Empty;
    public string PlayerName { get; private set; } = string.Empty;
    public string RestrictionType { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public long DurationMinutes { get; private set; }

    public RestrictionEditorForm(AdminBanTarget? target, string type, RestrictionRecord? existing)
    {
        Text = existing is null ? $"Add {type}" : $"Edit {type}";
        ClientSize = new Size(600, 310);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        _steam.Text = existing?.SteamId ?? target?.SteamId ?? string.Empty;
        _steam.ReadOnly = existing is not null || target is not null;
        _name.Text = existing?.PlayerName ?? target?.PlayerName ?? string.Empty;
        _type.Items.AddRange(new object[] { "Mute", "Gag" });
        _type.DropDownStyle = ComboBoxStyle.DropDownList;
        _type.SelectedItem = type;
        _reason.Text = existing?.Reason ?? string.Empty;
        _reason.MaxLength = 160;
        _duration.Value = existing is not null && existing.ExpiresUnix > (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            ? Math.Min(_duration.Maximum,
                (decimal)Math.Ceiling((existing.ExpiresUnix - (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 60d))
            : 0;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(14),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(table, 0, "SteamID64", _steam);
        AddRow(table, 1, "Player name", _name);
        AddRow(table, 2, "Restriction", _type);
        AddRow(table, 3, "Reason", _reason);
        var durationPanel = new FlowLayoutPanel { Dock = DockStyle.Fill };
        _duration.Width = 150;
        durationPanel.Controls.Add(_duration);
        durationPanel.Controls.Add(new Label { Text = "minutes (0 = permanent)", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        AddRow(table, 4, "Duration", durationPanel);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "SAVE", AutoSize = true };
        var cancel = new Button { Text = "CANCEL", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => Save();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        table.Controls.Add(buttons, 1, 5);
        Controls.Add(table);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control control)
    {
        control.Dock = DockStyle.Fill;
        table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private void Save()
    {
        string steam = _steam.Text.Trim();
        string reason = _reason.Text.Trim();
        if (!ulong.TryParse(steam, out ulong value) || value < 76561197960265728UL)
        {
            MessageBox.Show(this, "Enter a valid SteamID64.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (reason.Length is < 1 or > 160 || reason.Any(char.IsControl))
        {
            MessageBox.Show(this, "Enter a one-line reason (160 characters maximum).", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SteamId = steam;
        PlayerName = string.IsNullOrWhiteSpace(_name.Text) ? steam : _name.Text.Trim();
        RestrictionType = _type.SelectedItem?.ToString() ?? "Mute";
        Reason = reason;
        DurationMinutes = (long)_duration.Value;
        DialogResult = DialogResult.OK;
    }
}

internal sealed class PlayerDisciplineHistoryForm : NeoForm
{
    private readonly UdpVoiceReceiver _receiver;
    private readonly string _steamId;
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();

    public PlayerDisciplineHistoryForm(UdpVoiceReceiver receiver, string steamId, string playerName)
    {
        _receiver = receiver;
        _steamId = steamId;
        Text = $"Discipline History - {playerName}";
        Size = new Size(1100, 600);
        MinimumSize = new Size(850, 450);
        StartPosition = FormStartPosition.CenterParent;
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (string column in new[] { "Date", "Action", "Player", "SteamID64", "Reason", "Administrator", "Expires" })
            _grid.Columns.Add(column.Replace(" ", ""), column);
        _status.Dock = DockStyle.Bottom;
        _status.Height = 34;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(_grid);
        Controls.Add(_status);
        _receiver.PacketReceived += OnPacket;
        Shown += async (_, _) =>
        {
            _status.Text = "Loading discipline history...";
            await _receiver.SendAdminActionAsync(AdminActionCode.RequestDisciplineHistory, -1, 0, _steamId);
        };
        FormClosed += (_, _) => _receiver.PacketReceived -= OnPacket;
    }

    private void OnPacket(VoicePacket packet, System.Net.IPEndPoint _)
    {
        if (packet.MessageType != BridgeMessageType.DisciplineHistory || IsDisposed)
            return;
        try
        {
            DisciplineHistoryCatalog catalog = DisciplineHistoryCatalog.Parse(packet.CatalogJson);
            BeginInvoke(() =>
            {
                _grid.Rows.Clear();
                foreach (DisciplineHistoryRecord value in catalog.History)
                {
                    _grid.Rows.Add(AdminDisciplineForm.FormatDate(value.CreatedUtc), value.Action,
                        value.PlayerName, value.SteamId, value.Reason, value.CreatedBy,
                        AdminDisciplineForm.FormatExpiry(value.ExpiresUnix));
                }
                _status.Text = $"{catalog.History.Count} discipline event(s) for {_steamId}.";
            });
        }
        catch (Exception exception)
        {
            BeginInvoke(() => _status.Text = exception.Message);
        }
    }
}
