using Android.Content;
using Android.Graphics;
using Android.Views;
using System.Buffers.Binary;
using System.Text.Json;

namespace NeoAdmin.AndroidApp;

internal sealed class MobileMapOverviewDefinition
{
    public string MapName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float Scale { get; set; } = 1.0f;
    public bool Rotate { get; set; }
}

internal sealed record MobileMapMarker(
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
    bool Speaking);

internal sealed class MobileMapOverviewView : View
{
    private const int InvalidPointerId = -1;
    private static readonly TimeSpan DragSendInterval =
        TimeSpan.FromMilliseconds(120);

    private readonly List<MobileMapMarker> _players = new();
    private MobileMapOverviewDefinition? _definition;
    private Bitmap? _bitmap;
    private string _mapName = string.Empty;
    private MobileMapMarker? _dragPlayer;
    private PointF? _dragPoint;
    private int _dragPointerId = InvalidPointerId;
    private float _dragStartX;
    private float _dragStartY;
    private bool _dragMoved;
    private DateTime _lastDragSendUtc = DateTime.MinValue;

    public event Action<MobileMapMarker, float, float, float, bool>?
        PlayerDragTeleport;
    public event Action<string>? InteractionStatus;

    public bool TeleportEnabled { get; private set; }

    public MobileMapOverviewView(Context context) : base(context)
    {
        SetBackgroundColor(Color.Rgb(15, 18, 21));
        Clickable = true;
    }

    public void SetCurrentMap(string mapName)
    {
        string normalized = NormalizeMapName(mapName);
        if (string.Equals(normalized, _mapName, StringComparison.OrdinalIgnoreCase))
            return;

        _mapName = normalized;
        CancelDrag();
        _definition = null;
        _bitmap?.Dispose();
        _bitmap = null;
        Invalidate();
    }

    public bool SetPackage(byte[] package, out string error)
    {
        error = string.Empty;
        if (package.Length < 5)
        {
            error = "The overview package is empty.";
            return false;
        }

        int definitionLength = BinaryPrimitives.ReadInt32LittleEndian(package);
        if (definitionLength <= 0 || definitionLength > 64 * 1024 ||
            definitionLength + 4 >= package.Length)
        {
            error = "The overview definition is invalid.";
            return false;
        }

        try
        {
            MobileMapOverviewDefinition? definition =
                JsonSerializer.Deserialize<MobileMapOverviewDefinition>(
                    package.AsSpan(4, definitionLength),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
            if (definition is null || definition.Scale <= 0.0001f)
            {
                error = "The overview calibration is invalid.";
                return false;
            }

            int imageOffset = 4 + definitionLength;
            Bitmap? bitmap = BitmapFactory.DecodeByteArray(
                package,
                imageOffset,
                package.Length - imageOffset);
            if (bitmap is null)
            {
                error = "The overview image could not be decoded.";
                return false;
            }

            _bitmap?.Dispose();
            _bitmap = bitmap;
            _definition = definition;
            CancelDrag();
            if (TeleportEnabled)
            {
                InteractionStatus?.Invoke(
                    "Ready: drag an alive player marker or name to move them.");
            }
            Invalidate();
            return true;
        }
        catch (Exception exception)
        {
            error = $"The overview package is invalid: {exception.Message}";
            return false;
        }
    }

    public void UpdatePlayers(IEnumerable<MobilePlayer> players)
    {
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(2.5);
        _players.Clear();
        _players.AddRange(players
            .Where(player => !player.SourceTv && player.LastSeenUtc >= cutoff)
            .Select(player => new MobileMapMarker(
                player.Key,
                player.SteamId,
                player.Slot,
                player.Name,
                player.X,
                player.Y,
                player.Z,
                player.Yaw,
                player.Team,
                player.Health,
                player.Alive,
                player.Speaking)));

        if (_dragPlayer is not null)
        {
            MobileMapMarker? refreshed = _players.FirstOrDefault(player =>
                player.Key == _dragPlayer.Key && player.Alive);
            if (refreshed is null)
                CancelDrag();
            else
                _dragPlayer = refreshed;
        }
        Invalidate();
    }

    public void SetTeleportEnabled(bool enabled)
    {
        if (TeleportEnabled == enabled)
            return;

        TeleportEnabled = enabled;
        if (!enabled)
            CancelDrag();
        else if (_definition is not null)
        {
            InteractionStatus?.Invoke(
                "Ready: drag an alive player marker or name to move them.");
        }
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);

        RectF radar = GetRadarRectangle();

        using var imagePaint = new Paint(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
        if (_bitmap is not null)
        {
            canvas.DrawBitmap(_bitmap, null, radar, imagePaint);
        }
        else
        {
            imagePaint.Color = Color.Rgb(27, 32, 37);
            canvas.DrawRect(radar, imagePaint);
            using var waitingPaint = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.Rgb(164, 174, 184),
                TextAlign = Paint.Align.Center,
                TextSize = 15.0f * Resources.DisplayMetrics.Density,
            };
            canvas.DrawText(
                string.IsNullOrWhiteSpace(_mapName)
                    ? "WAITING FOR MAP"
                    : "SYNCING OVERVIEW FROM SERVER",
                radar.CenterX(),
                radar.CenterY(),
                waitingPaint);
        }

        using var gridPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Argb(38, 255, 255, 255),
            StrokeWidth = 1.0f,
        };
        for (int index = 1; index < 8; index++)
        {
            float x = radar.Left + radar.Width() * index / 8.0f;
            float y = radar.Top + radar.Height() * index / 8.0f;
            canvas.DrawLine(x, radar.Top, x, radar.Bottom, gridPaint);
            canvas.DrawLine(radar.Left, y, radar.Right, y, gridPaint);
        }

