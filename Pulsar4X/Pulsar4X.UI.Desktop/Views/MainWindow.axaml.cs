using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Pulsar4X.UI.Core.ViewModels;
using Pulsar4X.UI.Desktop.Controls;

namespace Pulsar4X.UI.Desktop.Views;

public partial class MainWindow : Window
{
    // The view models pull from the game client once per "frame". 30 Hz is plenty for a table UI
    // and keeps idle CPU low; the star map will drive its own render loop later.
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var starMap = this.FindControl<StarMapControl>("StarMap");
        _frameTimer.Tick += (_, _) =>
        {
            (DataContext as MainWindowViewModel)?.Tick();
            // Planets move every frame while time runs; redraw the map (a no-op while its tab is hidden).
            starMap?.InvalidateVisual();
        };
        Opened += (_, _) => _frameTimer.Start();
        Closed += (_, _) => _frameTimer.Stop();
    }
}
