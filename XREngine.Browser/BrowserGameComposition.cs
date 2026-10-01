namespace XREngine.Browser;

/// <summary>Compile-time anchor for the game's generated registrations in an authored browser publish.</summary>
internal static partial class BrowserGameComposition
{
    internal static Func<IGameLaunchBootstrap>? BootstrapFactory { get; set; }
    internal static IGameLaunchBootstrap? CreateBootstrap() => BootstrapFactory?.Invoke();
    internal static void Initialize() => RegisterProvidedGame();
    static partial void RegisterProvidedGame();
}
