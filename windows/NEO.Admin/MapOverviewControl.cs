using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NeoAdmin;

internal sealed record MapPlayerSnapshot(
    string Key,
    ulong SteamId,
    int Slot,
    string Name,
    float X,
    float Y,
    float Z,
    float Yaw,
    int Team,
    int Health,
    bool Alive,
    bool Bot,
    bool Speaking,
    DateTime UpdatedUtc);

internal sealed class MapOverviewDefinition
{
    public string MapName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float Scale { get; set; } = 1.0f;
    public bool Rotate { get; set; }
    public string ImageFile { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

internal sealed class MapOverviewControl : Control
{
    private float SourceRadarSize => _backgroundImage is null ? 1024.0f : _backgroundImage.Width;
    private static readonly TimeSpan PositionFreshness =
        TimeSpan.FromSeconds(2.5);

    private readonly Dictionary<string, MapPlayerSnapshot> _players = new();
    private string _currentMapName = string.Empty;
    private MapOverviewDefinition? _definition;
    private Image? _backgroundImage;
    private string _loadMessage = "Waiting for map data from the server...";

    // NEO ADMIN map drag state.
    private const float DragHitRadius = 22.0f;
    private static readonly TimeSpan DragSendInterval =
        TimeSpan.FromMilliseconds(100);

    private MapPlayerSnapshot? _dragPlayer;
    private PointF? _dragScreenPoint;
    private DateTime _lastDragSendUtc = DateTime.MinValue;

    public event Action<MapPlayerSnapshot, float, float, float>?
        PlayerDragTeleport;
    public event Action<string>? InteractionStatus;

    public MapOverviewControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(22, 25, 29);
        ForeColor = Color.WhiteSmoke;
        Dock = DockStyle.Fill;
    }

    public string CurrentMapName => _currentMapName;

    public string MapsFolder => Path.Combine(
        AppContext.BaseDirectory,
        "maps");

    public void SetCurrentMap(string? mapName)
    {
        string normalized = NormalizeMapName(mapName);
        if (string.Equals(
                normalized,
                _currentMapName,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CancelDrag();
        _currentMapName = normalized;
        _players.Clear();
        LoadDefinitionAndImage();
        Invalidate();
    }

    public void ReloadCurrentMap()
    {
        LoadDefinitionAndImage();
        Invalidate();
    }

    public void UpsertPlayer(MapPlayerSnapshot snapshot)
    {
        _players[snapshot.Key] = snapshot;
        Invalidate();
    }

    public void UpdateIdentity(
        string key,
        ulong steamId,
        int slot,
        string name)
    {
        if (!_players.TryGetValue(key, out MapPlayerSnapshot? player))
            return;

        _players[key] = player with
        {
            SteamId = steamId,
            Slot = slot,
            Name = name,
        };
        Invalidate();
    }

    public void SetSpeaking(string key, bool speaking)
    {
        if (!_players.TryGetValue(key, out MapPlayerSnapshot? player) ||
            player.Speaking == speaking)
        {
            return;
        }

        _players[key] = player with { Speaking = speaking };
        Invalidate();
    }

    public void RemovePlayer(string key)
    {
        if (_dragPlayer?.Key == key)
            CancelDrag();

        if (_players.Remove(key))
            Invalidate();
    }

    public bool ImportCurrentMapImage(IWin32Window owner)
    {
        if (string.IsNullOrWhiteSpace(_currentMapName))
        {
            MessageBox.Show(
                owner,
                "The server has not reported a map yet.",
                "Map Overview",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        using var dialog = new OpenFileDialog
        {
            Title = $"Choose an overview image for {_currentMapName}",
            Filter = "Map images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return false;

        Directory.CreateDirectory(MapsFolder);
        string destination = Path.Combine(
            MapsFolder,
            $"{_currentMapName}.png");

        using (Image source = Image.FromFile(dialog.FileName))
        using (var bitmap = new Bitmap(source.Width, source.Height))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);
            bitmap.Save(destination, System.Drawing.Imaging.ImageFormat.Png);
        }

        MapOverviewDefinition definition =
            _definition ?? CreateDefaultDefinition(_currentMapName);
        definition.MapName = _currentMapName;
        definition.ImageFile = Path.GetFileName(destination);
        SaveDefinition(definition);
        ReloadCurrentMap();
        return true;
    }

    public bool ConfigureCurrentMap(IWin32Window owner)
    {
        if (string.IsNullOrWhiteSpace(_currentMapName))
        {
            MessageBox.Show(
                owner,
                "The server has not reported a map yet.",
                "Map Overview",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        MapOverviewDefinition definition = CloneDefinition(
            _definition ?? CreateDefaultDefinition(_currentMapName));

        using var dialog = new MapOverviewConfigurationDialog(definition);
        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return false;

        SaveDefinition(dialog.Definition);
        ReloadCurrentMap();
        return true;
    }

    public void OpenMapsFolder()
    {
        Directory.CreateDirectory(MapsFolder);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{MapsFolder}\"",
            UseShellExecute = true,
        });
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left || _definition is null)
        {
            if (e.Button == MouseButtons.Left && _definition is null)
            {
                InteractionStatus?.Invoke(
                    "Drag teleport requires a calibrated map overview.");
            }
            return;
        }

        RectangleF radarRect = GetRadarRectangle(ClientRectangle);
        if (!radarRect.Contains(e.Location))
            return;

        List<MapPlayerSnapshot> freshPlayers = GetFreshPlayers();
        if (freshPlayers.Count == 0)
            return;

        WorldBounds fallbackBounds = BuildFallbackBounds(freshPlayers);
        MapPlayerSnapshot? selected = null;
        float bestDistanceSquared = DragHitRadius * DragHitRadius;

        foreach (MapPlayerSnapshot player in freshPlayers)
        {
            if (!player.Alive)
                continue;

            PointF point = WorldToScreen(player, radarRect, fallbackBounds);
            float dx = point.X - e.X;
            float dy = point.Y - e.Y;
            float distanceSquared = dx * dx + dy * dy;

            if (distanceSquared <= bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                selected = player;
            }
        }

        if (selected is null)
            return;

        _dragPlayer = selected;
        _dragScreenPoint = ClampToRadar(e.Location, radarRect);
        _lastDragSendUtc = DateTime.MinValue;
        Capture = true;
        Cursor = Cursors.SizeAll;

        InteractionStatus?.Invoke(
            $"Dragging {selected.Name}: release to finish teleporting.");
        SendDragTeleport(force: true);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragPlayer is null || !Capture)
            return;

        RectangleF radarRect = GetRadarRectangle(ClientRectangle);
        _dragScreenPoint = ClampToRadar(e.Location, radarRect);
        SendDragTeleport(force: false);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != MouseButtons.Left || _dragPlayer is null)
            return;

        RectangleF radarRect = GetRadarRectangle(ClientRectangle);
        _dragScreenPoint = ClampToRadar(e.Location, radarRect);

        string playerName = _dragPlayer.Name;
        SendDragTeleport(force: true);
        CancelDrag();

        InteractionStatus?.Invoke(
            $"Drag teleport finished for {playerName}.");
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);

        if (!Capture && _dragPlayer is not null)
        {
            CancelDrag();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics graphics = e.Graphics;
        graphics.SmoothingMode =
            System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.InterpolationMode =
            System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint =
            System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        RectangleF radarRect = GetRadarRectangle(ClientRectangle);
        DrawBackground(graphics, radarRect);
        DrawPlayers(graphics, radarRect);
        DrawStatus(graphics, radarRect);
    }

    private void DrawBackground(Graphics graphics, RectangleF radarRect)
    {
        using var backgroundBrush = new SolidBrush(BackColor);
        graphics.FillRectangle(backgroundBrush, ClientRectangle);

        if (_backgroundImage is not null)
        {
            graphics.DrawImage(_backgroundImage, radarRect);
        }
        else
        {
            using var radarBrush = new SolidBrush(Color.FromArgb(31, 36, 41));
            graphics.FillRectangle(radarBrush, radarRect);
        }

        using var gridPen = new Pen(Color.FromArgb(45, 255, 255, 255), 1.0f);
        for (int index = 1; index < 8; index++)
        {
            float x = radarRect.Left + radarRect.Width * index / 8.0f;
            float y = radarRect.Top + radarRect.Height * index / 8.0f;
            graphics.DrawLine(gridPen, x, radarRect.Top, x, radarRect.Bottom);
            graphics.DrawLine(gridPen, radarRect.Left, y, radarRect.Right, y);
        }

        using var borderPen = new Pen(Color.FromArgb(130, 255, 255, 255), 1.5f);
        graphics.DrawRectangle(
            borderPen,
            radarRect.X,
            radarRect.Y,
            radarRect.Width,
            radarRect.Height);
    }

    private void DrawPlayers(Graphics graphics, RectangleF radarRect)
    {
        List<MapPlayerSnapshot> freshPlayers = GetFreshPlayers();

        if (freshPlayers.Count == 0)
            return;

        WorldBounds fallbackBounds = BuildFallbackBounds(freshPlayers);

        foreach (MapPlayerSnapshot player in freshPlayers)
        {
            bool isDragged = _dragPlayer?.Key == player.Key &&
                _dragScreenPoint.HasValue;

            PointF point = isDragged
                ? _dragScreenPoint.GetValueOrDefault()
                : WorldToScreen(player, radarRect, fallbackBounds);

            DrawPlayer(graphics, player, point);

            if (isDragged)
            {
                using var dragPen = new Pen(Color.White, 2.5f)
                {
                    DashStyle =
                        System.Drawing.Drawing2D.DashStyle.Dash,
                };
                graphics.DrawEllipse(
                    dragPen,
                    point.X - 15,
                    point.Y - 15,
                    30,
                    30);
            }
        }
    }

    private void DrawPlayer(
        Graphics graphics,
        MapPlayerSnapshot player,
        PointF point)
    {
        Color teamColor = player.Team switch
        {
            2 => Color.FromArgb(238, 176, 55),
            3 => Color.FromArgb(80, 165, 255),
            _ => Color.FromArgb(190, 190, 190),
        };

        float radius = player.Speaking ? 10.5f : 8.0f;
        RectangleF marker = new(
            point.X - radius,
            point.Y - radius,
            radius * 2,
            radius * 2);

        if (player.Speaking)
        {
            using var speakingPen = new Pen(Color.LimeGreen, 3.0f);
            graphics.DrawEllipse(
                speakingPen,
                marker.X - 5,
                marker.Y - 5,
                marker.Width + 10,
                marker.Height + 10);
        }

        using var fill = new SolidBrush(
            player.Alive
                ? teamColor
                : Color.FromArgb(110, teamColor));
        using var outline = new Pen(Color.Black, 2.0f);
        graphics.FillEllipse(fill, marker);
        graphics.DrawEllipse(outline, marker);

        DrawDirection(graphics, player, point, radius + 8.0f, teamColor);

        if (!player.Alive)
        {
            using var deadPen = new Pen(Color.FromArgb(235, 40, 40), 2.5f);
            graphics.DrawLine(
                deadPen,
                marker.Left,
                marker.Top,
                marker.Right,
                marker.Bottom);
            graphics.DrawLine(
                deadPen,
                marker.Right,
                marker.Top,
                marker.Left,
                marker.Bottom);
        }

        string label = player.Bot
            ? $"{player.Name} [BOT]"
            : player.Name;

        using var labelFont = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);
        SizeF labelSize = graphics.MeasureString(label, labelFont);
        RectangleF labelBackground = new(
            point.X + 12,
            point.Y - labelSize.Height / 2 - 2,
            labelSize.Width + 8,
            labelSize.Height + 4);

        using var labelBackBrush = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
        using var labelBrush = new SolidBrush(Color.White);
        graphics.FillRectangle(labelBackBrush, labelBackground);
        graphics.DrawString(
            label,
            labelFont,
            labelBrush,
            labelBackground.Left + 4,
            labelBackground.Top + 2);

        if (player.Alive)
        {
            string health = player.Health.ToString(CultureInfo.InvariantCulture);
            using var healthFont = new Font(Font.FontFamily, 7.5f, FontStyle.Regular);
            using var healthBrush = new SolidBrush(Color.WhiteSmoke);
            graphics.DrawString(
                health,
                healthFont,
                healthBrush,
                point.X - 7,
                point.Y + radius + 2);
        }
    }

