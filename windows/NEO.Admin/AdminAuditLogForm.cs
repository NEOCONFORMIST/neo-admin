namespace NeoAdmin;

internal sealed class AdminAuditLogForm : NeoForm
{
    private readonly UdpVoiceReceiver _receiver;
    private readonly DataGridView _grid = new();
    private readonly TextBox _search = new();
    private readonly ComboBox _result = new();
    private readonly Button _refresh = new();
    private readonly Label _status = new();
    private readonly List<AdminAuditRecord> _events = new();

    public AdminAuditLogForm(UdpVoiceReceiver receiver)
    {
        _receiver = receiver;
        Text = "Administrator Audit Log";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 560);
        Size = new Size(1240, 720);
        ShowIcon = false;
        BuildUi();
        _receiver.PacketReceived += OnPacketReceived;
        Shown += async (_, _) => await RefreshAsync();
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "ADMINISTRATOR AUDIT LOG",
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        filters.Controls.Add(new Label
        {
            Text = "Search",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "Account, action, target, or details";
        _search.TextChanged += (_, _) => ApplyFilters();
        filters.Controls.Add(_search, 1, 0);
        filters.Controls.Add(new Label
        {
            Text = "Result",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
        }, 2, 0);
        _result.Dock = DockStyle.Fill;
        _result.DropDownStyle = ComboBoxStyle.DropDownList;
        _result.Items.AddRange(new object[] { "All", "Succeeded", "Failed" });
        _result.SelectedIndex = 0;
        _result.SelectedIndexChanged += (_, _) => ApplyFilters();
        filters.Controls.Add(_result, 3, 0);
        _refresh.Text = "REFRESH";
        _refresh.Dock = DockStyle.Right;
        _refresh.Width = 105;
        _refresh.Click += async (_, _) => await RefreshAsync();
        filters.Controls.Add(_refresh, 4, 0);
        root.Controls.Add(filters, 0, 1);

        ConfigureGrid();
        root.Controls.Add(_grid, 0, 2);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        _status.Dock = DockStyle.Fill;
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
        root.Controls.Add(footer, 0, 3);
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
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.Columns.Add("Time", "Time");
        _grid.Columns.Add("Account", "Account");
        _grid.Columns.Add("Action", "Action");
        _grid.Columns.Add("Target", "Target");
        _grid.Columns.Add("Result", "Result");
        _grid.Columns.Add("Details", "Details");
        _grid.Columns[0].FillWeight = 115;
        _grid.Columns[1].FillWeight = 75;
        _grid.Columns[2].FillWeight = 120;
        _grid.Columns[3].FillWeight = 130;
        _grid.Columns[4].FillWeight = 65;
        _grid.Columns[5].FillWeight = 165;
    }

    private async Task RefreshAsync()
    {
        SetBusy(true, "Refreshing audit log...");
        bool sent = await _receiver.SendAdminActionAsync(
            AdminActionCode.RequestAuditLog,
            -1);
        if (!sent)
            SetBusy(false, "The audit-log request could not be sent.", true);
    }

    private void OnPacketReceived(VoicePacket packet, System.Net.IPEndPoint _)
    {
        if (IsDisposed || Disposing)
            return;
        if (packet.MessageType == BridgeMessageType.AdminAuditCatalog)
        {
            try
            {
                AdminAuditCatalog catalog =
                    AdminAuditCatalog.Parse(packet.AdminAuditCatalogJson);
                BeginInvoke(() => LoadCatalog(catalog));
            }
            catch (Exception exception)
            {
                BeginInvoke(() => SetBusy(false, exception.Message, true));
            }
        }
        else if (packet.MessageType == BridgeMessageType.AdminActionResult &&
                 packet.AdminActionCode == (uint)AdminActionCode.RequestAuditLog)
        {
            BeginInvoke(() => SetBusy(
                false,
                packet.AdminActionMessage,
                !packet.AdminActionSucceeded));
        }
    }

    private void LoadCatalog(AdminAuditCatalog catalog)
    {
        _events.Clear();
        _events.AddRange(catalog.Events.OrderByDescending(item => item.Id));
        ApplyFilters();
        SetBusy(false, $"{_events.Count} recent audit event(s).");
    }

    private void ApplyFilters()
    {
        string search = _search.Text.Trim();
        int result = _result.SelectedIndex;
        IEnumerable<AdminAuditRecord> filtered = _events.Where(item =>
            (result == 0 || item.Success == (result == 1)) &&
            (search.Length == 0 ||
             item.AccountId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             item.Action.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             item.Target.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             item.Details.Contains(search, StringComparison.OrdinalIgnoreCase)));

        _grid.Rows.Clear();
        foreach (AdminAuditRecord item in filtered)
        {
            int row = _grid.Rows.Add(
                FormatTime(item.CreatedUtc),
                item.AccountId,
                item.Action,
                item.Target,
                item.Success ? "Succeeded" : "Failed",
                item.Details);
            if (!item.Success)
                _grid.Rows[row].DefaultCellStyle.ForeColor = NeoTheme.Danger;
        }
        _status.Text = $"Showing {_grid.Rows.Count} of {_events.Count} recent event(s).";
    }

    private void SetBusy(bool busy, string message, bool error = false)
    {
        _refresh.Enabled = !busy && _receiver.Can(AdminPermission.ViewAuditLog);
        _status.Text = message;
        _status.ForeColor = error ? NeoTheme.Danger : NeoTheme.MutedText;
    }

    private static string FormatTime(string value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : value;
}
