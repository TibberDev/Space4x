using Pulsar4X.Api;
using Pulsar4X.UI.Core;
using Pulsar4X.UI.Desktop.Hosting;

namespace Pulsar4X.UI.Tests;

/// <summary>
/// Galaxy generation takes seconds, so the tests share one quickstarted game. Tests that advance
/// time only ever move it forward and read relative changes, so sharing is safe.
/// </summary>
internal static class SharedGame
{
    private static readonly Lazy<(LocalGameHost Host, IGameClient Client)> Instance = new(() =>
    {
        var host = new LocalGameHost();
        var client = host.QuickstartAsync().GetAwaiter().GetResult();
        return (host, client);
    });

    public static LocalGameHost Host => Instance.Value.Host;
    public static IGameClient Client => Instance.Value.Client;

    /// <summary>A launcher that hands out the shared game instead of creating a new one.</summary>
    public sealed class Launcher : IGameLauncher
    {
        public Task<IGameClient> QuickstartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Client);
    }

    /// <summary>A launcher that always fails, for the error path.</summary>
    public sealed class FailingLauncher : IGameLauncher
    {
        public Task<IGameClient> QuickstartAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No game data found.");
    }
}
