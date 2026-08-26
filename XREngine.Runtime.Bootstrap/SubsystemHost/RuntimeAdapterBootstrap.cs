using XREngine.Components.Animation;
using XREngine.Input;
using XREngine.Networking;

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
        private readonly IRuntimeVrInputServices _previousVrInput;
        private readonly IRuntimeVrStateServices _previousVrState;
        private readonly IRuntimeVrLifecycleServices _previousVrLifecycle;
        private readonly IRuntimeGameModeHostServices? _previousGameMode;
        private readonly IRuntimePawnHostServices? _previousPawn;
        private readonly IRuntimePlayerControllerServices? _previousPlayerController;
        private readonly IDisposable _networkingLease;
        private readonly EngineRuntimeWorldHostServices _worldHost;
        private readonly IDisposable _worldHostLease;
        private readonly IDisposable _worldRegistryLease;
        private readonly IRuntimeModelImportServices _previousModeling;
        private readonly EngineRuntimeVrInputServices? _installedVrInput;
        private readonly EngineRuntimePawnHostServices? _installedPawn;
        private readonly EngineRuntimePlayerControllerServices? _installedPlayerController;
        private readonly RuntimeAdapterProfile _profile;
        private int _disposed;

        public RuntimeAdapterHostLease(RuntimeAdapterProfile profile)
        {
            _profile = profile;
            _previousAnimation = RuntimeAnimationHostServices.Current;
            _previousAudio = RuntimeAudioIntegrationServices.Current;
            _previousInput = RuntimeInputServices.Current;
            _previousVrInput = RuntimeVrInputServices.Current;
            _previousVrState = RuntimeVrStateServices.Current;
            _previousVrLifecycle = RuntimeEngine.VRState.LifecycleServices;
            _previousGameMode = RuntimeGameModeHostServices.Current;
            _previousPawn = RuntimePawnHostServices.Current;
            _previousPlayerController = RuntimePlayerControllerServices.Current;
            _previousModeling = RuntimeModelImportServices.Current;
            _worldHost = new EngineRuntimeWorldHostServices();
            _worldHostLease = RuntimeWorldHostServices.Install(_worldHost);
            _worldRegistryLease = RuntimeWorldRegistryServices.Install(_worldHost.CoreWorldRegistry);

            if (profile.HasFlag(RuntimeAdapterProfile.Animation))
                RuntimeAnimationHostServices.Current = new EngineRuntimeAnimationHostServices();
            if (profile.HasFlag(RuntimeAdapterProfile.Audio))
                RuntimeAudioIntegrationServices.Current = new EngineRuntimeAudioIntegrationServices();
            if (profile.HasFlag(RuntimeAdapterProfile.Input))
            {
                RuntimeInputServices.Current = new EngineRuntimeInputServices();
                RuntimeVrInputServices.Current = _installedVrInput = new EngineRuntimeVrInputServices();
                RuntimeVrStateServices.Current = new EngineRuntimeVrStateServices();
                RuntimeGameModeHostServices.Current = new EngineRuntimeGameModeHostServices();
                RuntimePawnHostServices.Current = _installedPawn = new EngineRuntimePawnHostServices();
                RuntimePlayerControllerServices.Current = _installedPlayerController = new EngineRuntimePlayerControllerServices();
                GameModeCompositionBootstrap.RegisterBuiltInGameModes();
            }
            _networkingLease = RuntimeNetworkingHostServices.Install(new EngineRuntimeNetworkingHostServices());
            if (profile.HasFlag(RuntimeAdapterProfile.Modeling))
                RuntimeModelImportServices.Current = new EngineRuntimeModelImportServices();
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
                _worldHost.Dispose();
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
                    _installedPlayerController?.Dispose();
                    RuntimeInputServices.Current = _previousInput;
                    RuntimeVrInputServices.Current = _previousVrInput;
                    RuntimeVrStateServices.Current = _previousVrState;
                    RuntimeEngine.VRState.LifecycleServices = _previousVrLifecycle;
                    RuntimeGameModeHostServices.Current = _previousGameMode;
                    RuntimePawnHostServices.Current = _previousPawn;
                    RuntimePlayerControllerServices.Current = _previousPlayerController;
                }
                _networkingLease.Dispose();
                _worldRegistryLease.Dispose();
                _worldHostLease.Dispose();
                if (_profile.HasFlag(RuntimeAdapterProfile.Modeling))
                    RuntimeModelImportServices.Current = _previousModeling;
            }
        }
    }
}