    private void DrawDirection(
        Graphics graphics,
        MapPlayerSnapshot player,
        PointF point,
        float length,
        Color color)
    {
        double radians = player.Yaw * Math.PI / 180.0;
        float dx = (float)Math.Cos(radians);
        float dy = -(float)Math.Sin(radians);

        if (_definition?.Rotate == true)
        {
            (dx, dy) = (-dy, dx);
        }

        PointF end = new(
            point.X + dx * length,
            point.Y + dy * length);

        using var directionPen = new Pen(color, 2.0f)
        {
            EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor,
        };
        graphics.DrawLine(directionPen, point, end);
    }

    private void DrawStatus(Graphics graphics, RectangleF radarRect)
    {
        // Map information overlay intentionally hidden.
    }

    private List<MapPlayerSnapshot> GetFreshPlayers()
    {
        DateTime now = DateTime.UtcNow;
        return _players.Values
            .Where(player => now - player.UpdatedUtc <= PositionFreshness)
            .OrderBy(player => player.Team)
            .ThenBy(player => player.Slot)
            .ToList();
    }

    private void SendDragTeleport(bool force)
    {
        if (_dragPlayer is null ||
            !_dragScreenPoint.HasValue ||
            _definition is null)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (!force && now - _lastDragSendUtc < DragSendInterval)
            return;

        if (!TryScreenToWorld(
                _dragScreenPoint.Value,
                GetRadarRectangle(ClientRectangle),
                out float worldX,
                out float worldY))
        {
            return;
        }

        _lastDragSendUtc = now;
        PlayerDragTeleport?.Invoke(
            _dragPlayer,
            worldX,
            worldY,
            _dragPlayer.Z);
    }

