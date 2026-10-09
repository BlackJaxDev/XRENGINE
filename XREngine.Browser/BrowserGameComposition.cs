namespace XREngine.Browser;

/// <summary>Runs launcher-provided game composition after engine asset services are ready.</summary>
internal static class BrowserGameComposition
{
    internal static IGameLaunchBootstrap? CreateBootstrap() => BrowserRuntime.CreateBootstrap();
    internal static void Initialize() => BrowserRuntime.RegisterGame();
}
