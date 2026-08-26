using System.Diagnostics;

namespace NeoAdmin;

internal sealed class WorkshopMapManagerForm : NeoForm
{
    private readonly UdpVoiceReceiver _receiver;
    private readonly TextBox _workshopInput = new();
    private readonly Button _lookupButton = new();
    private readonly Button _installButton = new();
    private readonly Button _openButton = new();
    private readonly PictureBox _preview = new();
    private readonly Label _title = new();
    private readonly Label _details = new();
    private readonly Label _status = new();
    private SteamWorkshopMapInfo? _selectedMap;
    private readonly CancellationTokenSource _closing = new();

    public WorkshopMapManagerForm(
        UdpVoiceReceiver receiver,
        IEnumerable<string> serverMaps)
    {
        _receiver = receiver;
        Text = "Workshop Map Manager";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(850, 560);
        Size = new Size(980, 650);
        ShowIcon = false;
        BuildUi(serverMaps);
        _receiver.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) =>
        {
            _receiver.PacketReceived -= OnPacketReceived;
            _closing.Cancel();
            _closing.Dispose();
            _preview.Image?.Dispose();
        };
    }

    private void BuildUi(IEnumerable<string> serverMaps)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(14),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 315));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var heading = new Label
        {
            Text = "WORKSHOP MAP MANAGER",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
        };
        root.Controls.Add(heading, 0, 0);
        root.SetColumnSpan(heading, 2);

        _workshopInput.Dock = DockStyle.Fill;
        _workshopInput.PlaceholderText = "Workshop URL or numeric item ID";
        _lookupButton.Text = "LOOK UP";
        _lookupButton.Dock = DockStyle.Fill;
        _lookupButton.Click += async (_, _) => await LookupAsync();
        root.Controls.Add(_workshopInput, 0, 1);
        root.Controls.Add(_lookupButton, 1, 1);

        var installedPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Margin = new Padding(0, 10, 12, 0),
        };
        installedPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        installedPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        installedPanel.Controls.Add(new Label
        {
            Text = "SERVER WORKSHOP MAPS",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
        }, 0, 0);
        var installed = new ListBox
        {
            Dock = DockStyle.Fill,
            HorizontalScrollbar = true,
        };
        foreach (string map in serverMaps
            .Where(map => map.StartsWith("workshop/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(map => map))
        {
            installed.Items.Add(map);
        }
        installedPanel.Controls.Add(installed, 0, 1);
        root.Controls.Add(installedPanel, 0, 2);

        var detailPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Margin = new Padding(0, 10, 0, 0),
        };
        detailPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        detailPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        detailPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        detailPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        _preview.Dock = DockStyle.Fill;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(31, 36, 41);
        _title.Text = "Enter a Workshop map URL or ID.";
        _title.Dock = DockStyle.Fill;
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
        _details.Dock = DockStyle.Fill;
        _details.AutoEllipsis = true;
        _details.ForeColor = NeoTheme.MutedText;
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        _installButton.Text = "INSTALL / UPDATE AND SWITCH";
        _installButton.Width = 230;
        _installButton.Height = 32;
        _installButton.Enabled = false;
        _installButton.Click += async (_, _) => await InstallAsync();
        _openButton.Text = "OPEN WORKSHOP PAGE";
        _openButton.Width = 175;
        _openButton.Height = 32;
        _openButton.Enabled = false;
        _openButton.Click += (_, _) => OpenWorkshopPage();
        actions.Controls.Add(_installButton);
        actions.Controls.Add(_openButton);
        detailPanel.Controls.Add(_preview, 0, 0);
        detailPanel.Controls.Add(_title, 0, 1);
        detailPanel.Controls.Add(_details, 0, 2);
        detailPanel.Controls.Add(actions, 0, 3);
        root.Controls.Add(detailPanel, 1, 2);

        _status.Text = "Only public Counter-Strike 2 map items are accepted.";
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = NeoTheme.MutedText;
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
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(close, 1, 0);
        root.Controls.Add(footer, 0, 3);
        root.SetColumnSpan(footer, 2);

        Controls.Add(root);
        CancelButton = close;
    }

    private async Task LookupAsync()
    {
        if (!SteamWorkshopClient.TryParseId(_workshopInput.Text, out ulong id))
        {
            SetStatus("Enter a valid Workshop URL or numeric item ID.", true);
            return;
        }

        SetBusy(true, "Checking the Workshop item with Steam...");
        try
        {
            SteamWorkshopMapInfo map = await SteamWorkshopClient.GetMapAsync(
                id,
                _closing.Token);
            _selectedMap = map;
            _title.Text = map.Title;
            _details.Text = $"Workshop ID: {map.PublishedFileId}{Environment.NewLine}" +
                $"File: {map.FileName}{Environment.NewLine}{Environment.NewLine}" +
                map.Description;
            _preview.Image?.Dispose();
            _preview.Image = null;
            if (map.PreviewBytes.Length > 0)
            {
                using var stream = new MemoryStream(map.PreviewBytes);
                using Image loaded = Image.FromStream(stream);
                _preview.Image = new Bitmap(loaded);
            }
            SetBusy(false, "Valid CS2 Workshop map. Ready to install or update.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _selectedMap = null;
            SetBusy(false, exception.Message, true);
        }
    }

    private async Task InstallAsync()
    {
        if (_selectedMap is null)
            return;
        SetBusy(true, "Sending the Workshop map request to CS2...");
        bool sent = await _receiver.SendAdminActionAsync(
            AdminActionCode.HostWorkshopMap,
            -1,
            0,
            _selectedMap.PublishedFileId.ToString());
        if (!sent)
            SetBusy(false, "The Workshop request could not be sent.", true);
    }

    private void OpenWorkshopPage()
    {
        if (_selectedMap is null)
            return;
        Process.Start(new ProcessStartInfo(
            $"https://steamcommunity.com/sharedfiles/filedetails/?id={_selectedMap.PublishedFileId}")
        {
            UseShellExecute = true,
        });
    }

    private void OnPacketReceived(VoicePacket packet, System.Net.IPEndPoint _)
    {
        if (IsDisposed || Disposing ||
            packet.MessageType != BridgeMessageType.AdminActionResult ||
            packet.AdminActionCode != (uint)AdminActionCode.HostWorkshopMap)
        {
            return;
        }
        BeginInvoke(() => SetBusy(
            false,
            packet.AdminActionMessage,
            !packet.AdminActionSucceeded));
    }

    private void SetBusy(bool busy, string message, bool error = false)
    {
        _lookupButton.Enabled = !busy;
        _workshopInput.Enabled = !busy;
        _installButton.Enabled = !busy && _selectedMap is not null &&
            _receiver.Can(AdminPermission.ManageWorkshopMaps);
        _openButton.Enabled = !busy && _selectedMap is not null;
        SetStatus(message, error);
    }

    private void SetStatus(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? NeoTheme.Danger : NeoTheme.MutedText;
    }
}