    private bool TryScreenToWorld(
        PointF point,
        RectangleF radarRect,
        out float worldX,
        out float worldY)
    {
        worldX = 0;
        worldY = 0;

        if (_definition is null ||
            _definition.Scale <= 0.0001f ||
            radarRect.Width <= 0 ||
            radarRect.Height <= 0)
        {
            return false;
        }

        float radarX =
            (point.X - radarRect.Left) / radarRect.Width * SourceRadarSize;
        float radarY =
            (point.Y - radarRect.Top) / radarRect.Height * SourceRadarSize;

        radarX = Math.Clamp(radarX, 0.0f, SourceRadarSize);
        radarY = Math.Clamp(radarY, 0.0f, SourceRadarSize);

        if (_definition.Rotate)
        {
            (radarX, radarY) =
                (radarY, SourceRadarSize - radarX);
        }

        worldX = _definition.PosX + radarX * _definition.Scale;
        worldY = _definition.PosY - radarY * _definition.Scale;
        return true;
    }

    private static PointF ClampToRadar(Point point, RectangleF radarRect)
    {
        return new PointF(
            Math.Clamp((float)point.X, radarRect.Left, radarRect.Right),
            Math.Clamp((float)point.Y, radarRect.Top, radarRect.Bottom));
    }

    private void CancelDrag()
    {
        _dragPlayer = null;
        _dragScreenPoint = null;
        _lastDragSendUtc = DateTime.MinValue;
        Capture = false;
        Cursor = Cursors.Default;
    }

