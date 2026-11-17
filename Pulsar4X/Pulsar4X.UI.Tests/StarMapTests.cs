using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Engine;
using Pulsar4X.UI.Core.Map;
using Pulsar4X.UI.Core.ViewModels;

namespace Pulsar4X.UI.Tests;

/// <summary>The star map model against the real quickstarted Sol.</summary>
[TestFixture, NonParallelizable]
public class StarMapTests
{
    private MainWindowViewModel _vm = null!;
    private StarMapViewModel Map => _vm.Map;

    [SetUp]
    public void SetUp()
    {
        _vm = new MainWindowViewModel(new SharedGame.Launcher());
        _vm.AttachClient(SharedGame.Client);
        Map.SetViewport(1200, 800);
        for (int i = 0; i < 100 && Map.Objects.Count == 0; i++)
        {
            _vm.Tick();
            Thread.Sleep(20);
        }
        Assert.That(Map.Objects, Is.Not.Empty, "the map never received the system");
    }

    private MapObject Named(string name)
        => Map.Objects.FirstOrDefault(o => o.Name == name) ?? throw new AssertionException($"{name} is not on the map");

    [Test]
    public void Map_shows_the_selected_system_framed_on_the_planets()
    {
        Assert.That(Map.SystemName, Is.EqualTo(_vm.SelectedSystem!.Name));
        Assert.That(Map.Objects.Count(o => o.Kind == BodyKind.Star), Is.GreaterThanOrEqualTo(1));

        // Every planet fits on screen after the initial fit.
        foreach (var planet in Map.Objects.Where(o => o.Kind == BodyKind.Planet))
        {
            var s = Map.Camera.WorldToScreen(planet.Position);
            Assert.That(s.X, Is.InRange(0, 1200), planet.Name);
            Assert.That(s.Y, Is.InRange(0, 800), planet.Name);
        }
    }

    [Test]
    public void Parents_come_before_children_and_planets_have_orbit_paths()
    {
        var seen = new HashSet<int>();
        foreach (var obj in Map.Objects)
        {
            if (obj.ParentId is { } parent)
                Assert.That(seen, Does.Contain(parent), $"{obj.Name} is listed before its parent");
            seen.Add(obj.Id);
        }

        var earth = Named("Earth");
        Assert.That(earth.OrbitPath, Is.Not.Null.And.Length.GreaterThan(100));
        Assert.That(earth.HasOwnColony, Is.True, "Earth should carry the player's colony marker");
        Assert.That(Map.Objects.Any(o => o.IsMinorBody), Is.False, "asteroids are hidden by default");
    }

    [Test]
    public void Propagated_positions_match_the_engine()
    {
        var game = SharedGame.Host.Game!;
        var engineSystem = game.Systems.First(s => s.ID == Map.SystemId);

        foreach (var name in new[] { "Earth", "Mars", "Jupiter", "Luna" })
        {
            var obj = Named(name);
            Assert.That(engineSystem.TryGetEntityById(obj.Id, out var entity), Is.True, name);
            var engine = entity.GetDataBlob<Pulsar4X.Movement.PositionDB>().AbsolutePosition;

            double error = Math.Sqrt(Math.Pow(engine.X - obj.Position.X, 2) + Math.Pow(engine.Y - obj.Position.Y, 2));
            // Within 0.1% of the orbit size: same instant, same elements, same maths.
            Assert.That(error, Is.LessThan(Math.Max(obj.OrbitRadiusMetres * 1e-3, 1000)),
                $"{name} is {error:N0} m away from where the engine has it");
        }
    }

    [Test]
    public void Moons_hide_when_zoomed_out_and_appear_when_zoomed_in()
    {
        var luna = Named("Luna");
        Map.FitSystem();
        Assert.That(Map.IsVisible(luna), Is.False, "Luna should be inside Earth's dot at system scale");

        Map.Focus(Named("Earth"));
        Assert.That(Map.IsVisible(luna), Is.True, "Luna should be visible when focused on Earth");
        Assert.That(Map.ShowsOrbit(luna), Is.True);
        Assert.That(Map.LabelCandidates().Select(o => o.Name), Does.Contain("Luna"));
    }

