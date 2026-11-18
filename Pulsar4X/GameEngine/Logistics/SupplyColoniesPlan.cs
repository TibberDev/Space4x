using System;
using System.Collections.Generic;
using System.Linq;
using GameEngine.Engine.Orders;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.Engine;
using Pulsar4X.Extensions;
using Pulsar4X.Fleets;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Logistics;

/// <summary>
/// A standing order for one cargo ship: keep the faction's colonies in this system supplied with the
/// state's own goods. Each trip carries what one colony's queued work lacks (<see cref="ColonyAdministration.Wants"/>)
/// from the colony with the most to spare. No market and no logistics office: the trip itself is a
/// <see cref="MoveGoodsPlan"/> run on this goal's own move-goods fields. With nothing wanted the ship waits
/// and looks again on its next recheck.
/// </summary>
public class SupplyColoniesPlan : IGoalPlanner
{
    public GoalType Type => GoalType.SupplyColonies;

    public PlanResult Plan(Entity managedEntity, Goal goal, DateTime atDateTime)
    {
        if (managedEntity.HasDataBlob<FleetDB>())
            return PlanResult.Fail("Supply colonies is one ship");
        if (!managedEntity.HasDataBlob<ShipInfoDB>() || !managedEntity.HasDataBlob<CargoStorageDB>())
            return PlanResult.Fail("The ship has no cargo storage.");

        // A trip in hand: run it to its end, then pick the next.
        if (goal.MoveGoodsLines.Count > 0)
        {
            var trip = new MoveGoodsPlan().Plan(managedEntity, goal, atDateTime);
            if (trip.Status == GoalStatus.Active)
                return trip;
            string outcome = trip.Status == GoalStatus.Completed ? "Delivered" : "Trip ended: " + trip.Message;
            ShipLogBook.Append(managedEntity, atDateTime, ShipLogKind.Own, $"Supply: {outcome}.");
            ClearTrip(goal);
        }

        if (!TryChooseTrip(managedEntity, out var source, out var dest, out var lines, out var why))
            return PlanResult.Continue(new List<EntityAction>(), "Nothing needed");

        goal.SourceEntityId = source.Id;
        goal.DestEntityId = dest.Id;
        goal.MoveGoodsLines = lines;
        goal.MoveGoodsLeg = MoveGoodsLeg.Load;
        goal.MoveGoodsUnloaded = false;
        ShipLogBook.Append(managedEntity, atDateTime, ShipLogKind.Own, why);

        var first = new MoveGoodsPlan().Plan(managedEntity, goal, atDateTime);
        if (first.Status != GoalStatus.Active)
        {
            ShipLogBook.Append(managedEntity, atDateTime, ShipLogKind.Own, $"Supply: could not start: {first.Message}.");
            ClearTrip(goal);
            return PlanResult.Continue(new List<EntityAction>(), first.Message);
        }
        return first;
    }

    static void ClearTrip(Goal goal)
    {
        goal.MoveGoodsLines = new List<MoveGoodsLine>();
        goal.MoveGoodsLeg = MoveGoodsLeg.Load;
        goal.MoveGoodsUnloaded = false;
        goal.SourceEntityId = -1;
        goal.DestEntityId = -1;
    }

    /// <summary>
    /// The colony that wants the most (by units), and the colony with the most to spare of its biggest want;
    /// every want that source can spare goes on the same trip.
    /// </summary>
    internal static bool TryChooseTrip(Entity ship, out Entity source, out Entity dest, out List<MoveGoodsLine> lines, out string why)
    {
        source = dest = null!;
        lines = new List<MoveGoodsLine>();
        why = "";
        if (ship.Manager == null)
            return false;

        var colonies = ship.Manager.GetAllEntitiesWithDataBlob<ColonyInfoDB>()
            .Where(c => c.FactionOwnerID == ship.FactionOwnerID && c.HasDataBlob<CargoStorageDB>())
            .OrderBy(c => c.GetName(ship.FactionOwnerID), StringComparer.Ordinal)
            .ToList();
        if (colonies.Count < 2)
            return false;

        foreach (var wanting in colonies
                     .Select(c => (colony: c, wants: ColonyAdministration.Wants(c)))
                     .Where(w => w.wants.Count > 0)
                     .OrderByDescending(w => w.wants.Values.Sum()))
        {
            foreach (var (topId, _) in wanting.wants.OrderByDescending(w => w.Value).ThenBy(w => w.Key, StringComparer.Ordinal))
            {
                var giver = colonies
                    .Where(c => c.Id != wanting.colony.Id)
                    .Select(c => (colony: c, spare: ColonyAdministration.Spare(c, topId)))
                    .Where(g => g.spare > 0)
                    .OrderByDescending(g => g.spare)
                    .FirstOrDefault();
                if (giver.colony == null)
                    continue;

                var names = new List<string>();
                foreach (var (id, want) in wanting.wants.OrderBy(w => w.Key, StringComparer.Ordinal))
                {
                    long units = Math.Min(want, ColonyAdministration.Spare(giver.colony, id));
                    if (units <= 0 || RunMarketPlan.FactionOf(giver.colony)?.Data.CargoGoods.GetAny(id) is not { } cargo)
                        continue;
                    lines.Add(new MoveGoodsLine { ItemId = cargo.ID, Requested = units });
                    names.Add($"{units:N0} {cargo.Name}");
                }
                if (lines.Count == 0)
                    continue;

                source = giver.colony;
                dest = wanting.colony;
                why = $"Supply: carrying {string.Join(", ", names)} from {source.GetName(ship.FactionOwnerID)} to {dest.GetName(ship.FactionOwnerID)}, which its queued work needs.";
                return true;
            }
        }
        return false;
    }
}