    private PointF WorldToScreen(
        MapPlayerSnapshot player,
        RectangleF radarRect,
        WorldBounds fallbackBounds)
    {
        float radarX;
        float radarY;

        if (_definition is not null && _definition.Scale > 0.0001f)
        {
            radarX = (player.X - _definition.PosX) / _definition.Scale;
            radarY = (_definition.PosY - player.Y) / _definition.Scale;

            if (_definition.Rotate)
            {
                (radarX, radarY) =
                    (SourceRadarSize - radarY, radarX);
            }

            radarX = Math.Clamp(radarX, -128.0f, SourceRadarSize + 128.0f);
            radarY = Math.Clamp(radarY, -128.0f, SourceRadarSize + 128.0f);

            return new PointF(
                radarRect.Left + radarX / SourceRadarSize * radarRect.Width,
                radarRect.Top + radarY / SourceRadarSize * radarRect.Height);
        }

        radarX = (player.X - fallbackBounds.MinX) /
            Math.Max(1.0f, fallbackBounds.MaxX - fallbackBounds.MinX);
        radarY = (fallbackBounds.MaxY - player.Y) /
            Math.Max(1.0f, fallbackBounds.MaxY - fallbackBounds.MinY);

        return new PointF(
            radarRect.Left + radarX * radarRect.Width,
            radarRect.Top + radarY * radarRect.Height);
    }

