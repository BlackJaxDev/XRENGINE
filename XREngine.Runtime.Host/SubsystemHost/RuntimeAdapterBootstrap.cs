using XREngine.Components.Animation;
using XREngine.Input;
using XREngine.Networking;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Installs the facade-backed capabilities used by optional runtime subsystem adapters.
/// </summary>
public static class RuntimeAdapterBootstrap
{
    private static readonly object Sync = new();
    private static IRuntimeAdapterHostLease? _installedLease;

    /// <summary>
    /// Installs the selected adapter host capabilities and returns a lease that restores the
    /// previous composition when disposed.
    /// </summary>
    public static IDisposable InstallEngineHostServices(
        RuntimeAdapterProfile profile = RuntimeAdapterProfile.All,
        Func<AbstractPhysicsScene>? createHeadlessPhysicsScene = null,
        bool composeRenderedWorlds = true)
    {
        bool headless = !composeRenderedWorlds || profile == RuntimeApplicationProfile.HeadlessServer.AdapterProfile;
        if (headless && createHeadlessPhysicsScene is null)
            throw new InvalidOperationException("Headless world composition requires an explicit physics scene factory.");
        lock (Sync)
        {
            IRuntimeAdapterHostLease? previous = _installedLease;
            _installedLease = null;
            previous?.DisposeWithoutLock();

            IRuntimeAdapterHostLease lease = profile == RuntimeApplicationProfile.HeadlessServer.AdapterProfile
                ? new HeadlessRuntimeAdapterHostLease(createHeadlessPhysicsScene!)
                : new RuntimeAdapterHostLease(profile, composeRenderedWorlds, createHeadlessPhysicsScene);
            _installedLease = lease;
            return lease;
        }
    }

    /// <summary>
    /// Removes the currently installed adapter capabilities and restores the lower-layer defaults.
    /// </summary>
    public static void UninstallEngineHostServices()
    {
        lock (Sync)
        {
            IRuntimeAdapterHostLease? lease = _installedLease;
            _installedLease = null;
            lease?.DisposeWithoutLock();
        }
    }

    private interface IRuntimeAdapterHostLease : IDisposable
    {
        void DisposeWithoutLock();
    }

    private static void Attempt(Action action, ref List<Exception>? failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
    }

    private static void ThrowCleanupFailures(List<Exception>? failures)
    {
        if (failures is [Exception failure])
            throw failure;
        if (failures is { Count: > 1 })
            throw new AggregateException("Runtime adapter host cleanup failed.", failures);
    }

    /// <summary>
    /// Headless composition is kept free of local audio, window, and VR service types so a
    /// dedicated-server publish does not need to carry their managed or native implementations.
    /// </summary>
    private sealed class HeadlessRuntimeAdapterHostLease : IRuntimeAdapterHostLease
    {
        private readonly IRuntimeAnimationHostServices _previousAnimation;
        private readonly IRuntimePlayerControllerServices? _previousPlayerController;
        private readonly IDisposable? _installedPlayerController;
        private readonly IDisposable? _networkingLease;
        private readonly HeadlessRuntimeWorldHostServices? _worldHost;
        private readonly IDisposable? _worldHostLease;
        private readonly IDisposable? _worldRegistryLease;
        private int _disposed;

        public HeadlessRuntimeAdapterHostLease(Func<AbstractPhysicsScene> createPhysicsScene)
        {
            _previousAnimation = RuntimeAnimationHostServices.Current;
            _previousPlayerController = RuntimePlayerControllerServices.Current;
            try
            {
                _worldHost = new HeadlessRuntimeWorldHostServices(createPhysicsScene);
                _worldRegistryLease = RuntimeWorldRegistryServices.Install(_worldHost.CoreWorldRegistry);
                _worldHostLease = RuntimeWorldHostServices.Install(_worldHost);
                RuntimeAnimationHostServices.Current = new EngineRuntimeAnimationHostServices();
                RemoteOnlyPlayerControllerServices playerControllers = new();
                _installedPlayerController = playerControllers;
                RuntimePlayerControllerServices.Current = playerControllers;
                _networkingLease = RuntimeNetworkingHostServices.Install(new EngineRuntimeNetworkingHostServices());
            }
            catch (Exception installationFailure)
            {
                try
                {
                    DisposeWithoutLock();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Headless runtime host installation and rollback failed.", installationFailure, cleanupFailure);
                }
                throw;
            }
        }

        public void Dispose()
        {
            lock (Sync)
            {
                if (ReferenceEquals(_installedLease, this))
                    _installedLease = null;
                DisposeWithoutLock();
            }
        }

