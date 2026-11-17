using Pulsar4X.Api;
using Pulsar4X.Orbital;

namespace Pulsar4X.UI.Core.Map;

/// <summary>
/// Client-side orbit propagation over <see cref="OrbitView"/> elements, via the engine-free
/// Pulsar4X.Orbital library. Mirrors the ImGui client's SnapshotOrbits: bodies on Keplerian orbits
/// move smoothly between server pushes; everything else uses its last pushed position.
/// </summary>
public static class MapOrbits
{
    /// <summary>The orbit to propagate: a body's orbit, or a ship's current newtonian trajectory.</summary>
    public static OrbitView? ResolveOrbit(EntitySnapshot entity)
        => entity.GetView<OrbitView>()
           ?? entity.GetView<NewtonMoveView>()?.Trajectory
           ?? entity.GetView<NewtonSimpleMoveView>()?.CurrentTrajectory;

    public static bool CanPropagate(OrbitView? orbit)
        => orbit is { StandardGravParameter: > 0, SemiMajorAxisM: not 0 }
           && !double.IsNaN(orbit.Eccentricity);

    public static KeplerElements ToKeplerElements(OrbitView orbit)
    {
        double a = orbit.SemiMajorAxisM;
        double e = orbit.Eccentricity;
        return new KeplerElements
        {
            StandardGravParameter = orbit.StandardGravParameter,
            SemiMajorAxis = a,
            SemiMinorAxis = EllipseMath.SemiMinorAxis(a, e),
            Eccentricity = e,
            LinearEccentricity = EllipseMath.LinearEccentricityFromEccentricity(a, e),
            Periapsis = EllipseMath.Periapsis(e, a),
            Apoapsis = EllipseMath.Apoapsis(e, a),
            LoAN = orbit.LongitudeOfAscendingNodeRad,
            AoP = orbit.ArgumentOfPeriapsisRad,
            Inclination = orbit.InclinationRad,
            MeanMotion = orbit.MeanMotionRadPerSec,
            Period = orbit.OrbitalPeriodSeconds,
            MeanAnomalyAtEpoch = orbit.MeanAnomalyAtEpochRad,
            Epoch = orbit.Epoch,
        };
    }

    /// <summary>Position relative to the orbit parent at <paramref name="time"/>, projected onto the system plane.</summary>
    public static WorldPoint RelativePosition(OrbitView orbit, DateTime time)
    {
        var p = Orbital.OrbitalMath.GetPosition(ToKeplerElements(orbit), time);
        return new WorldPoint(p.X, p.Y);
    }

    /// <summary>Position relative to the parent at a given true anomaly, on the system plane.</summary>
    public static WorldPoint PositionAtTrueAnomaly(OrbitView orbit, double trueAnomaly)
    {
        var p = Orbital.OrbitalMath.GetPosition(orbit.SemiMajorAxisM, orbit.Eccentricity,
            orbit.LongitudeOfAscendingNodeRad, orbit.ArgumentOfPeriapsisRad, orbit.InclinationRad, trueAnomaly);
        return new WorldPoint(p.X, p.Y);
    }

    /// <summary>
    /// Projects a closed orbit to screen polylines accurate to about half a pixel at any zoom.
    /// A fixed set of samples is fine when the whole orbit is on screen, but zoomed in on a planet
    /// its 1 AU orbit is millions of pixels round and straight segments miss the planet by tens of
    /// pixels. So each sample segment is subdivided (by true anomaly) until it is straight on
    /// screen, and segments that cannot reach the viewport are skipped. Each returned list is one
    /// continuous run of the orbit.
    /// </summary>
    public static List<List<ScreenPoint>> ProjectOrbit(OrbitView orbit, WorldPoint parent, MapCamera camera,
        int baseSegments = 180, double tolerancePixels = 0.5)
    {
        var runs = new List<List<ScreenPoint>>();
        if (!(orbit.SemiMajorAxisM > 0) || !(orbit.Eccentricity >= 0) || orbit.Eccentricity >= 1) return runs;

        double w = camera.ViewportWidth, h = camera.ViewportHeight;
        List<ScreenPoint>? run = null;

        ScreenPoint Project(double nu) => camera.WorldToScreen(parent + PositionAtTrueAnomaly(orbit, nu));

        bool MayBeVisible(ScreenPoint a, ScreenPoint b)
        {
            // The arc between two samples stays within one chord length of them (it subtends < 2°).
            double pad = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) + 2;
            return Math.Max(a.X, b.X) + pad >= 0 && Math.Min(a.X, b.X) - pad <= w
                && Math.Max(a.Y, b.Y) + pad >= 0 && Math.Min(a.Y, b.Y) - pad <= h;
        }

        void Emit(ScreenPoint a, ScreenPoint b)
        {
            if (run == null)
            {
                run = new List<ScreenPoint> { a };
                runs.Add(run);
            }
            run.Add(b);
        }

        void Segment(double nu0, ScreenPoint p0, double nu1, ScreenPoint p1, int depth)
        {
            if (!MayBeVisible(p0, p1))
            {
                run = null; // pen up
                return;
            }
            double numid = (nu0 + nu1) / 2;
            var pm = Project(numid);
            double dx = pm.X - (p0.X + p1.X) / 2, dy = pm.Y - (p0.Y + p1.Y) / 2;
            if (depth < 24 && dx * dx + dy * dy > tolerancePixels * tolerancePixels)
            {
                Segment(nu0, p0, numid, pm, depth + 1);
                Segment(numid, pm, nu1, p1, depth + 1);
            }
            else
            {
                Emit(p0, p1);
            }
        }

        double step = 2 * Math.PI / baseSegments;
        var previous = Project(0);
        for (int i = 1; i <= baseSegments; i++)
        {
            double nu = i * step;
            var current = Project(nu);
            Segment(nu - step, previous, nu, current, 0);
            previous = current;
        }
        return runs;
    }

    /// <summary>
    /// The closed orbit outline relative to its parent, sampled by true anomaly (denser near
    /// periapsis, where the body moves fastest and the curve bends most). Null for open
    /// (hyperbolic) or degenerate orbits, which the map does not outline.
    /// </summary>
    public static WorldPoint[]? SampleEllipse(OrbitView orbit, int segments = 180)
    {
        double a = orbit.SemiMajorAxisM;
        double e = orbit.Eccentricity;
        if (!(a > 0) || !(e >= 0) || e >= 1) return null;

        var points = new WorldPoint[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            double trueAnomaly = 2 * Math.PI * i / segments;
            var p = Orbital.OrbitalMath.GetPosition(a, e, orbit.LongitudeOfAscendingNodeRad,
                orbit.ArgumentOfPeriapsisRad, orbit.InclinationRad, trueAnomaly);
            points[i] = new WorldPoint(p.X, p.Y);
        }
        return points;
    }
}
