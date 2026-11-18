using System;
using System.Linq;
using GameEngine.Engine.Orders;
using GameEngine.People;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.DataStructures;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Factions;
using Pulsar4X.Galaxy;
using Pulsar4X.Industry;
using Pulsar4X.Modding;
using Pulsar4X.Names;
using Pulsar4X.People;

namespace Pulsar4X.Tests;

/// <summary>
/// Vertical slice step 3: a colony's administrator reviews the colony, proposes (Advise) or does (Auto)
/// construction and production, and logs why. Traits shape the choices; Construction skill sets how often.
/// </summary>
[TestFixture]
public class ColonyAutomationTests
{
    Game _game = null!;
    StarSystem _sol = null!;
    Entity _player = null!;
    Entity _earth = null!;

    [SetUp]
    public void SetUp()
    {
        var store = new ModDataStore();
        new ModLoader().LoadModManifest("Data/basemod/modInfo.json", store);
        new ModLoader().LoadScenario("Data/scenarios/warp-tech-start/scenarioInfo.json", store);
        _game = new Game(new NewGameSettings { MaxSystems = 1, CreatePlayerFaction = false }, store);
        _game.Settings.EnforceSingleThread = true;
        StarSystemFactory.LoadFromBlueprint(_game, store.Systems["system-sol"]);
        _sol = _game.Systems.Single(s => s.ID == "system-sol");
        _player = FactionFactory.CreateBasicFaction(_game, "United Earth Corp", "UEC", 0);
        _player.FactionOwnerID = _player.Id;
        var species = SpeciesFactory.CreateFromBlueprint(_sol, store.Species["species-human"]);
        species.FactionOwnerID = _player.Id;
        _player.GetDataBlob<FactionInfoDB>().Species.Add(species);
        _earth = ColonyFactory.CreateFromBlueprint(_game, _player, species, _sol,
            NameLookup.GetFirstEntityWithName(_sol, "Earth"), store.Colonies["colony-earth"]);
        _earth.GetDataBlob<LocalConstructionDB>().BuildQueue.Clear();
    }

    DateTime Now => _earth.StarSysDateTime;
    ColonyAutomationDB Automation => _earth.GetDataBlob<ColonyAutomationDB>();
    InfrastructureDB Infra => _earth.GetDataBlob<InfrastructureDB>();

    /// <summary>Seats a civilian in Earth's colony chair with the given traits.</summary>
    AgentDB SeatAdministrator(float patience = 1f, float loyalty = 1f, float specialization = 1f)
    {
        var person = CommanderFactory.Create(_sol, _player.Id, new CommanderDB("Ada Ito", 1, CommanderTypes.Civilian));
        var agent = person.GetDataBlob<AgentDB>();
        agent.Patience = patience;
        agent.Loyalty = loyalty;
        agent.Specialization = specialization;
        var seat = _earth.GetDataBlob<AdminSpaceDB>().CommanderSeats.First(s => s.SeatType >= AdminLevel.Colony);
        seat.CommanderID = person.Id;
        return agent;
    }

    /// <summary>Only Infrastructure is short: infrastructure needed is twice what is built.</summary>
    void ShortOfInfrastructureOnly()
    {
        Infra.CapacityProvided = 1000;
        Infra.CapacityRequired = 2000;
        var power = _earth.GetDataBlob<ColonyPowerDB>();
        power.Generation_kW = 1e9;
        power.Demand_kW = 1;
    }

    [Test]
    public void AVacantChair_AutomatesNothing()
    {
        ShortOfInfrastructureOnly();
        Assert.That(ColonyAdministration.Review(_earth, Now), Is.Zero);
        Assert.That(Automation.Suggestions, Is.Empty);
        Assert.That(Automation.NextReview, Is.EqualTo(Now + TimeSpan.FromDays(1)), "looks again tomorrow");
    }

