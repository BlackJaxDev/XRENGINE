namespace XREngine;

/// <summary>
/// Provides a statically rooted composition entry point for a published game.
/// </summary>
/// <remarks>
/// A game assembly may expose exactly one public, concrete implementation. The
/// project builder emits a direct constructor call into the generated launcher,
/// allowing NativeAOT games to construct their world and custom component graph
/// without runtime type discovery or reflection-based activation.
/// </remarks>
public interface IGameLaunchBootstrap
{
    /// <summary>
    /// Converts the cooked project settings into the concrete settings and world
    /// graph used by the standalone game.
    /// </summary>
    GameStartupSettings ConfigureStartup(GameStartupSettings cookedSettings);

    /// <summary>
    /// Creates the initial runtime state for a new standalone game session.
    /// </summary>
    GameState CreateInitialGameState();
}
