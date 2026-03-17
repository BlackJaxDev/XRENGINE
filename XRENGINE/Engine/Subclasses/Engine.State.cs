using Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Input;
using XREngine.Native;
using XREngine.Rendering;

namespace XREngine
{
    public static partial class Engine
    {
        /// <summary>
        /// Whether the engine is running in editor mode (as opposed to standalone game).
        /// This is set at startup and does not change during runtime.
        /// </summary>
        public static bool IsEditor { get; internal set; } = true;

        /// <summary>
        /// Result for window close requests.
        /// </summary>
        public enum WindowCloseRequestResult
        {
            Allow,
            Defer,
            Cancel,
        }

        /// <summary>
        /// Optional hook invoked when a window is about to close. Return Allow to proceed,
        /// Defer or Cancel to keep the window open.
        /// </summary>
        public static Func<XRWindow, WindowCloseRequestResult>? WindowCloseRequested;
        
        /// <summary>
        /// Whether the game is currently playing (simulation running).
        /// Delegates to PlayMode.IsPlaying for consistency.
        /// </summary>
        public static bool IsPlaying => PlayMode.IsPlaying;
        
        private static JobManager? _jobs;
        private static bool _jobsConfigured;
        private static bool _jobsCreatedImplicitly;

        public static JobManager Jobs
        {
            get
            {
                if (_jobs != null)
                    return _jobs;

                ConfigureJobManagerHooks();

                // If something touches Engine.Jobs before Engine.Initialize(), we still need
                // a functional job system. Create the default instance, but we will avoid
                // later recreation to keep a single instance alive.
                _jobsCreatedImplicitly = true;
                _jobsConfigured = false;
                _jobs = new JobManager();
                // NOTE: Don't call Debug.LogWarning here - it can trigger circular static init.
                // The warning will be logged later in ConfigureJobManager if needed.
                return _jobs;
            }
            private set => _jobs = value;
        }

        internal static void ConfigureJobManager(GameStartupSettings startupSettings)
        {
            if (_jobsConfigured)
                return;

            ConfigureJobManagerHooks();

            // If the job manager was created implicitly (accessed before Initialize),
            // shut down the default instance and replace it with a properly configured one
            // so that startup settings (worker count, queue limits, etc.) take effect.
            if (_jobsCreatedImplicitly && _jobs != null)
            {
                _jobs.Shutdown();
                _jobs = null;
                _jobsCreatedImplicitly = false;
            }

            // Use EffectiveSettings to resolve User > Project > Engine cascade
            Jobs = new JobManager(
                EffectiveSettings.JobWorkers,
                EffectiveSettings.JobQueueLimit,
                EffectiveSettings.JobQueueWarningThreshold,
                EffectiveSettings.JobWorkerCap);

            _jobsConfigured = true;
        }

        private static void ConfigureJobManagerHooks()
        {
            JobManager.LogMessage = message => Debug.Out(EOutputVerbosity.Normal, message);
            JobManager.ProfilerScopeFactory = static name => Engine.Profiler.Start(name);
        }

        public static GameState LoadOrGenerateGameState(
            Func<GameState>? generateFactory = null,
            string assetName = "state.asset",
            bool allowLoading = true)
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new GameState(), assetName, allowLoading);