    [Test]
    public void Advise_ProposesWithAReason_AndApproveQueuesIt()
    {
        SeatAdministrator();
        ShortOfInfrastructureOnly();
        Assert.That(Automation.Construction, Is.EqualTo(AutomationLevel.Advise), "the default");

        ColonyAdministration.Review(_earth, Now);

        var suggestion = Automation.Suggestions.Single();
        Assert.That(suggestion.Area, Is.EqualTo(AutomationArea.Construction));
        Assert.That(suggestion.Text, Does.StartWith("Build ").And.Contain("Infrastructure"));
        Assert.That(suggestion.Reason, Does.Contain("1,000 of 2,000"));
        Assert.That(suggestion.Proposer, Is.EqualTo("Ada Ito"));
        var queue = _earth.GetDataBlob<LocalConstructionDB>().BuildQueue;
        Assert.That(queue, Is.Empty, "Advise does nothing on its own");

        Assert.That(ColonyAdministration.Approve(_earth, suggestion.Id, Now, out var reason), Is.True, reason);
        Assert.That(queue.Single().Design.UniqueID, Is.EqualTo(suggestion.DesignId));
        Assert.That(Automation.Suggestions, Is.Empty);
        Assert.That(_earth.GetDataBlob<ShipLogDB>().Entries.Last().Text, Does.StartWith("Approved: Build"));
    }

    [Test]
    public void Dismiss_KeepsItOffTheListFor30Days()
    {
        SeatAdministrator();
        ShortOfInfrastructureOnly();
        ColonyAdministration.Review(_earth, Now);
        var suggestion = Automation.Suggestions.Single();

        Assert.That(ColonyAdministration.Dismiss(_earth, suggestion.Id, Now), Is.True);
        ColonyAdministration.Review(_earth, Now + TimeSpan.FromDays(7));
        Assert.That(Automation.Suggestions, Is.Empty, "dismissed");
        ColonyAdministration.Review(_earth, Now + TimeSpan.FromDays(31));
        Assert.That(Automation.Suggestions, Has.Count.EqualTo(1), "back after 30 days");
    }

    [Test]
    public void Auto_ActsAtOnce_AndLogsWhy()
    {
        SeatAdministrator();
        ShortOfInfrastructureOnly();
        Automation.Construction = AutomationLevel.Auto;

        Assert.That(ColonyAdministration.Review(_earth, Now), Is.EqualTo(1));

        Assert.That(Automation.Suggestions, Is.Empty);
        Assert.That(_earth.GetDataBlob<LocalConstructionDB>().BuildQueue, Has.Count.EqualTo(1));
        var line = _earth.GetDataBlob<ShipLogDB>().Entries.Last();
        Assert.That(line.Kind, Is.EqualTo(ShipLogKind.Administrator));
        Assert.That(line.Text, Does.StartWith("Ada Ito: Build").And.Contain("Infrastructure 1,000 of 2,000"));
    }

    [Test]
    public void Patience_SetsTheSpareMargin()
    {
        Assert.That(ColonyAdministration.Margin(new AgentDB { Patience = 0.5f }), Is.EqualTo(0).Within(1e-6));
        Assert.That(ColonyAdministration.Margin(new AgentDB { Patience = 1f }), Is.EqualTo(0.2).Within(1e-6));
        Assert.That(ColonyAdministration.Margin(new AgentDB { Patience = 1.5f }), Is.EqualTo(0.4).Within(1e-6));

        // 10% spare: enough for the impatient, not for the patient.
        ShortOfInfrastructureOnly();
        Infra.CapacityProvided = 2200;
        var agent = SeatAdministrator(patience: 0.5f);
        ColonyAdministration.Review(_earth, Now);
        Assert.That(Automation.Suggestions, Is.Empty, "the impatient build only once short");

        agent.Patience = 1.5f;
        ColonyAdministration.Review(_earth, Now);
        Assert.That(Automation.Suggestions.Single().Reason, Does.Contain("wants 40% spare"));
    }

