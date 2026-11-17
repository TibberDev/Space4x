using NUnit.Framework;
using Pulsar4X.UI.Core.Map;

namespace Pulsar4X.UI.Tests;

/// <summary>Pure camera math: projection, panning, zooming and scale text.</summary>
public class MapCameraTests
{
    private static MapCamera Camera()
    {
        var camera = new MapCamera { Center = new WorldPoint(0, 0), MetresPerPixel = 1000 };
        camera.SetViewport(800, 600);
        return camera;
    }

    [Test]
    public void World_and_screen_round_trip_with_y_up()
    {
        var camera = Camera();
        Assert.That(camera.WorldToScreen(new WorldPoint(0, 0)), Is.EqualTo(new ScreenPoint(400, 300)));

        // +y in the world is up on screen.
        var up = camera.WorldToScreen(new WorldPoint(0, 100_000));
        Assert.That(up.Y, Is.EqualTo(200).Within(1e-9));

        var world = camera.ScreenToWorld(new ScreenPoint(123, 456));
        var back = camera.WorldToScreen(world);
        Assert.That(back.X, Is.EqualTo(123).Within(1e-9));
        Assert.That(back.Y, Is.EqualTo(456).Within(1e-9));
    }

    [Test]
    public void Dragging_moves_content_with_the_pointer()
    {
        var camera = Camera();
        var world = new WorldPoint(50_000, -20_000);
        var before = camera.WorldToScreen(world);

        camera.PanByPixels(30, -10);

        var after = camera.WorldToScreen(world);
        Assert.That(after.X - before.X, Is.EqualTo(30).Within(1e-9));
        Assert.That(after.Y - before.Y, Is.EqualTo(-10).Within(1e-9));
    }

    [Test]
    public void Zooming_keeps_the_point_under_the_cursor_fixed()
    {
        var camera = Camera();
        var anchor = new ScreenPoint(650, 120);
        var worldUnderCursor = camera.ScreenToWorld(anchor);

        camera.ZoomAt(anchor, 3);

        Assert.That(camera.MetresPerPixel, Is.EqualTo(1000.0 / 3).Within(1e-9));
        var after = camera.WorldToScreen(worldUnderCursor);
        Assert.That(after.X, Is.EqualTo(anchor.X).Within(1e-6));
        Assert.That(after.Y, Is.EqualTo(anchor.Y).Within(1e-6));
    }

    [Test]
    public void Zoom_is_clamped()
    {
        var camera = Camera();
        camera.ZoomAt(new ScreenPoint(0, 0), 1e12);
        Assert.That(camera.MetresPerPixel, Is.EqualTo(MapCamera.MinMetresPerPixel));
        camera.ZoomAt(new ScreenPoint(0, 0), 1e-12);
        Assert.That(camera.MetresPerPixel, Is.EqualTo(MapCamera.MaxMetresPerPixel));
    }

    [Test]
    public void Frame_fits_a_radius_in_the_smaller_side()
    {
        var camera = Camera();
        camera.Frame(new WorldPoint(5, 5), 2_600_000, marginPixels: 40);

        Assert.That(camera.Center, Is.EqualTo(new WorldPoint(5, 5)));
        // Smaller side 600 px → 300 px half-height minus 40 px margin = 260 px for the radius.
        Assert.That(camera.MetresPerPixel, Is.EqualTo(10_000).Within(1e-9));
    }

    [TestCase(150, "150 m")]
    [TestCase(1500, "1.5 km")]
    [TestCase(384_399_000, "384,399 km")]
    [TestCase(MapCamera.MetresPerAu * 5.2, "5.2 AU")]
    public void Describes_lengths_in_sensible_units(double metres, string expected)
        => Assert.That(MapCamera.DescribeMetres(metres), Is.EqualTo(expected));
}
