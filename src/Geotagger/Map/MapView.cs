using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Geotagger.Core.Geo;

namespace Geotagger.Map;

/// <summary>
/// A lightweight slippy map: raster tiles, GPX polylines and draggable, clustering photo pins.
/// Interaction: drag to pan, wheel/double-click to zoom, click pins to select, drag pins to move
/// photos, Shift+drag to box-select, and drop photos from elsewhere in the app to place them.
/// </summary>
public sealed class MapView : Control
{
    public const double MinZoom = 2;
    public const double MaxZoom = 20;

    public static readonly StyledProperty<MapStyle> MapStyleProperty =
        AvaloniaProperty.Register<MapView, MapStyle>(nameof(MapStyle), MapStyles.Streets);

    public static readonly StyledProperty<IReadOnlyList<MapMarker>?> MarkersProperty =
        AvaloniaProperty.Register<MapView, IReadOnlyList<MapMarker>?>(nameof(Markers));

    public static readonly StyledProperty<IReadOnlyList<MapTrack>?> TracksProperty =
        AvaloniaProperty.Register<MapView, IReadOnlyList<MapTrack>?>(nameof(Tracks));

    public static readonly StyledProperty<GeoPoint?> SearchPinProperty =
        AvaloniaProperty.Register<MapView, GeoPoint?>(nameof(SearchPin));

    public static readonly StyledProperty<TileCache?> TileCacheProperty =
        AvaloniaProperty.Register<MapView, TileCache?>(nameof(TileCache));

    public static readonly StyledProperty<GeoPoint?> HomeLocationProperty =
        AvaloniaProperty.Register<MapView, GeoPoint?>(nameof(HomeLocation));

    private static readonly Color HomeColor = Color.FromRgb(0x10, 0x7C, 0x41);
    private static readonly Geometry HouseGlyph = Geometry.Parse("M0,-27.5 L-6.5,-21.5 L-4.5,-21.5 L-4.5,-16 L4.5,-16 L4.5,-21.5 L6.5,-21.5 Z");

    /// <summary>Film mode: drops and pin drags snap onto track logs and carry the time of that point.</summary>
    public static readonly StyledProperty<bool> SnapToTracksProperty =
        AvaloniaProperty.Register<MapView, bool>(nameof(SnapToTracks));

    private const double SnapDistance = 30;

    private static readonly Geometry PinGeometry =
        Geometry.Parse("M0,0 C-2.5,-6 -11,-12.5 -11,-21.5 A11,11 0 1 1 11,-21.5 C11,-12.5 2.5,-6 0,0 Z");
    private const double PinHeadY = -21.5;

