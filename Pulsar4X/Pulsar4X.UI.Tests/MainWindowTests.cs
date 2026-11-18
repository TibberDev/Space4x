using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;
using Pulsar4X.UI.Core;
using Pulsar4X.UI.Core.ViewModels;
using Pulsar4X.UI.Core.Colonies;
using Pulsar4X.UI.Core.People;
using Pulsar4X.UI.Desktop.Views;

namespace Pulsar4X.UI.Tests;

/// <summary>
/// The real window, themes and bindings on the headless platform. Each test also saves what the
/// window looked like to bin/.../Screenshots so a screen can be checked by eye without running the app.
/// </summary>
[NonParallelizable]
public class MainWindowTests
{
    private static readonly string ScreenshotDir = Path.Combine(AppContext.BaseDirectory, "Screenshots");

    private static (MainWindow Window, MainWindowViewModel Vm) Open(IGameLauncher launcher)
    {
        var vm = new MainWindowViewModel(launcher);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Pump();
        return (window, vm);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static string Capture(Window window, string name)
    {
        Pump();
        Directory.CreateDirectory(ScreenshotDir);
        var path = Path.Combine(ScreenshotDir, name + ".png");
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("No frame was rendered.");
        frame.Save(path);
        TestContext.Out.WriteLine($"Screenshot: {path}");
        return path;
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
           ?? throw new AssertionException($"No {typeof(T).Name} named '{name}' in the window.");

    [AvaloniaTest]
    public void Start_screen_shows_new_game_and_hides_the_game_view()
    {
        var (window, _) = Open(new SharedGame.Launcher());

        Assert.That(Find<Button>(window, "NewGameButton").IsEffectivelyVisible, Is.True);
        Assert.That(Find<Button>(window, "NewGameButton").IsEnabled, Is.True);
        Assert.That(window.GetVisualDescendants().OfType<ListBox>().Any(l => l.Name == "SystemList" && l.IsEffectivelyVisible),
            Is.False, "the system list should be hidden before a game starts");

        Assert.That(new FileInfo(Capture(window, "01-start-screen")).Length, Is.GreaterThan(0));
        window.Close();
    }

    [AvaloniaTest]
    public async Task New_game_shows_the_clock_systems_and_bodies()
    {
        var (window, vm) = Open(new SharedGame.Launcher());

        await vm.StartGameCommand.ExecuteAsync(null);
        for (int i = 0; i < 50 && vm.Bodies.Count == 0; i++)
        {
            vm.Tick();
            Pump();
            await Task.Delay(20);
        }
        vm.Tick();
        Pump();

        Assert.That(vm.HasGame, Is.True);
        Assert.That(Find<Button>(window, "NewGameButton").IsEffectivelyVisible, Is.False);
        Assert.That(Find<TextBlock>(window, "DateText").Text, Is.EqualTo(vm.Time.DateText));
        Assert.That(Find<TextBlock>(window, "FactionNameText").Text, Is.Not.Empty);
        Assert.That(Find<Button>(window, "PlayPauseButton").IsEffectivelyEnabled, Is.True);

        var systemList = Find<ListBox>(window, "SystemList");
        Assert.That(systemList.ItemCount, Is.EqualTo(vm.Systems.Count).And.GreaterThan(0));
        Assert.That(systemList.SelectedItem, Is.SameAs(vm.SelectedSystem));

        // The map is the default tab; the body table is the second.
        Assert.That(Find<TabControl>(window, "MainTabs").SelectedIndex, Is.EqualTo(0));
        vm.SelectedTab = 1;
        Pump();

        var grid = Find<DataGrid>(window, "BodiesGrid");
        Assert.That(grid.IsEffectivelyVisible, Is.True);
        Assert.That(grid.GetVisualDescendants().OfType<DataGridRow>().Count(), Is.GreaterThan(0),
            "the body table rendered no rows");

        Capture(window, "02-bodies-table");
        window.Close();
    }

    private static async Task<(MainWindow Window, MainWindowViewModel Vm)> OpenRunningGame()
    {
        var (window, vm) = Open(new SharedGame.Launcher());
        await vm.StartGameCommand.ExecuteAsync(null);
        for (int i = 0; i < 100 && vm.Map.Objects.Count == 0; i++)
        {
            vm.Tick();
            Pump();
            await Task.Delay(20);
        }
        vm.Tick();
        Pump();
        return (window, vm);
    }

    [AvaloniaTest]
    public async Task Map_tab_draws_the_system_with_scale_bar()
    {
        var (window, vm) = await OpenRunningGame();

        var map = Find<Pulsar4X.UI.Desktop.Controls.StarMapControl>(window, "StarMap");
        Assert.That(map.IsEffectivelyVisible, Is.True);
        Assert.That(map.Bounds.Width, Is.GreaterThan(400));
        Assert.That(vm.Map.Camera.ViewportWidth, Is.EqualTo(map.Bounds.Width).Within(0.5), "the map camera should match the control size");
        Assert.That(Find<TextBlock>(window, "ScaleText").Text, Does.EndWith("AU"));
        Assert.That(Find<Border>(window, "SelectionPanel").IsEffectivelyVisible, Is.False);

        Capture(window, "04-map-system");
        window.Close();
    }

    [AvaloniaTest]
    public async Task Clicking_a_planet_on_the_map_opens_its_panel()
    {
        var (window, vm) = await OpenRunningGame();
        var map = Find<Pulsar4X.UI.Desktop.Controls.StarMapControl>(window, "StarMap");

        var earth = vm.Map.Objects.First(o => o.Name == "Earth");
        var s = vm.Map.Camera.WorldToScreen(earth.Position);
        var inWindow = map.TranslatePoint(new Avalonia.Point(s.X, s.Y), window)!.Value;
        window.MouseDown(inWindow, Avalonia.Input.MouseButton.Left);
        window.MouseUp(inWindow, Avalonia.Input.MouseButton.Left);
        vm.Tick();
        Pump();

        Assert.That(vm.Map.SelectedId, Is.EqualTo(earth.Id), "a real click on Earth should select it");
        Assert.That(Find<Border>(window, "SelectionPanel").IsEffectivelyVisible, Is.True);
        Assert.That(Find<TextBlock>(window, "SelectionTitle").Text, Is.EqualTo("Earth"));
        Capture(window, "05-map-earth-selected");

        // Focus on Earth: Luna and its orbit come into view.
        vm.Map.Focus(earth);
        vm.Tick();
        Pump();
        Assert.That(vm.Map.IsVisible(vm.Map.Objects.First(o => o.Name == "Luna")), Is.True);
        Capture(window, "06-map-earth-focused");

        // Drag pans the map and stops following.
        var centreBefore = vm.Map.Camera.Center;
        var start = map.TranslatePoint(new Avalonia.Point(300, 300), window)!.Value;
        window.MouseDown(start, Avalonia.Input.MouseButton.Left);
        window.MouseMove(start + new Avalonia.Point(60, 0));
        window.MouseMove(start + new Avalonia.Point(120, 0));
        window.MouseUp(start + new Avalonia.Point(120, 0), Avalonia.Input.MouseButton.Left);
        Assert.That(vm.Map.Camera.Center, Is.Not.EqualTo(centreBefore));
        Assert.That(vm.Map.IsFollowing, Is.False);
        Assert.That(vm.Map.SelectedId, Is.EqualTo(earth.Id), "a drag must not change the selection");

        // Mouse wheel zooms.
        double before = vm.Map.Camera.MetresPerPixel;
        window.MouseWheel(start, new Avalonia.Vector(0, 2));
        Assert.That(vm.Map.Camera.MetresPerPixel, Is.LessThan(before));
        window.Close();
    }

    [AvaloniaTest]
    public async Task Colony_screen_renders_every_section()
    {
        var (window, vm) = await OpenRunningGame();
        window.Height = 900;
        vm.SelectedTab = MainWindowViewModel.ColoniesTab;
        vm.Tick();
        Pump();

        var colonies = vm.Colonies;
        Assert.That(Find<ListBox>(window, "ColonyList").ItemCount, Is.EqualTo(colonies.Colonies.Count).And.GreaterThan(0));
        Assert.That(Find<TextBlock>(window, "ColonyTitle").Text, Is.EqualTo(colonies.Title));
        Assert.That(Find<ItemsControl>(window, "InstallationsList").ItemCount, Is.GreaterThan(0));
        Assert.That(Find<Border>(window, "SelectionPanel").IsEffectivelyVisible, Is.False);
        Assert.That(window.GetVisualDescendants().OfType<ListBox>().Any(l => l.Name == "SystemList" && l.IsEffectivelyVisible),
            Is.False, "the known-systems list is hidden on the colony screen");
        Capture(window, "07-colony-summary");

        colonies.SelectedSection = 1;
        var production = colonies.Production;
        production.SelectedLine = production.LineOptions.First(l => l.Name == "Factory");
        production.SelectedOption = production.Options.First(o => o.Name.Contains("Cargo")) ;
        production.Quantity = 4;
        Pump();
        Assert.That(Find<ItemsControl>(window, "ProductionLines").ItemCount, Is.EqualTo(production.Lines.Count));
        Assert.That(Find<ItemsControl>(window, "CostList").ItemCount, Is.EqualTo(production.Costs.Count).And.GreaterThan(0));
        Assert.That(Find<Button>(window, "QueueButton").IsEffectivelyEnabled, Is.True);
        Capture(window, "08-colony-production");

        colonies.SelectedSection = 2;
        Pump();
        Assert.That(Find<ItemsControl>(window, "SiteList").ItemCount, Is.GreaterThan(0));
        Capture(window, "09-colony-construction");

        colonies.SelectedSection = 3;
        Pump();
        Assert.That(Find<ItemsControl>(window, "MineralList").ItemCount, Is.GreaterThan(0));
        Capture(window, "10-colony-mining");
        window.Close();
    }

    [AvaloniaTest]
    public async Task People_screen_shows_skills_and_traits_and_opens_from_a_colony()
    {
        var (window, vm) = await OpenRunningGame();
        window.Height = 900;
        vm.SelectedTab = MainWindowViewModel.ColoniesTab;
        vm.Tick();
        Pump();
        var choose = Find<Button>(window, "ChooseAdministrator");
        Assert.That(choose.IsEffectivelyVisible, Is.True, "Earth has an administrative post");

        choose.Command!.Execute(null);
        vm.Tick();
        Pump();
        Assert.That(vm.SelectedTab, Is.EqualTo(MainWindowViewModel.PeopleTab));
        var people = vm.People;
        foreach (var p in people.People)
            TestContext.Out.WriteLine($"{p.Name} | {p.Role} | {p.Detail}");
        Assert.That(people.Filter, Is.EqualTo(PeopleViewModel.Administrators));
        Assert.That(people.SelectedPost?.ColonyId, Is.EqualTo(vm.Colonies.SelectedColony!.Id), "the colony's post is picked");
        Assert.That(window.GetVisualDescendants().OfType<ListBox>().Any(l => l.Name == "SystemList" && l.IsEffectivelyVisible),
            Is.False, "the known-systems list is hidden on the people screen");

        people.Filter = PeopleViewModel.Everyone;
        Pump();
        Assert.That(Find<ListBox>(window, "PeopleList").ItemCount, Is.EqualTo(people.People.Count).And.GreaterThan(0));
        people.SelectedPerson = people.People.First();
        Pump();
        Assert.That(Find<TextBlock>(window, "PersonName").Text, Is.EqualTo(people.SelectedPerson.Name));
        Assert.That(people.Traits.Select(t => t.Name), Does.Contain("Aggression").And.Contain("Patience"));
        Capture(window, "11-people");

        // Seat the administrator on the colony that asked for one; the colony's summary names them.
        var admin = people.People.First(p => p.CanAdminister);
        people.SelectedPerson = admin;
        people.SelectedPost = people.Posts.First(p => p.ColonyId == vm.Colonies.SelectedColony!.Id);
        Pump();
        Assert.That(Find<Button>(window, "AssignButton").IsEffectivelyEnabled, Is.True);
        await people.AssignCommand.ExecuteAsync(null);
        Assert.That(people.Sink.IsError, Is.False, people.Sink.Message);
        for (int i = 0; i < 50 && people.AdministeredColonyId == null; i++)
        {
            vm.Tick();
            Pump();
            await Task.Delay(20);
        }
        Assert.That(people.AdministeredColonyId, Is.EqualTo(vm.Colonies.SelectedColony!.Id));
        Assert.That(Find<Button>(window, "ReleaseButton").IsEffectivelyEnabled, Is.True);
        vm.SelectedTab = MainWindowViewModel.ColoniesTab;
        vm.Tick();
        Pump();
        Assert.That(vm.Colonies.Summary.People.Single(r => r.Label == "Administrator").Value, Is.EqualTo(admin.Name));
        window.Close();
    }

    [AvaloniaTest]
    public async Task Administrator_section_proposes_and_the_inbox_approves()
    {
        var (window, vm) = await OpenRunningGame();
        window.Height = 900;
        var client = vm.Client!;
        vm.SelectedTab = MainWindowViewModel.ColoniesTab;
        vm.Tick();
        Pump();
        var colony = vm.Colonies.SelectedColony!;

        // Seat a civilian (the People test may already have), then ask for a review by setting the level.
        var civilian = client.Galaxy.Commanders.First(c => c.Kind == Pulsar4X.Api.CommanderKind.Civilian);
        var seated = await client.SubmitCommandAsync(new Pulsar4X.Api.AssignAdministratorCommand(colony.Id, civilian.Id));
        TestContext.Out.WriteLine($"seat: {seated.Accepted} {seated.RejectionReason}");
        var advise = await client.SubmitCommandAsync(new Pulsar4X.Api.SetColonyAutomationCommand(colony.Id,
            Pulsar4X.Api.AutomationArea.Construction, Pulsar4X.Api.AutomationLevel.Advise));
        Assert.That(advise.Accepted, Is.True, advise.RejectionReason);
        for (int i = 0; i < 50 && vm.Inbox.Count == 0; i++)
        {
            vm.Tick();
            Pump();
            await Task.Delay(20);
        }

        vm.Colonies.SelectedSection = MainWindowViewModel.AdministratorSection;
        vm.Tick();
        Pump();
        var admin = vm.Colonies.Administrator;
        Assert.That(admin.HasAdministrator, Is.True, admin.AdministratorText);
        Assert.That(admin.Construction, Is.EqualTo(Pulsar4X.Api.AutomationLevel.Advise));
        Assert.That(Find<ItemsControl>(window, "PriorityList").ItemCount, Is.EqualTo(4));
        Assert.That(admin.Suggestions, Is.Not.Empty, "Earth is short of infrastructure");
        foreach (var s in admin.Suggestions)
            TestContext.Out.WriteLine($"{s.Text} | {s.Reason}");
        Capture(window, "12-colony-administrator");

        // The empire-wide inbox shows the same proposal; approving it queues the build.
        Assert.That(vm.Inbox.Count, Is.EqualTo(admin.Suggestions.Count));
        Assert.That(vm.Inbox.Header, Is.EqualTo($"Inbox ({vm.Inbox.Count})"));
        vm.SelectedTab = MainWindowViewModel.InboxTab;
        vm.Tick();
        Pump();
        Assert.That(Find<ItemsControl>(window, "InboxList").ItemCount, Is.EqualTo(vm.Inbox.Count));
        Capture(window, "13-inbox");

        int before = vm.Inbox.Count;
        var first = vm.Inbox.Items.First();
        await vm.Inbox.ApproveCommand.ExecuteAsync(first);
        Assert.That(vm.Inbox.Sink.IsError, Is.False, vm.Inbox.Sink.Message);
        for (int i = 0; i < 50 && vm.Inbox.Count == before; i++)
        {
            vm.Tick();
            Pump();
            await Task.Delay(20);
        }
        Assert.That(vm.Inbox.Count, Is.EqualTo(before - 1));
        var construction = client.Galaxy.GetSystem(colony.SystemId)!.GetEntity(colony.Id)!.GetView<Pulsar4X.Api.ConstructionView>()!;
        Assert.That(construction.BuildQueue.Select(j => j.Name), Has.Some.Contains(first.Text.Replace("Build ", "")));
        window.Close();
    }

    [AvaloniaTest]
    public async Task Failed_start_shows_the_error_message()
    {
        var (window, vm) = Open(new SharedGame.FailingLauncher());

        await vm.StartGameCommand.ExecuteAsync(null);
        Pump();

        var error = Find<TextBlock>(window, "ErrorText");
        Assert.That(error.IsEffectivelyVisible, Is.True);
        Assert.That(error.Text, Is.EqualTo("No game data found."));

        Capture(window, "03-start-error");
        window.Close();
    }
}