    [Test]
    public void Loyalty_DecidesWhoseOrderComesFirst()
    {
        // Power is the player's first priority and a little short; Infrastructure is badly short.
        Infra.CapacityProvided = 1000;
        Infra.CapacityRequired = 3000;
        var power = _earth.GetDataBlob<ColonyPowerDB>();
        power.Demand_kW = 1000;
        power.Generation_kW = 1100 / power.GridEfficiency; // 10% spare, under the 20% an average administrator wants
        Automation.Priorities = new() { ColonyNeed.Power, ColonyNeed.Infrastructure, ColonyNeed.Storage, ColonyNeed.Mines };

        var agent = SeatAdministrator(loyalty: 1.4f);
        ColonyAdministration.Review(_earth, Now);
        Assert.That(Automation.Suggestions.Single().Key, Is.EqualTo("build|Power"), "the loyal follow your order");

        agent.Loyalty = 0.6f;
        ColonyAdministration.Review(_earth, Now);
        var own = Automation.Suggestions.Single();
        Assert.That(own.Key, Is.EqualTo("build|Infrastructure"), "the disloyal follow their own urgency");
        Assert.That(own.Reason, Does.Contain("Put ahead of power, your first priority"));
    }

    [Test]
    public void Specialization_SetsHowManyItemsTheFactoryWorks()
    {
        var info = _player.GetDataBlob<FactionInfoDB>();
        var industry = _earth.GetDataBlob<IndustryAbilityDB>();
        var makeable = info.IndustryDesigns.Values
            .Where(d => !string.IsNullOrEmpty(d.IndustryTypeID)
                        && industry.ProductionLines.Values.Any(l => l.IndustryTypeRates.ContainsKey(d.IndustryTypeID)))
            .Take(3).ToList();
        Assert.That(makeable, Has.Count.EqualTo(3));
        foreach (var design in makeable)
            Automation.StockTargets[design.UniqueID] = 5;
        Automation.Construction = AutomationLevel.Manual;

        var agent = SeatAdministrator(specialization: 1.4f);
        ColonyAdministration.Review(_earth, Now);
        Assert.That(Automation.Suggestions, Has.Count.EqualTo(1), "a specialist works one item");
        Assert.That(Automation.Suggestions[0].Text, Does.StartWith("Make 5 ×"));

        agent.Specialization = 1f;
        ColonyAdministration.Review(_earth, Now);
        Assert.That(Automation.Suggestions, Has.Count.EqualTo(2));

        agent.Specialization = 0.6f;
        ColonyAdministration.Review(_earth, Now);
        Assert.That(Automation.Suggestions, Has.Count.EqualTo(3), "a generalist spreads the work");
    }

    [Test]
    public void ConstructionSkill_SetsHowOftenTheyReview()
    {
        var weekly = new CommanderDB("New", 1, CommanderTypes.Civilian);
        Assert.That(ColonyAdministration.ReviewEveryDays(weekly), Is.EqualTo(7));
        var expert = new CommanderDB("Old hand", 1, CommanderTypes.Civilian);
        CommanderSkills.Grant(expert, SkillDomain.Construction, 100);
        Assert.That(ColonyAdministration.ReviewEveryDays(expert), Is.EqualTo(1));

        SeatAdministrator();
        ColonyAdministration.Review(_earth, Now);
        Assert.That(Automation.NextReview, Is.EqualTo(Now + TimeSpan.FromDays(7)));
    }

    [Test]
    public void Manual_ProposesNothing()
    {
        SeatAdministrator();
        ShortOfInfrastructureOnly();
        Automation.Construction = AutomationLevel.Manual;
        Automation.Production = AutomationLevel.Manual;
        Assert.That(ColonyAdministration.Review(_earth, Now), Is.Zero);
        Assert.That(Automation.Suggestions, Is.Empty);
    }

    [Test]
    public void TheDailyProcessor_ReviewsWhenDue()
    {
        SeatAdministrator();
        ShortOfInfrastructureOnly();
        var processor = new ColonyAdministratorProcessor();
        processor.ProcessEntity(_earth, 0);
        Assert.That(Automation.Suggestions, Has.Count.EqualTo(1));
        Automation.Suggestions.Clear();
        processor.ProcessEntity(_earth, 0);
        Assert.That(Automation.Suggestions, Is.Empty, "not due again for a week");
    }
}
