using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulsar4X.Api;
using Pulsar4X.UI.Core.ViewModels;

namespace Pulsar4X.UI.Core.Map;

/// <summary>
/// State and rules for the 2D system map, independent of any UI toolkit: which system is shown,
/// where everything is right now (propagated from orbital elements each frame), the camera,
/// selection and follow, what is visible or labelled at the current zoom, and hit-testing.
/// A renderer only has to draw <see cref="Objects"/> through <see cref="Camera"/>.
/// </summary>
public sealed partial class StarMapViewModel : ObservableObject
{
    /// <summary>A child whose orbit is smaller than this on screen is hidden inside its parent's dot.</summary>
    public const double MinOrbitPixelsToShow = 8;

    /// <summary>A body is labelled once its orbit is at least this big on screen.</summary>
    public const double MinOrbitPixelsToLabel = 36;

    private readonly Dictionary<int, MapObject> _byId = new();
    private readonly List<MapObject> _objects = new();
    private readonly Dictionary<int, (OrbitView Orbit, WorldPoint[]? Path)> _pathCache = new();
    private readonly Dictionary<int, OrbitView> _orbits = new();
    private readonly Dictionary<int, WorldPoint> _staticPositions = new();
    private int _structureSignature;
    private bool _hasFramed;

    public MapCamera Camera { get; } = new();

    /// <summary>Everything that may be drawn this frame, parents before children.</summary>
    public IReadOnlyList<MapObject> Objects => _objects;

    public string? SystemId { get; private set; }

