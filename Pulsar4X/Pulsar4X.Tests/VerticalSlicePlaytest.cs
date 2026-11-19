using System;
using System.Collections.Generic;
using System.Linq;
using GameEngine.Engine.Orders;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Client;
using Pulsar4X.Client.Host;
using Pulsar4X.Colonies;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Api;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Fleets;
using Pulsar4X.Industry;
using Pulsar4X.Names;
using Pulsar4X.People;
using Pulsar4X.Ships;
using Pulsar4X.Storage;

namespace Pulsar4X.Tests;

/// <summary>
/// The vertical slice play-test (docs/plans/vertical-slice-colonies.md, "Done when"): an early-tech game played
/// through the same commands the client sends. Setup orders, then Earth builds the starter crates and a Starship
/// Cargo, the ship founds Luna, Luna gets an administrator, a ship goes on Supply, and five years run with no
/// orders. It prints a timeline. Explicit: it runs for minutes.
/// </summary>
[TestFixture, Explicit("Long: the vertical slice play-test")]
public class VerticalSlicePlaytest
{
    Game _game = null!;
    Entity _faction = null!;
    FactionInfoDB Info => _faction.GetDataBlob<FactionInfoDB>();
    CommandTranslator _translator = null!;

    void Log(string line) => TestContext.Out.WriteLine($"[{_game.TimePulse.GameGlobalDateTime:yyyy-MM-dd}] {line}");

    CommandResult Do(Entity target, GameCommand command)
    {
        var result = _translator.Translate(_faction, target, command);
        Log($"  > {command.GetType().Name.Replace("Command", "")}: {(result.Accepted ? "ok" : "REFUSED " + result.RejectionReason)}");
        return result;
    }

    void Advance(TimeSpan span)
    {
        foreach (var system in _game.Systems)
            system.UpdateActivityState();
        _game.TimePulse.TimeStep(_game.TimePulse.GameGlobalDateTime + span);
        Assert.That(_game.TimePulse.LastFault, Is.Null, _game.TimePulse.LastFault?.Report);
    }

    /// <summary>Advances in steps until <paramref name="done"/> or the time runs out, reporting each step.</summary>
    bool RunUntil(Func<bool> done, TimeSpan limit, TimeSpan step, Action report)
    {
        var end = _game.TimePulse.GameGlobalDateTime + limit;
        while (_game.TimePulse.GameGlobalDateTime < end)
        {
            if (done())
                return true;
            Advance(step);
            report();
        }
        return done();
    }

    long Units(Entity colony, string id) => ColonyAdministration.Stock(colony, id);

    IEnumerable<Entity> Ships => _game.Systems.SelectMany(s => s.GetAllEntitiesWithDataBlob<ShipInfoDB>()).Where(s => s.FactionOwnerID == _faction.Id);

    string ColonyLine(Entity colony)
    {
        var info = colony.GetDataBlob<ColonyInfoDB>();
        long pop = info.Population.Values.Sum();
        var infra = colony.TryGetDataBlob<InfrastructureDB>(out var i) ? $"{i.CapacityProvided:N0}/{i.CapacityRequired:N0}" : "-";
        var queue = colony.TryGetDataBlob<LocalConstructionDB>(out var lc) ? string.Join(",", lc.BuildQueue.Select(j => j.Design.Name)) : "";
        var jobs = colony.TryGetDataBlob<IndustryAbilityDB>(out var ind)
            ? string.Join(",", ind.ProductionLines.Values.SelectMany(l => l.Jobs).Select(j => $"{j.ItemGuid}x{j.NumberOrdered - j.NumberCompleted}"))
            : "";
        var auto = colony.GetDataBlob<ColonyAutomationDB>();
        return $"{colony.GetName(_faction.Id)}: pop {pop:N0}, infra {infra}, build [{queue}], jobs [{jobs}], suggestions {auto.Suggestions.Count}";
    }

    IEnumerable<string> AdminLines(Entity colony, DateTime since)
        => colony.TryGetDataBlob<ShipLogDB>(out var log)
            ? log.Entries.Where(e => e.At >= since).Select(e => e.Text)
            : Enumerable.Empty<string>();

