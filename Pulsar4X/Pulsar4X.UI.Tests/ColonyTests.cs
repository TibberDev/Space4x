using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.UI.Core.Colonies;
using Pulsar4X.UI.Core.ViewModels;

namespace Pulsar4X.UI.Tests;

/// <summary>The colony screen against the real starting colony. These tests queue and cancel real
/// jobs on the shared game; each one cleans up what it adds.</summary>
[TestFixture, NonParallelizable]
public class ColonyTests
{
    private MainWindowViewModel _vm = null!;
    private ColoniesViewModel Colonies => _vm.Colonies;
    private static IClientGalaxy Galaxy => SharedGame.Client.Galaxy;

    [SetUp]
    public void SetUp()
    {
        _vm = new MainWindowViewModel(new SharedGame.Launcher());
        _vm.AttachClient(SharedGame.Client);
        _vm.SelectedTab = MainWindowViewModel.ColoniesTab;
        Refresh();
        Assert.That(Colonies.SelectedColony, Is.Not.Null, "no colony was selected");
    }

    /// <summary>Applies pushed updates and refreshes the screen, like one frame.</summary>
    private void Refresh() => _vm.Tick();

    private ProductionLineViewModel Line(string name)
        => Colonies.Production.Lines.FirstOrDefault(l => l.Name == name)
           ?? throw new AssertionException($"no {name} line; lines: {string.Join(", ", Colonies.Production.Lines.Select(l => l.Name))}");

    [Test]
    public void Lists_the_starting_colony_with_its_summary()
    {
        var colony = Colonies.Colonies.Single();
        Assert.That(colony.IsOwned, Is.True);
        Assert.That(colony.BodyName, Is.EqualTo("Earth"));
        Assert.That(Colonies.Title, Is.EqualTo(colony.Name));
        Assert.That(Colonies.Subtitle, Is.EqualTo("Earth · Sol"));

        var summary = Colonies.Summary;
        Assert.That(summary.People.First(r => r.Label == "Population").Value, Is.EqualTo("500,000"));
        Assert.That(summary.HasInfrastructure && summary.HasPower, Is.True);
        Assert.That(summary.Infrastructure.Select(r => r.Label), Does.Contain("Efficiency"));
        Assert.That(summary.Power.Select(r => r.Label), Does.Contain("Generation").And.Contain("Balance"));
        Assert.That(summary.Industry.Select(r => r.Label), Does.Contain("Factory").And.Contain("Construction"));
        Assert.That(summary.Installations.Select(i => i.Name), Does.Contain("Factory").And.Contain("Mine"));
    }

    [Test]
    public void Stockpile_groups_identical_items()
    {
        var general = Colonies.Summary.Stores.First(s => s.Items.Any(i => i.Name == "Iron"));
        Assert.That(general.Items.Select(i => i.Key), Is.Unique, "each good should be one row");
        Assert.That(general.Items.First(i => i.Name == "Iron").Units, Is.GreaterThan(0));
        Assert.That(general.FillPercent, Is.InRange(0, 100));
        Assert.That(general.CapacityText, Does.Contain(" of "));
    }

    [Test]
    public void New_job_form_previews_costs_for_the_quantity()
    {
        var production = Colonies.Production;
        production.SelectedLine = production.LineOptions.First(l => l.Name == "Factory");
        var option = production.Options.First(o => o.IsAvailable);
        production.SelectedOption = option;
        production.Quantity = 3;

        Assert.That(production.Costs, Is.Not.Empty, $"{option.Name} has no costs");
        foreach (var cost in production.Costs)
            Assert.That(cost.Total, Is.EqualTo(cost.PerUnit * 3), cost.Name);
        Assert.That(production.TotalPointsText, Does.Contain(" total"));
        Assert.That(production.QueueJobCommand.CanExecute(null), Is.True);
    }

    [Test]
    public void Search_filters_the_options_and_keeps_the_selection()
    {
        var production = Colonies.Production;
        production.SelectedLine = production.LineOptions.First(l => l.Name == "Factory");
        int all = production.Options.Count;
        var pick = production.Options.First();
        production.SelectedOption = pick;

        production.Search = pick.Name[..Math.Min(4, pick.Name.Length)];
        Assert.That(production.Options.Count, Is.LessThan(all).Or.EqualTo(all));
        Assert.That(production.Options, Has.All.Matches<ConstructibleOption>(
            o => o.Name.Contains(production.Search, StringComparison.CurrentCultureIgnoreCase)
                 || o.Category.Contains(production.Search, StringComparison.CurrentCultureIgnoreCase)));
        Assert.That(production.SelectedOption?.DesignId, Is.EqualTo(pick.DesignId));

        // A refresh from new data must not drop the selection.
        Refresh();
        Assert.That(production.SelectedOption?.DesignId, Is.EqualTo(pick.DesignId));
        production.Search = "";
    }

