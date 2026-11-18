using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulsar4X.Api;
using Pulsar4X.UI.Core.Colonies;
using Pulsar4X.UI.Core.Map;

namespace Pulsar4X.UI.Core.ViewModels;

/// <summary>
/// The app shell: starts a game through <see cref="IGameLauncher"/>, then mirrors the client's galaxy
/// model into bindable state once per frame (<see cref="Tick"/>): the clock, the faction, the list of
/// known systems, the star map and body table of the selected system, and the selected body.
/// The map owns the selection (<see cref="StarMapViewModel.SelectedId"/>); the table and the info
/// panel follow it.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IGameLauncher _launcher;
    private int _bodiesSignature;

    public MainWindowViewModel(IGameLauncher launcher)
    {
        _launcher = launcher;
        Colonies = new ColoniesViewModel(command => Client is { } client
            ? client.SubmitCommandAsync(command)
            : Task.FromResult(CommandResult.Reject("No game is running.")));
        Map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StarMapViewModel.SelectedId))
                OnMapSelectionChanged();
        };
    }

    public StarMapViewModel Map { get; } = new();

    public ColoniesViewModel Colonies { get; }

    public const int MapTab = 0, BodiesTab = 1, ColoniesTab = 2;

    /// <summary>The connected game client, or null before a game has started.</summary>
    public IGameClient? Client { get; private set; }

    public TimeControlViewModel Time { get; } = new();

    public ObservableCollection<SystemSummary> Systems { get; } = new();

    public ObservableCollection<BodyRowViewModel> Bodies { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStartScreen))]
    private bool _hasGame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStartScreen))]
    [NotifyCanExecuteChangedFor(nameof(StartGameCommand))]
    private bool _isStarting;

    [ObservableProperty] private string _statusText = "No game running.";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string _factionName = "";
    [ObservableProperty] private string _fundsText = "";
    [ObservableProperty] private string _bodiesHeader = "";

    [ObservableProperty] private SystemSummary? _selectedSystem;

    /// <summary>The table row of the selected body (null when the selection is not in the table).</summary>
    [ObservableProperty] private BodyRowViewModel? _selectedBodyRow;

    /// <summary>Facts about the selected body or ship for the side panel; null when nothing is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowSelectionPanel))]
    private SelectionInfo? _selection;

    public bool HasSelection => Selection != null;

    /// <summary>0 = map, 1 = body table, 2 = colonies.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelectionPanel), nameof(ShowSystemList))]
    private int _selectedTab;

    /// <summary>The known-systems list feeds the map and body table; the colony screen hides it for room.</summary>
    public bool ShowSystemList => SelectedTab != ColoniesTab;

    /// <summary>The body panel belongs to the map and table; the colony screen has its own layout.</summary>
    public bool ShowSelectionPanel => HasSelection && SelectedTab != ColoniesTab;

    /// <summary>The selected body hosts one of the player's colonies, so the panel offers to open it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenColonyCommand))]
    private int? _selectedBodyColonyId;

    partial void OnSelectedTabChanged(int value)
    {
        if (value == ColoniesTab && Client is { } client) Colonies.Update(client.Galaxy);
    }

    private bool CanOpenColony() => SelectedBodyColonyId != null;

    [RelayCommand(CanExecute = nameof(CanOpenColony))]
    private void OpenColony()
    {
        if (Client is not { } client || SelectedBodyColonyId is not { } colonyId) return;
        SelectedTab = ColoniesTab;
        Colonies.Open(colonyId, client.Galaxy);
    }

    private bool _syncingSelection;

    partial void OnSelectedBodyRowChanged(BodyRowViewModel? value)
    {
        if (_syncingSelection || value == null) return;
        Map.Select(value.Id);
    }

    private void OnMapSelectionChanged()
    {
        _syncingSelection = true;
        try
        {
            SelectedBodyRow = Map.SelectedId is { } id ? Bodies.FirstOrDefault(b => b.Id == id) : null;
        }
        finally
        {
            _syncingSelection = false;
        }
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        var system = Client != null && SelectedSystem != null ? Client.Galaxy.GetSystem(SelectedSystem.SystemId) : null;
        Selection = system != null && Map.SelectedId is { } id ? SelectionInfo.From(system, id) : null;
        SelectedBodyColonyId = system != null && Map.SelectedId is { } bodyId ? ColoniesViewModel.OwnColonyOnBody(system, bodyId) : null;
    }

    [RelayCommand]
    private void ClearSelection() => Map.Select(null);

    [RelayCommand]
    private void ShowOnMap()
    {
        SelectedTab = 0;
        Map.CentreOnSelectedCommand.Execute(null);
    }

    /// <summary>Whether the body table lists asteroids and comets. Off by default: Sol alone has hundreds.</summary>
    [ObservableProperty] private bool _showMinorBodies;

    partial void OnShowMinorBodiesChanged(bool value)
    {
        // One toggle for both views, so anything in the table can also be selected on the map.
        Map.ShowMinorBodies = value;
        if (Client is { } client) RefreshBodies(client.Galaxy, force: true);
    }

    public bool ShowStartScreen => !HasGame;

    private bool CanStartGame() => !IsStarting;

    [RelayCommand(CanExecute = nameof(CanStartGame))]
    private async Task StartGame()
    {
        IsStarting = true;
        ErrorText = null;
        StatusText = "Generating the galaxy…";
        try
        {
            // Galaxy generation is heavy; keep it off the UI thread. The await resumes on the UI thread.
            var client = await Task.Run(() => _launcher.QuickstartAsync());
            AttachClient(client);
        }
        catch (Exception ex)
        {
            ErrorText = ex.GetBaseException().Message;
            StatusText = "Could not start a game.";
        }
        finally
        {
            IsStarting = false;
        }
    }

    /// <summary>Binds an already-connected client. Public so tests can attach one directly.</summary>
    public void AttachClient(IGameClient client)
    {
        Client = client;
        HasGame = true;
        StatusText = "Game running.";
        Time.Attach(client);
        Tick();
        SelectedSystem ??= Systems.FirstOrDefault();
    }

    /// <summary>Applies pending server updates and refreshes the bound state. Call once per frame on the UI thread.</summary>
    public void Tick()
    {
        if (Client is not { } client) return;

        client.Update();
        Time.Refresh();
        RefreshFaction(client.Galaxy);
        RefreshSystems(client.Galaxy);
        RefreshBodies(client.Galaxy, force: false);
        Map.Update(SelectedSystem is { } s ? client.Galaxy.GetSystem(s.SystemId) : null, client.Galaxy.Time.GameDateTime);
        if (SelectedTab == ColoniesTab) Colonies.Update(client.Galaxy);
    }

    partial void OnSelectedSystemChanged(SystemSummary? value)
    {
        if (Client is { } client)
        {
            // Tells the engine which system the player is looking at so it gets processing priority.
            _ = client.SetSystemFocusAsync(value?.SystemId);
            RefreshBodies(client.Galaxy, force: true);
        }
    }

    private void RefreshFaction(IClientGalaxy galaxy)
    {
        if (galaxy.Faction is not { } faction) return;
        FactionName = faction.Name;
        FundsText = faction.Funds.ToString("N0", CultureInfo.InvariantCulture) + " cr";
    }

    private void RefreshSystems(IClientGalaxy galaxy)
    {
        var known = galaxy.KnownSystems;
        if (known.Count == Systems.Count && known.Zip(Systems).All(p => p.First.SystemId == p.Second.SystemId))
            return;

        var selectedId = SelectedSystem?.SystemId;
        Systems.Clear();
        foreach (var system in known.OrderBy(s => s.Name, StringComparer.CurrentCulture))
            Systems.Add(system);
        SelectedSystem = Systems.FirstOrDefault(s => s.SystemId == selectedId) ?? Systems.FirstOrDefault();
    }

    private void RefreshBodies(IClientGalaxy galaxy, bool force)
    {
        var system = SelectedSystem is { } selected ? galaxy.GetSystem(selected.SystemId) : null;
        if (system == null)
        {
            if (Bodies.Count > 0) Bodies.Clear();
            _bodiesSignature = 0;
            BodiesHeader = SelectedSystem == null ? "" : $"{SelectedSystem.Name}: no data yet";
            return;
        }

        // Rebuilding resets the table's scroll and selection, so only do it when something the table
        // shows has changed: which bodies exist, or the colony population on them.
        int signature = Signature(system);
        if (!force && signature == _bodiesSignature) return;
        _bodiesSignature = signature;

        var rows = SystemBodies.BuildRows(system, ShowMinorBodies);
        Bodies.Clear();
        foreach (var row in rows)
            Bodies.Add(row);
        OnMapSelectionChanged(); // re-select the row and refresh the panel after a rebuild

        int total = SystemBodies.CountBodies(system);
        BodiesHeader = rows.Count == total
            ? $"{system.Name}: {total} bodies"
            : $"{system.Name}: {rows.Count} of {total} bodies";
    }

    private static int Signature(IClientSystem system)
    {
        var hash = new HashCode();
        hash.Add(system.SystemId);
        foreach (var entity in system.Entities.OrderBy(e => e.Id))
        {
            if (SystemBodies.IsBody(entity))
            {
                hash.Add(entity.Id);
                hash.Add(entity.GetView<NameView>()?.Name);
            }
            else if (entity.GetView<ColonyView>() is { } colony)
            {
                hash.Add(colony.PlanetEntityId);
                hash.Add(colony.Population);
            }
        }
        return hash.ToHashCode();
    }
}
