using Pulsar4X.Api;

namespace Pulsar4X.UI.Core.Colonies;

// Flat display rows for the colony screen. Records compare by value, so CollectionSync only touches
// a table row when something it shows has changed.

/// <summary>One colony in the colony list.</summary>
public sealed record ColonyListItem(int Id, string SystemId, string Name, string BodyName, string SystemName, long Population, bool IsOwned)
{
    public string PopulationText => Fmt.Quantity(Population);
    public string LocationText => string.IsNullOrEmpty(BodyName) ? SystemName : $"{BodyName} · {SystemName}";
}

/// <summary>A labelled figure on a summary card.</summary>
public sealed record StatRow(string Label, string Value, bool IsWarning = false);

public sealed record InstallationRow(string DesignId, string Name, string Type, int Count, int Operational)
{
    public string OperationalText => Operational == Count ? Fmt.Int(Count) : $"{Fmt.Int(Operational)} of {Fmt.Int(Count)}";
    public bool HasInactive => Operational < Count;
}

/// <summary>Stock of one good, summed over the individual items of that name (e.g. three Merlin engines).</summary>
public sealed record StockRow(string Key, string Name, string Kind, long Units, double MassKg, double Volume)
{
    public string UnitsText => Fmt.Int(Units);
    public string MassText => Fmt.Mass(MassKg);
    public string VolumeText => Fmt.Volume(Volume);
}

public sealed record IndustryJobRow(
    string LineId, string JobId, string Name, int Completed, int Ordered, bool Repeat, string Status,
    double Percent, long PointsLeft, string Warning, bool IsFirst, bool IsLast)
{
    public string CountText => Repeat ? $"{Completed} / {Ordered} ↻" : $"{Completed} / {Ordered}";
    public string PointsLeftText => Fmt.Points(PointsLeft);
    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public bool CanMoveUp => !IsFirst;
    public bool CanMoveDown => !IsLast;
}

/// <summary>One input of a job being planned: per unit, for the whole order, and what the stockpile holds.</summary>
public sealed record CostRow(string Name, long PerUnit, long Total, long Available, bool CanProduce)
{
    public bool IsShort => Available < Total;
    public string PerUnitText => Fmt.Int(PerUnit);
    public string TotalText => Fmt.Int(Total);
    public string AvailableText => Fmt.Quantity(Available);
}

public sealed record ConstructionJobRow(int Index, string Name, string Kind, long PointCost, long PointsAccumulated, double Progress,
    string Target, bool IsFirst, bool IsLast)
{
    public double Percent => Progress * 100;
    public string PointsText => $"{Fmt.Int(PointsAccumulated)} / {Fmt.Points(PointCost)}";
    public bool CanMoveUp => !IsFirst;
    public bool CanMoveDown => !IsLast;
}

public sealed record CrateRow(int ComponentId, string DesignId, string Name, long MountCost)
{
    public string CostText => Fmt.Points(MountCost);
}

public sealed record SiteDesignRow(string DesignId, string Name, long Cost, string Missing)
{
    public string CostText => Fmt.Points(Cost);
    public bool CanAssemble => string.IsNullOrEmpty(Missing);
}

public sealed record MineralRow(int MineralId, string Name, long? Stockpile, long? AvailableToMine, double Accessibility,
    long AnnualProduction, bool CanMine)
{
    public string StockText => Stockpile is { } s ? Fmt.Quantity(s) : "—";
    public string DepositText => AvailableToMine is { } a ? Fmt.Quantity(a) : "unknown";
    public string AccessibilityText => Fmt.Percent(Accessibility);
    public string AnnualText => AnnualProduction > 0 ? Fmt.Quantity(AnnualProduction) + " / year" : "";
    public double AccessibilityPercent => Accessibility * 100;
}

/// <summary>Something a production line can build, as offered in the new-job picker. Flat, so the
/// picker's list (and its selection) survives the per-tick refresh; full details are looked up by id.</summary>
public sealed record ConstructibleOption(string DesignId, string Name, string Category, long PointsPerUnit, bool IsAvailable)
{
    public static ConstructibleOption From(ConstructibleItemView item) => new(
        item.DesignId,
        item.Name,
        item.IsShipDesign ? "Ship" : item.IsMaterial ? "Material" : item.ComponentType,
        item.IndustryPointsPerUnit,
        !item.OverSlip);

    public string PointsText => Fmt.Points(PointsPerUnit);
    public override string ToString() => Name;
}