    [Test]
    public void Orbit_line_passes_through_the_planet_at_any_zoom()
    {
        var earth = Named("Earth");
        var sun = Map.Find(earth.ParentId!.Value)!;

        // From system scale down to 1 km per pixel the drawn orbit must stay on the planet.
        foreach (double metresPerPixel in new[] { 1e9, 1e7, 1e6, 1e4, 1e3 })
        {
            Map.Camera.Center = earth.Position;
            Map.Camera.MetresPerPixel = metresPerPixel;
            var runs = MapOrbits.ProjectOrbit(earth.Orbit!, sun.Position, Map.Camera);
            Assert.That(runs, Is.Not.Empty, $"no orbit drawn at {metresPerPixel} m/px");

            var planet = Map.Camera.WorldToScreen(earth.Position);
            double nearest = runs.SelectMany(r => r.Zip(r.Skip(1)))
                .Min(seg => DistanceToSegment(planet, seg.First, seg.Second));
            Assert.That(nearest, Is.LessThan(1.0), $"orbit misses Earth by {nearest:0.0} px at {metresPerPixel} m/px");
        }
    }

    private static double DistanceToSegment(ScreenPoint p, ScreenPoint a, ScreenPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double t = dx == 0 && dy == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    [TestCase("Terrestrial Planet", "Terrestrial planet")]
    [TestCase("GasGiant", "Gas giant")]
    [TestCase("IceGiant", "Ice giant")]
    [TestCase("Asteroid", "Asteroid")]
    public void Body_types_read_as_words(string raw, string expected)
        => Assert.That(SelectionInfo.SplitWords(raw), Is.EqualTo(expected));

    [Test]
    public void Clicking_a_planet_selects_it_and_syncs_table_and_panel()
    {
        var mars = Named("Mars");
        var screen = Map.Camera.WorldToScreen(mars.Position);

        Map.Pick(new ScreenPoint(screen.X + 2, screen.Y - 1));

        Assert.That(Map.SelectedId, Is.EqualTo(mars.Id));
        Assert.That(_vm.SelectedBodyRow?.Name, Is.EqualTo("Mars"));
        Assert.That(_vm.Selection?.Title, Is.EqualTo("Mars"));
        Assert.That(_vm.Selection!.Rows.Select(r => r.Label), Does.Contain("Orbital period").And.Contain("Gravity"));
        Assert.That(_vm.Selection.Subtitle, Does.Not.Contain("  ").And.Not.StartWith("Planet ·"));

        // Clicking empty space clears it.
        Map.Pick(new ScreenPoint(3, 3));
        Assert.That(Map.SelectedId, Is.Null);
        Assert.That(_vm.HasSelection, Is.False);
        Assert.That(_vm.SelectedBodyRow, Is.Null);
    }

    [Test]
    public void Selecting_a_table_row_selects_it_on_the_map()
    {
        var jupiter = _vm.Bodies.First(b => b.Name == "Jupiter");
        _vm.SelectedBodyRow = jupiter;

        Assert.That(Map.SelectedId, Is.EqualTo(jupiter.Id));
        Assert.That(_vm.Selection?.Title, Is.EqualTo("Jupiter"));
    }

    [Test]
    public void Following_keeps_the_body_centred_as_time_passes()
    {
        var earth = Named("Earth");
        Map.Focus(earth);
        Assert.That(Map.IsFollowing, Is.True);
        var before = earth.Position;

        var later = SharedGame.Client.Galaxy.Time.GameDateTime + TimeSpan.FromDays(20);
        Map.Update(SharedGame.Client.Galaxy.GetSystem(Map.SystemId!), later);

        Assert.That(Named("Earth").Position, Is.Not.EqualTo(before), "Earth should have moved in 20 days");
        Assert.That(Map.Camera.Center, Is.EqualTo(Named("Earth").Position));

        Map.Pan(10, 0);
        Assert.That(Map.IsFollowing, Is.False, "dragging the map stops following");
    }

    [Test]
    public void Minor_bodies_toggle_adds_asteroids_to_the_map()
    {
        _vm.ShowMinorBodies = true;
        _vm.Tick();
        Assert.That(Map.Objects.Count(o => o.IsMinorBody), Is.GreaterThan(100));

        _vm.ShowMinorBodies = false;
        _vm.Tick();
        Assert.That(Map.Objects.Any(o => o.IsMinorBody), Is.False);
    }

    [Test]
    public void Scale_bar_is_a_round_number_about_100_pixels_long()
    {
        Map.FitSystem();
        Assert.That(Map.ScalePixels, Is.InRange(50, 101));
        Assert.That(Map.ScaleText, Does.Match(@"^(1|2|5|10|20|50|100|0\.1|0\.2|0\.5)[0-9,]* (AU|km)$"));
    }
}
