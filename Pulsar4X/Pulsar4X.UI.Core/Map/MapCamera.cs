using System.Globalization;

namespace Pulsar4X.UI.Core.Map;

/// <summary>A point in screen space: pixels from the top-left of the map view, y down.</summary>
public readonly record struct ScreenPoint(double X, double Y);

/// <summary>A point on the system plane in metres from the system origin, y up (ecliptic x/y).</summary>
public readonly record struct WorldPoint(double X, double Y)
{
    public static WorldPoint operator +(WorldPoint a, WorldPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static WorldPoint operator -(WorldPoint a, WorldPoint b) => new(a.X - b.X, a.Y - b.Y);
}

/// <summary>
/// Top-down 2D camera over a star system. Distances span from moon orbits (10⁵ m) to the outer
/// system (10¹³ m), so everything is double precision and zoom is a scale in metres per pixel.
/// </summary>
public sealed class MapCamera
{
    public const double MetresPerAu = 149_597_870_700.0;

    /// <summary>Closest zoom: 100 m per pixel (a ship-sized view).</summary>
    public const double MinMetresPerPixel = 100;

    /// <summary>Furthest zoom: 50 AU per pixel, far beyond any system.</summary>
    public const double MaxMetresPerPixel = 50 * MetresPerAu;

    private double _metresPerPixel = MetresPerAu / 100; // 1 AU = 100 px

    /// <summary>The world point at the centre of the view.</summary>
    public WorldPoint Center { get; set; }

    public double MetresPerPixel
    {
        get => _metresPerPixel;
        set => _metresPerPixel = Math.Clamp(value, MinMetresPerPixel, MaxMetresPerPixel);
    }

    public double ViewportWidth { get; private set; } = 800;
    public double ViewportHeight { get; private set; } = 600;

    public void SetViewport(double width, double height)
    {
        ViewportWidth = Math.Max(1, width);
        ViewportHeight = Math.Max(1, height);
    }

    public ScreenPoint WorldToScreen(WorldPoint world) => new(
        ViewportWidth / 2 + (world.X - Center.X) / MetresPerPixel,
        ViewportHeight / 2 - (world.Y - Center.Y) / MetresPerPixel);

    public WorldPoint ScreenToWorld(ScreenPoint screen) => new(
        Center.X + (screen.X - ViewportWidth / 2) * MetresPerPixel,
        Center.Y - (screen.Y - ViewportHeight / 2) * MetresPerPixel);

    /// <summary>Moves the view by a screen-space drag (dragging right moves the content right).</summary>
    public void PanByPixels(double dx, double dy)
        => Center = new WorldPoint(Center.X - dx * MetresPerPixel, Center.Y + dy * MetresPerPixel);

    /// <summary>Zooms by <paramref name="factor"/> (&gt;1 zooms in) keeping the world point under
    /// <paramref name="anchor"/> fixed on screen, like map apps do with the mouse wheel.</summary>
    public void ZoomAt(ScreenPoint anchor, double factor)
    {
        if (factor <= 0) return;
        var before = ScreenToWorld(anchor);
        MetresPerPixel /= factor;
        var after = ScreenToWorld(anchor);
        Center = new WorldPoint(Center.X + before.X - after.X, Center.Y + before.Y - after.Y);
    }

    /// <summary>Centres on <paramref name="center"/> and zooms so a circle of
    /// <paramref name="radiusMetres"/> fits the smaller viewport side with a margin.</summary>
    public void Frame(WorldPoint center, double radiusMetres, double marginPixels = 40)
    {
        Center = center;
        double usable = Math.Max(1, Math.Min(ViewportWidth, ViewportHeight) / 2 - marginPixels);
        MetresPerPixel = Math.Max(radiusMetres, 1) / usable;
    }

    /// <summary>A human-readable scale for a bar of <paramref name="pixels"/> length, e.g. "1.5 AU".</summary>
    public string DescribeLength(double pixels) => DescribeMetres(pixels * MetresPerPixel);

    public static string DescribeMetres(double metres) => metres switch
    {
        >= MetresPerAu * 0.1 => string.Create(CultureInfo.InvariantCulture, $"{metres / MetresPerAu:0.##} AU"),
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{metres / 1000:N0} km"),
        >= 1000 => string.Create(CultureInfo.InvariantCulture, $"{metres / 1000:0.#} km"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{metres:0} m"),
    };
}