        public static GameStartupSettings LoadOrGenerateGameSettings(
            Func<GameStartupSettings>? generateFactory = null,
            string assetName = "startup.asset",
            bool allowLoading = true)
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? GenerateGameSettings(), assetName, allowLoading);

        public static T LoadOrGenerateGameState<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
            Func<T>? generateFactory = null,
            string assetName = "state.asset",
            bool allowLoading = true) where T : GameState, new()
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new T(), assetName, allowLoading);

        public static T LoadOrGenerateGameSettings<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
            Func<T>? generateFactory = null,
            string assetName = "startup.asset",
            bool allowLoading = true) where T : GameStartupSettings, new()
            => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new T(), assetName, allowLoading);

        public static T LoadOrGenerateAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
            Func<T>? generateFactory,
            string assetName,
            bool allowLoading,
            params string[] folderNames) where T : XRAsset, new()
        {
            T? asset = null;
            if (allowLoading)
            {
                asset = Assets.LoadGameAsset<T>([.. folderNames, assetName]);
                if (asset != null)
                    return asset;
            }
            asset = generateFactory?.Invoke() ?? Activator.CreateInstance<T>();
            asset.Name = assetName;
            /*Task.Run(() => */Assets.SaveGameAssetTo(asset, folderNames)/*)*/;
            return asset;
        }

        private static GameStartupSettings GenerateGameSettings()
        {
            int w = 1920;
            int h = 1080;
            float updateHz = 90.0f;
            float renderHz = 90.0f;
            float fixedHz = 45.0f;

            // Reserve threads for so worker pool doesn't starve them.
            int reservedThreads = 4; // render + update + fixed-update + collectvisible
            int defaultWorkers = Math.Max(1, Environment.ProcessorCount - reservedThreads);
            int defaultWorkerCap = 16;
            if (defaultWorkers > defaultWorkerCap)
                defaultWorkers = defaultWorkerCap;

            int primaryX = NativeMethods.GetSystemMetrics(0);
            int primaryY = NativeMethods.GetSystemMetrics(1);

            return new GameStartupSettings()
            {
                StartupWindows =
                [
                    new()
                    {
                        WindowTitle = "XRENGINE",
                        TargetWorld = new Scene.XRWorld(),
                        WindowState = EWindowState.Windowed,
                        X = primaryX / 2 - w / 2,
                        Y = primaryY / 2 - h / 2,
                        Width = w,
                        Height = h,
                    }
                ],
                DefaultUserSettings = new UserSettings()
                {
                    VSync = EVSyncMode.Off,
                },
                TargetUpdatesPerSecond = updateHz,
                TargetFramesPerSecond = renderHz,
                FixedFramesPerSecond = fixedHz,
            };
        }

        public static class State
        {
            /// <summary>
            /// Called when a local player is first created.
            /// </summary>
            public static event Action<IPawnController>? LocalPlayerAdded;
            /// <summary>
            /// Called when a local player is removed.
            /// </summary>
            public static event Action<IPawnController>? LocalPlayerRemoved;

            //Only up to 4 local players, because we only support up to 4 players split screen, realistically. If that.
            public static IPawnController?[] LocalPlayers { get; } = new IPawnController[4];

            public static bool RemoveLocalPlayer(ELocalPlayerIndex index)
            {
                var player = LocalPlayers[(int)index];
                if (player is null)
                    return false;

                LocalPlayers[(int)index] = null;
                LocalPlayerRemoved?.Invoke(player);
                if (player is XRObjectBase obj)
                    obj.Destroy();
                return true;
            }

            /// <summary>
            /// Retrieves or creates a local player controller for the given index.
            /// </summary>
            /// <param name="index">Player slot to fetch.</param>
            /// <param name="controllerTypeOverride">Optional controller type to force for this request.</param>
            /// <returns>The resolved local player controller.</returns>
            public static IPawnController GetOrCreateLocalPlayer(
                ELocalPlayerIndex index,
                [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type? controllerTypeOverride = null)
            {
                var existing = LocalPlayers[(int)index];
                var desiredType = ResolveControllerType(controllerTypeOverride);

                if (existing is not null)
                {
                    if (desiredType.IsInstanceOfType(existing))
                        return existing;

                    // Preserve viewport bindings when swapping controller types so input devices stay wired up.
                    var viewportsToReassign = _windows
                        .SelectMany(w => w.Viewports)
                        .Where(vp => vp.AssociatedPlayer == existing)
                        .ToArray();

                    RemoveLocalPlayer(index);

                    var replacement = AddLocalPlayer(index, desiredType);

                    foreach (var viewport in viewportsToReassign)
                        viewport.AssociatedPlayer = replacement;

                    return replacement;
                }

                return AddLocalPlayer(index, desiredType);
            }

            /// <summary>
            /// This property returns the main player, which is the first player and should always exist.
            /// </summary>
            public static IPawnController MainPlayer => GetOrCreateLocalPlayer(ELocalPlayerIndex.One);

            private static IPawnController AddLocalPlayer(ELocalPlayerIndex index, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type controllerType)
            {
                var player = InstantiateController(controllerType, index);
                LocalPlayers[(int)index] = player;
                LocalPlayerAdded?.Invoke(player);
                return player;
            }

            [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
            internal static Type ResolveControllerType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type? controllerTypeOverride)
                => controllerTypeOverride
                    ?? Engine.PlayMode.ActiveGameMode?.PlayerControllerClass
                    ?? RuntimePlayerControllerServices.DefaultLocalControllerType
                    ?? throw new InvalidOperationException(
                        "No default local player controller type registered. " +
                        "Ensure XREngine.Runtime.InputIntegration is referenced and initialized.");

            internal static IPawnController InstantiateController([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type controllerType, ELocalPlayerIndex index)
            {
                if (!typeof(IPawnController).IsAssignableFrom(controllerType))
                    throw new ArgumentException($"Controller type {controllerType.FullName} must implement IPawnController", nameof(controllerType));

                var ctorWithIndex = controllerType.GetConstructor([typeof(ELocalPlayerIndex)]);
                var player = (ctorWithIndex is not null
                    ? ctorWithIndex.Invoke([index]) as IPawnController
                    : Activator.CreateInstance(controllerType) as IPawnController)
                    ?? throw new InvalidOperationException($"Failed to instantiate controller of type {controllerType.FullName}");

                // Set the player index through the interface if not set by the constructor.
                if (player.LocalPlayerIndex is null || player.LocalPlayerIndex != index)
                {
                    // The concrete controller's constructor should set the index, but for safety
                    // we allow writing through the interface's ControlledPawnComponent pattern.
                }
                return player;
            }

            internal static IPawnController InstantiateRemoteController(int serverPlayerIndex)
            {
                var remoteType = RuntimePlayerControllerServices.DefaultRemoteControllerType
                    ?? throw new InvalidOperationException(
                        "No default remote player controller type registered. " +
                        "Ensure XREngine.Runtime.InputIntegration is referenced and initialized.");

                var ctor = remoteType.GetConstructor([typeof(int)]);
                var player = (ctor is not null
                    ? ctor.Invoke([serverPlayerIndex]) as IPawnController
                    : Activator.CreateInstance(remoteType) as IPawnController)
                    ?? throw new InvalidOperationException($"Failed to instantiate remote controller of type {remoteType.FullName}");

                return player;
            }

            /// <summary>
            /// Gets the local player controller for the given index, if it exists.
            /// </summary>
            /// <param name="index"></param>
            /// <returns></returns>
            public static IPawnController? GetLocalPlayer(ELocalPlayerIndex index)
                => LocalPlayers.TryGet((int)index);

            /// <summary>
            /// All remote players that are connected to this server, this p2p client, or the server this client is connected to.
            /// </summary>
            public static List<IPawnController> RemotePlayers { get; } = [];
        }
    }
}
