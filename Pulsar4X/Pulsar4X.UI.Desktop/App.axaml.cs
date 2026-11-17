using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Pulsar4X.UI.Core;
using Pulsar4X.UI.Core.ViewModels;
using Pulsar4X.UI.Desktop.Hosting;
using Pulsar4X.UI.Desktop.Views;

namespace Pulsar4X.UI.Desktop;

public partial class App : Application
{
    /// <summary>Overridable so tests can supply their own launcher before the window is created.</summary>
    internal static Func<IGameLauncher> LauncherFactory { get; set; } = () => new LocalGameHost();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var launcher = LauncherFactory();
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel(launcher) };
            desktop.Exit += (_, _) => (launcher as IDisposable)?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
