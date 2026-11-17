using Avalonia;
using Avalonia.Headless;
using Pulsar4X.UI.Desktop;

[assembly: AvaloniaTestApplication(typeof(Pulsar4X.UI.Tests.TestApp))]

namespace Pulsar4X.UI.Tests;

/// <summary>Boots the real <see cref="App"/> (themes, styles) on the headless platform, drawing with
/// Skia so frames can be captured as real images.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
