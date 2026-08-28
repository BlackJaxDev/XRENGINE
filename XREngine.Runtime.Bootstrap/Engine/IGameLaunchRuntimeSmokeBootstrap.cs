namespace XREngine;

/// <summary>
/// Extends a published game's bootstrap with an automated runtime validation
/// that runs after the ordinary archive checks performed by <c>--aot-smoke</c>.
/// </summary>
public interface IGameLaunchRuntimeSmokeBootstrap
{
    /// <summary>
    /// Enables runtime-smoke behavior before the bootstrap configures startup.
    /// Implementations may select a deterministic desktop profile and arrange
    /// for the running game to close itself after its acceptance checks finish.
    /// </summary>
    void ConfigureRuntimeSmoke();

    /// <summary>
    /// Verifies the result after the engine loop exits. Implementations should
    /// throw when any required runtime condition was not observed so the
    /// published launcher exits unsuccessfully.
    /// </summary>
    void CompleteRuntimeSmoke();
}
