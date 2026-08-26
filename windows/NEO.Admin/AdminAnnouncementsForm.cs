using System.Text.Json;

namespace NeoAdmin;

internal sealed class AdminAnnouncementsForm : NeoForm
{
    private readonly UdpVoiceReceiver _receiver;
    private readonly TextBox _message = new() { MaxLength = 220 };
    private readonly DateTimePicker _when = new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd  HH:mm", Width = 180,
    };
    private readonly NumericUpDown _repeat = new() { Minimum = 0, Maximum = 525600, Width = 110 };
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();

    public AdminAnnouncementsForm(UdpVoiceReceiver receiver)
    {
        _receiver = receiver;
        Text = "Admin Announcements";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 580);
        Size = new Size(1100, 680);
        _when.Value = DateTime.Now.AddMinutes(10);
        BuildUi();
        _receiver.PacketReceived += OnPacket;
        Shown += async (_, _) => await RefreshAsync();
        FormClosed += (_, _) => _receiver.PacketReceived -= OnPacket;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(new Label
        {
            Text = "ADMIN ANNOUNCEMENTS", Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        _message.Dock = DockStyle.Fill;
        _message.Multiline = true;
        _message.PlaceholderText = "Announcement text (220 characters maximum)";
        root.Controls.Add(_message, 0, 1);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill };
        AddButton(tools, "SEND NOW", SendNowAsync);
        tools.Controls.Add(_when);
        tools.Controls.Add(new Label { Text = "Repeat every", AutoSize = true, Padding = new Padding(8, 7, 0, 0) });
        tools.Controls.Add(_repeat);
        tools.Controls.Add(new Label { Text = "minutes (0 = once)", AutoSize = true, Padding = new Padding(0, 7, 8, 0) });
        AddButton(tools, "SCHEDULE", ScheduleAsync);
        AddButton(tools, "REMOVE", RemoveAsync);
        AddButton(tools, "REFRESH", RefreshAsync);
        root.Controls.Add(tools, 0, 2);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add("Message", "Message");
        _grid.Columns.Add("When", "Next delivery");
        _grid.Columns.Add("Repeat", "Repeat");
        _grid.Columns.Add("CreatedBy", "Created by");
        _grid.Columns[0].FillWeight = 220;
        root.Controls.Add(_grid, 0, 3);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var close = new Button { Text = "CLOSE", AutoSize = true, DialogResult = DialogResult.Cancel };
        footer.Controls.Add(close);
        _status.AutoSize = true;
        _status.Padding = new Padding(10, 7, 0, 0);
        footer.Controls.Add(_status);
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

    private bool TryMessage(out string value)
    {
        value = _message.Text.Trim();
        if (value.Length is >= 1 and <= 220 && !value.Any(char.IsControl))
            return true;
        MessageBox.Show(this, "Enter a one-line announcement (220 characters maximum).",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private async Task SendNowAsync()
    {
        if (!TryMessage(out string text))
            return;
        await _receiver.SendAdminActionAsync(AdminActionCode.SendAnnouncementNow, -1, 0, text);
    }

    private async Task ScheduleAsync()
    {
        if (!TryMessage(out string text))
            return;
        string json = JsonSerializer.Serialize(new
        {
            message = text,
            scheduledUnix = new DateTimeOffset(_when.Value).ToUnixTimeSeconds(),
            repeatMinutes = (ulong)_repeat.Value,
        });
        await _receiver.SendAdminActionAsync(AdminActionCode.SaveAnnouncement, -1, 0, json);
    }

    private async Task RemoveAsync()
    {
        if (_grid.SelectedRows.Count != 1 ||
            _grid.SelectedRows[0].Tag is not ScheduledAnnouncementRecord value)
            return;
        await _receiver.SendAdminActionAsync(
            AdminActionCode.DeleteAnnouncement, -1, 0, value.Id.ToString());
    }

    private async Task RefreshAsync()
    {
        _status.Text = "Refreshing announcements...";
        await _receiver.SendAdminActionAsync(AdminActionCode.RequestAnnouncements, -1);
    }

    private void OnPacket(VoicePacket packet, System.Net.IPEndPoint _)
    {
        if (IsDisposed)
            return;
        if (packet.MessageType == BridgeMessageType.AnnouncementCatalog)
        {
            try
            {
                AnnouncementCatalog catalog = AnnouncementCatalog.Parse(packet.CatalogJson);
                BeginInvoke(() => LoadCatalog(catalog));
            }
            catch (Exception exception)
            {
                BeginInvoke(() => _status.Text = exception.Message);
            }
        }
        else if (packet.MessageType == BridgeMessageType.AdminActionResult &&
                 packet.AdminActionCode is >= 130 and <= 133)
            BeginInvoke(() => _status.Text = packet.AdminActionMessage);
    }

    private void LoadCatalog(AnnouncementCatalog catalog)
    {
        _grid.Rows.Clear();
        foreach (ScheduledAnnouncementRecord value in catalog.Announcements.OrderBy(item => item.ScheduledUnix))
        {
            int index = _grid.Rows.Add(value.Message,
                AdminDisciplineForm.FormatExpiry(value.ScheduledUnix),
                value.RepeatMinutes == 0 ? "Once" : $"Every {value.RepeatMinutes} min",
                value.CreatedBy);
            _grid.Rows[index].Tag = value;
        }
        _status.Text = $"{catalog.Announcements.Count} scheduled announcement(s).";
    }
}
