using XREngine.Components.Animation;
using XREngine.Input;
using XREngine.Networking;
using XREngine.Runtime.InputIntegration;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Installs the facade-backed capabilities used by optional runtime subsystem adapters.
/// </summary>
public static class RuntimeAdapterBootstrap
{
    private static readonly object Sync = new();
    private static RuntimeAdapterHostLease? _installedLease;

    /// <summary>
    /// Installs the selected adapter host capabilities and returns a lease that restores the
    /// previous composition when disposed.
    /// </summary>
    public static IDisposable InstallEngineHostServices(RuntimeAdapterProfile profile = RuntimeAdapterProfile.All)
    {
        lock (Sync)
        {
            RuntimeAdapterHostLease? previous = _installedLease;
            _installedLease = null;
            previous?.DisposeWithoutLock();

            RuntimeAdapterHostLease lease = new(profile);
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
            RuntimeAdapterHostLease? lease = _installedLease;
            _installedLease = null;
            lease?.DisposeWithoutLock();
        }
    }

    private sealed class RuntimeAdapterHostLease : IDisposable
    {
        private readonly IRuntimeAnimationHostServices _previousAnimation;
        private readonly IRuntimeAudioIntegrationServices _previousAudio;
        private readonly IRuntimeInputServices _previousInput;
        private readonly IRuntimeInputCaptureServices _previousInputCapture;
        private readonly IRuntimeVrInputServices _previousVrInput;
        private readonly IRuntimeVrStateServices _previousVrState;
        private readonly IRuntimeVrLifecycleServices _previousVrLifecycle;
        private readonly IRuntimeGameModeHostServices? _previousGameMode;
        private readonly IRuntimePawnHostServices? _previousPawn;
        private readonly IRuntimePlayerControllerServices? _previousPlayerController;
        private readonly IDisposable _networkingLease;
        private readonly IRuntimeWorldHostServices _worldHost;
        private readonly IDisposable _worldHostOwner;
        private readonly IDisposable _worldHostLease;
        private readonly IDisposable _worldRegistryLease;
        private readonly EngineRuntimeVrInputServices? _installedVrInput;
        private readonly EngineRuntimePawnHostServices? _installedPawn;
        private readonly IDisposable _installedPlayerController;
        private readonly RuntimeAdapterProfile _profile;
        private int _disposed;

        public RuntimeAdapterHostLease(RuntimeAdapterProfile profile)
        {
            _profile = profile;
            _previousAnimation = RuntimeAnimationHostServices.Current;
            _previousAudio = RuntimeAudioIntegrationServices.Current;
            _previousInput = RuntimeInputServices.Current;
            _previousInputCapture = RuntimeInputCaptureServices.Current;
            _previousVrInput = RuntimeVrInputServices.Current;
            _previousVrState = RuntimeVrStateServices.Current;
            _previousVrLifecycle = RuntimeEngine.VRState.LifecycleServices;
            _previousGameMode = RuntimeGameModeHostServices.Current;
            _previousPawn = RuntimePawnHostServices.Current;
            _previousPlayerController = RuntimePlayerControllerServices.Current;
            bool allowsWindows = !RuntimeApplicationCapabilityServices.Current.IsConfigured
                || RuntimeApplicationCapabilityServices.Current.AllowsWindows;
            if (allowsWindows)
            {
                EngineRuntimeWorldHostServices renderedWorldHost = new();
                _worldHost = renderedWorldHost;
                _worldHostOwner = renderedWorldHost;
                _worldRegistryLease = RuntimeWorldRegistryServices.Install(renderedWorldHost.CoreWorldRegistry);
            }
            else
            {
                HeadlessRuntimeWorldHostServices headlessWorldHost = new();
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
                RuntimeVrInputServices.Current = _installedVrInput = new EngineRuntimeVrInputServices();
                RuntimeVrStateServices.Current = new EngineRuntimeVrStateServices();
                RuntimeGameModeHostServices.Current = new EngineRuntimeGameModeHostServices();
                RuntimePawnHostServices.Current = _installedPawn = new EngineRuntimePawnHostServices();
                EngineRuntimePlayerControllerServices playerControllers = new();
                _installedPlayerController = playerControllers;
                RuntimePlayerControllerServices.Current = playerControllers;
                GameModeCompositionBootstrap.RegisterBuiltInGameModes();
            }
            else
            {
                RuntimePlayerControllerServices.Current = new RemoteOnlyPlayerControllerServices();
                _installedPlayerController = (IDisposable)RuntimePlayerControllerServices.Current;
            }
            _networkingLease = RuntimeNetworkingHostServices.Install(new EngineRuntimeNetworkingHostServices());
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
            try
            {
                _worldHostOwner.Dispose();
            }
            finally
            {
                if (_profile.HasFlag(RuntimeAdapterProfile.Animation))
                    RuntimeAnimationHostServices.Current = _previousAnimation;
                if (_profile.HasFlag(RuntimeAdapterProfile.Audio))
                    RuntimeAudioIntegrationServices.Current = _previousAudio;
                if (_profile.HasFlag(RuntimeAdapterProfile.Input))
                {
                    _installedVrInput?.Dispose();
                    _installedPawn?.Dispose();
                    _installedPlayerController.Dispose();
                    RuntimeInputServices.Current = _previousInput;
                    RuntimeInputCaptureServices.Current = _previousInputCapture;
                    RuntimeVrInputServices.Current = _previousVrInput;
                    RuntimeVrStateServices.Current = _previousVrState;
                    RuntimeEngine.VRState.LifecycleServices = _previousVrLifecycle;
                    RuntimeGameModeHostServices.Current = _previousGameMode;
                    RuntimePawnHostServices.Current = _previousPawn;
                    RuntimePlayerControllerServices.Current = _previousPlayerController;
                }
                else
                {
                    _installedPlayerController.Dispose();
                    RuntimePlayerControllerServices.Current = _previousPlayerController;
                }
                _networkingLease.Dispose();
                _worldRegistryLease.Dispose();
                _worldHostLease.Dispose();
            }
        }
    }
}
