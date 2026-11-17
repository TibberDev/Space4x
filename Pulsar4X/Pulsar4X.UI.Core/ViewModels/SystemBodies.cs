using Pulsar4X.Api;

namespace Pulsar4X.UI.Core.ViewModels;

/// <summary>Builds the body table rows for one system: an orbit tree flattened depth-first, so each
/// moon sits under its planet and siblings are ordered by distance from their parent.</summary>
public static class SystemBodies
{
    private static readonly HashSet<BodyKind> BodyKinds = new()
    {
        BodyKind.Star, BodyKind.Planet, BodyKind.DwarfPlanet, BodyKind.Moon, BodyKind.Asteroid, BodyKind.Comet,
    };

    public static bool IsBody(EntitySnapshot entity) => BodyKinds.Contains(entity.Kind);

    /// <summary>Asteroids and comets: numerous (hundreds in Sol), so the table can hide them.</summary>
    public static bool IsMinorBody(EntitySnapshot entity) => entity.Kind is BodyKind.Asteroid or BodyKind.Comet;

    public static int CountBodies(IClientSystem system) => system.Entities.Count(IsBody);

    public static IReadOnlyList<BodyRowViewModel> BuildRows(IClientSystem system, bool includeMinorBodies = true)
    {
        var bodies = system.Entities
            .Where(e => IsBody(e) && (includeMinorBodies || !IsMinorBody(e)))
            .ToDictionary(e => e.Id);

        // Colonies are separate entities that point at their planet; total population per body.
        var population = new Dictionary<int, long>();
        foreach (var entity in system.Entities)
        {
            if (entity.GetView<ColonyView>() is { PlanetEntityId: { } planetId } colony)
                population[planetId] = population.GetValueOrDefault(planetId) + colony.Population;
        }

        int? ParentOf(EntitySnapshot e)
        {
            var parent = e.GetView<OrbitView>()?.ParentId ?? e.GetView<PositionView>()?.ParentId;
            return parent is { } id && id != e.Id && bodies.ContainsKey(id) ? id : null;
        }

        var children = new Dictionary<int, List<EntitySnapshot>>();
        var roots = new List<EntitySnapshot>();
        foreach (var body in bodies.Values)
        {
            if (ParentOf(body) is { } parentId)
            {
                if (!children.TryGetValue(parentId, out var list))
                    children[parentId] = list = new List<EntitySnapshot>();
                list.Add(body);
            }
            else
            {
                roots.Add(body);
            }
        }

        static double Distance(EntitySnapshot e) => e.GetView<OrbitView>()?.SemiMajorAxisKm ?? 0;
        static string NameOf(EntitySnapshot e) => e.GetView<NameView>()?.Name ?? $"#{e.Id}";

        var rows = new List<BodyRowViewModel>(bodies.Count);
        var visited = new HashSet<int>();

        void Visit(EntitySnapshot body, int depth, string parentName)
        {
            if (!visited.Add(body.Id)) return; // guards against a malformed parent cycle
            var orbit = body.GetView<OrbitView>();
            rows.Add(new BodyRowViewModel(
                body.Id,
                NameOf(body),
                body.Kind,
                depth,
                parentName,
                orbit?.SemiMajorAxisKm,
                orbit?.OrbitalPeriodSeconds,
                population.TryGetValue(body.Id, out var pop) ? pop : null));

            if (children.TryGetValue(body.Id, out var kids))
            {
                foreach (var kid in kids.OrderBy(Distance).ThenBy(NameOf, StringComparer.Ordinal))
                    Visit(kid, depth + 1, NameOf(body));
            }
        }

        // Stars first, then any orphaned bodies, each group by distance.
        foreach (var root in roots
                     .OrderBy(r => r.Kind == BodyKind.Star ? 0 : 1)
                     .ThenBy(Distance)
                     .ThenBy(NameOf, StringComparer.Ordinal))
        {
            Visit(root, 0, "");
        }

        return rows;
    }
}