    [ObservableProperty] private string _systemName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedObject))]
    [NotifyCanExecuteChangedFor(nameof(CentreOnSelectedCommand))]
    private int? _selectedId;

    /// <summary>While true the camera stays centred on the selected object as it moves.</summary>
    [ObservableProperty] private bool _isFollowing;

    [ObservableProperty] private int? _hoveredId;

    /// <summary>Draw asteroids and comets (hundreds in Sol; off by default).</summary>
    [ObservableProperty] private bool _showMinorBodies;

    [ObservableProperty] private string _scaleText = "";
    [ObservableProperty] private double _scalePixels;

    public MapObject? SelectedObject => SelectedId is { } id && _byId.TryGetValue(id, out var o) ? o : null;

    public MapObject? Find(int id) => _byId.GetValueOrDefault(id);

    /// <summary>Raised when the player picks something on the map (not when selection is set from elsewhere).</summary>
    public event Action<int?>? ObjectPicked;

    partial void OnShowMinorBodiesChanged(bool value) => _structureSignature = 0;

    // ---- per-frame update ----

    /// <summary>Rebuilds the object list when the system's contents change and moves everything to
    /// <paramref name="time"/>. Call once per frame.</summary>
    public void Update(IClientSystem? system, DateTime time)
    {
        if (system == null)
        {
            if (SystemId != null) Clear();
            return;
        }

        bool newSystem = system.SystemId != SystemId;
        if (newSystem)
        {
            Clear();
            SystemId = system.SystemId;
            SystemName = system.Name;
        }

        int signature = StructureSignature(system);
        if (signature != _structureSignature)
        {
            Rebuild(system);
            _structureSignature = signature;
        }

        UpdatePositions(time);

        if (newSystem || !_hasFramed)
        {
            FitSystem();
            _hasFramed = _objects.Count > 0;
        }

        if (IsFollowing && SelectedObject is { } followed)
            Camera.Center = followed.Position;

        UpdateScaleBar();
    }

    private void Clear()
    {
        _objects.Clear();
        _byId.Clear();
        _pathCache.Clear();
        _orbits.Clear();
        _staticPositions.Clear();
        _structureSignature = 0;
        _hasFramed = false;
        SystemId = null;
        SystemName = "";
        SelectedId = null;
        HoveredId = null;
        IsFollowing = false;
    }

    private int StructureSignature(IClientSystem system)
    {
        // Changes when entities appear/disappear, are renamed, change orbit, or colonies change hands.
        var hash = new HashCode();
        hash.Add(system.SystemId);
        hash.Add(ShowMinorBodies);
        foreach (var e in system.Entities.OrderBy(e => e.Id))
        {
            hash.Add(e.Id);
            hash.Add(e.Kind);
            hash.Add(e.Relation);
            hash.Add(e.GetView<NameView>()?.Name);
            hash.Add(MapOrbits.ResolveOrbit(e));
            hash.Add(e.GetView<ColonyView>()?.PlanetEntityId);
            if (e.GetView<OrbitView>() == null)
                hash.Add(e.GetView<PositionView>());
        }
        return hash.ToHashCode();
    }

    private void Rebuild(IClientSystem system)
    {
        var colonyOwners = new Dictionary<int, bool>(); // body id → owned by the player
        foreach (var e in system.Entities)
        {
            if (e.GetView<ColonyView>() is { PlanetEntityId: { } planetId })
                colonyOwners[planetId] = colonyOwners.GetValueOrDefault(planetId) || e.Relation == OwnerRelation.Owned;
        }

        var candidates = system.Entities
            .Where(e => (SystemBodies.IsBody(e) || e.Kind == BodyKind.Ship)
                        && (ShowMinorBodies || e.Kind is not (BodyKind.Asteroid or BodyKind.Comet)))
            .ToDictionary(e => e.Id);

        int? ParentOf(EntitySnapshot e)
        {
            int? parent = MapOrbits.ResolveOrbit(e)?.ParentId ?? e.GetView<PositionView>()?.ParentId;
            return parent is { } id && id != e.Id && candidates.ContainsKey(id) ? id : null;
        }

        _objects.Clear();
        _byId.Clear();
        _orbits.Clear();
        _staticPositions.Clear();

        // Parents before children, so positions can be resolved in a single pass.
        var visited = new HashSet<int>();
        var ordered = new List<EntitySnapshot>(candidates.Count);
        void Visit(EntitySnapshot e)
        {
            if (!visited.Add(e.Id)) return;
            if (ParentOf(e) is { } parentId) Visit(candidates[parentId]);
            ordered.Add(e);
        }
        foreach (var e in candidates.Values.OrderBy(e => e.Id)) Visit(e);

        foreach (var e in ordered)
        {
            var orbit = MapOrbits.ResolveOrbit(e);
            bool propagates = MapOrbits.CanPropagate(orbit);
            WorldPoint[]? path = null;
            if (propagates && e.Kind != BodyKind.Ship)
            {
                if (_pathCache.TryGetValue(e.Id, out var cached) && ReferenceEquals(cached.Orbit, orbit))
                    path = cached.Path;
                else
                    _pathCache[e.Id] = (orbit!, path = MapOrbits.SampleEllipse(orbit!));
            }

            var colony = colonyOwners.TryGetValue(e.Id, out bool own);
            var obj = new MapObject
            {
                Id = e.Id,
                Name = e.GetView<NameView>()?.Name ?? $"#{e.Id}",
                Kind = e.Kind,
                Relation = e.Relation,
                RadiusMetres = e.GetView<MassVolumeView>()?.RadiusMetres ?? 0,
                ParentId = ParentOf(e),
                OrbitPath = path,
                Orbit = path != null ? orbit : null,
                OrbitRadiusMetres = propagates ? Math.Abs(orbit!.SemiMajorAxisM) : 0,
                HasColony = colony,
                HasOwnColony = colony && own,
                Color = ColorFor(e),
            };
            _objects.Add(obj);
            _byId[obj.Id] = obj;

            if (propagates) _orbits[obj.Id] = orbit!;
            else
            {
                var p = e.GetView<PositionView>()?.AbsolutePosition ?? default;
                _staticPositions[obj.Id] = new WorldPoint(p.X, p.Y);
            }
        }

        if (SelectedId is { } sel && !_byId.ContainsKey(sel))
        {
            SelectedId = null;
            IsFollowing = false;
        }
        OnPropertyChanged(nameof(SelectedObject));
    }

    private static uint ColorFor(EntitySnapshot e) => e.Kind switch
    {
        BodyKind.Star => MapPalette.ForStar(e.GetView<StarView>()),
        BodyKind.Planet or BodyKind.DwarfPlanet => MapPalette.ForPlanet(e.GetView<BodyView>()),
        BodyKind.Moon => MapPalette.Moon,
        BodyKind.Comet => MapPalette.Comet,
        BodyKind.Asteroid => MapPalette.Asteroid,
        BodyKind.Ship => MapPalette.ForShip(e.Relation),
        _ => MapPalette.LabelMuted,
    };

    private void UpdatePositions(DateTime time)
    {
        foreach (var obj in _objects) // parents come first
        {
            if (_orbits.TryGetValue(obj.Id, out var orbit))
            {
                var relative = MapOrbits.RelativePosition(orbit, time);
                var parent = obj.ParentId is { } pid && _byId.TryGetValue(pid, out var p) ? p.Position : default;
                obj.Position = parent + relative;
            }
            else
            {
                obj.Position = _staticPositions.GetValueOrDefault(obj.Id);
            }
        }
    }

    // ---- visibility rules ----

    public double OrbitPixels(MapObject obj) => obj.OrbitRadiusMetres / Camera.MetresPerPixel;

    /// <summary>On-screen radius: true size when zoomed in close, never smaller than the minimum.</summary>
    public double DrawRadius(MapObject obj)
        => Math.Max(obj.RadiusMetres / Camera.MetresPerPixel, obj.MinimumPixelRadius);

    /// <summary>Whether the object is drawn at all at the current zoom.</summary>
    public bool IsVisible(MapObject obj)
    {
        if (obj.Id == SelectedId) return true;
        if (obj.ParentId is { } parentId && _byId.TryGetValue(parentId, out var parent) && parent.Kind != BodyKind.Star)
        {
            // Moons (and ships orbiting planets) vanish into their parent when zoomed out.
            return OrbitPixels(obj) >= MinOrbitPixelsToShow;
        }
        return true;
    }

    public bool ShowsOrbit(MapObject obj)
        => obj.OrbitPath != null && IsVisible(obj) && OrbitPixels(obj) >= MinOrbitPixelsToShow;

    /// <summary>Label candidates, most important first. The renderer drops any that would overlap one already placed.</summary>
    public IEnumerable<MapObject> LabelCandidates()
    {
        return _objects
            .Where(o => IsVisible(o) && WantsLabel(o))
            .OrderBy(LabelPriority)
            .ThenByDescending(o => o.OrbitRadiusMetres);
    }

    private bool WantsLabel(MapObject o)
    {
        if (o.Id == SelectedId || o.Id == HoveredId || o.Kind == BodyKind.Star) return true;
        if (o.IsShip || o.IsMinorBody) return false;
        return OrbitPixels(o) >= MinOrbitPixelsToLabel;
    }

    private int LabelPriority(MapObject o)
    {
        if (o.Id == SelectedId) return 0;
        if (o.Id == HoveredId) return 1;
        return o.Kind switch
        {
            BodyKind.Star => 2,
            BodyKind.Planet => 3,
            BodyKind.DwarfPlanet => 4,
            BodyKind.Moon => 5,
            _ => 6,
        };
    }

    // ---- interaction ----

    /// <summary>The visible object under <paramref name="point"/>, preferring the closest edge and
    /// major bodies over asteroids. Null when nothing is within a few pixels.</summary>
    public MapObject? HitTest(ScreenPoint point, double tolerancePixels = 6)
    {
        MapObject? best = null;
        double bestScore = double.MaxValue;
        foreach (var obj in _objects)
        {
            if (!IsVisible(obj)) continue;
            var s = Camera.WorldToScreen(obj.Position);
            double distance = Math.Sqrt((s.X - point.X) * (s.X - point.X) + (s.Y - point.Y) * (s.Y - point.Y));
            double radius = DrawRadius(obj);
            if (distance > radius + tolerancePixels) continue;

            double score = Math.Max(0, distance - radius) + (obj.IsMinorBody ? 4 : 0);
            if (score < bestScore || (score == bestScore && obj.OrbitRadiusMetres < (best?.OrbitRadiusMetres ?? 0)))
            {
                best = obj;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>Selects what is under <paramref name="point"/> (or clears the selection on empty space).</summary>
    public void Pick(ScreenPoint point)
    {
        var hit = HitTest(point);
        if (hit?.Id != SelectedId) IsFollowing = false;
        SelectedId = hit?.Id;
        ObjectPicked?.Invoke(SelectedId);
    }

    /// <summary>Selects and starts following what is under <paramref name="point"/>, zooming in to its orbit.</summary>
    public void PickAndFocus(ScreenPoint point)
    {
        Pick(point);
        if (SelectedObject is { } obj) Focus(obj);
    }

    public void Hover(ScreenPoint? point) => HoveredId = point is { } p ? HitTest(p)?.Id : null;

    public void Pan(double dx, double dy)
    {
        IsFollowing = false;
        Camera.PanByPixels(dx, dy);
        UpdateScaleBar();
    }

    public void ZoomAt(ScreenPoint anchor, double factor)
    {
        // While following, zoom around the followed object so it stays centred.
        Camera.ZoomAt(IsFollowing ? new ScreenPoint(Camera.ViewportWidth / 2, Camera.ViewportHeight / 2) : anchor, factor);
        UpdateScaleBar();
    }

    public void SetViewport(double width, double height)
    {
        bool first = Camera.ViewportWidth == 800 && Camera.ViewportHeight == 600;
        Camera.SetViewport(width, height);
        if (first && _objects.Count > 0) FitSystem();
        UpdateScaleBar();
    }

    [RelayCommand]
    private void ZoomIn() => ZoomAt(new ScreenPoint(Camera.ViewportWidth / 2, Camera.ViewportHeight / 2), 2);

    [RelayCommand]
    private void ZoomOut() => ZoomAt(new ScreenPoint(Camera.ViewportWidth / 2, Camera.ViewportHeight / 2), 0.5);

    /// <summary>Frames the planets around the primary star (or every body when there are none).</summary>
    [RelayCommand]
    public void FitSystem()
    {
        IsFollowing = false;
        var star = _objects.FirstOrDefault(o => o.Kind == BodyKind.Star);
        var center = star?.Position ?? default;
        var framed = _objects.Where(o => o.Kind == BodyKind.Planet).ToList();
        if (framed.Count == 0) framed = _objects.Where(o => !o.IsShip).ToList();

        double radius = framed.Count == 0
            ? MapCamera.MetresPerAu
            : framed.Max(o => Distance(o.Position, center) + o.OrbitRadiusMetres * 0.1);
        Camera.Frame(center, Math.Max(radius, MapCamera.MetresPerAu * 0.05));
        UpdateScaleBar();
    }

    private bool CanCentreOnSelected() => SelectedId != null;

    [RelayCommand(CanExecute = nameof(CanCentreOnSelected))]
    private void CentreOnSelected()
    {
        if (SelectedObject is { } obj) Focus(obj);
    }

    /// <summary>Selects an object chosen elsewhere (e.g. the body table) without moving the camera.</summary>
    public void Select(int? id)
    {
        if (id == SelectedId) return;
        IsFollowing = false;
        SelectedId = id is { } i && _byId.ContainsKey(i) ? i : null;
    }

    /// <summary>Centres on and follows <paramref name="obj"/>, zooming so its moons (or its own orbit) are in view.</summary>
    public void Focus(MapObject obj)
    {
        SelectedId = obj.Id;
        double childOrbits = _objects.Where(o => o.ParentId == obj.Id && !o.IsShip && !o.IsMinorBody)
            .Select(o => o.OrbitRadiusMetres).DefaultIfEmpty(0).Max();
        double radius = childOrbits > 0 ? childOrbits * 1.15
            : obj.Kind == BodyKind.Star ? MapCamera.MetresPerAu
            : Math.Max(obj.RadiusMetres * 30, 5_000_000);
        Camera.Frame(obj.Position, radius);
        IsFollowing = true;
        UpdateScaleBar();
    }

    private static double Distance(WorldPoint a, WorldPoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---- scale bar ----

    private void UpdateScaleBar()
    {
        // Pick a round length (1, 2 or 5 × 10ⁿ in km or AU) that is about 100 px long.
        double target = 100 * Camera.MetresPerPixel;
        bool useAu = target >= MapCamera.MetresPerAu * 0.1;
        double unit = useAu ? MapCamera.MetresPerAu : 1000;
        double value = target / unit;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double nice = new[] { 1, 2, 5, 10 }.Select(m => m * magnitude).Last(v => v <= value * 1.0001);
        ScalePixels = nice * unit / Camera.MetresPerPixel;
        ScaleText = useAu
            ? nice.ToString(nice < 1 ? "0.##" : "0", CultureInfo.InvariantCulture) + " AU"
            : nice.ToString("N0", CultureInfo.InvariantCulture) + " km";
    }
}
