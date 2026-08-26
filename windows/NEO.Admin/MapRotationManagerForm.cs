using System.Text.Json;

namespace NeoAdmin;

internal sealed class MapRotationManagerForm : NeoForm
{
    private readonly UdpVoiceReceiver _receiver;
    private readonly ListBox _catalog = new();
    private readonly ListBox _rotation = new();
    private readonly DataGridView _schedules = new();
    private readonly CheckBox _enabled = new() { Text = "Enable rotation", AutoSize = true };
    private readonly DateTimePicker _when = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd  HH:mm",
        ShowUpDown = false,
        Width = 180,
    };
    private readonly Label _status = new();

    public MapRotationManagerForm(UdpVoiceReceiver receiver, IEnumerable<string> knownMaps)
    {
        _receiver = receiver;
        Text = "Map Rotation Manager";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1000, 650);
        Size = new Size(1250, 780);
        foreach (string map in knownMaps.OrderBy(value => value))
            _catalog.Items.Add(map);
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
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(new Label
        {
            Text = "MAP ROTATION", Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        lists.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lists.Controls.Add(new Label { Text = "SERVER MAPS", Dock = DockStyle.Fill }, 0, 0);
        lists.Controls.Add(new Label { Text = "ROTATION ORDER", Dock = DockStyle.Fill }, 2, 0);
        _catalog.Dock = DockStyle.Fill;
        _rotation.Dock = DockStyle.Fill;
        lists.Controls.Add(_catalog, 0, 1);
        lists.Controls.Add(_rotation, 2, 1);
        var listButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(8, 16, 8, 0),
        };
        AddButton(listButtons, "ADD >", () =>
        {
            if (_rotation.Items.Count >= 40)
            {
                MessageBox.Show(this, "A rotation can contain up to 40 maps.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (_catalog.SelectedItem is string map && !_rotation.Items.Contains(map))
                _rotation.Items.Add(map);
        });
        AddButton(listButtons, "REMOVE", () =>
        {
            if (_rotation.SelectedIndex >= 0)
                _rotation.Items.RemoveAt(_rotation.SelectedIndex);
        });
        AddButton(listButtons, "UP", () => MoveRotationItem(-1));
        AddButton(listButtons, "DOWN", () => MoveRotationItem(1));
        listButtons.Controls.Add(_enabled);
        lists.Controls.Add(listButtons, 1, 1);
        root.Controls.Add(lists, 0, 1);

        var schedulePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        schedulePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        schedulePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var scheduleTools = new FlowLayoutPanel { Dock = DockStyle.Fill };
        scheduleTools.Controls.Add(new Label
        {
            Text = "SCHEDULED MAP CHANGES", AutoSize = true,
            Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 7, 15, 0),
        });
        scheduleTools.Controls.Add(_when);
        AddButton(scheduleTools, "SCHEDULE SELECTED", async () => await ScheduleAsync());
        AddButton(scheduleTools, "REMOVE SCHEDULE", async () => await RemoveScheduleAsync());
        schedulePanel.Controls.Add(scheduleTools, 0, 0);
        _schedules.Dock = DockStyle.Fill;
        _schedules.ReadOnly = true;
        _schedules.AllowUserToAddRows = false;
        _schedules.AllowUserToDeleteRows = false;
        _schedules.RowHeadersVisible = false;
        _schedules.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _schedules.MultiSelect = false;
        _schedules.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _schedules.Columns.Add("Map", "Map");
        _schedules.Columns.Add("When", "Scheduled time");
        _schedules.Columns.Add("CreatedBy", "Created by");
        schedulePanel.Controls.Add(_schedules, 0, 1);
        root.Controls.Add(schedulePanel, 0, 2);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill };
        AddButton(footer, "SAVE ROTATION", async () => await SaveAsync());
        AddButton(footer, "RUN NEXT MAP", async () =>
            await _receiver.SendAdminActionAsync(AdminActionCode.RunNextMap, -1));
        AddButton(footer, "REFRESH", async () => await RefreshAsync());
        var close = new Button { Text = "CLOSE", AutoSize = true, DialogResult = DialogResult.Cancel };
        footer.Controls.Add(close);
        _status.AutoSize = true;
        _status.Padding = new Padding(15, 7, 0, 0);
        footer.Controls.Add(_status);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);
        CancelButton = close;
    }

    private static void AddButton(Control parent, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32 };
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
    }

    private void MoveRotationItem(int offset)
    {
        int index = _rotation.SelectedIndex;
        int destination = index + offset;
        if (index < 0 || destination < 0 || destination >= _rotation.Items.Count)
            return;
        object value = _rotation.Items[index];
        _rotation.Items.RemoveAt(index);
        _rotation.Items.Insert(destination, value);
        _rotation.SelectedIndex = destination;
    }

    private async Task RefreshAsync()
    {
        _status.Text = "Refreshing...";
        await _receiver.SendAdminActionAsync(AdminActionCode.RequestMapCatalog, -1);
        await _receiver.SendAdminActionAsync(AdminActionCode.RequestMapRotation, -1);
    }

    private async Task SaveAsync()
    {
        string json = JsonSerializer.Serialize(new
        {
            enabled = _enabled.Checked,
            maps = _rotation.Items.Cast<string>().ToArray(),
        });
        _status.Text = "Saving rotation...";
        await _receiver.SendAdminActionAsync(AdminActionCode.SaveMapRotation, -1, 0, json);
    }

    private async Task ScheduleAsync()
    {
        string? map = _rotation.SelectedItem as string ?? _catalog.SelectedItem as string;
        if (map is null)
        {
            MessageBox.Show(this, "Select a map first.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string json = JsonSerializer.Serialize(new
        {
            map,
            scheduledUnix = new DateTimeOffset(_when.Value).ToUnixTimeSeconds(),
        });
        await _receiver.SendAdminActionAsync(AdminActionCode.SaveScheduledMap, -1, 0, json);
    }

    private async Task RemoveScheduleAsync()
    {
        if (_schedules.SelectedRows.Count != 1 ||
            _schedules.SelectedRows[0].Tag is not ScheduledMapRecord value)
            return;
        await _receiver.SendAdminActionAsync(
            AdminActionCode.DeleteScheduledMap, -1, 0, value.Id.ToString());
    }

    private void OnPacket(VoicePacket packet, System.Net.IPEndPoint _)
    {
        if (IsDisposed)
            return;
        if (packet.MessageType == BridgeMessageType.MapCatalog)
        {
            string[] maps = packet.MapCatalogText.Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            BeginInvoke(() =>
            {
                _catalog.Items.Clear();
                _catalog.Items.AddRange(maps.Cast<object>().ToArray());
            });
        }
        else if (packet.MessageType == BridgeMessageType.MapRotationCatalog)
        {
            try
            {
                MapRotationCatalog catalog = MapRotationCatalog.Parse(packet.CatalogJson);
                BeginInvoke(() => LoadCatalog(catalog));
            }
            catch (Exception exception)
            {
                BeginInvoke(() => _status.Text = exception.Message);
            }
        }
        else if (packet.MessageType == BridgeMessageType.AdminActionResult &&
                 packet.AdminActionCode is >= 120 and <= 124)
            BeginInvoke(() => _status.Text = packet.AdminActionMessage);
    }

    private void LoadCatalog(MapRotationCatalog catalog)
    {
        _enabled.Checked = catalog.Enabled;
        _rotation.Items.Clear();
        _rotation.Items.AddRange(catalog.Maps.Cast<object>().ToArray());
        _schedules.Rows.Clear();
        foreach (ScheduledMapRecord value in catalog.Schedules.OrderBy(item => item.ScheduledUnix))
        {
            int index = _schedules.Rows.Add(value.Map,
                AdminDisciplineForm.FormatExpiry(value.ScheduledUnix), value.CreatedBy);
            _schedules.Rows[index].Tag = value;
        }
        _status.Text = $"{catalog.Maps.Count} rotation map(s), {catalog.Schedules.Count} scheduled change(s).";
    }
}