        foreach (MobileMapMarker player in _players)
        {
            PointF? point = _dragPlayer?.Key == player.Key && _dragPoint is not null
                ? _dragPoint
                : WorldToScreen(radar, player);
            if (point is not null)
                DrawPlayer(canvas, player, point);
        }
    }

    private void DrawPlayer(Canvas canvas, MobileMapMarker player, PointF point)
    {
        float x = point.X;
        float y = point.Y;

        Color color = player.Team switch
        {
            2 => Color.Rgb(239, 184, 75),
            3 => Color.Rgb(93, 174, 226),
            _ => Color.Rgb(220, 224, 228),
        };
        float density = Resources!.DisplayMetrics!.Density;
        float radius = (player.Speaking ? 7.5f : 6.0f) * density;
        using var markerPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = player.Alive ? color : Color.Argb(135, color.R, color.G, color.B),
        };
        markerPaint.SetStyle(Paint.Style.Fill);
        canvas.DrawCircle(x, y, radius, markerPaint);

        markerPaint.Color = Color.Black;
        markerPaint.SetStyle(Paint.Style.Stroke);
        markerPaint.StrokeWidth = 1.5f * density;
        canvas.DrawCircle(x, y, radius, markerPaint);

        if (_dragPlayer?.Key == player.Key)
        {
            markerPaint.Color = Color.White;
            markerPaint.StrokeWidth = 2.5f * density;
            canvas.DrawCircle(x, y, radius + 6.0f * density, markerPaint);
        }

        double radians = player.Yaw * Math.PI / 180.0;
        using var directionPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = color,
            StrokeWidth = 2.0f * density,
        };
        canvas.DrawLine(
            x,
            y,
            x + (float)Math.Cos(radians) * radius * 2.1f,
            y - (float)Math.Sin(radians) * radius * 2.1f,
            directionPaint);

        using var textPaint = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.White,
            TextSize = 11.0f * density,
        };
        textPaint.SetTypeface(
            Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
        textPaint.SetShadowLayer(3.0f, 1.0f, 1.0f, Color.Black);
        canvas.DrawText(
            $"{player.Name}  {Math.Max(0, player.Health)}",
            x + radius + 3.0f * density,
            y + 4.0f * density,
            textPaint);
    }

    public override bool OnTouchEvent(MotionEvent? touch)
    {
        if (touch is null)
            return false;

        switch (touch.ActionMasked)
        {
            case MotionEventActions.Down:
                return BeginDrag(touch);

            case MotionEventActions.Move:
                return MoveDrag(touch);

            case MotionEventActions.Up:
                return FinishDrag(touch, touch.ActionIndex);

            case MotionEventActions.PointerUp:
                return touch.GetPointerId(touch.ActionIndex) == _dragPointerId
                    ? FinishDrag(touch, touch.ActionIndex)
                    : _dragPlayer is not null;

            case MotionEventActions.Cancel:
                if (_dragPlayer is null)
                    return false;
                CancelDrag();
                Invalidate();
                return true;

            default:
                return _dragPlayer is not null;
        }
    }

    private bool BeginDrag(MotionEvent touch)
    {
        if (_definition is null)
        {
            InteractionStatus?.Invoke(
                "Player drag will be available after the map overview finishes syncing.");
            return true;
        }
        if (!TeleportEnabled)
        {
            InteractionStatus?.Invoke(
                "Player drag requires Teleport Players permission.");
            return true;
        }

        float x = touch.GetX();
        float y = touch.GetY();
        RectF radar = GetRadarRectangle();
        if (!radar.Contains(x, y))
        {
            InteractionStatus?.Invoke("Touch an alive player marker inside the map.");
            return true;
        }

        float density = Resources!.DisplayMetrics!.Density;
        // The visible label sits beside the small position dot. A generous
        // target lets either the marker or its name begin the same drag.
        float hitRadius = 72.0f * density;
        float bestDistanceSquared = hitRadius * hitRadius;
        MobileMapMarker? selected = null;

        foreach (MobileMapMarker player in _players)
        {
            if (!player.Alive)
                continue;

            PointF? point = WorldToScreen(radar, player);
            if (point is null)
                continue;

            float dx = point.X - x;
            float dy = point.Y - y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared <= bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                selected = player;
            }
        }

        if (selected is null)
        {
            InteractionStatus?.Invoke(
                "No alive player was selected. Touch a marker or its name, then drag.");
            return true;
        }

        _dragPlayer = selected;
        _dragPoint = WorldToScreen(radar, selected) ?? ClampToRadar(x, y, radar);
        _dragPointerId = touch.GetPointerId(touch.ActionIndex);
        _dragStartX = x;
        _dragStartY = y;
        _dragMoved = false;
        _lastDragSendUtc = DateTime.MinValue;
        Parent?.RequestDisallowInterceptTouchEvent(true);
        Pressed = true;
        PerformHapticFeedback(FeedbackConstants.LongPress);
        InteractionStatus?.Invoke(
            $"Selected {selected.Name}. Keep holding, drag, then release.");
        Invalidate();
        return true;
    }

    private bool MoveDrag(MotionEvent touch)
    {
        if (_dragPlayer is null || _dragPointerId == InvalidPointerId)
            return false;

        int pointerIndex = touch.FindPointerIndex(_dragPointerId);
        if (pointerIndex < 0)
            return true;

        float x = touch.GetX(pointerIndex);
        float y = touch.GetY(pointerIndex);
        float dx = x - _dragStartX;
        float dy = y - _dragStartY;
        int touchSlop = ViewConfiguration.Get(Context!)?.ScaledTouchSlop ??
            (int)(8.0f * Resources!.DisplayMetrics!.Density);
        _dragMoved |= dx * dx + dy * dy >= touchSlop * touchSlop;
        _dragPoint = ClampToRadar(x, y, GetRadarRectangle());
        if (_dragMoved)
            SendDragTeleport(force: false, final: false);
        Invalidate();
        return true;
    }

    private bool FinishDrag(MotionEvent touch, int pointerIndex)
    {
        if (_dragPlayer is null)
            return false;

        string playerName = _dragPlayer.Name;
        _dragPoint = ClampToRadar(
            touch.GetX(pointerIndex),
            touch.GetY(pointerIndex),
            GetRadarRectangle());

        if (_dragMoved)
        {
            SendDragTeleport(force: true, final: true);
            InteractionStatus?.Invoke(
                $"Placed {playerName}; finding safe ground on the server.");
        }
        else
        {
            InteractionStatus?.Invoke($"Drag {playerName} to move them.");
        }

        CancelDrag();
        Invalidate();
        return true;
    }

    private void SendDragTeleport(bool force, bool final)
    {
        MobileMapMarker? player = _dragPlayer;
        PointF? dragPoint = _dragPoint;
        if (player is null || dragPoint is null)
            return;

        DateTime now = DateTime.UtcNow;
        if (!force && now - _lastDragSendUtc < DragSendInterval)
            return;
        if (!TryScreenToWorld(
                dragPoint,
                GetRadarRectangle(),
                out float worldX,
                out float worldY))
        {
            return;
        }

        _lastDragSendUtc = now;
        PlayerDragTeleport?.Invoke(
            player,
            worldX,
            worldY,
            player.Z,
            final);
    }

    private PointF? WorldToScreen(RectF radar, MobileMapMarker player)
    {
        if (_definition is null || _definition.Scale <= 0.0001f)
            return null;

        float sourceSize = _bitmap?.Width ?? 1024.0f;
        float radarX = (player.X - _definition.PosX) / _definition.Scale;
        float radarY = (_definition.PosY - player.Y) / _definition.Scale;
        if (_definition.Rotate)
            (radarX, radarY) = (sourceSize - radarY, radarX);

        float x = radar.Left + radarX / sourceSize * radar.Width();
        float y = radar.Top + radarY / sourceSize * radar.Height();
        return x < radar.Left - 20 || x > radar.Right + 20 ||
               y < radar.Top - 20 || y > radar.Bottom + 20
            ? null
            : new PointF(x, y);
    }

    private bool TryScreenToWorld(
        PointF point,
        RectF radar,
        out float worldX,
        out float worldY)
    {
        worldX = 0;
        worldY = 0;
        if (_definition is null || _definition.Scale <= 0.0001f ||
            radar.Width() <= 0 || radar.Height() <= 0)
        {
            return false;
        }

        float sourceSize = _bitmap?.Width ?? 1024.0f;
        float radarX = (point.X - radar.Left) / radar.Width() * sourceSize;
        float radarY = (point.Y - radar.Top) / radar.Height() * sourceSize;
        radarX = Math.Clamp(radarX, 0.0f, sourceSize);
        radarY = Math.Clamp(radarY, 0.0f, sourceSize);
        if (_definition.Rotate)
            (radarX, radarY) = (radarY, sourceSize - radarX);

        worldX = _definition.PosX + radarX * _definition.Scale;
        worldY = _definition.PosY - radarY * _definition.Scale;
        return true;
    }

    private RectF GetRadarRectangle()
    {
        float margin = 12.0f * Resources!.DisplayMetrics!.Density;
        float availableWidth = Math.Max(1.0f, Width - margin * 2.0f);
        float availableHeight = Math.Max(1.0f, Height - margin * 2.0f);
        float side = Math.Min(availableWidth, availableHeight);
        return new RectF(
            (Width - side) / 2.0f,
            (Height - side) / 2.0f,
            (Width + side) / 2.0f,
            (Height + side) / 2.0f);
    }

    private static PointF ClampToRadar(float x, float y, RectF radar) =>
        new(
            Math.Clamp(x, radar.Left, radar.Right),
            Math.Clamp(y, radar.Top, radar.Bottom));

    private void CancelDrag()
    {
        _dragPlayer = null;
        _dragPoint = null;
        _dragPointerId = InvalidPointerId;
        _dragMoved = false;
        _lastDragSendUtc = DateTime.MinValue;
        Pressed = false;
        Parent?.RequestDisallowInterceptTouchEvent(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelDrag();
            _bitmap?.Dispose();
            _bitmap = null;
        }
        base.Dispose(disposing);
    }

    private static string NormalizeMapName(string value)
    {
        string normalized = value.Trim().Replace('\\', '/');
        int separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }
}
