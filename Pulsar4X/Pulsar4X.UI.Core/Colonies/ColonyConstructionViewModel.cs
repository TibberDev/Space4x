using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulsar4X.Api;

namespace Pulsar4X.UI.Core.Colonies;

/// <summary>
/// The Construction tab: the colony's installation queue (built with construction points), the
/// stored crates it can mount, and the site-only designs it can assemble from raw materials.
/// </summary>
public sealed partial class ColonyConstructionViewModel : ObservableObject
{
    private readonly CommandSink _sink;
    private int _colonyId;

    public ColonyConstructionViewModel(CommandSink sink) => _sink = sink;

    public CommandSink Result => _sink;
    public ObservableCollection<ConstructionJobRow> Queue { get; } = new();
    public ObservableCollection<CrateRow> Crates { get; } = new();
    public ObservableCollection<SiteDesignRow> SiteDesigns { get; } = new();

    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private bool _hasQueue;
    [ObservableProperty] private bool _hasCrates;

    public void Update(int colonyId, ConstructionView? view)
    {
        if (colonyId != _colonyId)
        {
            _colonyId = colonyId;
            _sink.Clear();
        }

        RateText = view == null ? "This colony cannot build installations." : Fmt.PointsPerDay(view.PointsPerDay);

        var queue = view?.BuildQueue ?? Array.Empty<ConstructionJobView>();
        // Jobs have no id; position plus name identifies a row well enough for in-place updates.
        var rows = queue.Select((j, i) => new ConstructionJobRow(i, j.Name, j.Kind, j.PointCost, j.PointsAccumulated,
            j.Progress, j.TargetName, i == 0, i == queue.Count - 1)).ToList();
        CollectionSync.Sync(Queue, rows, r => (r.Index, r.Name));
        HasQueue = rows.Count > 0;

        var crates = (view?.Crates ?? Array.Empty<ConstructionCrateView>())
            .Select(c => new CrateRow(c.ComponentId, c.DesignId, c.Name, c.MountCost))
            .OrderBy(c => c.Name, StringComparer.CurrentCulture).ToList();
        CollectionSync.Sync(Crates, crates, c => c.ComponentId);
        HasCrates = crates.Count > 0;

        var sites = (view?.SiteDesigns ?? Array.Empty<ConstructionSiteView>())
            .Select(s => new SiteDesignRow(s.DesignId, s.Name, s.IndustryPointCosts,
                s.MissingResources.Count == 0 ? "" : "Needs " + string.Join(", ", s.MissingResources)))
            .OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList();
        CollectionSync.Sync(SiteDesigns, sites, s => s.DesignId);
    }

    [RelayCommand]
    private Task MoveUp(ConstructionJobRow job)
        => _sink.SendAsync(new MoveConstructionJobCommand(_colonyId, job.Index, MoveUp: true), $"Moved {job.Name} up.");

    [RelayCommand]
    private Task MoveDown(ConstructionJobRow job)
        => _sink.SendAsync(new MoveConstructionJobCommand(_colonyId, job.Index, MoveUp: false), $"Moved {job.Name} down.");

    [RelayCommand]
    private Task Remove(ConstructionJobRow job)
        => _sink.SendAsync(new RemoveConstructionJobCommand(_colonyId, job.Index), $"Removed {job.Name} from the queue.");

    [RelayCommand]
    private Task Mount(CrateRow crate)
        => _sink.SendAsync(new AddToConstructionQueueCommand(_colonyId, crate.DesignId, crate.ComponentId), $"Queued mounting {crate.Name}.");

    [RelayCommand]
    private Task Assemble(SiteDesignRow site)
        => _sink.SendAsync(new AddToConstructionQueueCommand(_colonyId, site.DesignId), $"Queued assembling {site.Name}.");
}
