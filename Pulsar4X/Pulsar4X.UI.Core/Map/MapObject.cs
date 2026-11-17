using Pulsar4X.Api;

namespace Pulsar4X.UI.Core.Map;

/// <summary>
/// One thing drawn on the star map this frame: a body or ship with its propagated position, plus
/// everything the renderer needs to style it. Colours are ARGB so the type stays UI-toolkit neutral.
/// </summary>
public sealed class MapObject
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required BodyKind Kind { get; init; }
    public required OwnerRelation Relation { get; init; }

    /// <summary>Current position on the system plane, metres.</summary>
    public WorldPoint Position { get; set; }

    /// <summary>Physical radius in metres (0 when unknown, e.g. ships).</summary>
    public double RadiusMetres { get; init; }

    public int? ParentId { get; init; }

    /// <summary>Orbit outline relative to the parent, or null when not drawn.</summary>
    public WorldPoint[]? OrbitPath { get; init; }

    /// <summary>The orbital elements behind <see cref="OrbitPath"/>, for zoom-accurate drawing.</summary>
    public OrbitView? Orbit { get; init; }

    /// <summary>Semi-major axis in metres (0 when not orbiting), used to decide when moons and labels fit.</summary>
    public double OrbitRadiusMetres { get; init; }

    /// <summary>The body hosts at least one colony.</summary>
    public bool HasColony { get; init; }

    /// <summary>The player's faction owns a colony on this body.</summary>
    public bool HasOwnColony { get; init; }

    public uint Color { get; init; }

    public bool IsMinorBody => Kind is BodyKind.Asteroid or BodyKind.Comet;
    public bool IsShip => Kind == BodyKind.Ship;

    /// <summary>Smallest on-screen radius in pixels so tiny bodies stay visible and clickable.</summary>
    public double MinimumPixelRadius => Kind switch
    {
        BodyKind.Star => 7,
        BodyKind.Planet => 4.5,
        BodyKind.DwarfPlanet => 3.5,
        BodyKind.Moon => 3,
        BodyKind.Ship => 4,
        _ => 1.75,
    };
}

/// <summary>Map colours as ARGB. Kept here so desktop and a future mobile renderer agree.</summary>
public static class MapPalette
{
    public const uint Orbit = 0x5A3B82C4;
    public const uint MinorOrbit = 0x283B82C4;
    public const uint Selection = 0xFFFFFFFF;
    public const uint Label = 0xFFC9D1D9;
    public const uint LabelMuted = 0xFF8B949E;
    public const uint OwnColony = 0xFF3FB950;
    public const uint OtherColony = 0xFFD29922;
    public const uint Moon = 0xFF9DA5AE;
    public const uint Asteroid = 0xFF6E7681;
    public const uint Comet = 0xFF79C0FF;

    public static uint ForStar(StarView? star) => (star?.SpectralType?.Trim().ToUpperInvariant() ?? "G") switch
    {
        "O" => 0xFF9BB0FF,
        "B" => 0xFFAABFFF,
        "A" => 0xFFCAD7FF,
        "F" => 0xFFF8F7FF,
        "G" => 0xFFFFE9A8,
        "K" => 0xFFFFC27A,
        "M" => 0xFFFF8F6B,
        _ => 0xFFFFE9A8,
    };

    public static uint ForPlanet(BodyView? body)
    {
        var type = body?.BodyType ?? "";
        if (type.Contains("Gas", StringComparison.OrdinalIgnoreCase)) return 0xFFE3A869;
        if (type.Contains("Ice", StringComparison.OrdinalIgnoreCase)) return 0xFF7FD3E8;
        if (type.Contains("Terrestrial", StringComparison.OrdinalIgnoreCase)) return 0xFF6FA8DC;
        return 0xFFB0A090;
    }

    public static uint ForShip(OwnerRelation relation) => relation switch
    {
        OwnerRelation.Owned => 0xFF58A6FF,
        OwnerRelation.Friendly => 0xFF3FB950,
        OwnerRelation.Neutral => 0xFFD29922,
        OwnerRelation.Hostile => 0xFFF85149,
        _ => 0xFF8B949E,
    };
}
