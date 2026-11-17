using System.Globalization;
using Pulsar4X.Api;
using Pulsar4X.UI.Core.Map;

namespace Pulsar4X.UI.Core.ViewModels;

/// <summary>One labelled fact in the selection panel, e.g. ("Gravity", "0.98 g").</summary>
public sealed record InfoRow(string Label, string Value);

/// <summary>What the side panel shows about the selected body or ship.</summary>
public sealed record SelectionInfo(int Id, string Title, string Subtitle, IReadOnlyList<InfoRow> Rows)
{
    private const double EarthMassKg = 5.9722e24;
    private const double StandardGravity = 9.80665;
    private const double SecondsPerYear = 365.25 * 86_400;

    private static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    public static SelectionInfo? From(IClientSystem system, int entityId)
    {
        if (system.GetEntity(entityId) is not { } entity) return null;

        string name = entity.GetView<NameView>()?.Name ?? $"#{entity.Id}";
        var rows = new List<InfoRow>();
        string subtitle = entity.Kind switch
        {
            BodyKind.DwarfPlanet => "Dwarf planet",
            _ => entity.Kind.ToString(),
        };

        if (entity.GetView<StarView>() is { } star)
        {
            subtitle = $"Star, class {star.SpectralClass}{star.LuminosityClass}".TrimEnd();
            rows.Add(new("Temperature", F(star.SurfaceTemperatureC, "N0") + " °C"));
            rows.Add(new("Luminosity", F(star.Luminosity, "0.###") + " L☉"));
            rows.Add(new("Age", F(star.AgeYears / 1e9, "0.##") + " billion years"));
            rows.Add(new("Habitable zone", $"{F(star.MinHabitableRadiusAu, "0.##")} – {F(star.MaxHabitableRadiusAu, "0.##")} AU"));
        }

        if (entity.GetView<BodyView>() is { } body && entity.Kind != BodyKind.Star)
        {
            // "Terrestrial planet" says enough for a planet; moons and others keep their kind.
            string type = SplitWords(body.BodyType);
            if (!string.IsNullOrWhiteSpace(type))
                subtitle = entity.Kind == BodyKind.Planet ? Capitalise(type) : $"{subtitle} · {type}";
            rows.Add(new("Gravity", F(body.GravityMetresPerSec2 / StandardGravity, "0.##") + " g"));
            rows.Add(new("Surface temp.", F(body.SurfaceTemperatureC, "N0") + " °C"));
            if (body.DayLength > TimeSpan.Zero)
                rows.Add(new("Day length", F(body.DayLength.TotalHours, "0.#") + " h"));
        }

        if (entity.GetView<MassVolumeView>() is { RadiusMetres: > 0 } mass && entity.Kind != BodyKind.Ship)
        {
            rows.Add(new("Radius", F(mass.RadiusMetres / 1000, "N0") + " km"));
            rows.Add(new("Mass", mass.MassKg >= EarthMassKg * 0.001
                ? F(mass.MassKg / EarthMassKg, "0.00#") + " Earth masses"
                : F(mass.MassKg, "0.##e+0") + " kg"));
        }

        if (MapOrbits.ResolveOrbit(entity) is { } orbit && MapOrbits.CanPropagate(orbit))
        {
            if (orbit.ParentId is { } parentId && system.GetEntity(parentId)?.GetView<NameView>() is { } parentName)
                rows.Add(new("Orbits", parentName.Name));
            rows.Add(new("Semi-major axis", MapCamera.DescribeMetres(Math.Abs(orbit.SemiMajorAxisM))));
            rows.Add(new("Eccentricity", F(orbit.Eccentricity, "0.###")));
            if (orbit.OrbitalPeriodSeconds > 0 && !double.IsInfinity(orbit.OrbitalPeriodSeconds))
            {
                rows.Add(new("Orbital period", orbit.OrbitalPeriodSeconds >= SecondsPerYear * 2
                    ? F(orbit.OrbitalPeriodSeconds / SecondsPerYear, "0.##") + " years"
                    : F(orbit.OrbitalPeriodSeconds / 86_400, "0.#") + " days"));
            }
        }

        long population = system.Entities
            .Select(e => e.GetView<ColonyView>())
            .Where(c => c?.PlanetEntityId == entity.Id)
            .Sum(c => c!.Population);
        if (population > 0)
            rows.Add(new("Population", F(population, "N0")));

        if (entity.Kind == BodyKind.Ship)
            rows.Add(new("Owner", entity.Relation == OwnerRelation.Owned ? "You" : entity.Relation.ToString()));

        return new SelectionInfo(entity.Id, name, subtitle, rows);
    }

    private static string Capitalise(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>"GasGiant" or "Gas Giant" → "Gas giant" (lower-cases words after the first).</summary>
    internal static string SplitWords(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var sb = new System.Text.StringBuilder(text.Length + 4);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (i > 0 && char.IsUpper(c) && text[i - 1] == ' ')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (i > 0 && char.IsUpper(c) && !char.IsUpper(text[i - 1]))
            {
                sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
