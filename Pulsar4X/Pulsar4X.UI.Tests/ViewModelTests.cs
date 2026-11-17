using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.UI.Core.ViewModels;

namespace Pulsar4X.UI.Tests;

/// <summary>The engine → API → view model path, against a real quickstarted game.</summary>
[TestFixture, NonParallelizable]
public class ViewModelTests
{
    private static MainWindowViewModel AttachedViewModel()
    {
        var vm = new MainWindowViewModel(new SharedGame.Launcher());
        vm.AttachClient(SharedGame.Client);
        return vm;
    }

    /// <summary>Ticks the view model like the frame timer does until <paramref name="condition"/> holds.</summary>
    private static bool TickUntil(MainWindowViewModel vm, Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            vm.Tick();
            if (condition()) return true;
            Thread.Sleep(20);
        }
        vm.Tick();
        return condition();
    }

    [Test]
    public void Quickstart_gives_a_connected_client_with_a_faction_and_known_systems()
    {
        var client = SharedGame.Client;
        client.Update();

        Assert.That(client.IsConnected, Is.True);
        Assert.That(client.Galaxy.KnownSystems, Is.Not.Empty);
        Assert.That(client.Galaxy.Faction, Is.Not.Null);
        Assert.That(SharedGame.Host.Game, Is.Not.Null);
    }

    [Test]
    public void Attaching_fills_faction_systems_and_bodies()
    {
        var vm = AttachedViewModel();

        Assert.That(vm.HasGame, Is.True);
        Assert.That(vm.ShowStartScreen, Is.False);
        Assert.That(vm.FactionName, Is.Not.Empty);
        Assert.That(vm.FundsText, Does.EndWith(" cr"));
        Assert.That(vm.Systems, Is.Not.Empty);
        Assert.That(vm.SelectedSystem, Is.Not.Null);
        Assert.That(vm.Time.DateText, Is.Not.EqualTo("—"));

        Assert.That(TickUntil(vm, () => vm.Bodies.Count > 0, TimeSpan.FromSeconds(5)), Is.True,
            "the selected system's bodies never arrived");
        Assert.That(vm.Bodies.Any(b => b.Kind == BodyKind.Star && b.Depth == 0), Is.True, "no star at the root");
        Assert.That(vm.Bodies.Any(b => b.Kind == BodyKind.Planet && b.Depth >= 1), Is.True, "no planet under a star");
        Assert.That(vm.BodiesHeader, Does.Contain(vm.SelectedSystem!.Name));
    }

    [Test]
    public void Bodies_are_listed_under_their_parent()
    {
        var vm = AttachedViewModel();
        TickUntil(vm, () => vm.Bodies.Count > 0, TimeSpan.FromSeconds(5));

        // Each non-root row must come after a row it orbits, one level shallower.
        for (int i = 0; i < vm.Bodies.Count; i++)
        {
            var row = vm.Bodies[i];
            if (row.Depth == 0) continue;
            var parent = vm.Bodies.Take(i).LastOrDefault(r => r.Depth == row.Depth - 1);
            Assert.That(parent, Is.Not.Null, $"{row.Name} has no parent row above it");
            Assert.That(parent!.Name, Is.EqualTo(row.ParentName), $"{row.Name} is listed under the wrong body");
        }
    }

    [Test]
    public void Asteroids_and_comets_are_hidden_until_toggled_on()
    {
        var vm = AttachedViewModel();
        TickUntil(vm, () => vm.Bodies.Count > 0, TimeSpan.FromSeconds(5));

        Assert.That(vm.ShowMinorBodies, Is.False);
        Assert.That(vm.Bodies.Any(b => b.Kind is BodyKind.Asteroid or BodyKind.Comet), Is.False);
        int majorCount = vm.Bodies.Count;

        vm.ShowMinorBodies = true;
        Assert.That(vm.Bodies.Count, Is.GreaterThanOrEqualTo(majorCount));
        Assert.That(vm.BodiesHeader, Does.Contain($"{vm.Bodies.Count} bodies").And.Not.Contain(" of "));

        vm.ShowMinorBodies = false;
        Assert.That(vm.Bodies.Count, Is.EqualTo(majorCount));
    }

    [Test]
    public void Selecting_another_system_switches_the_body_table()
    {
        var vm = AttachedViewModel();
        if (vm.Systems.Count < 2)
            Assert.Ignore("The default scenario only reveals one system.");

        var other = vm.Systems.First(s => s.SystemId != vm.SelectedSystem!.SystemId);
        vm.SelectedSystem = other;
        TickUntil(vm, () => vm.BodiesHeader.StartsWith(other.Name), TimeSpan.FromSeconds(5));

        Assert.That(vm.BodiesHeader, Does.StartWith(other.Name));
    }

    [Test]
    public async Task Step_advances_the_date_by_the_step_size()
    {
        var vm = AttachedViewModel();
        Assume.That(vm.Time.IsRunning, Is.False);

        vm.Time.SelectedStepSize = TimeControlViewModel.StepSizes.First(s => s.Length == TimeSpan.FromDays(1));
        var before = vm.Time.GameDateTime;

        Assert.That(vm.Time.StepCommand.CanExecute(null), Is.True);
        await vm.Time.StepCommand.ExecuteAsync(null);

        Assert.That(TickUntil(vm, () => vm.Time.GameDateTime > before, TimeSpan.FromSeconds(30)), Is.True,
            "the date did not move after a step");
        Assert.That(vm.Time.GameDateTime - before, Is.EqualTo(TimeSpan.FromDays(1)));
    }

    [Test]
    public async Task Play_runs_the_clock_and_pause_stops_it()
    {
        var vm = AttachedViewModel();
        vm.Time.SelectedStepSize = TimeControlViewModel.StepSizes.First(s => s.Length == TimeSpan.FromHours(1));
        var before = vm.Time.GameDateTime;

        await vm.Time.PlayPauseCommand.ExecuteAsync(null);
        try
        {
            Assert.That(TickUntil(vm, () => vm.Time.IsRunning, TimeSpan.FromSeconds(5)), Is.True, "clock never started");
            Assert.That(vm.Time.StepCommand.CanExecute(null), Is.False, "step should be disabled while running");
            Assert.That(TickUntil(vm, () => vm.Time.GameDateTime > before, TimeSpan.FromSeconds(20)), Is.True,
                "the date did not advance while running");
        }
        finally
        {
            if (vm.Time.IsRunning)
                await vm.Time.PlayPauseCommand.ExecuteAsync(null);
            SharedGame.Host.Game!.TimePulse.WaitUntilStopped();
        }

        Assert.That(TickUntil(vm, () => !vm.Time.IsRunning, TimeSpan.FromSeconds(10)), Is.True, "clock never paused");
    }

    [Test]
    public async Task A_failed_start_shows_the_error_and_keeps_the_start_screen()
    {
        var vm = new MainWindowViewModel(new SharedGame.FailingLauncher());

        await vm.StartGameCommand.ExecuteAsync(null);

        Assert.That(vm.HasGame, Is.False);
        Assert.That(vm.ShowStartScreen, Is.True);
        Assert.That(vm.ErrorText, Is.EqualTo("No game data found."));
        Assert.That(vm.StartGameCommand.CanExecute(null), Is.True, "should be able to retry");
    }
}