        public void DisposeWithoutLock()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            List<Exception>? failures = null;
            if (_worldHost is not null)
                Attempt(_worldHost.Dispose, ref failures);
            if (_installedPlayerController is not null)
                Attempt(_installedPlayerController.Dispose, ref failures);
            Attempt(() => RuntimeAnimationHostServices.Current = _previousAnimation, ref failures);
            Attempt(() => RuntimePlayerControllerServices.Current = _previousPlayerController, ref failures);
            if (_networkingLease is not null)
                Attempt(_networkingLease.Dispose, ref failures);
            if (_worldRegistryLease is not null)
                Attempt(_worldRegistryLease.Dispose, ref failures);
            if (_worldHostLease is not null)
                Attempt(_worldHostLease.Dispose, ref failures);
            ThrowCleanupFailures(failures);
        }
    }

    private sealed class RuntimeAdapterHostLease : IRuntimeAdapterHostLease
    {
        private readonly IRuntimeAnimationHostServices _previousAnimation;
        private readonly IRuntimeAudioIntegrationServices _previousAudio;
        private readonly IRuntimeInputServices _previousInput;
        private readonly IRuntimeInputCaptureServices _previousInputCapture;
        private readonly IRuntimeGameModeHostServices? _previousGameMode;
        private readonly IRuntimePawnHostServices? _previousPawn;
        private readonly IRuntimePlayerControllerServices? _previousPlayerController;
        private readonly IDisposable? _networkingLease;
        private readonly IRuntimeWorldHostServices? _worldHost;
        private readonly IDisposable? _worldHostOwner;
        private readonly IDisposable? _worldHostLease;
        private readonly IDisposable? _worldRegistryLease;
        private readonly EngineRuntimePawnHostServices? _installedPawn;
        private readonly IDisposable? _installedPlayerController;
        private readonly RuntimeAdapterProfile _profile;
        private int _disposed;

        public RuntimeAdapterHostLease(RuntimeAdapterProfile profile, bool composeRenderedWorlds, Func<AbstractPhysicsScene>? createHeadlessPhysicsScene)
        {
            _profile = profile;
            _previousAnimation = RuntimeAnimationHostServices.Current;
            _previousAudio = RuntimeAudioIntegrationServices.Current;
            _previousInput = RuntimeInputServices.Current;
            _previousInputCapture = RuntimeInputCaptureServices.Current;
            _previousGameMode = RuntimeGameModeHostServices.Current;
            _previousPawn = RuntimePawnHostServices.Current;
            _previousPlayerController = RuntimePlayerControllerServices.Current;
            try
            {
                if (composeRenderedWorlds)
                {
                    EngineRuntimeWorldHostServices renderedWorldHost = new();
                    _worldHost = renderedWorldHost;
                    _worldHostOwner = renderedWorldHost;
                    _worldRegistryLease = RuntimeWorldRegistryServices.Install(renderedWorldHost.CoreWorldRegistry);
                }
                else
                {
                    HeadlessRuntimeWorldHostServices headlessWorldHost = new(createHeadlessPhysicsScene!);
                    _worldHost = headlessWorldHost;
                    _worldHostOwner = headlessWorldHost;
                    _worldRegistryLease = RuntimeWorldRegistryServices.Install(headlessWorldHost.CoreWorldRegistry);
                }
                _worldHostLease = RuntimeWorldHostServices.Install(_worldHost);

                if (profile.HasFlag(RuntimeAdapterProfile.Animation))
                    RuntimeAnimationHostServices.Current = new EngineRuntimeAnimationHostServices();
                if (profile.HasFlag(RuntimeAdapterProfile.Audio))
                    RuntimeAudioIntegrationServices.Current = new EngineRuntimeAudioIntegrationServices();
                if (profile.HasFlag(RuntimeAdapterProfile.Input))
                {
                    RuntimeInputServices.Current = new EngineRuntimeInputServices();
                    RuntimeInputCaptureServices.Current = new RuntimeInputCaptureState();
                    RuntimeGameModeHostServices.Current = new EngineRuntimeGameModeHostServices();
                    RuntimePawnHostServices.Current = _installedPawn = new EngineRuntimePawnHostServices();
                    EngineRuntimePlayerControllerServices playerControllers = new();
                    _installedPlayerController = playerControllers;
                    RuntimePlayerControllerServices.Current = playerControllers;
                    GameModeCompositionBootstrap.RegisterBuiltInGameModes();
                }
                else
                {
                    RemoteOnlyPlayerControllerServices playerControllers = new();
                    _installedPlayerController = playerControllers;
                    RuntimePlayerControllerServices.Current = playerControllers;
                }
                _networkingLease = RuntimeNetworkingHostServices.Install(new EngineRuntimeNetworkingHostServices());
            }
            catch (Exception installationFailure)
            {
                try
                {
                    DisposeWithoutLock();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Runtime adapter host installation and rollback failed.", installationFailure, cleanupFailure);
                }
                throw;
            }
        }

        public void Dispose()
        {
            lock (Sync)
            {
                if (ReferenceEquals(_installedLease, this))
                    _installedLease = null;
                DisposeWithoutLock();
            }
        }

        public void DisposeWithoutLock()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // World teardown must run while every service installed for those
            // worlds is still current. Component cleanup may resolve input,
            // networking, rendering, registry, or game-mode capabilities.
            List<Exception>? failures = null;
            if (_worldHostOwner is not null)
                Attempt(_worldHostOwner.Dispose, ref failures);
            if (_installedPawn is not null)
                Attempt(_installedPawn.Dispose, ref failures);
            if (_installedPlayerController is not null)
                Attempt(_installedPlayerController.Dispose, ref failures);

            if (_profile.HasFlag(RuntimeAdapterProfile.Animation))
                Attempt(() => RuntimeAnimationHostServices.Current = _previousAnimation, ref failures);
            if (_profile.HasFlag(RuntimeAdapterProfile.Audio))
                Attempt(() => RuntimeAudioIntegrationServices.Current = _previousAudio, ref failures);
            if (_profile.HasFlag(RuntimeAdapterProfile.Input))
            {
                Attempt(() => RuntimeInputServices.Current = _previousInput, ref failures);
                Attempt(() => RuntimeInputCaptureServices.Current = _previousInputCapture, ref failures);
                Attempt(() => RuntimeGameModeHostServices.Current = _previousGameMode, ref failures);
                Attempt(() => RuntimePawnHostServices.Current = _previousPawn, ref failures);
            }
            Attempt(() => RuntimePlayerControllerServices.Current = _previousPlayerController, ref failures);
            if (_networkingLease is not null)
                Attempt(_networkingLease.Dispose, ref failures);
            if (_worldRegistryLease is not null)
                Attempt(_worldRegistryLease.Dispose, ref failures);
            if (_worldHostLease is not null)
                Attempt(_worldHostLease.Dispose, ref failures);
            ThrowCleanupFailures(failures);
        }
    }
}