    [Test]
    public void VerticalSlice_FoundLuna_ThenFiveYearsWithNoOrders()
    {
        var method = typeof(EarlyTechStartTests).GetMethod("StartGameLikeTheMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        (_game, _faction) = ((Game, Entity))method.Invoke(null, new object?[] { "Pulsar4x.Scenario.EarlyTech" })!;
        _game.Settings.EnforceSingleThread = true;
        _translator = new CommandTranslator(_game);
        var earth = Info.Colonies.Single();
        // The player looks at Sol, as the client's SetSystemFocus does: the system runs in Foreground.
        foreach (var system in _game.Systems.Where(sys => sys.GetAllEntitiesWithDataBlob<ColonyInfoDB>().Any(c => c.FactionOwnerID == _faction.Id)))
            system.IncrementExternalObserver(true);
        var starter = Pulsar4X.Logistics.BootstrapPackage.DefaultCrates(_game, Info)
            .ToDictionary(l => l.DesignId == "default-design-infrastructure" ? "default-design-infrastructure-luna" : l.DesignId, l => (long)l.Count);
        Log("starter package: " + string.Join(", ", starter.Select(p => $"{p.Key} x{p.Value}")));
        var start = _game.TimePulse.GameGlobalDateTime;
        Log("START " + ColonyLine(earth));
        foreach (var (lineId, line) in earth.GetDataBlob<IndustryAbilityDB>().ProductionLines)
            Log($"  line {line.Name}: {string.Join(", ", line.IndustryTypeRates.Select(r => r.Key + "=" + r.Value))}");
        Log($"  local construction {earth.GetDataBlob<LocalConstructionDB>().PointsPerDay} pts/day");

        // ---- Setup: Earth's administrator on Auto, starter crates as stock targets, a Starship Cargo at the yard.
        var civilian = Info.Commanders.First(c => c.GetDataBlob<CommanderDB>().Type == CommanderTypes.Civilian && c.GetDataBlob<CommanderDB>().AssignedTo < 0);
        Do(earth, new AssignAdministratorCommand(earth.Id, civilian.Id));
        Do(earth, new SetColonyAutomationCommand(earth.Id, AutomationArea.Construction, AutomationLevel.Auto));
        Do(earth, new SetColonyAutomationCommand(earth.Id, AutomationArea.Production, AutomationLevel.Auto));
        foreach (var (id, count) in starter)
            Do(earth, new SetStockTargetCommand(earth.Id, id, count));
        var cargoDesign = Info.ShipDesigns["early-ship-design-starship-cargo"];
        var industry = earth.GetDataBlob<IndustryAbilityDB>();
        var yard = industry.ProductionLines.First(l => l.Value.IndustryTypeRates.ContainsKey(cargoDesign.IndustryTypeID));
        Do(earth, new QueueIndustryJobCommand(earth.Id, yard.Key, cargoDesign.UniqueID, 1, false, false));

        // ---- Stage 1: crates and the ship.
        Entity? cargoShip = null;
        bool ready = RunUntil(
            () =>
            {
                cargoShip = Ships.FirstOrDefault(s => s.GetDataBlob<ShipInfoDB>().Design.UniqueID == cargoDesign.UniqueID);
                return cargoShip != null && starter.All(p => Units(earth, p.Key) >= p.Value);
            },
            TimeSpan.FromDays(3 * 365), TimeSpan.FromDays(30),
            () => Log($"{ColonyLine(earth)} | crates {string.Join(", ", starter.Keys.Select(id => $"{id.Replace("default-design-", "")}:{Units(earth, id)}"))} | ships {string.Join(",", Ships.Select(s => s.GetName(_faction.Id)))}"));
        foreach (var line in AdminLines(earth, start).Distinct().Take(40))
            Log("  log: " + line);
        foreach (var (lineId, line) in earth.GetDataBlob<IndustryAbilityDB>().ProductionLines)
            foreach (var job in line.Jobs)
                Log($"  job on {line.Name}: {job.ItemGuid} {job.NumberCompleted}/{job.NumberOrdered} left {job.ProductionPointsLeft} short {string.Join(",", job.ResourcesRequiredRemaining.Where(r => r.Value > 0).Select(r => $"{r.Key} {r.Value} (have {Units(earth, r.Key)})"))}");
        if (earth.TryGetDataBlob<LaunchComplexDB>(out var launch))
        {
            foreach (var (padId, pad) in launch.Pads)
                Log($"  pad {padId}: ship {pad.ShipName} design {pad.ShipDesignId} ready {pad.ReadyToLaunch} reason {LaunchComplexProcessor.PadHoldReason(earth, pad)}");
            Log($"  launch queue: {launch.LaunchQueue.Count}");
        }
        foreach (var job in earth.GetDataBlob<LocalConstructionDB>().BuildQueue)
            Log($"  build {job.Design.Name}: {job.PointsAccumulated}/{job.PointCost} pts, short {string.Join(", ", job.ResourcesRemaining.Where(r => r.Value > 0).Select(r => $"{r.Key} {r.Value:N0} (have {Units(earth, r.Key):N0})"))}");
        Assert.That(ready, Is.True, "Earth did not build the starter crates and a Starship Cargo within three years");
        Log($"STAGE 1 done after {(_game.TimePulse.GameGlobalDateTime - start).TotalDays:N0} days");

        // ---- Stage 2: captain the cargo ship, found Luna, seat an administrator there, start supply.
        var officer = Info.Commanders.First(c => c.GetDataBlob<CommanderDB>().Type == CommanderTypes.Navy);
        Do(cargoShip!, new AssignCaptainCommand(cargoShip!.Id, officer.Id));
        // A launched ship is in no fleet: form one around it, as the Fleets screen's button does.
        Do(cargoShip, new FormFleetCommand(cargoShip.Id));
        var fleet = _game.Systems.SelectMany(s => s.GetAllEntitiesWithDataBlob<FleetDB>())
            .First(f => f.GetDataBlob<FleetDB>().Children.Contains(cargoShip));
        var luna = NameLookup.GetFirstEntityWithName(cargoShip.Manager!, "Luna");
        var thrust = cargoShip.GetDataBlob<Pulsar4X.Movement.NewtonThrustAbilityDB>();
        Log($"  cargo ship: dv {thrust.DeltaV:N0} m/s, fuel {thrust.FuelType}, parent {cargoShip.GetDataBlob<Pulsar4X.Movement.PositionDB>().Parent?.GetName(_faction.Id)}");
        var founded = Do(fleet, new FoundColonyCommand(fleet.Id, luna.Id, earth.Id));
        Assert.That(founded.Accepted, Is.True, founded.RejectionReason);
        var lunaColony = Info.Colonies.Single(c => c.GetDataBlob<ColonyInfoDB>().PlanetEntity.Id == luna.Id);

        var fleetGoal = fleet.GetDataBlob<GoalsDB>();
        bool settled = RunUntil(
            () => fleetGoal.ActiveGoal is { Status: GoalStatus.Completed or GoalStatus.Failed },
            TimeSpan.FromDays(365), TimeSpan.FromDays(10),
            () => Log($"{ColonyLine(lunaColony)} | fleet {fleetGoal.ActiveGoal?.Status} {fleetGoal.ActiveGoal?.Message}"));
        Log($"Bootstrap ended: {fleetGoal.ActiveGoal?.Status} {fleetGoal.ActiveGoal?.Message?.Replace(Environment.NewLine, " | ").Replace("\n", " | ")}");
        foreach (var line in AdminLines(cargoShip, start).Distinct().Take(20))
            Log("  ship log: " + line);
        Log($"  cargo ship now: dv {thrust.DeltaV:N0} m/s, parent {cargoShip.GetDataBlob<Pulsar4X.Movement.PositionDB>().Parent?.GetName(_faction.Id)}");
        var lunaR = luna.GetDataBlob<Pulsar4X.Galaxy.MassVolumeDB>().RadiusInM;
        var rel = cargoShip.GetDataBlob<Pulsar4X.Movement.PositionDB>().RelativePosition;
        Log($"  cargo ship: {rel.Length():N0} m from {cargoShip.GetDataBlob<Pulsar4X.Movement.PositionDB>().Parent?.GetName(_faction.Id)} centre, Luna radius {lunaR:N0} m, orbit {(cargoShip.HasDataBlob<Pulsar4X.Orbits.OrbitDB>() ? "kepler" : "none")}, newton-move {cargoShip.HasDataBlob<Pulsar4X.Movement.NewtonMoveDB>()}");
        var shipGoal = cargoShip.GetDataBlob<GoalsDB>().ActiveGoal;
        Log($"  cargo ship goal: {shipGoal?.Name} {shipGoal?.Status} {shipGoal?.Message}");
        var hc = Info.Data.CargoGoods.GetAny("hydrocarbons");
        Log($"  fuel: Earth hydrocarbons {Units(earth, "hydrocarbons"):N0}, reserve {Pulsar4X.Energy.ColonyPowerProcessor.FuelReserveUnits(earth, "hydrocarbons"):N0}, ship tank room {cargoShip.GetDataBlob<CargoStorageDB>().GetFreeUnitSpace(hc):N0}, aboard {cargoShip.GetDataBlob<CargoStorageDB>().GetUnitsStored(hc, false):N0}");
        Log($"  Luna support: {lunaColony.GetDataBlob<Pulsar4X.Datablobs.ComponentInstancesDB>().GetPopulationSupportValue(luna):N0} from components, infrastructure designs {string.Join(", ", lunaColony.GetDataBlob<Pulsar4X.Datablobs.ComponentInstancesDB>().AllDesigns.Values.Select(d => d.Name))}");
        foreach (var (typeId, store) in lunaColony.GetDataBlob<CargoStorageDB>().TypeStores)
            Log($"  Luna store {typeId}: free {store.FreeVolume:N1} of {store.MaxVolume:N1} m3");
        foreach (var (typeId, store) in cargoShip.GetDataBlob<CargoStorageDB>().TypeStores)
            Log($"  ship store {typeId}: free {store.FreeVolume:N1} of {store.MaxVolume:N1} m3, {string.Join(", ", store.CurrentStoreInUnits.Where(u => u.Value > 0).Select(u => $"{u.Key} {u.Value:N0}"))}");
        Assert.That(settled && fleetGoal.ActiveGoal!.Status == GoalStatus.Completed, Is.True, "the Luna bootstrap did not finish");

        // Luna's administrator: hire a candidate from Earth's pool into Luna's chair, then automation on Auto.
        var pool = earth.GetDataBlob<CandidatePoolDB>();
        var candidate = pool.Candidates.First();
        Do(earth, new HireCandidateCommand(earth.Id, candidate.Id, Pulsar4X.Api.HireRole.Administrator, lunaColony.Id));
        Do(lunaColony, new SetColonyAutomationCommand(lunaColony.Id, AutomationArea.Construction, AutomationLevel.Auto));
        Do(lunaColony, new SetColonyAutomationCommand(lunaColony.Id, AutomationArea.Production, AutomationLevel.Auto));
        Do(cargoShip, new SupplyColoniesCommand(cargoShip.Id));
        var setupDone = _game.TimePulse.GameGlobalDateTime;
        Log($"STAGE 2 done after {(setupDone - start).TotalDays:N0} days. {ColonyLine(lunaColony)}");

        // ---- Stage 3: five years, no orders.
        long lunaPopStart = lunaColony.GetDataBlob<ColonyInfoDB>().Population.Values.Sum();
        int lunaInstallsStart = lunaColony.GetDataBlob<Pulsar4X.Datablobs.ComponentInstancesDB>().AllComponents.Count;
        RunUntil(() => false, TimeSpan.FromDays(5 * 365), TimeSpan.FromDays(90),
            () =>
            {
                Log(ColonyLine(earth));
                Log(ColonyLine(lunaColony));
                Log($"  cargo ship: {cargoShip.GetDataBlob<GoalsDB>().ActiveGoal?.Name} {cargoShip.GetDataBlob<GoalsDB>().ActiveGoal?.Message}");
            });
        foreach (var line in AdminLines(lunaColony, setupDone).Distinct().Take(40))
            Log("  luna log: " + line);
        foreach (var line in AdminLines(cargoShip, setupDone).Distinct().Take(40))
            Log("  ship log: " + line);

        long lunaPopEnd = lunaColony.GetDataBlob<ColonyInfoDB>().Population.Values.Sum();
        int lunaInstallsEnd = lunaColony.GetDataBlob<Pulsar4X.Datablobs.ComponentInstancesDB>().AllComponents.Count;
        Log($"END Luna population {lunaPopStart:N0} -> {lunaPopEnd:N0}, installations {lunaInstallsStart} -> {lunaInstallsEnd}");
        Assert.That(lunaInstallsEnd, Is.GreaterThan(lunaInstallsStart), "Luna grew under its administrator and the supply ship");
    }
}
