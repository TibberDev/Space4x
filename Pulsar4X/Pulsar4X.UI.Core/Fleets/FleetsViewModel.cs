using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulsar4X.Api;
using Pulsar4X.UI.Core.Colonies;

namespace Pulsar4X.UI.Core.Fleets;

/// <summary>One fleet in the list, sub-fleets flattened under their parent.</summary>
public sealed record FleetRow(int Id, string Name, string Location, int ShipCount, string GoalText, int Depth)
{
    public string ShipsText => ShipCount == 1 ? "1 ship" : $"{ShipCount} ships";
    /// <summary>Sub-fleets read as nested under their parent.</summary>
    public string DisplayName => Depth == 0 ? Name : new string(' ', Depth * 3) + "↳ " + Name;
}

/// <summary>One ship of the selected fleet.</summary>
public sealed record ShipRow(int Id, string Name, string Design, string Captain, string GoalText, bool HasCaptain);

/// <summary>A body or colony to pick in an order.</summary>
public sealed record TargetOption(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>An officer who can captain a ship.</summary>
public sealed record OfficerOption(int Id, string Name, string Detail)
{
    public string Text => string.IsNullOrEmpty(Detail) ? Name : $"{Name} ({Detail})";
}

/// <summary>
/// The Fleets screen: your fleets and their ships, who captains them, what they are doing, and the orders
/// the slice needs: move to a body, survey it, found a colony there, keep the colonies supplied, or cancel.
/// </summary>
public sealed partial class FleetsViewModel : ObservableObject
{
    private IClientGalaxy? _galaxy;
    private bool _updating;

    public FleetsViewModel(Func<GameCommand, Task<CommandResult>> submit) => Sink = new CommandSink(submit);

    public CommandSink Sink { get; }

    public ObservableCollection<FleetRow> Fleets { get; } = new();
    public ObservableCollection<ShipRow> Ships { get; } = new();
    public ObservableCollection<TargetOption> Bodies { get; } = new();
    public ObservableCollection<TargetOption> Colonies { get; } = new();
    public ObservableCollection<OfficerOption> Officers { get; } = new();
    public ObservableCollection<string> Orders { get; } = new();

    /// <summary>Ships in no fleet, such as one just launched. Form a fleet to give them orders.</summary>
    public ObservableCollection<ShipRow> LooseShips { get; } = new();

    [ObservableProperty] private bool _hasLooseShips;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFleet))]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand), nameof(SurveyCommand), nameof(FoundColonyCommand), nameof(CancelCommand))]
    private FleetRow? _selectedFleet;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AssignCaptainCommand), nameof(RelieveCaptainCommand), nameof(SupplyCommand))]
    private ShipRow? _selectedShip;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand), nameof(SurveyCommand), nameof(FoundColonyCommand))]
    private TargetOption? _selectedBody;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FoundColonyCommand))]
    private TargetOption? _selectedSource;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AssignCaptainCommand))]
    private OfficerOption? _selectedOfficer;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _commander = "";
    [ObservableProperty] private string _goalText = "";
    [ObservableProperty] private bool _canGeoSurvey;
    [ObservableProperty] private bool _hasOrders;

    public bool HasFleet => SelectedFleet != null;
    public bool HasNoFleets => Fleets.Count == 0;

    /// <summary>Selects the fleet that holds this ship, and the ship itself (from the map).</summary>
    public bool ShowShip(int shipId, IClientGalaxy galaxy)
    {
        Update(galaxy);
        var fleet = Flatten(galaxy.Fleets, 0).FirstOrDefault(f => f.fleet.Ships.Any(s => s.Id == shipId)).fleet;
        if (fleet == null)
            return false;
        SelectedFleet = Fleets.FirstOrDefault(f => f.Id == fleet.Id);
        Update(galaxy);
        SelectedShip = Ships.FirstOrDefault(s => s.Id == shipId) ?? SelectedShip;
        return true;
    }

    public void Update(IClientGalaxy galaxy)
    {
        _galaxy = galaxy;
        _updating = true;
        try
        {
            var loose = galaxy.UnattachedShips
                .Select(s => new ShipRow(s.Id, s.Name, s.DesignName, s.CommanderName ?? "No captain", GoalLine(s.Goal), s.CommanderId != null))
                .OrderBy(s => s.Name, StringComparer.CurrentCulture)
                .ToList();
            CollectionSync.Sync(LooseShips, loose, s => s.Id);
            HasLooseShips = LooseShips.Count > 0;

            var all = Flatten(galaxy.Fleets, 0).ToList();
            var rows = all.Select(f => new FleetRow(f.fleet.Id, f.fleet.Name,
                    f.fleet.OrbitingName ?? f.fleet.SystemName ?? "In transit",
                    f.fleet.Ships.Count, GoalLine(f.fleet.Goal), f.depth))
                .ToList();
            CollectionSync.Sync(Fleets, rows, r => r.Id);
            OnPropertyChanged(nameof(HasNoFleets));
            if (SelectedFleet == null || Fleets.All(f => f.Id != SelectedFleet.Id))
                SelectedFleet = Fleets.FirstOrDefault(f => f.ShipCount > 0) ?? Fleets.FirstOrDefault();
            else
                SelectedFleet = Fleets.First(f => f.Id == SelectedFleet.Id);

            var fleet = all.FirstOrDefault(f => f.fleet.Id == SelectedFleet?.Id).fleet;
            if (fleet == null)
            {
                Ships.Clear();
                Orders.Clear();
                Name = Location = Commander = GoalText = "";
                return;
            }

            Name = fleet.Name;
            Location = string.Join(" · ", new[] { fleet.OrbitingName, fleet.SystemName }.Where(s => !string.IsNullOrEmpty(s)));
            Commander = fleet.CommanderName ?? "No fleet commander";
            GoalText = GoalLine(fleet.Goal);
            CanGeoSurvey = fleet.CanGeoSurvey;
            CollectionSync.Sync(Orders, fleet.Orders.Select(o => o.IsRunning ? "▶ " + o.Name : o.Name).ToList(), o => o);
            HasOrders = Orders.Count > 0;

            var ships = fleet.Ships.Select(s => new ShipRow(s.Id, s.Name, s.DesignName, s.CommanderName ?? "No captain",
                    GoalLine(s.Goal), s.CommanderId != null))
                .ToList();
            CollectionSync.Sync(Ships, ships, s => s.Id);
            if (SelectedShip == null || Ships.All(s => s.Id != SelectedShip.Id))
                SelectedShip = Ships.FirstOrDefault(s => s.Id == fleet.FlagshipId) ?? Ships.FirstOrDefault();
            else
                SelectedShip = Ships.First(s => s.Id == SelectedShip.Id);

            var system = fleet.SystemId != null ? galaxy.GetSystem(fleet.SystemId) : null;
            var bodies = system?.Entities
                .Where(e => e.Kind is BodyKind.Planet or BodyKind.Moon or BodyKind.DwarfPlanet or BodyKind.Asteroid or BodyKind.Comet)
                .Select(e => new TargetOption(e.Id, e.GetView<NameView>()?.Name ?? $"Body {e.Id}"))
                .OrderBy(b => b.Name, StringComparer.CurrentCulture)
                .ToList() ?? new List<TargetOption>();
            CollectionSync.Sync(Bodies, bodies, b => b.Id);
            if (SelectedBody != null && Bodies.All(b => b.Id != SelectedBody.Id))
                SelectedBody = null;

            var colonies = system?.Entities
                .Where(e => e.Relation == OwnerRelation.Owned && e.GetView<ColonyView>() != null)
                .Select(e => new TargetOption(e.Id, e.GetView<NameView>()?.Name ?? $"Colony {e.Id}"))
                .OrderBy(c => c.Name, StringComparer.CurrentCulture)
                .ToList() ?? new List<TargetOption>();
            CollectionSync.Sync(Colonies, colonies, c => c.Id);
            SelectedSource = Colonies.FirstOrDefault(c => c.Id == SelectedSource?.Id) ?? Colonies.FirstOrDefault();

            var officers = galaxy.Commanders
                .Where(c => c.Kind == CommanderKind.Navy)
                .OrderBy(c => c.IsAssigned)
                .ThenBy(c => c.Name, StringComparer.CurrentCulture)
                .Select(c => new OfficerOption(c.Id, c.Name, c.AssignmentName ?? "unassigned"))
                .ToList();
            CollectionSync.Sync(Officers, officers, o => o.Id);
            if (SelectedOfficer != null && Officers.All(o => o.Id != SelectedOfficer.Id))
                SelectedOfficer = null;
        }
        finally
        {
            _updating = false;
        }
    }

    partial void OnSelectedFleetChanged(FleetRow? oldValue, FleetRow? newValue)
    {
        if (!_updating && _galaxy != null && oldValue?.Id != newValue?.Id)
        {
            SelectedShip = null;
            Update(_galaxy);
        }
    }

    private static IEnumerable<(FleetSnapshot fleet, int depth)> Flatten(IEnumerable<FleetSnapshot> fleets, int depth)
    {
        foreach (var fleet in fleets.OrderBy(f => f.Name, StringComparer.CurrentCulture))
        {
            yield return (fleet, depth);
            foreach (var sub in Flatten(fleet.SubFleets, depth + 1))
                yield return sub;
        }
    }

    private static string GoalLine(GoalSnapshot? goal)
        => goal == null || string.IsNullOrEmpty(goal.Name) ? "Idle"
            : string.IsNullOrEmpty(goal.Message) ? $"{goal.Name} ({goal.Status})" : $"{goal.Name}: {goal.Message}";

    private bool HasFleetAndBody() => SelectedFleet != null && SelectedBody != null;

    [RelayCommand(CanExecute = nameof(HasFleetAndBody))]
    private Task Move()
        => Sink.SendAsync(new MoveToBodyCommand(SelectedFleet!.Id, SelectedBody!.Id), $"{SelectedFleet.Name} is moving to {SelectedBody.Name}.");

    [RelayCommand(CanExecute = nameof(HasFleetAndBody))]
    private Task Survey()
        => Sink.SendAsync(new GeoSurveyCommand(SelectedFleet!.Id, SelectedBody!.Id), $"{SelectedFleet.Name} will survey {SelectedBody.Name}.");

    private bool CanFound() => HasFleetAndBody() && SelectedSource != null;

    [RelayCommand(CanExecute = nameof(CanFound))]
    private Task FoundColony()
        => Sink.SendAsync(new FoundColonyCommand(SelectedFleet!.Id, SelectedBody!.Id, SelectedSource!.Id),
            $"{SelectedFleet.Name} is founding a colony on {SelectedBody.Name}, supplied from {SelectedSource.Name}.");

    private bool HasFleet_() => SelectedFleet != null;

    [RelayCommand(CanExecute = nameof(HasFleet_))]
    private Task Cancel()
        => Sink.SendAsync(new CancelGoalCommand(SelectedFleet!.Id, CancelTiming.Now), $"{SelectedFleet.Name}'s order is cancelled.");

    private bool CanSupply() => SelectedShip is { HasCaptain: true };

    [RelayCommand(CanExecute = nameof(CanSupply))]
    private Task Supply()
        => Sink.SendAsync(new SupplyColoniesCommand(SelectedShip!.Id), $"{SelectedShip.Name} will keep your colonies supplied.");

    [RelayCommand]
    private Task FormFleet(ShipRow ship)
        => Sink.SendAsync(new FormFleetCommand(ship.Id), $"{ship.Name} now leads a fleet of its own.");

    private bool CanAssignCaptain() => SelectedShip != null && SelectedOfficer != null;

    [RelayCommand(CanExecute = nameof(CanAssignCaptain))]
    private Task AssignCaptain()
        => Sink.SendAsync(new AssignCaptainCommand(SelectedShip!.Id, SelectedOfficer!.Id), $"{SelectedOfficer.Name} now captains {SelectedShip.Name}.");

    private bool CanRelieve() => SelectedShip is { HasCaptain: true };

    [RelayCommand(CanExecute = nameof(CanRelieve))]
    private Task RelieveCaptain()
        => Sink.SendAsync(new AssignCaptainCommand(SelectedShip!.Id, -1), $"{SelectedShip.Name}'s captain stood down.");
}
