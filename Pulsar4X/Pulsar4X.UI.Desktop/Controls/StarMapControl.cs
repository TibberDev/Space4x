using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Pulsar4X.Api;
using Pulsar4X.UI.Core.Map;

namespace Pulsar4X.UI.Desktop.Controls;

/// <summary>
/// Draws a <see cref="StarMapViewModel"/> (its DataContext) and turns mouse input into map actions.
/// All rules (what is visible, labelled, hit) live in the view model; this class only paints and
/// forwards input, so a mobile renderer can reuse the same model.
///
/// Input: drag to pan, wheel to zoom at the cursor, click to select, double-click to centre and
/// follow, Home to fit the system, +/- to zoom.
/// </summary>
public sealed class StarMapControl : Control
{
    private const double DragThresholdPixels = 4;

    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0A, 0x10));
    private static readonly IBrush StarfieldBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xC9, 0xD1, 0xD9));
    private static readonly IPen OrbitPen = new Pen(new SolidColorBrush(Color.FromUInt32(MapPalette.Orbit)), 1);
    private static readonly IPen MinorOrbitPen = new Pen(new SolidColorBrush(Color.FromUInt32(MapPalette.MinorOrbit)), 1);
    private static readonly IPen SelectedOrbitPen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x79, 0xC0, 0xFF)), 1.5);
    private static readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.FromUInt32(MapPalette.Selection)), 1.5);
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen OwnColonyPen = new Pen(new SolidColorBrush(Color.FromUInt32(MapPalette.OwnColony)), 2);
    private static readonly IPen OtherColonyPen = new Pen(new SolidColorBrush(Color.FromUInt32(MapPalette.OtherColony)), 2);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromUInt32(MapPalette.Label));
    private static readonly IBrush LabelMutedBrush = new SolidColorBrush(Color.FromUInt32(MapPalette.LabelMuted));
    private static readonly Typeface LabelFont = new(FontFamily.Default);
    private static readonly Typeface LabelFontBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private readonly Dictionary<uint, IBrush> _brushes = new();
    private readonly Dictionary<(string, bool), FormattedText> _labelCache = new();
    private readonly Point[] _stars = CreateStarfield(400);

    private Point? _pressPoint;
    private Point _lastPoint;
    private bool _dragging;
    private int _clickCount;

    public StarMapControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    private StarMapViewModel? Map => DataContext as StarMapViewModel;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Map?.SetViewport(e.NewSize.Width, e.NewSize.Height);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Bounds.Width > 0) Map?.SetViewport(Bounds.Width, Bounds.Height);
        InvalidateVisual();
    }

    // ---- rendering ----

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds);
        DrawStarfield(context, bounds);

        if (Map is not { } map) return;
        var camera = map.Camera;
        if (camera.ViewportWidth != bounds.Width || camera.ViewportHeight != bounds.Height)
            map.SetViewport(bounds.Width, bounds.Height);

        DrawOrbits(context, map, bounds);
        DrawObjects(context, map, bounds);
        DrawLabels(context, map, bounds);
    }

    private void DrawStarfield(DrawingContext context, Rect bounds)
    {
        foreach (var p in _stars)
            context.FillRectangle(StarfieldBrush, new Rect(p.X * bounds.Width, p.Y * bounds.Height, 1, 1));
    }

    private static void DrawOrbits(DrawingContext context, StarMapViewModel map, Rect bounds)
    {
        var camera = map.Camera;
        foreach (var obj in map.Objects)
        {
            if (!map.ShowsOrbit(obj) || obj.OrbitPath is not { } path) continue;
            var parent = obj.ParentId is { } pid ? map.Find(pid) : null;
            var origin = parent?.Position ?? default;

            // Skip orbits entirely outside the view (bounding circle test).
            var c = camera.WorldToScreen(origin);
            double r = obj.OrbitRadiusMetres * 2 / camera.MetresPerPixel;
            if (c.X + r < 0 || c.X - r > bounds.Width || c.Y + r < 0 || c.Y - r > bounds.Height) continue;

            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                // The cached 180-point outline is within ~0.3 px while the orbit is under ~2000 px
                // across; beyond that (zoomed in on a planet) refine it against the viewport.
                if (r / 2 < 2000 || obj.Orbit is not { } orbit)
                {
                    var first = camera.WorldToScreen(origin + path[0]);
                    g.BeginFigure(new Point(first.X, first.Y), false);
                    for (int i = 1; i < path.Length; i++)
                    {
                        var s = camera.WorldToScreen(origin + path[i]);
                        g.LineTo(new Point(s.X, s.Y));
                    }
                    g.EndFigure(false);
                }
                else
                {
                    foreach (var run in MapOrbits.ProjectOrbit(orbit, origin, camera))
                    {
                        g.BeginFigure(new Point(run[0].X, run[0].Y), false);
                        for (int i = 1; i < run.Count; i++) g.LineTo(new Point(run[i].X, run[i].Y));
                        g.EndFigure(false);
                    }
                }
            }

            var pen = obj.Id == map.SelectedId ? SelectedOrbitPen : obj.IsMinorBody ? MinorOrbitPen : OrbitPen;
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private void DrawObjects(DrawingContext context, StarMapViewModel map, Rect bounds)
    {
        var camera = map.Camera;
        // Minor bodies first so planets and ships draw on top of the asteroid belt.
        foreach (var obj in map.Objects.OrderBy(o => o.IsMinorBody ? 0 : o.IsShip ? 2 : 1))
        {
            if (!map.IsVisible(obj)) continue;
            var s = camera.WorldToScreen(obj.Position);
            double r = map.DrawRadius(obj);
            if (s.X + r < -20 || s.X - r > bounds.Width + 20 || s.Y + r < -20 || s.Y - r > bounds.Height + 20) continue;
            var center = new Point(s.X, s.Y);

            if (obj.IsShip)
                DrawShip(context, center, r, BrushFor(obj.Color));
            else if (obj.Kind == BodyKind.Star)
                DrawStar(context, center, r, obj.Color);
            else
                context.DrawEllipse(BrushFor(obj.Color), null, center, r, r);

            if (obj.HasColony)
                context.DrawEllipse(null, obj.HasOwnColony ? OwnColonyPen : OtherColonyPen, center, r + 3, r + 3);
            if (obj.Id == map.SelectedId)
                context.DrawEllipse(null, SelectionPen, center, r + 6, r + 6);
            else if (obj.Id == map.HoveredId)
                context.DrawEllipse(null, HoverPen, center, r + 5, r + 5);
        }
    }

    private void DrawStar(DrawingContext context, Point center, double r, uint argb)
    {
        var color = Color.FromUInt32(argb);
        double glow = r * 3.5;
        var halo = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x90, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1),
            },
        };
        context.DrawEllipse(halo, null, center, glow, glow);
        context.DrawEllipse(BrushFor(argb), null, center, r, r);
    }

    private static void DrawShip(DrawingContext context, Point c, double r, IBrush brush)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - r), true);
            g.LineTo(new Point(c.X + r, c.Y));
            g.LineTo(new Point(c.X, c.Y + r));
            g.LineTo(new Point(c.X - r, c.Y));
            g.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }

    private void DrawLabels(DrawingContext context, StarMapViewModel map, Rect bounds)
    {
        var camera = map.Camera;
        var placed = new List<Rect>();
        foreach (var obj in map.LabelCandidates())
        {
            var s = camera.WorldToScreen(obj.Position);
            double r = map.DrawRadius(obj);
            bool emphasised = obj.Id == map.SelectedId || obj.Kind == BodyKind.Star;
            var text = Label(obj.Name, emphasised);
            var rect = new Rect(s.X + r + 5, s.Y - text.Height / 2, text.Width, text.Height);
            if (!rect.Intersects(bounds)) continue;
            if (obj.Id != map.SelectedId && placed.Any(p => p.Intersects(rect.Inflate(2)))) continue;
            placed.Add(rect);
            context.DrawText(text, rect.TopLeft);
        }
    }

    private FormattedText Label(string name, bool emphasised)
    {
        if (_labelCache.TryGetValue((name, emphasised), out var cached)) return cached;
        if (_labelCache.Count > 2000) _labelCache.Clear();
        var text = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            emphasised ? LabelFontBold : LabelFont, emphasised ? 13 : 12, emphasised ? LabelBrush : LabelMutedBrush);
        _labelCache[(name, emphasised)] = text;
        return text;
    }

    private IBrush BrushFor(uint argb)
    {
        if (!_brushes.TryGetValue(argb, out var brush))
            _brushes[argb] = brush = new SolidColorBrush(Color.FromUInt32(argb));
        return brush;
    }

    private static Point[] CreateStarfield(int count)
    {
        var rng = new Random(4242); // fixed so the background doesn't shimmer between runs
        var stars = new Point[count];
        for (int i = 0; i < count; i++) stars[i] = new Point(rng.NextDouble(), rng.NextDouble());
        return stars;
    }

    // ---- input ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        _pressPoint = _lastPoint = e.GetPosition(this);
        _dragging = false;
        _clickCount = e.ClickCount;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_pressPoint is { } press && Map is { } map)
        {
            if (!_dragging && Distance(press, point) > DragThresholdPixels)
                _dragging = true;
            if (_dragging)
            {
                map.Pan(point.X - _lastPoint.X, point.Y - _lastPoint.Y);
                InvalidateVisual();
            }
        }
        else
        {
            Map?.Hover(new ScreenPoint(point.X, point.Y));
        }
        _lastPoint = point;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetPosition(this);
        if (_pressPoint != null && !_dragging && e.InitialPressMouseButton == MouseButton.Left)
        {
            if (_clickCount >= 2) Map?.PickAndFocus(new ScreenPoint(point.X, point.Y));
            else Map?.Pick(new ScreenPoint(point.X, point.Y));
        }
        _pressPoint = null;
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Map?.Hover(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        Map?.ZoomAt(new ScreenPoint(point.X, point.Y), Math.Pow(1.25, e.Delta.Y));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Map is not { } map) return;
        switch (e.Key)
        {
            case Key.Home: map.FitSystem(); break;
            case Key.Add or Key.OemPlus: map.ZoomInCommand.Execute(null); break;
            case Key.Subtract or Key.OemMinus: map.ZoomOutCommand.Execute(null); break;
            case Key.F when map.SelectedObject is { } selected: map.Focus(selected); break;
            default: return;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