    [Test]
    public async Task Queue_reorder_and_cancel_a_production_job()
    {
        var production = Colonies.Production;
        production.SelectedLine = production.LineOptions.First(l => l.Name == "Factory");
        var options = production.Options.Where(o => o.IsAvailable).Take(2).ToList();
        int before = Line("Factory").Jobs.Count;

        foreach (var option in options)
        {
            production.SelectedOption = option;
            production.Quantity = 2;
            await production.QueueJobCommand.ExecuteAsync(null);
            Assert.That(production.Result.IsError, Is.False, production.Result.Message);
            Assert.That(production.Result.Message, Does.Contain(option.Name));
        }
        Refresh();

        var jobs = Line("Factory").Jobs;
        Assert.That(jobs.Count, Is.EqualTo(before + 2), "both jobs should be on the factory queue");
        var last = jobs[^1];
        Assert.That(last.Name, Is.EqualTo(options[1].Name));
        Assert.That(last.Ordered, Is.EqualTo(2));

        await Line("Factory").MoveUpCommand.ExecuteAsync(last);
        Refresh();
        Assert.That(Line("Factory").Jobs[^2].JobId, Is.EqualTo(last.JobId), "move up should swap it with the job above");

        foreach (var job in Line("Factory").Jobs.Where(j => options.Any(o => o.Name == j.Name)).ToList())
            await Line("Factory").CancelCommand.ExecuteAsync(job);
        Refresh();
        Assert.That(Line("Factory").Jobs.Count, Is.EqualTo(before), "cancel should remove the jobs");
    }

    [Test]
    public async Task Assemble_and_remove_a_building_on_site()
    {
        var construction = Colonies.Construction;
        var site = construction.SiteDesigns.First();
        int before = construction.Queue.Count;

        await construction.AssembleCommand.ExecuteAsync(site);
        Refresh();
        Assert.That(construction.Result.IsError, Is.False, construction.Result.Message);
        Assert.That(construction.Queue.Count, Is.EqualTo(before + 1));
        var job = construction.Queue[^1];
        Assert.That(job.Name, Is.EqualTo(site.Name));
        Assert.That(job.PointCost, Is.GreaterThan(0));

        await construction.RemoveCommand.ExecuteAsync(job);
        Refresh();
        Assert.That(construction.Result.IsError, Is.False, construction.Result.Message);
        Assert.That(construction.Queue.Count, Is.EqualTo(before),
            "after remove: " + string.Join(", ", construction.Queue.Select(q => $"{q.Index}:{q.Name}")) + " | " + construction.Result.Message);
    }

    [Test]
    public async Task Reorder_the_construction_queue_while_paused()
    {
        // Regression: Move/Remove construction orders used to be marked done without running.
        var construction = Colonies.Construction;
        var sites = construction.SiteDesigns.Take(2).ToList();
        int before = construction.Queue.Count;
        foreach (var site in sites)
            await construction.AssembleCommand.ExecuteAsync(site);
        Refresh();
        Assert.That(construction.Queue.Skip(before).Select(q => q.Name), Is.EqualTo(sites.Select(s => s.Name)));

        await construction.MoveUpCommand.ExecuteAsync(construction.Queue[^1]);
        Refresh();
        Assert.That(construction.Queue.Skip(before).Select(q => q.Name), Is.EqualTo(sites.Select(s => s.Name).Reverse()),
            construction.Result.Message);

        await construction.MoveDownCommand.ExecuteAsync(construction.Queue[before]);
        Refresh();
        Assert.That(construction.Queue.Skip(before).Select(q => q.Name), Is.EqualTo(sites.Select(s => s.Name)));

        while (construction.Queue.Count > before)
        {
            await construction.RemoveCommand.ExecuteAsync(construction.Queue[^1]);
            Refresh();
        }
        Assert.That(construction.Queue.Count, Is.EqualTo(before));
    }

    [Test]
    public void Mining_lists_minerals_with_output()
    {
        Assert.That(Colonies.MinesText, Does.Match(@"^\d+ mines?$"));
        Assert.That(Colonies.Minerals, Is.Not.Empty);
        Assert.That(Colonies.Minerals.Select(m => m.MineralId), Is.Unique);
    }

    [Test]
    public void Open_colony_from_the_map_selection()
    {
        _vm.SelectedTab = MainWindowViewModel.MapTab;
        for (int i = 0; i < 50 && _vm.Map.Objects.Count == 0; i++) { Refresh(); Thread.Sleep(20); }
        var earth = _vm.Map.Objects.First(o => o.Name == "Earth");

        _vm.Map.Select(earth.Id);
        Assert.That(_vm.SelectedBodyColonyId, Is.Not.Null, "Earth's colony should be offered");
        Assert.That(_vm.OpenColonyCommand.CanExecute(null), Is.True);

        _vm.OpenColonyCommand.Execute(null);
        Assert.That(_vm.SelectedTab, Is.EqualTo(MainWindowViewModel.ColoniesTab));
        Assert.That(_vm.ShowSelectionPanel, Is.False, "the body panel belongs to the map");
        Assert.That(Colonies.SelectedColony?.BodyName, Is.EqualTo("Earth"));

        // A body without a colony offers nothing.
        _vm.SelectedTab = MainWindowViewModel.MapTab;
        _vm.Map.Select(_vm.Map.Objects.First(o => o.Name == "Mars").Id);
        Assert.That(_vm.SelectedBodyColonyId, Is.Null);
    }
}
