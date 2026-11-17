using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;
using Pulsar4X.UI.Core;
using Pulsar4X.UI.Core.ViewModels;
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

        var grid = Find<DataGrid>(window, "BodiesGrid");
        Assert.That(grid.IsEffectivelyVisible, Is.True);
        Assert.That(grid.GetVisualDescendants().OfType<DataGridRow>().Count(), Is.GreaterThan(0),
            "the body table rendered no rows");

        Capture(window, "02-game-screen");
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
