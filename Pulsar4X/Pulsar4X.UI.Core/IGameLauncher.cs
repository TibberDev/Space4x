using Pulsar4X.Api;

namespace Pulsar4X.UI.Core;

/// <summary>
/// The seam between the view models and whatever hosts the engine. The desktop app implements it
/// with an in-process engine; a future network client would connect to a server instead. The view
/// models only ever see the <see cref="IGameClient"/> it returns.
/// </summary>
public interface IGameLauncher
{
    /// <summary>
    /// Creates a new game from the default mods and scenario and returns a connected client bound to
    /// the player's faction. May take several seconds (galaxy generation) and may be called off the
    /// UI thread. Throws with a player-facing message when the game cannot be created.
    /// </summary>
    Task<IGameClient> QuickstartAsync(CancellationToken cancellationToken = default);
}
