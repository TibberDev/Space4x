using System.Globalization;
using Pulsar4X.Api;

namespace Pulsar4X.UI.Core.ViewModels;

/// <summary>One row of the system body table: a star, planet, moon, asteroid or comet.</summary>
public sealed class BodyRowViewModel
{
    private const double KmPerAu = 149_597_870.7;

    public BodyRowViewModel(int id, string name, BodyKind kind, int depth, string parentName,
        double? semiMajorAxisKm, double? periodSeconds, long? population)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Depth = depth;
        ParentName = parentName;
        SemiMajorAxisKm = semiMajorAxisKm;
        PeriodSeconds = periodSeconds;
        Population = population;
    }

    public int Id { get; }
    public string Name { get; }
    public BodyKind Kind { get; }

    /// <summary>Nesting level in the orbit tree: 0 for stars, 1 for planets, 2 for moons.</summary>
    public int Depth { get; }

    public string ParentName { get; }
    public double? SemiMajorAxisKm { get; }
    public double? PeriodSeconds { get; }

    /// <summary>Population of the player's colony on this body, if any.</summary>
    public long? Population { get; }

    /// <summary>The name indented by orbit depth, for a tree-like look in a flat table.</summary>
    public string DisplayName => new string(' ', Depth * 4) + Name;

    public string KindText => Kind switch
    {
        BodyKind.DwarfPlanet => "Dwarf planet",
        _ => Kind.ToString(),
    };

    public string DistanceText => SemiMajorAxisKm switch
    {
        null or <= 0 => "",
        // Moons read better in km, everything orbiting a star in AU.
        { } km when Depth >= 2 => km.ToString("N0", CultureInfo.InvariantCulture) + " km",
        { } km => (km / KmPerAu).ToString("0.###", CultureInfo.InvariantCulture) + " AU",
    };

    public string PeriodText => PeriodSeconds switch
    {
        null or <= 0 => "",
        { } s when s < 86_400 * 2 => (s / 3600).ToString("0.#", CultureInfo.InvariantCulture) + " h",
        { } s when s < 86_400 * 365.25 * 2 => (s / 86_400).ToString("0.#", CultureInfo.InvariantCulture) + " d",
        { } s => (s / (86_400 * 365.25)).ToString("0.##", CultureInfo.InvariantCulture) + " y",
    };

    public string PopulationText => Population is { } p && p > 0
        ? p.ToString("N0", CultureInfo.InvariantCulture)
        : "";
}
