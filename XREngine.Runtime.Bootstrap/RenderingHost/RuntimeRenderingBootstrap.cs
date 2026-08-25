using XREngine.Rendering;
using XREngine.Rendering.VideoStreaming;
using XREngine.Components.Physics;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Installs XRENGINE's concrete rendering adapters at the application composition root.
/// </summary>
public static class RuntimeRenderingBootstrap
{
    private static readonly object Sync = new();
    private static EngineRuntimeRenderingHostServices? _installedRenderingHost;

    /// <summary>
    /// Installs concrete render-object, shader, renderer, VR-rendering, and video services.
    /// This must run before <see cref="Engine.Run(GameStartupSettings, GameState)"/>.
    /// </summary>
    public static void InstallEngineHostServices(RuntimeAdapterProfile adapterProfile = RuntimeAdapterProfile.All)
    {
        lock (Sync)
        {
            EngineRuntimeRenderingHostServices renderingHost =
                new(
                    registerRendererBackends: true,
                    installAssetServices: true);
            EngineRuntimeRenderingHostServices? previousRenderingHost =
                _installedRenderingHost;
            _installedRenderingHost = renderingHost;

            RuntimeRenderObjectServices.Current = new EngineRuntimeRenderObjectServices();
            RuntimeShaderServices.Current = new EngineRuntimeShaderServices();
            RuntimeRenderingHostServices.Current = renderingHost;
            RuntimeRenderingHostServices.GameCachePath = ConvexHullDiskCache.ResolveCacheRoot();
            RuntimeVrRenderingServices.Current = new EngineRuntimeVrRenderingServices();
            RuntimeVideoStreamingServices.Current = new EngineRuntimeVideoStreamingServices();
            RuntimeAdapterBootstrap.InstallEngineHostServices(adapterProfile);

            previousRenderingHost?.Dispose();
        }
    }

    /// <summary>
    /// Creates an isolated concrete rendering host for focused tests. Renderer modules are
    /// omitted by default so a test can install exactly the backend generation it exercises.
    /// Pass <paramref name="registerRendererBackends"/> only for tests that need the static
    /// production composition. Asset services remain the caller's composition-root
    /// responsibility so creating a focused host cannot retain process-global registrations.
    /// The returned host is also <see cref="IDisposable"/>.
    /// </summary>
    public static IRuntimeRenderingHostServices CreateEngineHostServices(
        bool registerRendererBackends = false)
        => new EngineRuntimeRenderingHostServices(
            registerRendererBackends,
            installAssetServices: false);
}