    private static readonly Color SavedColor = Color.FromRgb(0x1F, 0x6F, 0xD1);
    private static readonly Color PendingColor = Color.FromRgb(0xF7, 0x63, 0x0C);
    private static readonly Color SelectedRing = Color.FromRgb(0xFF, 0xD4, 0x00);
    private static readonly Color SearchColor = Color.FromRgb(0xD1, 0x34, 0x38);
    private static readonly IBrush ShadowBrush = new SolidColorBrush(Colors.Black, 0.28);
    private static readonly IPen WhitePen = new Pen(Brushes.White, 1.6);
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(SelectedRing), 3.2);

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);

    // View state, in normalised Web Mercator coordinates.
    private double _cx = 0.5;
    private double _cy = 0.35;
    private double _zoom = 3;
    private double _targetZoom = 3;

    // Animation.
    private Action<double>? _animation;
    private TimeSpan? _animationStart;
    private double _animationMs;
    private Action? _pendingFit;

    // Pointer interaction.
    private enum PressMode { None, Pan, MarkerPending, MarkerDrag, Box }
    private PressMode _mode;
    private Point _pressPoint;
    private double _panStartCx, _panStartCy;
    private Cluster? _pressCluster;
    private List<MapMarker>? _dragMarkers;
    private Point _dragPoint;
    private Vector _dragOffset;
    private Point _boxEnd;
    private MapMarker? _hoverMarker;
    private Point? _dropGhost;
    private TrackSnap? _snap;
    private Point _snapScreen;
    private int _dropCount;

    private readonly List<Cluster> _clusters = [];
    private readonly Dictionary<string, FormattedText> _textCache = [];

    static MapView()
    {
        AffectsRender<MapView>(MapStyleProperty, MarkersProperty, TracksProperty, SearchPinProperty, SnapToTracksProperty, HomeLocationProperty);
        FocusableProperty.OverrideDefaultValue<MapView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<MapView>(true);
    }

    public MapView()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public MapStyle MapStyle { get => GetValue(MapStyleProperty); set => SetValue(MapStyleProperty, value); }
    public IReadOnlyList<MapMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public IReadOnlyList<MapTrack>? Tracks { get => GetValue(TracksProperty); set => SetValue(TracksProperty, value); }
    public bool SnapToTracks { get => GetValue(SnapToTracksProperty); set => SetValue(SnapToTracksProperty, value); }
    public GeoPoint? HomeLocation { get => GetValue(HomeLocationProperty); set => SetValue(HomeLocationProperty, value); }

    /// <summary>The home location if <paramref name="p"/> is on (or very near) the home pin.</summary>
    private GeoPoint? HomeAt(Point p)
    {
        if (HomeLocation is not { } home) return null;
        var h = GeoToScreen(home);
        double dx = p.X - h.X, dy = p.Y - (h.Y + PinHeadY);
        return dx * dx + dy * dy <= 22 * 22 || (Math.Abs(p.X - h.X) <= 14 && Math.Abs(p.Y - h.Y) <= 14) ? home : null;
    }

    /// <summary>Formats a track time for the snap label (e.g. in the photos' time zone).</summary>
    public Func<DateTimeOffset, string>? FormatTime { get; set; }

    /// <summary>The track position under the last right-click, when snapping to tracks.</summary>
    public TrackSnap? ContextSnap { get; private set; }
    public GeoPoint? SearchPin { get => GetValue(SearchPinProperty); set => SetValue(SearchPinProperty, value); }
    public TileCache? TileCache { get => GetValue(TileCacheProperty); set => SetValue(TileCacheProperty, value); }

    /// <summary>Whether pins can be dragged to move photos.</summary>
    public bool AllowMarkerDrag { get; set; } = true;

    /// <summary>Returns how many items a drag payload carries (0 = not droppable here).</summary>
    public Func<IDataTransfer, int>? DropItemCount { get; set; }

    /// <summary>Areas (in map coordinates) covered by floating controls that hover cards should avoid.</summary>
    public Func<IEnumerable<Rect>>? AvoidAreas { get; set; }

    /// <summary>Where the user last right-clicked (for context menu actions).</summary>
    public GeoPoint? ContextLocation { get; private set; }

    /// <summary>Markers under the pointer at the last right-click, if any.</summary>
    public IReadOnlyList<MapMarker>? ContextMarkers { get; private set; }

    public double Zoom => _zoom;

    public GeoPoint Center => WebMercator.Unproject(_cx, _cy);

    public event EventHandler<MapMarkersEventArgs>? MarkersClicked;
    public event EventHandler<MapMarkersMovedEventArgs>? MarkersMoved;
    public event EventHandler<MapMarkersEventArgs>? BoxSelected;
    public event EventHandler<MapDropEventArgs>? ItemsDropped;
    public event EventHandler? ViewChanged;

    // ---------------------------------------------------------------- View control

    public void SetView(GeoPoint center, double zoom)
    {
        _animation = null;
        _zoom = _targetZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        (_cx, _cy) = WebMercator.Project(center.Latitude, center.Longitude);
        ClampCenter();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FlyTo(GeoPoint center, double? zoom = null)
    {
        double z0 = _zoom, z1 = Math.Clamp(zoom ?? _zoom, MinZoom, MaxZoom);
        double x0 = _cx, y0 = _cy;
        var (x1, y1) = WebMercator.Project(center.Latitude, center.Longitude);
        // Go the short way round the world.
        if (x1 - x0 > 0.5) x1 -= 1; else if (x0 - x1 > 0.5) x1 += 1;
        _targetZoom = z1;
        Animate(350, t =>
        {
            _zoom = z0 + (z1 - z0) * t;
            _cx = x0 + (x1 - x0) * t;
            _cy = y0 + (y1 - y0) * t;
        });
    }

    /// <summary>Zooms so all points are visible. Deferred until the control has a size.</summary>
    public void FitTo(IReadOnlyCollection<GeoPoint> points, double maxZoom = 17, bool animate = true)
    {
        if (points.Count == 0) return;
        void Apply()
        {
            var (center, zoom) = WebMercator.Fit(points, Bounds.Width, Bounds.Height, 60, MinZoom, maxZoom);
            if (animate) FlyTo(center, zoom); else SetView(center, zoom);
        }
        if (Bounds.Width < 10 || Bounds.Height < 10) _pendingFit = Apply;
        else Apply();
    }

    public void ZoomIn() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), CurrentTargetZoom + 1);

    public void ZoomOut() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), CurrentTargetZoom - 1);

    private double CurrentTargetZoom => _animation is not null ? _targetZoom : _zoom;

    public void ZoomAt(Point anchor, double newZoom, bool animate = true)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        var (ax, ay) = ToWorld(anchor);
        double startZoom = _zoom;
        _targetZoom = newZoom;
        void Step(double t)
        {
            _zoom = startZoom + (newZoom - startZoom) * t;
            double ws = WebMercator.WorldSize(_zoom);
            _cx = ax - (anchor.X - Bounds.Width / 2) / ws;
            _cy = ay - (anchor.Y - Bounds.Height / 2) / ws;
        }
        if (animate) Animate(170, Step);
        else
        {
            Step(1);
            ClampCenter();
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public GeoPoint ScreenToGeo(Point p)
    {
        var (x, y) = ToWorld(p);
        return WebMercator.Unproject(x - Math.Floor(x), Math.Clamp(y, 0, 1));
    }

    public Point GeoToScreen(GeoPoint g)
    {
        var (x, y) = WebMercator.Project(g.Latitude, g.Longitude);
        return ToScreen(x, y);
    }

    private void Animate(double durationMs, Action<double> step)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || !IsEffectivelyVisible)
        {
            step(1);
            ClampCenter();
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        bool running = _animation is not null;
        _animation = step;
        _animationMs = durationMs;
        _animationStart = null;
        if (!running) top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        if (_animation is null) return;
        _animationStart ??= now;
        double t = Math.Clamp((now - _animationStart.Value).TotalMilliseconds / _animationMs, 0, 1);
        _animation(1 - Math.Pow(1 - t, 3));
        ClampCenter();
        InvalidateVisual();
        if (t < 1)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
        }
        else
        {
            _animation = null;
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private double WorldSize => WebMercator.WorldSize(_zoom);

    private Point ToScreen(double x, double y)
    {
        double ws = WorldSize;
        double dx = x - _cx;
        dx -= Math.Round(dx);
        return new Point(dx * ws + Bounds.Width / 2, (y - _cy) * ws + Bounds.Height / 2);
    }

    private (double X, double Y) ToWorld(Point p)
    {
        double ws = WorldSize;
        return (_cx + (p.X - Bounds.Width / 2) / ws, _cy + (p.Y - Bounds.Height / 2) / ws);
    }

    private void ClampCenter()
    {
        _cx -= Math.Floor(_cx);
        double half = Bounds.Height / 2 / WorldSize;
        _cy = half * 2 >= 1 ? 0.5 : Math.Clamp(_cy, half, 1 - half);
    }

    // ---------------------------------------------------------------- Property plumbing

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TileCacheProperty)
        {
            if (change.OldValue is TileCache old) old.TilesChanged -= InvalidateVisual;
            if (change.NewValue is TileCache @new) @new.TilesChanged += InvalidateVisual;
        }
        else if (change.Property == MarkersProperty)
        {
            _hoverMarker = null;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_pendingFit is not null && Bounds.Width >= 10 && Bounds.Height >= 10)
        {
            var fit = _pendingFit;
            _pendingFit = null;
            fit();
        }
        ClampCenter();
    }

    // ---------------------------------------------------------------- Rendering

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        bool dark = ActualThemeVariant == ThemeVariant.Dark;

        ctx.FillRectangle(new SolidColorBrush(MapStyle.IsDark || dark ? Color.FromRgb(0x26, 0x26, 0x26) : Color.FromRgb(0xE6, 0xE4, 0xE0)),
            new Rect(0, 0, w, h));

        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
            DrawTiles(ctx, w, h);

        DrawTracks(ctx, w, h);
        if (HomeLocation is { } home) DrawHomePin(ctx, GeoToScreen(home)); // under the photo pins
        BuildClusters(w, h);
        DrawClusters(ctx);

        if (SearchPin is { } sp) DrawPin(ctx, GeoToScreen(sp), SearchColor, 1.15, WhitePen, null, 1);

        if (SnapToTracks && _snap is not null) DrawSnapIndicator(ctx, w);

        if (_mode == PressMode.MarkerDrag && _dragMarkers is not null)
        {
            int count = _dragMarkers.Sum(m => m.Photos.Count);
            if (!SnapToTracks)
                DrawPin(ctx, _dragPoint - _dragOffset, PendingColor, 1.3, SelectedPen, count > 1 ? Count(count) : null, 0.9);
            else if (_snap is not null)
                DrawPin(ctx, _snapScreen, PendingColor, 1.3, SelectedPen, count > 1 ? Count(count) : null, 0.9);
            else // off the track: releasing here leaves the pin where it was
                DrawPin(ctx, _dragPoint - _dragOffset, SavedColor, 1.1, WhitePen, null, 0.35);
        }

        if (_dropGhost is { } g)
            DrawPin(ctx, g, PendingColor, 1.3, SelectedPen, _dropCount > 1 ? Count(_dropCount) : null, 0.85);

        if (_mode == PressMode.Box)
        {
            var r = new Rect(_pressPoint, _boxEnd).Normalize();
            ctx.DrawRectangle(new SolidColorBrush(SavedColor, 0.15), new Pen(new SolidColorBrush(SavedColor), 1.2), r);
        }

        if (_hoverMarker is not null && _mode == PressMode.None && _dropGhost is null &&
            _clusters.FirstOrDefault(c => c.Markers.Contains(_hoverMarker)) is { } hovered)
            DrawHoverCard(ctx, hovered, w, h, dark);

        DrawScaleBar(ctx, h, dark);
        DrawAttribution(ctx, w, h, dark);
    }

    private void DrawTiles(DrawingContext ctx, double w, double h)
    {
        var cache = TileCache;
        var wanted = new List<(TileLayer, TileKey)>();

        foreach (var layer in MapStyle.Layers)
        {
            int z = Math.Clamp((int)Math.Round(_zoom), 0, layer.MaxZoom);
            int n = 1 << z;
            double tileSize = WebMercator.TileSize * Math.Pow(2, _zoom - z);
            double ctX = _cx * n, ctY = _cy * n;
            int x0 = (int)Math.Floor(ctX - w / 2 / tileSize), x1 = (int)Math.Floor(ctX + w / 2 / tileSize);
            int y0 = Math.Max(0, (int)Math.Floor(ctY - h / 2 / tileSize)), y1 = Math.Min(n - 1, (int)Math.Floor(ctY + h / 2 / tileSize));
            var layerWanted = new List<(double Distance, TileKey Key)>();

            for (int ty = y0; ty <= y1; ty++)
            {
                for (int tx = x0; tx <= x1; tx++)
                {
                    double sx = Math.Floor((tx - ctX) * tileSize + w / 2);
                    double sy = Math.Floor((ty - ctY) * tileSize + h / 2);
                    var dest = new Rect(sx, sy, Math.Ceiling(tileSize) + 1, Math.Ceiling(tileSize) + 1);
                    int wx = ((tx % n) + n) % n;
                    var key = new TileKey(layer.Id, z, wx, ty);

                    if (cache is not null && cache.TryGet(key, out var bmp))
                    {
                        ctx.DrawImage(bmp, new Rect(bmp.Size), dest);
                        continue;
                    }

                    double dx = tx + 0.5 - ctX, dy = ty + 0.5 - ctY;
                    layerWanted.Add((dx * dx + dy * dy, key));

                    // Show a scaled-up ancestor while the real tile loads.
                    if (cache is null) continue;
                    for (int up = 1; up <= Math.Min(z, 6); up++)
                    {
                        var pk = key.Parent(up);
                        if (!cache.TryGet(pk, out var pb)) continue;
                        double sub = pb.Size.Width / (1 << up);
                        double ox = (key.X - (pk.X << up)) * sub, oy = (key.Y - (pk.Y << up)) * sub;
                        ctx.DrawImage(pb, new Rect(ox, oy, sub, sub), dest);
                        break;
                    }
                }
            }
            wanted.AddRange(layerWanted.OrderBy(t => t.Distance).Select(t => (layer, t.Key)));
        }

        cache?.SetWanted(wanted);
    }

    private void DrawTracks(DrawingContext ctx, double w, double h)
    {
        var tracks = Tracks;
        if (tracks is null) return;
        double ws = WorldSize;
        var halo = new Pen(new SolidColorBrush(Colors.White, 0.85), 6.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        foreach (var t in tracks)
        {
            if (t.Xs.Length == 0) continue;
            double offset = -Math.Round(t.Xs[0] - _cx);
            var geometry = new StreamGeometry();
            Point first = default, last = default;
            using (var g = geometry.Open())
            {
                bool started = false;
                for (int i = 0; i < t.Xs.Length; i++)
                {
                    var p = new Point((t.Xs[i] + offset - _cx) * ws + w / 2, (t.Ys[i] - _cy) * ws + h / 2);
                    if (started && i != t.Xs.Length - 1 && Math.Abs(p.X - last.X) < 1.5 && Math.Abs(p.Y - last.Y) < 1.5) continue;
                    if (!started) { g.BeginFigure(p, false); first = p; started = true; }
                    else g.LineTo(p);
                    last = p;
                }
                if (started) g.EndFigure(false);
            }
            var brush = new SolidColorBrush(t.Color);
            ctx.DrawGeometry(null, halo, geometry);
            ctx.DrawGeometry(null, new Pen(brush, 3.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
            ctx.DrawEllipse(Brushes.White, new Pen(brush, 2.5), first, 5, 5);
            ctx.DrawEllipse(brush, new Pen(Brushes.White, 2), last, 5, 5);
        }
    }

    private sealed class Cluster
    {
        public double Sx, Sy;
        public readonly List<MapMarker> Markers = [];
        public int Count;
        public bool Selected, Pending;
        public bool SingleLocation = true;
    }

    private void BuildClusters(double w, double h)
    {
        _clusters.Clear();
        var markers = Markers;
        if (markers is null) return;

        const double radius = 26;
        var grid = new Dictionary<(int, int), List<Cluster>>();
        var dragging = _mode == PressMode.MarkerDrag ? _dragMarkers : null;

        foreach (var m in markers)
        {
            if (dragging is not null && dragging.Contains(m)) continue;
            var p = ToScreen(m.X, m.Y);
            if (p.X < -40 || p.Y < -10 || p.X > w + 40 || p.Y > h + 50) continue;

            int gx = (int)Math.Floor(p.X / radius), gy = (int)Math.Floor(p.Y / radius);
            Cluster? target = null;
            for (int ix = gx - 1; ix <= gx + 1 && target is null; ix++)
            for (int iy = gy - 1; iy <= gy + 1 && target is null; iy++)
            {
                if (!grid.TryGetValue((ix, iy), out var list)) continue;
                foreach (var c in list)
                {
                    double dx = c.Sx - p.X, dy = c.Sy - p.Y;
                    if (dx * dx + dy * dy <= radius * radius) { target = c; break; }
                }
            }

            if (target is null)
            {
                target = new Cluster { Sx = p.X, Sy = p.Y };
                _clusters.Add(target);
                if (!grid.TryGetValue((gx, gy), out var cell)) grid[(gx, gy)] = cell = [];
                cell.Add(target);
            }
            else if (target.Markers.Count > 0 && !target.Markers[0].Location.SamePlaceAs(m.Location))
            {
                target.SingleLocation = false;
            }

            target.Markers.Add(m);
            target.Count += m.Photos.Count;
            target.Selected |= m.IsSelected;
            target.Pending |= m.IsPending;
        }

        // Selected pins on top.
        _clusters.Sort((a, b) => a.Selected.CompareTo(b.Selected));
    }

    private void DrawClusters(DrawingContext ctx)
    {
        bool anySelected = _clusters.Any(c => c.Selected);
        foreach (var c in _clusters)
        {
            bool hover = _hoverMarker is not null && c.Markers.Contains(_hoverMarker);
            double scale = c.Selected ? 1.25 : hover ? 1.12 : 1.0;
            double opacity = anySelected && !c.Selected && !hover ? 0.78 : 1.0;
            DrawPin(ctx, new Point(c.Sx, c.Sy), c.Pending ? PendingColor : SavedColor, scale,
                c.Selected ? SelectedPen : WhitePen, c.Count > 1 ? Count(c.Count) : null, opacity);
        }
    }

    private void DrawHomePin(DrawingContext ctx, Point tip)
    {
        using var _ = ctx.PushTransform(Matrix.CreateScale(1.15, 1.15) * Matrix.CreateTranslation(tip.X, tip.Y));
        using (ctx.PushTransform(Matrix.CreateTranslation(1, 2)))
            ctx.DrawGeometry(ShadowBrush, null, PinGeometry);
        ctx.DrawGeometry(new SolidColorBrush(HomeColor), WhitePen, PinGeometry);
        ctx.DrawGeometry(Brushes.White, null, HouseGlyph);
    }

    private static string Count(int n) => n > 999 ? "999+" : n.ToString(CultureInfo.InvariantCulture);

    private void DrawPin(DrawingContext ctx, Point tip, Color fill, double scale, IPen stroke, string? label, double opacity)
    {
        using var _ = ctx.PushOpacity(opacity);
        using var __ = ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(tip.X, tip.Y));
        using (ctx.PushTransform(Matrix.CreateTranslation(1, 2)))
            ctx.DrawGeometry(ShadowBrush, null, PinGeometry);
        ctx.DrawGeometry(new SolidColorBrush(fill), stroke, PinGeometry);

        if (label is null)
        {
            ctx.DrawEllipse(Brushes.White, null, new Point(0, PinHeadY), 4, 4);
        }
        else
        {
            var text = Text(label, label.Length > 2 ? 8.5 : 10.5, Brushes.White, FontWeight.Bold);
            ctx.DrawText(text, new Point(-text.Width / 2, PinHeadY - text.Height / 2));
        }
    }

    private FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal)
    {
        string key = $"{s}|{size}|{weight}|{brush.GetHashCode()}";
        if (_textCache.TryGetValue(key, out var t)) return t;
        if (_textCache.Count > 400) _textCache.Clear();
        t = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush);
        _textCache[key] = t;
        return t;
    }

    private void DrawHoverCard(DrawingContext ctx, Cluster c, double w, double h, bool dark)
    {
        var photos = c.Markers.SelectMany(m => m.Photos).ToList();
        if (photos.Count == 0) return;

        Bitmap? thumb = photos.Select(p => p.Thumbnail).FirstOrDefault(t => t is not null);
        IBrush fg = dark ? Brushes.White : Brushes.Black;
        IBrush fg2 = new SolidColorBrush(dark ? Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xA0, 0, 0, 0));
        string title = photos.Count == 1 ? photos[0].FileName : $"{photos.Count} photos";
        var dates = photos.Where(p => p.DateTaken.HasValue).Select(p => p.DateTaken!.Value).OrderBy(d => d).ToList();
        string subtitle = dates.Count == 0 ? "" :
            dates[0] == dates[^1] ? dates[0].ToString("g", CultureInfo.CurrentCulture) :
            dates[0].Date == dates[^1].Date && (dates[^1] - dates[0]).TotalMinutes < 1 ? $"{dates[0]:d}  {dates[0]:T} – {dates[^1]:T}" :
            dates[0].Date == dates[^1].Date ? $"{dates[0]:d}  {dates[0]:t} – {dates[^1]:t}" :
            $"{dates[0]:d} – {dates[^1]:d}";
        if (!c.SingleLocation) subtitle += (subtitle.Length > 0 ? " · " : "") + "double-click to zoom";

        var t1 = Text(title, 12.5, fg, FontWeight.SemiBold);
        var t2 = Text(subtitle, 11, fg2);
        double thumbW = 0, thumbH = 0;
        if (thumb is not null)
        {
            double s = Math.Min(200 / thumb.Size.Width, 150 / thumb.Size.Height);
            thumbW = thumb.Size.Width * s;
            thumbH = thumb.Size.Height * s;
        }
        double pad = 8;
        double cardW = Math.Max(Math.Max(thumbW, t1.Width), t2.Width) + pad * 2;
        double cardH = pad + (thumb is not null ? thumbH + 6 : 0) + t1.Height + (subtitle.Length > 0 ? t2.Height + 2 : 0) + pad;
        var rect = PlaceCard(c, cardW, cardH, w, h);
        double x = rect.X, y = rect.Y;
        using (ctx.PushTransform(Matrix.CreateTranslation(0, 3)))
            ctx.DrawRectangle(new SolidColorBrush(Colors.Black, 0.18), null, rect.Inflate(1), 9, 9);
        ctx.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White, 0.97),
            new Pen(new SolidColorBrush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0, 0, 0)), 1), rect, 8, 8);

        double cy = y + pad;
        if (thumb is not null)
        {
            var dest = new Rect(x + (cardW - thumbW) / 2, cy, thumbW, thumbH);
            using (ctx.PushClip(new RoundedRect(dest, 4)))
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(thumb, new Rect(thumb.Size), dest);
            cy += thumbH + 6;
        }
        ctx.DrawText(t1, new Point(x + pad, cy));
        cy += t1.Height + 2;
        if (subtitle.Length > 0) ctx.DrawText(t2, new Point(x + pad, cy));
    }

    /// <summary>
    /// Picks a spot for the hover card: above the pin if possible, otherwise below, left or right,
    /// avoiding the map's edges and any floating controls drawn over it.
    /// </summary>
    private Rect PlaceCard(Cluster c, double cardW, double cardH, double w, double h)
    {
        const double margin = 6;
        double centred = Math.Clamp(c.Sx - cardW / 2, margin, Math.Max(margin, w - cardW - margin));
        double middle = Math.Clamp(c.Sy - 20 - cardH / 2, margin, Math.Max(margin, h - cardH - margin));
        Rect[] candidates =
        [
            new(centred, c.Sy - 40 - cardH, cardW, cardH),
            new(centred, c.Sy + 10, cardW, cardH),
            new(c.Sx - 24 - cardW, middle, cardW, cardH),
            new(c.Sx + 24, middle, cardW, cardH),
        ];
        var avoid = AvoidAreas?.Invoke().Select(r => r.Inflate(4)).ToList() ?? [];
        foreach (var r in candidates)
        {
            bool inside = r.X >= margin && r.Y >= margin && r.Right <= w - margin && r.Bottom <= h - margin;
            if (inside && !avoid.Any(a => a.Intersects(r))) return r;
        }
        var fallback = candidates[0];
        return fallback.Y < margin ? candidates[1] : fallback;
    }

    // ---------------------------------------------------------------- Track snapping (film mode)

    /// <summary>
    /// Finds the closest point on any timed track within <paramref name="maxDistance"/> pixels of
    /// <paramref name="p"/>, interpolating position, elevation and time between fixes.
    /// </summary>
    public TrackSnap? SnapAt(Point p, double maxDistance = SnapDistance)
    {
        var tracks = Tracks;
        if (tracks is null || Bounds.Width <= 0) return null;
        double ws = WorldSize, w2 = Bounds.Width / 2, h2 = Bounds.Height / 2;
        double best = maxDistance * maxDistance;
        MapTrack? bestTrack = null;
        int bestIndex = -1;
        double bestF = 0;

        foreach (var t in tracks)
        {
            int n = t.Xs.Length;
            if (n == 0 || t.Points.Count != n) continue;
            double offset = -Math.Round(t.Xs[0] - _cx);
            double ax = (t.Xs[0] + offset - _cx) * ws + w2, ay = (t.Ys[0] - _cy) * ws + h2;
            if (n == 1)
            {
                double d1 = (p.X - ax) * (p.X - ax) + (p.Y - ay) * (p.Y - ay);
                if (d1 < best && t.Points[0].Time is not null) { best = d1; bestTrack = t; bestIndex = 0; bestF = 0; }
                continue;
            }
            for (int i = 1; i < n; i++)
            {
                double bx = (t.Xs[i] + offset - _cx) * ws + w2, by = (t.Ys[i] - _cy) * ws + h2;
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                double f = len2 > 0 ? Math.Clamp(((p.X - ax) * dx + (p.Y - ay) * dy) / len2, 0, 1) : 0;
                double qx = ax + dx * f - p.X, qy = ay + dy * f - p.Y;
                double d = qx * qx + qy * qy;
                if (d < best && t.Points[i - 1].Time is not null && t.Points[i].Time is not null)
                {
                    best = d; bestTrack = t; bestIndex = i - 1; bestF = f;
                }
                ax = bx; ay = by;
            }
        }
        if (bestTrack is null) return null;

        int next = Math.Min(bestIndex + 1, bestTrack.Xs.Length - 1);
        var a = bestTrack.Points[bestIndex];
        var b = bestTrack.Points[next];
        double x = bestTrack.Xs[bestIndex] + (bestTrack.Xs[next] - bestTrack.Xs[bestIndex]) * bestF;
        double y = bestTrack.Ys[bestIndex] + (bestTrack.Ys[next] - bestTrack.Ys[bestIndex]) * bestF;
        var geo = WebMercator.Unproject(x - Math.Floor(x), y);
        double? ele = a.Elevation is { } ea && b.Elevation is { } eb ? ea + (eb - ea) * bestF : a.Elevation ?? b.Elevation;
        var ta = a.Time!.Value;
        var time = ta + TimeSpan.FromTicks((long)((b.Time!.Value - ta).Ticks * bestF));
        return new TrackSnap(geo with { Altitude = ele }, time, bestTrack.Name);
    }

    private void UpdateSnap(Point p)
    {
        var snap = SnapToTracks ? SnapAt(p) : null;
        if (snap is null && _snap is null) return;
        _snap = snap;
        if (snap is not null) _snapScreen = GeoToScreen(snap.Location);
        InvalidateVisual();
    }

    private void ClearSnap()
    {
        if (_snap is null) return;
        _snap = null;
        InvalidateVisual();
    }

    private void DrawSnapIndicator(DrawingContext ctx, double w)
    {
        var snap = _snap!;
        var c = _snapScreen;
        ctx.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(PendingColor), 3), c, 7, 7);

        string label = FormatTime?.Invoke(snap.Time) ?? snap.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        var text = Text(label, 12.5, Brushes.White, FontWeight.SemiBold);
        double bw = text.Width + 18, bh = text.Height + 8;
        double x = Math.Clamp(c.X + 14, 4, Math.Max(4, w - bw - 4));
        double y = c.Y - bh - 12;
        if (y < 4) y = c.Y + 12;
        var r = new Rect(x, y, bw, bh);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)), new Pen(new SolidColorBrush(PendingColor), 1.5), r, bh / 2, bh / 2);
        ctx.DrawText(text, new Point(r.X + 9, r.Y + 4));
    }

    private void DrawScaleBar(DrawingContext ctx, double h, bool dark)
    {
        double mpp = WebMercator.MetersPerPixel(Center.Latitude, _zoom);
        if (mpp <= 0 || double.IsNaN(mpp)) return;
        double maxMeters = mpp * 110;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(maxMeters)));
        double nice = new[] { 5, 2, 1 }.Select(f => f * pow).First(v => v <= maxMeters);
        double px = nice / mpp;
        string label = nice >= 1000 ? $"{nice / 1000:0.##} km" : $"{nice:0} m";

        var fg = dark ? Brushes.White : Brushes.Black;
        var text = Text(label, 10.5, fg);
        var bg = new SolidColorBrush(dark ? Colors.Black : Colors.White, 0.7);
        double x = 10, y = h - 26;
        ctx.DrawRectangle(bg, null, new Rect(x - 4, y - 3, Math.Max(px, text.Width) + 12, 22), 4, 4);
        var pen = new Pen(fg, 1.5);
        ctx.DrawLine(pen, new Point(x, y + 13), new Point(x + px, y + 13));
        ctx.DrawLine(pen, new Point(x, y + 9), new Point(x, y + 15));
        ctx.DrawLine(pen, new Point(x + px, y + 9), new Point(x + px, y + 15));
        ctx.DrawText(text, new Point(x + 3, y - 2));
    }

    private void DrawAttribution(DrawingContext ctx, double w, double h, bool dark)
    {
        var text = Text(MapStyle.Attribution, 10, dark ? Brushes.White : Brushes.Black);
        var r = new Rect(w - text.Width - 10, h - text.Height - 4, text.Width + 10, text.Height + 4);
        ctx.DrawRectangle(new SolidColorBrush(dark ? Colors.Black : Colors.White, 0.65), null, r, 4, 4);
        ctx.DrawText(text, new Point(r.X + 5, r.Y + 2));
    }

    // ---------------------------------------------------------------- Input

    private Cluster? HitTest(Point p)
    {
        for (int i = _clusters.Count - 1; i >= 0; i--)
        {
            var c = _clusters[i];
            double s = c.Selected ? 1.25 : 1.0;
            double dx = p.X - c.Sx, dy = p.Y - (c.Sy + PinHeadY * s);
            if (dx * dx + dy * dy <= 13 * 13 * s * s) return c;
            if (Math.Abs(dx) <= 6 * s && p.Y <= c.Sy + 1 && p.Y >= c.Sy + PinHeadY * s) return c;
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var p = point.Position;
        Focus();

        if (point.Properties.IsRightButtonPressed)
        {
            ContextLocation = ScreenToGeo(p);
            ContextMarkers = HitTest(p)?.Markers.ToList();
            ContextSnap = SnapToTracks ? SnapAt(p) : null;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            var c = HitTest(p);
            if (c is not null && !c.SingleLocation)
                FitTo(c.Markers.Select(m => m.Location).ToList(), Math.Min(MaxZoom, _zoom + 4));
            else
                ZoomAt(p, CurrentTargetZoom + 1);
            _mode = PressMode.None;
            e.Handled = true;
            return;
        }

        _animation = null;
        _pressPoint = p;
        _panStartCx = _cx;
        _panStartCy = _cy;
        _pressCluster = HitTest(p);
        _mode = _pressCluster is not null ? PressMode.MarkerPending
            : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? PressMode.Box
            : PressMode.Pan;
        _boxEnd = p;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        switch (_mode)
        {
            case PressMode.None:
            {
                var hit = HitTest(p);
                var marker = hit?.Markers[0];
                if (!ReferenceEquals(marker, _hoverMarker))
                {
                    _hoverMarker = marker;
                    InvalidateVisual();
                }
                if (hit is null) UpdateSnap(p); else ClearSnap();
                Cursor = hit is not null ? HandCursor : null;
                return;
            }
            case PressMode.Pan:
            {
                ClearSnap();
                double ws = WorldSize;
                _cx = _panStartCx - (p.X - _pressPoint.X) / ws;
                _cy = _panStartCy - (p.Y - _pressPoint.Y) / ws;
                ClampCenter();
                Cursor = MoveCursor;
                InvalidateVisual();
                return;
            }
            case PressMode.MarkerPending:
            {
                if (Distance(p, _pressPoint) < 5) return;
                if (_pressCluster is { SingleLocation: true } c && AllowMarkerDrag)
                {
                    _mode = PressMode.MarkerDrag;
                    _dragMarkers = c.Markers.ToList();
                    _dragOffset = _pressPoint - new Point(c.Sx, c.Sy);
                    _dragPoint = p;
                    _hoverMarker = null;
                    Cursor = MoveCursor;
                }
                else
                {
                    _mode = PressMode.Pan;
                }
                InvalidateVisual();
                return;
            }
            case PressMode.MarkerDrag:
                _dragPoint = p;
                if (SnapToTracks) UpdateSnap(p - _dragOffset);
                InvalidateVisual();
                return;
            case PressMode.Box:
                _boxEnd = p;
                Cursor = CrossCursor;
                InvalidateVisual();
                return;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mode == PressMode.None) return;
        var p = e.GetPosition(this);
        var mode = _mode;
        _mode = PressMode.None;
        e.Pointer.Capture(null);
        Cursor = null;

        switch (mode)
        {
            case PressMode.MarkerPending when _pressCluster is not null:
                MarkersClicked?.Invoke(this, new MapMarkersEventArgs(_pressCluster.Markers.ToList(), e.KeyModifiers));
                break;
            case PressMode.MarkerDrag when _dragMarkers is not null:
                if (SnapToTracks)
                {
                    // In film mode a pin only moves if it lands on the track.
                    if (SnapAt(p - _dragOffset) is { } snap)
                        MarkersMoved?.Invoke(this, new MapMarkersMovedEventArgs(_dragMarkers, snap.Location, snap));
                }
                else
                {
                    MarkersMoved?.Invoke(this, new MapMarkersMovedEventArgs(_dragMarkers, HomeAt(p - _dragOffset) ?? ScreenToGeo(p - _dragOffset)));
                }
                ClearSnap();
                break;
            case PressMode.Box:
            {
                var r = new Rect(_pressPoint, p).Normalize();
                var inside = _clusters.Where(c => r.Contains(new Point(c.Sx, c.Sy))).SelectMany(c => c.Markers).ToList();
                BoxSelected?.Invoke(this, new MapMarkersEventArgs(inside, e.KeyModifiers));
                break;
            }
            case PressMode.Pan:
                ViewChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
        _dragMarkers = null;
        _pressCluster = null;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_mode != PressMode.None)
        {
            _mode = PressMode.None;
            _dragMarkers = null;
            Cursor = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverMarker is not null)
        {
            _hoverMarker = null;
            InvalidateVisual();
        }
        if (_mode == PressMode.None) ClearSnap();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double delta = Math.Clamp(e.Delta.Y, -3, 3);
        if (Math.Abs(delta) < 0.001) return;
        ZoomAt(e.GetPosition(this), CurrentTargetZoom + delta * 0.6);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = 80 / WorldSize;
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus: ZoomIn(); break;
            case Key.Subtract or Key.OemMinus: ZoomOut(); break;
            case Key.Left: _cx -= step; break;
            case Key.Right: _cx += step; break;
            case Key.Up: _cy -= step; break;
            case Key.Down: _cy += step; break;
            default: return;
        }
        ClampCenter();
        InvalidateVisual();
        e.Handled = true;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---------------------------------------------------------------- Drag & drop from the filmstrip

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        int n = DropItemCount?.Invoke(e.DataTransfer) ?? 0;
        if (n <= 0)
        {
            if (_dropGhost is not null) { _dropGhost = null; InvalidateVisual(); }
            return;
        }
        e.Handled = true;
        _dropCount = n;
        var p = e.GetPosition(this);
        if (SnapToTracks)
        {
            UpdateSnap(p);
            if (_snap is null)
            {
                // Film mode: photos can only be dropped onto a track.
                e.DragEffects = DragDropEffects.None;
                _dropGhost = null;
                InvalidateVisual();
                return;
            }
            p = _snapScreen;
        }
        else if (HomeAt(p) is { } home)
        {
            p = GeoToScreen(home);
        }
        e.DragEffects &= DragDropEffects.Link | DragDropEffects.Copy;
        _dropGhost = p;
        InvalidateVisual();
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dropGhost = null;
        _snap = null;
        InvalidateVisual();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dropGhost = null;
        _snap = null;
        InvalidateVisual();
        if ((DropItemCount?.Invoke(e.DataTransfer) ?? 0) <= 0) return;
        var p = e.GetPosition(this);
        if (SnapToTracks)
        {
            if (SnapAt(p) is { } snap)
                ItemsDropped?.Invoke(this, new MapDropEventArgs(e.DataTransfer, snap.Location, snap));
        }
        else
        {
            ItemsDropped?.Invoke(this, new MapDropEventArgs(e.DataTransfer, HomeAt(p) ?? ScreenToGeo(p)));
        }
        e.Handled = true;
    }
}
