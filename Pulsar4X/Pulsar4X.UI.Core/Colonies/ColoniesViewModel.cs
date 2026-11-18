using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pulsar4X.Api;

namespace Pulsar4X.UI.Core.Colonies;

/// <summary>
/// The Colonies screen: the player's colonies across every known system, and the selected one's
/// Summary, Production, Construction and Mining tabs. Refreshes from the galaxy model each frame,
/// but only rebuilds a section when the colony's snapshot has actually been replaced by a push.
/// </summary>
public sealed partial class ColoniesViewModel : ObservableObject
{
    private EntitySnapshot? _lastSnapshot;

    public ColoniesViewModel(Func<GameCommand, Task<CommandResult>> submit)
    {
        Production = new ColonyProductionViewModel(new CommandSink(submit));
        Construction = new ColonyConstructionViewModel(new CommandSink(submit));
        Administrator = new ColonyAutomationViewModel(new CommandSink(submit));
    }

    public ObservableCollection<ColonyListItem> Colonies { get; } = new();
    public ColonySummaryViewModel Summary { get; } = new();
    public ColonyProductionViewModel Production { get; }
    public ColonyConstructionViewModel Construction { get; }
    public ColonyAutomationViewModel Administrator { get; }
    public ObservableCollection<MineralRow> Minerals { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasColony))]
    private ColonyListItem? _selectedColony;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _minesText = "";

    /// <summary>0 Summary, 1 Production, 2 Construction, 3 Mining, 4 Administrator.</summary>
    [ObservableProperty] private int _selectedSection;

    public bool HasColony => SelectedColony != null;
    public bool HasNoColonies => Colonies.Count == 0;

    /// <summary>Opens the colony on <paramref name="bodyId"/> (or with that colony id), if the player has one there.</summary>
    public bool Open(int bodyOrColonyId, IClientGalaxy galaxy)
    {
        Update(galaxy);
        foreach (var item in Colonies)
        {
            if (item.Id == bodyOrColonyId || ColonyBodyId(galaxy, item) == bodyOrColonyId)
            {
                SelectedColony = item;
                Update(galaxy);
                return true;
            }
        }
        return false;
    }

    /// <summary>The player's colony on <paramref name="bodyId"/> in the given system, if any.</summary>
    public static int? OwnColonyOnBody(IClientSystem system, int bodyId)
        => system.Entities.FirstOrDefault(e => e.Relation == OwnerRelation.Owned
                                               && e.GetView<ColonyView>()?.PlanetEntityId == bodyId)?.Id;

    private static int? ColonyBodyId(IClientGalaxy galaxy, ColonyListItem item)
        => galaxy.GetSystem(item.SystemId)?.GetEntity(item.Id)?.GetView<ColonyView>()?.PlanetEntityId;

    public void Update(IClientGalaxy galaxy)
    {
        var colonies = new List<ColonyListItem>();
        foreach (var summary in galaxy.KnownSystems)
        {
            if (galaxy.GetSystem(summary.SystemId) is not { } system) continue;
            foreach (var entity in system.Entities)
            {
                if (entity.Relation != OwnerRelation.Owned || entity.GetView<ColonyView>() is not { } colony) continue;
                string body = colony.PlanetEntityId is { } pid ? system.GetEntity(pid)?.GetView<NameView>()?.Name ?? "" : "";
                colonies.Add(new ColonyListItem(entity.Id, system.SystemId, entity.GetView<NameView>()?.Name ?? $"Colony {entity.Id}",
                    body, system.Name, colony.Population, true));
            }
        }
        colonies.Sort((a, b) => b.Population.CompareTo(a.Population));
        CollectionSync.Sync(Colonies, colonies, c => c.Id);
        OnPropertyChanged(nameof(HasNoColonies));

        if (SelectedColony == null || Colonies.All(c => c.Id != SelectedColony.Id))
            SelectedColony = Colonies.FirstOrDefault();
        else if (!ReferenceEquals(Colonies.First(c => c.Id == SelectedColony.Id), SelectedColony))
            SelectedColony = Colonies.First(c => c.Id == SelectedColony.Id);

        var selected = SelectedColony;
        var entitySnapshot = selected != null ? galaxy.GetSystem(selected.SystemId)?.GetEntity(selected.Id) : null;
        if (entitySnapshot == null)
        {
            _lastSnapshot = null;
            Title = "";
            Subtitle = "";
            return;
        }
        if (ReferenceEquals(entitySnapshot, _lastSnapshot)) return;
        _lastSnapshot = entitySnapshot;

        Title = selected!.Name;
        Subtitle = selected.LocationText;
        Summary.Update(entitySnapshot);
        Production.Update(entitySnapshot.Id, entitySnapshot.GetView<IndustryView>());
        Construction.Update(entitySnapshot.Id, entitySnapshot.GetView<ConstructionView>());
        Administrator.Update(entitySnapshot, selected.Name);

        var mining = entitySnapshot.GetView<ColonyMiningView>();
        MinesText = mining == null ? "No mining data." : $"{Fmt.Int(mining.NumberOfMines)} mine{(mining.NumberOfMines == 1 ? "" : "s")}";
        var minerals = (mining?.Minerals ?? Array.Empty<MineralMiningRow>())
            .Select(m => new MineralRow(m.MineralId, m.Name, m.Stockpile, m.AvailableToMine, m.Accessibility, m.AnnualProduction, m.CanMine))
            .OrderByDescending(m => m.AnnualProduction)
            .ThenBy(m => m.Name, StringComparer.CurrentCulture)
            .ToList();
        CollectionSync.Sync(Minerals, minerals, m => m.MineralId);
    }

    partial void OnSelectedColonyChanged(ColonyListItem? oldValue, ColonyListItem? newValue)
    {
        if (oldValue?.Id != newValue?.Id) _lastSnapshot = null; // force a rebuild for the new colony
    }
}
