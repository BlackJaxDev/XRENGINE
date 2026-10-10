using XREngine.Audio.WebAudio;
using XREngine.Scene.Physics.Jolt;

namespace XREngine.Browser;

/// <summary>Installs engine composition once for the lifetime of the browser process.</summary>
public static class BrowserRuntime
{
    private static int _state;
    private static Func<IGameLaunchBootstrap>? _bootstrapFactory;
    private static Action? _registerGameModule;

    /// <summary>
    /// Call once from the launcher entry point. Game callbacks run at world startup,
    /// after engine asset services are ready. The registration callback must be idempotent.
    /// A failed initialization requires a page reload.
    /// </summary>
    public static void Initialize(Func<IGameLaunchBootstrap>? bootstrapFactory = null,
        Action? registerGameModule = null)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("BrowserRuntime.AlreadyInitialized: initialize the browser runtime exactly once; reload the page after an initialization failure.");

        _bootstrapFactory = bootstrapFactory;
        _registerGameModule = registerGameModule;
        BrowserStaticRegistrations.Initialize();
        BrowserRendererComposition.Initialize();
        BrowserEngineExports.InstallPhysicsBackend(new JoltPhysicsBackendModule());
        WebAudioTransport.Register();
        // Keep state 1 after a failure because engine registration is not transactional.
        Volatile.Write(ref _state, 2);
    }

    internal static bool IsReady => Volatile.Read(ref _state) == 2;
    internal static void RegisterGame() => _registerGameModule?.Invoke();
    internal static IGameLaunchBootstrap? CreateBootstrap() => _bootstrapFactory?.Invoke();
}
