using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pulsar4X.Api;

namespace Pulsar4X.UI.Core.Colonies;

/// <summary>One cargo store (general, fuel, …) with its fill level and contents.</summary>
public sealed partial class StoreGroupViewModel : ObservableObject
{
    public StoreGroupViewModel(string typeId) => TypeId = typeId;

    public string TypeId { get; }
    [ObservableProperty] private string _typeName = "";
    [ObservableProperty] private string _capacityText = "";
    [ObservableProperty] private double _fillPercent;
    public ObservableCollection<StockRow> Items { get; } = new();

    public void Update(CargoTypeStoreView store)
    {
        TypeName = store.TypeName;
        double used = Math.Max(0, store.MaxVolume - store.FreeVolume);
        FillPercent = store.MaxVolume > 0 ? used / store.MaxVolume * 100 : 0;
        CapacityText = $"{Fmt.Volume(used)} of {Fmt.Volume(store.MaxVolume)} ({FillPercent:0}%)";

        var rows = store.Items
            .GroupBy(i => (i.Name, i.ItemKind))
            .Select(g => new StockRow($"{g.Key.ItemKind}|{g.Key.Name}", g.Key.Name, g.Key.ItemKind,
                g.Sum(i => i.Units), g.Sum(i => i.MassStoredKg), g.Sum(i => i.VolumeStored)))
            .OrderBy(r => r.Kind, StringComparer.CurrentCulture)
            .ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
        CollectionSync.Sync(Items, rows, r => r.Key);
    }
}

/// <summary>The colony at a glance: people, infrastructure, power, industry, installations and stockpile.</summary>
public sealed partial class ColonySummaryViewModel : ObservableObject
{
    public ObservableCollection<StatRow> People { get; } = new();
    public ObservableCollection<StatRow> Infrastructure { get; } = new();
    public ObservableCollection<StatRow> Power { get; } = new();
    public ObservableCollection<StatRow> Industry { get; } = new();
    public ObservableCollection<InstallationRow> Installations { get; } = new();
    public ObservableCollection<StoreGroupViewModel> Stores { get; } = new();

    [ObservableProperty] private bool _hasPower;
    [ObservableProperty] private bool _hasInfrastructure;
    [ObservableProperty] private double _infrastructurePercent;

    public void Update(EntitySnapshot colony)
    {
        var view = colony.GetView<ColonyView>();
        var people = new List<StatRow>();
        if (view != null)
        {
            people.Add(new("Population", Fmt.Int(view.Population)));
            if (view.SpeciesPopulations.Count > 1)
                people.AddRange(view.SpeciesPopulations.Select(s => new StatRow("  " + s.SpeciesName, Fmt.Int(s.Population))));
            else if (view.SpeciesPopulations.Count == 1)
                people.Add(new("Species", view.SpeciesPopulations[0].SpeciesName));
            people.Add(new("Trained crew", Fmt.Int(view.CrewPool)));
            if (view.HasAdministrativePost)
                people.Add(new("Administrator", view.AdministratorName ?? "Vacant", IsWarning: view.AdministratorName == null));
            if (!string.IsNullOrEmpty(view.CommandSpan))
                people.Add(new("Command reach", view.CommandSpan));
        }
        CollectionSync.Sync(People, people, r => r.Label);

        var infra = colony.GetView<InfrastructureView>();
        HasInfrastructure = infra != null;
        var infraRows = new List<StatRow>();
        if (infra != null)
        {
            bool short_ = infra.CapacityAvailable < 0;
            InfrastructurePercent = Math.Clamp(infra.Efficiency, 0, 1) * 100;
            infraRows.Add(new("Capacity", Fmt.Int(infra.CapacityProvided)));
            infraRows.Add(new("Needed", Fmt.Int(infra.CapacityRequired), short_));
            infraRows.Add(new("Efficiency", Fmt.Percent(infra.Efficiency), infra.Efficiency < 0.999));
            if (short_)
                infraRows.Add(new("Shortfall", Fmt.Int(-infra.CapacityAvailable) + " — build Infrastructure", true));
        }
        CollectionSync.Sync(Infrastructure, infraRows, r => r.Label);

        var power = colony.GetView<ColonyPowerView>();
        HasPower = power != null;
        var powerRows = new List<StatRow>();
        if (power != null)
        {
            powerRows.Add(new("Generation", Fmt.Power(power.Generation_kW)));
            powerRows.Add(new("Delivered", $"{Fmt.Power(power.Delivered_kW)} ({Fmt.Percent(power.GridEfficiency)} grid)"));
            powerRows.Add(new("Demand", Fmt.Power(power.Demand_kW)));
            powerRows.Add(new("Balance", (power.Balance_kW >= 0 ? "+" : "") + Fmt.Power(power.Balance_kW), power.Balance_kW < 0));
            var worst = power.Tiers.Where(t => t.Demand_kW > 0).OrderBy(t => t.Satisfaction).FirstOrDefault();
            if (worst != null && worst.Satisfaction < 0.999)
                powerRows.Add(new("Short tier", $"{worst.Tier}: {Fmt.Percent(worst.Satisfaction)} supplied", true));
            if (power.StoreMax_kJ > 0)
                powerRows.Add(new("Batteries", Fmt.Percent(power.StoredFraction) + " charged"));
        }
        CollectionSync.Sync(Power, powerRows, r => r.Label);

        var industryRows = new List<StatRow>();
        if (colony.GetView<IndustryView>() is { } industry)
        {
            foreach (var line in industry.ProductionLines)
            {
                var head = line.Jobs.FirstOrDefault();
                string value = head == null
                    ? "Idle"
                    : $"{head.Name} ({head.PercentComplete:0}%) · {Fmt.PointsPerDay(line.CurrentRatePerDay)}";
                industryRows.Add(new(line.Name, value, head?.MissingResources == true));
            }
        }
        if (colony.GetView<ConstructionView>() is { } construction)
        {
            industryRows.Add(new("Construction", construction.BuildQueue.Count == 0
                ? $"Idle · {Fmt.PointsPerDay(construction.PointsPerDay)}"
                : $"{construction.BuildQueue.Count} queued · {Fmt.PointsPerDay(construction.PointsPerDay)}"));
        }
        CollectionSync.Sync(Industry, industryRows, r => r.Label);

        var installations = colony.GetView<InstallationsView>()?.Installations
            .Select(g => new InstallationRow(g.DesignId, g.Name, g.TemplateName, g.Count, g.OperationalCount))
            .OrderBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList() ?? new List<InstallationRow>();
        CollectionSync.Sync(Installations, installations, r => r.DesignId);

        var stores = colony.GetView<CargoStorageView>()?.Stores ?? Array.Empty<CargoTypeStoreView>();
        CollectionSync.Sync(Stores, stores, s => s.TypeId, vm => vm.TypeId,
            s => { var vm = new StoreGroupViewModel(s.TypeId); vm.Update(s); return vm; },
            (vm, s) => vm.Update(s));
    }
}