    private static WorldBounds BuildFallbackBounds(
        IReadOnlyCollection<MapPlayerSnapshot> players)
    {
        float minX = players.Min(player => player.X);
        float maxX = players.Max(player => player.X);
        float minY = players.Min(player => player.Y);
        float maxY = players.Max(player => player.Y);

        float width = Math.Max(512.0f, maxX - minX);
        float height = Math.Max(512.0f, maxY - minY);
        float padding = Math.Max(width, height) * 0.18f;

        return new WorldBounds(
            minX - padding,
            maxX + padding,
            minY - padding,
            maxY + padding);
    }

    private static RectangleF GetRadarRectangle(Rectangle client)
    {
        const float margin = 16.0f;
        float availableWidth = Math.Max(1.0f, client.Width - margin * 2);
        float availableHeight = Math.Max(1.0f, client.Height - margin * 2);
        float side = Math.Min(availableWidth, availableHeight);

        return new RectangleF(
            client.Left + (client.Width - side) / 2.0f,
            client.Top + (client.Height - side) / 2.0f,
            side,
            side);
    }

    private void LoadDefinitionAndImage()
    {
        _backgroundImage?.Dispose();
        _backgroundImage = null;
        _definition = null;

        if (string.IsNullOrWhiteSpace(_currentMapName))
        {
            _loadMessage = "Waiting for map data from the server...";
            return;
        }

        Directory.CreateDirectory(MapsFolder);
        string definitionPath = Path.Combine(
            MapsFolder,
            $"{_currentMapName}.json");

        if (File.Exists(definitionPath))
        {
            try
            {
                _definition = JsonSerializer.Deserialize<MapOverviewDefinition>(
                    File.ReadAllText(definitionPath),
                    JsonOptions());
            }
            catch (Exception exception)
            {
                _loadMessage = $"Map config error: {exception.Message}";
            }
        }

        _definition ??= TryBuiltInDefinition(_currentMapName);

        string imagePath = ResolveImagePath();
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(imagePath);
                using var stream = new MemoryStream(bytes);
                using Image loaded = Image.FromStream(stream);
                _backgroundImage = new Bitmap(loaded);
                _loadMessage = Path.GetFileName(imagePath);
                return;
            }
            catch (Exception exception)
            {
                _loadMessage = $"Overview image error: {exception.Message}";
                return;
            }
        }

        _loadMessage = _definition is null
            ? "No map configuration; positions are auto-fitted."
            : "No overview image. Use Map > Import Current Map Overview.";
    }

    private string ResolveImagePath()
    {
        if (_definition is not null &&
            !string.IsNullOrWhiteSpace(_definition.ImageFile))
        {
            string configured = Path.IsPathRooted(_definition.ImageFile)
                ? _definition.ImageFile
                : Path.Combine(MapsFolder, _definition.ImageFile);

            if (File.Exists(configured))
                return configured;
        }

        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" })
        {
            string candidate = Path.Combine(
                MapsFolder,
                _currentMapName + extension);
            if (File.Exists(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private void SaveDefinition(MapOverviewDefinition definition)
    {
        Directory.CreateDirectory(MapsFolder);
        definition.MapName = _currentMapName;

        string path = Path.Combine(
            MapsFolder,
            $"{_currentMapName}.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(definition, JsonOptions()));
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static MapOverviewDefinition CreateDefaultDefinition(
        string mapName)
    {
        return TryBuiltInDefinition(mapName) ?? new MapOverviewDefinition
        {
            MapName = mapName,
            DisplayName = mapName,
            PosX = 0,
            PosY = 0,
            Scale = 1,
            Rotate = false,
            ImageFile = $"{mapName}.png",
        };
    }

    private static MapOverviewDefinition? TryBuiltInDefinition(
        string mapName)
    {
        if (mapName.Equals("de_dust2", StringComparison.OrdinalIgnoreCase))
        {
            return new MapOverviewDefinition
            {
                MapName = "de_dust2",
                DisplayName = "Dust II",
                PosX = -2476,
                PosY = 3239,
                Scale = 4.4f,
                Rotate = false,
                ImageFile = "de_dust2.png",
                Notes = "Built-in Dust II calibration.",
            };
        }

        if (mapName.Equals(
                ZombieSurvivalProfile.MapName,
                StringComparison.OrdinalIgnoreCase))
        {
            return new MapOverviewDefinition
            {
                MapName = ZombieSurvivalProfile.MapName,
                DisplayName = ZombieSurvivalProfile.DisplayName,
                PosX = -3010.5164f,
                PosY = 1078.8701f,
                Scale = 3.9169022f,
                Rotate = false,
                ImageFile = "zm_lila_panic_371.png",
                Notes = "Generated from the map's Source 2 collision geometry.",
            };
        }

        return null;
    }

    private static MapOverviewDefinition CloneDefinition(
        MapOverviewDefinition source)
    {
        return new MapOverviewDefinition
        {
            MapName = source.MapName,
            DisplayName = source.DisplayName,
            PosX = source.PosX,
            PosY = source.PosY,
            Scale = source.Scale,
            Rotate = source.Rotate,
            ImageFile = source.ImageFile,
            Notes = source.Notes,
        };
    }

    private static string NormalizeMapName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = value.Trim().Replace('\\', '/');
        string finalSegment = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? normalized;

        return Path.GetFileNameWithoutExtension(finalSegment);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _backgroundImage?.Dispose();
            _backgroundImage = null;
        }

        base.Dispose(disposing);
    }

    private readonly record struct WorldBounds(
        float MinX,
        float MaxX,
        float MinY,
        float MaxY);
}

internal sealed class MapOverviewConfigurationDialog : NeoForm
{
    private readonly TextBox _displayName = new();
    private readonly NumericUpDown _posX = CreateCoordinateBox();
    private readonly NumericUpDown _posY = CreateCoordinateBox();
    private readonly NumericUpDown _scale = CreateScaleBox();
    private readonly CheckBox _rotate = new();
    private readonly TextBox _imageFile = new();

    public MapOverviewConfigurationDialog(MapOverviewDefinition definition)
    {
        Definition = definition;

        Text = $"Configure {definition.MapName}";
        Width = 520;
        Height = 340;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        _displayName.Text = definition.DisplayName;
        _posX.Value = Clamp(_posX, (decimal)definition.PosX);
        _posY.Value = Clamp(_posY, (decimal)definition.PosY);
        _scale.Value = Clamp(_scale, (decimal)Math.Max(0.001f, definition.Scale));
        _rotate.Text = "Rotate radar coordinates 90 degrees";
        _rotate.Checked = definition.Rotate;
        _rotate.AutoSize = true;
        _imageFile.Text = definition.ImageFile;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 7,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(grid, 0, "Map", new Label
        {
            Text = definition.MapName,
            AutoSize = true,
        });
        AddRow(grid, 1, "Display name", _displayName);
        AddRow(grid, 2, "pos_x", _posX);
        AddRow(grid, 3, "pos_y", _posY);
        AddRow(grid, 4, "Scale", _scale);
        AddRow(grid, 5, "Image filename", _imageFile);
        AddRow(grid, 6, string.Empty, _rotate);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.RightToLeft,
        };

        var save = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            AutoSize = true,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
        };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);

        Controls.Add(grid);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;

        save.Click += (_, _) => SaveValues();
    }

    public MapOverviewDefinition Definition { get; }

    private void SaveValues()
    {
        Definition.DisplayName = _displayName.Text.Trim();
        Definition.PosX = (float)_posX.Value;
        Definition.PosY = (float)_posY.Value;
        Definition.Scale = (float)_scale.Value;
        Definition.Rotate = _rotate.Checked;
        Definition.ImageFile = _imageFile.Text.Trim();
    }

    private static NumericUpDown CreateCoordinateBox() => new()
    {
        Minimum = -1000000,
        Maximum = 1000000,
        DecimalPlaces = 2,
        Increment = 1,
        ThousandsSeparator = true,
        Dock = DockStyle.Fill,
    };

    private static NumericUpDown CreateScaleBox() => new()
    {
        Minimum = 0.001M,
        Maximum = 10000,
        DecimalPlaces = 4,
        Increment = 0.1M,
        Dock = DockStyle.Fill,
    };

    private static decimal Clamp(NumericUpDown box, decimal value)
    {
        return Math.Min(box.Maximum, Math.Max(box.Minimum, value));
    }

    private static void AddRow(
        TableLayoutPanel grid,
        int row,
        string label,
        Control control)
    {
        var labelControl = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };

        control.Anchor =
            AnchorStyles.Left | AnchorStyles.Right;
        grid.Controls.Add(labelControl, 0, row);
        grid.Controls.Add(control, 1, row);
    }
}







