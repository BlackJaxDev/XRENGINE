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

    /// <summary>
    /// Installs concrete render-object, shader, renderer, VR-rendering, and video services.
    /// This must run before <see cref="Engine.Run(GameStartupSettings, GameState)"/>.
    /// </summary>
    public static void InstallEngineHostServices()
    {
        lock (Sync)
        {
            RuntimeRenderObjectServices.Current = new EngineRuntimeRenderObjectServices();
            RuntimeShaderServices.Current = new EngineRuntimeShaderServices();
            RuntimeRenderingHostServices.Current = new EngineRuntimeRenderingHostServices();
            RuntimeRenderingHostServices.GameCachePath = ConvexHullDiskCache.ResolveCacheRoot();
            RuntimeVrRenderingServices.Current = new EngineRuntimeVrRenderingServices();
            RuntimeVideoStreamingServices.Current = new EngineRuntimeVideoStreamingServices();
        }
    }

    /// <summary>
    /// Creates an isolated concrete rendering host for focused tests.
    /// </summary>
    public static IRuntimeRenderingHostServices CreateEngineHostServices()
        => new EngineRuntimeRenderingHostServices();
}
