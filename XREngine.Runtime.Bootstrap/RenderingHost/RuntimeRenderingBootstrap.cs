using XREngine.Components.Physics;
using XREngine.Components.Movement;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.VideoStreaming;
using XREngine.Data;
using XREngine.Scene.Physics;

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

        // Retire old worlds while their rendering, input and VR providers are still installed.
        RuntimeAdapterBootstrap.UninstallEngineHostServices();

        EngineRuntimeRenderingHostServices renderingHost = CreateRenderingHost(
            profile.RegisterRendererBackends, installAssetServices: true);

        IRuntimeRenderObjectServices? previousRenderObjects = RuntimeRenderObjectServices.Current;
        IRuntimeVideoStreamingServices? previousVideo = RuntimeVideoStreamingServices.Current;
        IRuntimeCharacterMovementVisualizationServices? previousCharacterMovementVisualization = RuntimeCharacterMovementVisualizationServices.Current;
        IRuntimeWindowApplicationServices previousWindowApplication = RuntimeWindowApplicationServices.Current;
        string? previousGameCachePath = RuntimeRenderingHostServices.GameCachePath;
        IDisposable? renderingHostLease = null;
        IDisposable? physicsChainRenderingLease = null;
        IDisposable? adapterLease = null;
        IDisposable? vrLease = null;
        IDisposable? shaderLease = null;
        EngineRuntimeWindowApplicationServices? installedWindowApplication = null;

        try
        {
            RuntimeRenderObjectServices.Current = new EngineRuntimeRenderObjectServices();
            shaderLease = EngineRuntimeShaderServices.Install();
            renderingHostLease = RuntimeRenderingHostServices.Install(renderingHost);
            physicsChainRenderingLease = RuntimePhysicsChainRenderingBridge.Install();
            RuntimeRenderingHostServices.GameCachePath = ConvexHullDiskCache.ResolveCacheRoot();
            RuntimeCharacterMovementVisualizationServices.Current = new RenderingCharacterMovementVisualizationServices();
            installedWindowApplication = new EngineRuntimeWindowApplicationServices();
            RuntimeWindowApplicationServices.Current = installedWindowApplication;

            if (profile.AllowsWindows)
                RuntimeVideoStreamingServices.Current = new EngineRuntimeVideoStreamingServices();

            vrLease = new DesktopVrServicesLease(profile);
            adapterLease = RuntimeAdapterBootstrap.InstallEngineHostServices(
                profile.AdapterProfile,
                static () => new Scene.Physics.Jolt.JoltScene(),
                composeRenderedWorlds: profile.AdapterProfile != RuntimeApplicationProfile.HeadlessServer.AdapterProfile);
            return new InstallationLease(
                renderingHost,
                renderingHostLease,
                physicsChainRenderingLease,
                adapterLease,
                vrLease,
                previousRenderObjects,
                shaderLease,
                previousVideo,
                previousCharacterMovementVisualization,
                previousWindowApplication,
                previousGameCachePath,
                profile);
        }
        catch (Exception installationFailure)
        {
            List<Exception>? failures = null;
            if (adapterLease is not null)
                AttemptCleanup(adapterLease.Dispose, ref failures);
            if (vrLease is not null)
                AttemptCleanup(vrLease.Dispose, ref failures);
            AttemptCleanup(() => RuntimeVideoStreamingServices.Current = previousVideo, ref failures);
            AttemptCleanup(() => RuntimeCharacterMovementVisualizationServices.Current = previousCharacterMovementVisualization, ref failures);
            if (installedWindowApplication is not null)
                AttemptCleanup(installedWindowApplication.Dispose, ref failures);
            AttemptCleanup(() => RuntimeWindowApplicationServices.Current = previousWindowApplication, ref failures);
            AttemptCleanup(() => RuntimeRenderingHostServices.GameCachePath = previousGameCachePath, ref failures);
            if (physicsChainRenderingLease is not null)
                AttemptCleanup(physicsChainRenderingLease.Dispose, ref failures);
            if (renderingHostLease is not null)
                AttemptCleanup(renderingHostLease.Dispose, ref failures);
            if (shaderLease is not null)
                AttemptCleanup(shaderLease.Dispose, ref failures);
            AttemptCleanup(() => RuntimeRenderObjectServices.Current = previousRenderObjects, ref failures);
            AttemptCleanup(renderingHost.Dispose, ref failures);
            if (failures is not null)
            {
                failures.Insert(0, installationFailure);
                throw new AggregateException("Runtime rendering host installation and rollback failed.", failures);
            }
            throw;
        }
    }

    private static void AttemptCleanup(Action cleanup, ref List<Exception>? failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(failure);
        }
    }

    /// <summary>
    /// Creates an isolated concrete rendering host for focused tests. Renderer
    /// modules and asset services remain explicit caller choices.
    /// </summary>
    public static IRuntimeRenderingHostServices CreateEngineHostServices(bool registerRendererBackends = false)
        => CreateRenderingHost(registerRendererBackends, installAssetServices: false);

    private static EngineRuntimeRenderingHostServices CreateRenderingHost(bool registerRendererBackends, bool installAssetServices)
    {
        DesktopRuntimeBackendBootstrap.EnsureRegistered();
        RendererBackendCatalog renderers = new();
        PhysicsBackendCatalog physics = new();
        IDisposable resources = RegistrationLeaseGroup.Create(leases =>
        {
            if (installAssetServices)
            {
                leases.Add(RuntimeEngineStartupPolicyServices.Install(DesktopEngineStartupPolicy.Instance));
                leases.Add(RuntimeAssetBootstrap.InstallEngineAssetServices());
            }
            leases.Add(renderers);
            BuiltInPhysicsBackendModules.RegisterDesktop(physics);
            if (registerRendererBackends)
                leases.Add(BuiltInRendererBackendModules.RegisterAll(renderers));
        });
        try
        {
            return new EngineRuntimeRenderingHostServices(
                renderers, physics, static () => BootstrapRenderSettings.CreateSceneRenderPipeline(), resources);
        }
        catch
        {
            resources.Dispose();
            throw;
        }
    }

    private sealed class InstallationLease(
        EngineRuntimeRenderingHostServices renderingHost,
        IDisposable renderingHostLease,
        IDisposable physicsChainRenderingLease,
        IDisposable adapterLease,
        IDisposable vrLease,
        IRuntimeRenderObjectServices? previousRenderObjects,
        IDisposable shaderLease,
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
            DisposeStep(vrLease, ref failures);

            RuntimeCharacterMovementVisualizationServices.Current = previousCharacterMovementVisualization;
            if (RuntimeWindowApplicationServices.Current is IDisposable windowApplication)
                DisposeStep(windowApplication, ref failures);
            RuntimeWindowApplicationServices.Current = previousWindowApplication;

            if (profile.AllowsWindows && RuntimeVideoStreamingServices.Current is EngineRuntimeVideoStreamingServices)
                RuntimeVideoStreamingServices.Current = previousVideo;

            RuntimeRenderingHostServices.GameCachePath = previousGameCachePath;
            DisposeStep(physicsChainRenderingLease, ref failures);
            DisposeStep(renderingHostLease, ref failures);

            DisposeStep(shaderLease, ref failures);
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
