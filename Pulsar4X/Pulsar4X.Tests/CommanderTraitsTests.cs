using System;
using System.Collections.Generic;
using System.Linq;
using GameEngine.Engine.Orders;
using GameEngine.People;
using NUnit.Framework;
using Pulsar4X.Colonies;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Factions;
using Pulsar4X.Galaxy;
using Pulsar4X.Modding;
using Pulsar4X.Names;
using Pulsar4X.People;

namespace Pulsar4X.Tests;

/// <summary>
/// Vertical slice step 2: people have personality traits (AgentDB) next to their skills. Every new
/// commander rolls them, and a unit's choices follow the traits of the person in charge.
/// </summary>
[TestFixture]
public class CommanderTraitsTests
{
    static Game NewGame()
    {
        var store = new ModDataStore();
        new ModLoader().LoadModManifest("Data/basemod/modInfo.json", store);
        new ModLoader().LoadScenario("Data/scenarios/warp-tech-start/scenarioInfo.json", store);
        var game = new Game(new NewGameSettings { MaxSystems = 1, CreatePlayerFaction = false }, store);
        game.Settings.EnforceSingleThread = true;
        return game;
    }

    static float[] Traits(AgentDB a) => new[] { a.Aggression, a.Caution, a.Curiosity, a.Patience, a.Loyalty, a.Specialization };

    [Test]
    public void Roll_StaysInRange_RoundsToTwentieths_AndRepeatsForASeed()
    {
        var seen = new HashSet<float>();
        for (int seed = 0; seed < 200; seed++)
        {
            var agent = AgentDB.Roll(seed, CommanderTypes.Civilian);
            foreach (float t in Traits(agent))
            {
                Assert.That(t, Is.InRange(AgentDB.Lowest, AgentDB.Highest));
                Assert.That(t * 20, Is.EqualTo(Math.Round(t * 20)).Within(1e-4), "steps of 0.05");
                seen.Add(t);
            }
            Assert.That(Traits(AgentDB.Roll(seed, CommanderTypes.Civilian)), Is.EqualTo(Traits(agent)), "same seed, same person");
        }
        Assert.That(seen.Count, Is.GreaterThan(10), "people differ");
    }

    [Test]
    public void Specialization_IsAnAdministratorsTrait()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            Assert.That(AgentDB.Roll(seed, CommanderTypes.Navy).Specialization, Is.EqualTo(1f));
            Assert.That(AgentDB.Roll(seed, CommanderTypes.Scientist).Specialization, Is.EqualTo(1f));
        }
        Assert.That(Enumerable.Range(0, 50).Any(seed => AgentDB.Roll(seed, CommanderTypes.Civilian).Specialization != 1f), Is.True);
    }

    [Test]
    public void NewCommander_HasTraits_WithoutDrawingOnTheGamesRandomStream()
    {
        var withPerson = NewGame();
        var without = NewGame();
        var faction = FactionFactory.CreateFaction(withPerson, "People");
        FactionFactory.CreateFaction(without, "People");

        var person = CommanderFactory.Create(withPerson.GlobalManager, faction.Id, new CommanderDB("Ada Ito", 1, CommanderTypes.Civilian));

        Assert.That(person.TryGetDataBlob<AgentDB>(out var agent), Is.True);
        Assert.That(Traits(agent), Is.All.InRange(AgentDB.Lowest, AgentDB.Highest));
        Assert.That(withPerson.RNG.Next(), Is.EqualTo(without.RNG.Next()), "rolling traits must not shift seeded outcomes");
    }

    [Test]
    public void Patience_ReplacesGreed_TheImpatientTradeMore()
    {
        Assert.That(new AgentDB { Patience = 0.7f }.TradeDrive, Is.EqualTo(1.3f).Within(1e-6));
        Assert.That(new AgentDB().TradeDrive, Is.EqualTo(1f));
    }

    [Test]
    public void AColonysAdministrator_IsThePersonInCharge()
    {
        var game = NewGame();
        var faction = FactionFactory.CreateFaction(game, "People");
        var system = new StarSystem();
        system.Initialize(game, "Traits", -1);
        var admin = new AdminSpaceDB();
        admin.CommanderSeats.Add(new AdminSpaceAbilityState(AdminLevel.Ship, "bridge"));
        admin.CommanderSeats.Add(new AdminSpaceAbilityState(AdminLevel.Colony, "City Hall"));
        var colony = Entity.Create(faction.Id);
        system.AddEntity(colony, new List<BaseDataBlob> { new ColonyInfoDB(), new NameDB("Traits Colony", faction.Id, "Traits Colony"), admin, });

        Assert.That(CommanderSkills.TryGetCommander(colony, out _, out _), Is.False, "an empty chair");

        var person = CommanderFactory.Create(system, faction.Id, new CommanderDB("Ada Ito", 1, CommanderTypes.Civilian));
        person.GetDataBlob<AgentDB>().Patience = 0.5f;
        admin.CommanderSeats[1].CommanderID = person.Id;

        Assert.That(CommanderSkills.TryGetCommander(colony, out var found, out _), Is.True);
        Assert.That(found.Id, Is.EqualTo(person.Id), "the colony seat, not the bridge");
    }
}
