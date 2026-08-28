using XREngine.Components.Physics;
using XREngine.Components.Movement;
using XREngine.Rendering;
using XREngine.Rendering.VideoStreaming;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Installs concrete rendering and adapter services at an application composition root.</summary>
public static class RuntimeRenderingBootstrap
{
    /// <summary>
    /// Compatibility entry point for callers that have not selected an explicit
    /// application profile. New application roots should use
    /// <see cref="RuntimeApplicationBootstrap.Install(RuntimeApplicationProfile)"/>.
    /// </summary>
    public static IDisposable InstallEngineHostServices(RuntimeAdapterProfile adapterProfile = RuntimeAdapterProfile.All)
        => InstallEngineHostServices(new RuntimeApplicationProfile(
            "LegacyDesktop",
            adapterProfile,
            AllowsWindows: true,
            AllowsVr: adapterProfile.HasFlag(RuntimeAdapterProfile.Input),
            RegisterRendererBackends: true));

    /// <summary>
    /// Installs only the services permitted by <paramref name="profile"/>. The
    /// returned lease restores every prior registration in reverse order.
    /// Headless profiles still install the backend-neutral rendering host needed
    /// by composed worlds, but register no desktop renderer backend or VR service.
    /// </summary>
    public static IDisposable InstallEngineHostServices(RuntimeApplicationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        EngineRuntimeRenderingHostServices renderingHost = new(
            registerRendererBackends: profile.RegisterRendererBackends,
            installAssetServices: true);

        IRuntimeRenderObjectServices? previousRenderObjects = RuntimeRenderObjectServices.Current;
        IRuntimeShaderServices? previousShaders = RuntimeShaderServices.Current;
        IRuntimeVrRenderingServices previousVrRendering = RuntimeVrRenderingServices.Current;
        IRuntimeVideoStreamingServices? previousVideo = RuntimeVideoStreamingServices.Current;
        IRuntimeCharacterMovementVisualizationServices? previousCharacterMovementVisualization = RuntimeCharacterMovementVisualizationServices.Current;
        IRuntimeWindowApplicationServices previousWindowApplication = RuntimeWindowApplicationServices.Current;
        string? previousGameCachePath = RuntimeRenderingHostServices.GameCachePath;
        IDisposable? renderingHostLease = null;
        IDisposable? adapterLease = null;

        try
        {
            RuntimeRenderObjectServices.Current = new EngineRuntimeRenderObjectServices();
            RuntimeShaderServices.Current = new EngineRuntimeShaderServices();
            renderingHostLease = RuntimeRenderingHostServices.Install(renderingHost);
            RuntimeRenderingHostServices.GameCachePath = ConvexHullDiskCache.ResolveCacheRoot();
            RuntimeCharacterMovementVisualizationServices.Current = new RenderingCharacterMovementVisualizationServices();
            RuntimeWindowApplicationServices.Current = new EngineRuntimeWindowApplicationServices();

            if (profile.AllowsVr)
                RuntimeVrRenderingServices.Current = new EngineRuntimeVrRenderingServices();
            if (profile.AllowsWindows)
                RuntimeVideoStreamingServices.Current = new EngineRuntimeVideoStreamingServices();

            adapterLease = RuntimeAdapterBootstrap.InstallEngineHostServices(profile.AdapterProfile);
            return new InstallationLease(
                renderingHost,
                renderingHostLease,
                adapterLease,
                previousRenderObjects,
                previousShaders,
                previousVrRendering,
                previousVideo,
                previousCharacterMovementVisualization,
                previousWindowApplication,
                previousGameCachePath,
                profile);
        }
        catch
        {
            adapterLease?.Dispose();
            RuntimeVideoStreamingServices.Current = previousVideo;
            RuntimeVrRenderingServices.Current = previousVrRendering;
            RuntimeCharacterMovementVisualizationServices.Current = previousCharacterMovementVisualization;
            if (RuntimeWindowApplicationServices.Current is IDisposable windowApplication)
                windowApplication.Dispose();
            RuntimeWindowApplicationServices.Current = previousWindowApplication;
            RuntimeRenderingHostServices.GameCachePath = previousGameCachePath;
            renderingHostLease?.Dispose();
            RuntimeShaderServices.Current = previousShaders;
            RuntimeRenderObjectServices.Current = previousRenderObjects;
            renderingHost.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates an isolated concrete rendering host for focused tests. Renderer
    /// modules and asset services remain explicit caller choices.
    /// </summary>
    public static IRuntimeRenderingHostServices CreateEngineHostServices(bool registerRendererBackends = false)
        => new EngineRuntimeRenderingHostServices(registerRendererBackends, installAssetServices: false);

    private sealed class InstallationLease(
        EngineRuntimeRenderingHostServices renderingHost,
        IDisposable renderingHostLease,
        IDisposable adapterLease,
        IRuntimeRenderObjectServices? previousRenderObjects,
        IRuntimeShaderServices? previousShaders,
        IRuntimeVrRenderingServices previousVrRendering,
        IRuntimeVideoStreamingServices? previousVideo,
        IRuntimeCharacterMovementVisualizationServices? previousCharacterMovementVisualization,
        IRuntimeWindowApplicationServices previousWindowApplication,
        string? previousGameCachePath,
        RuntimeApplicationProfile profile) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            List<Exception>? failures = null;
            DisposeStep(adapterLease, ref failures);

            RuntimeCharacterMovementVisualizationServices.Current = previousCharacterMovementVisualization;
            if (RuntimeWindowApplicationServices.Current is IDisposable windowApplication)
                DisposeStep(windowApplication, ref failures);
            RuntimeWindowApplicationServices.Current = previousWindowApplication;

            if (profile.AllowsWindows && RuntimeVideoStreamingServices.Current is EngineRuntimeVideoStreamingServices)
                RuntimeVideoStreamingServices.Current = previousVideo;
            if (profile.AllowsVr && RuntimeVrRenderingServices.Current is EngineRuntimeVrRenderingServices)
                RuntimeVrRenderingServices.Current = previousVrRendering;

            RuntimeRenderingHostServices.GameCachePath = previousGameCachePath;
            DisposeStep(renderingHostLease, ref failures);

            if (RuntimeShaderServices.Current is EngineRuntimeShaderServices)
                RuntimeShaderServices.Current = previousShaders;
            if (RuntimeRenderObjectServices.Current is EngineRuntimeRenderObjectServices)
                RuntimeRenderObjectServices.Current = previousRenderObjects;

            DisposeStep(renderingHost, ref failures);
            if (failures is [Exception failure])
                throw failure;
            if (failures is { Count: > 1 })
                throw new AggregateException("Runtime rendering services failed to tear down cleanly.", failures);
        }

        private static void DisposeStep(IDisposable disposable, ref List<Exception>? failures)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(ex);
            }
        }
    }
}
