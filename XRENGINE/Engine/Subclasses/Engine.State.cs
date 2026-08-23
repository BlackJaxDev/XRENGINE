using XREngine.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Execution;
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
        private static readonly object _jobsConfigurationSync = new();
        private static bool _jobsConfigured;
        private static bool _jobsCreatedImplicitly;
        private static int _jobsConfigurationState;

        /// <summary>
        /// Gets the immutable process execution budget resolved during startup.
        /// Its render-lane count configures the renderer-neutral scheduler;
        /// Vulkan recording remains on its legacy workers until migration.
        /// </summary>
        public static EngineExecutionTopology? ExecutionTopology { get; private set; }

        /// <summary>
        /// One process-wide owner for general and renderer-neutral render work.
        /// Vulkan and OpenXR keep their legacy recording workers during Phase 1B.
        /// </summary>
        public static EngineWorkScheduler? WorkScheduler { get; private set; }

        public static JobManager Jobs
        {
            get
            {
                lock (_jobsConfigurationSync)
                {
                    while (_jobs is null && _jobsConfigurationState == 1)
                        Monitor.Wait(_jobsConfigurationSync);

                    if (_jobs != null)
                        return _jobs;

                    ConfigureJobManagerHooks();

                    // If something touches Engine.Jobs before Engine.Initialize(), we still need
                    // a functional job system. Publication is serialized so concurrent first
                    // access cannot create an overwritten/orphaned worker domain.
                    _jobsCreatedImplicitly = true;
                    _jobsConfigured = false;
                    _jobs = new JobManager();
                    // NOTE: Don't call Debug.LogWarning here - it can trigger circular static init.
                    // The warning will be logged later in ConfigureJobManager if needed.
                    return _jobs;
                }
            }
            private set => _jobs = value;
        }

        internal static void ConfigureJobManager(GameStartupSettings startupSettings)
        {
            JobManager? implicitManager;
            lock (_jobsConfigurationSync)
            {
                while (_jobsConfigurationState == 1)
                    Monitor.Wait(_jobsConfigurationSync);

                if (_jobsConfigured)
                    return;

                _jobsConfigurationState = 1;
                ConfigureJobManagerHooks();
                implicitManager = _jobsCreatedImplicitly ? _jobs : null;
            }

            try
            {
                // Keep an implicit manager published while stopping it outside the
                // configuration lock. A worker that resolves Engine.Jobs during its
                // own shutdown sees the same manager instead of blocking the join or
                // constructing a second pool.
                if (implicitManager is not null && !implicitManager.Shutdown(waitForWorkers: true))
                {
                    throw new InvalidOperationException(
                        "The implicit JobManager did not quiesce within the fatal lifecycle bound; " +
                        "installing the process scheduler would create a second worker domain.");
                }

                EngineExecutionTopology topology = ExecutionTopology ?? ResolveExecutionTopology();

                var scheduler = new EngineWorkScheduler(
                    topology,
                    EffectiveSettings.JobQueueLimit,
                    EffectiveSettings.JobQueueWarningThreshold);

                lock (_jobsConfigurationSync)
                {
                    WorkScheduler = scheduler;
                    Jobs = scheduler.GeneralJobs;
                    _jobsCreatedImplicitly = false;
                    _jobsConfigured = true;
                    _jobsConfigurationState = 2;
                    Monitor.PulseAll(_jobsConfigurationSync);
                }
            }
            catch
            {
                lock (_jobsConfigurationSync)
                {
                    _jobsConfigurationState = 0;
                    Monitor.PulseAll(_jobsConfigurationSync);
                }

                throw;
            }
        }

        internal static EngineExecutionTopology ResolveExecutionTopology()
        {
            int generalWorkers = ReadExecutionIntegerOverride(
                XREngineEnvironmentVariables.JobWorkers,
                EffectiveSettings.GeneralWorkerThreadCount,
                MapExecutionSettingSource(EffectiveSettings.GetGeneralWorkerThreadCountSource()),
                out EEngineExecutionSettingSource generalWorkersSource);
            int generalWorkerCap = ReadExecutionIntegerOverride(
                XREngineEnvironmentVariables.JobWorkerCap,
                EffectiveSettings.GeneralWorkerThreadCap,
                MapExecutionSettingSource(EffectiveSettings.GetGeneralWorkerThreadCapSource()),
                out EEngineExecutionSettingSource generalWorkerCapSource);
            int renderWorkers = ReadExecutionIntegerOverride(
                XREngineEnvironmentVariables.RenderWorkerThreads,
                EffectiveSettings.RenderWorkerThreadCount,
                MapExecutionSettingSource(EffectiveSettings.GetRenderWorkerThreadCountSource()),
                out EEngineExecutionSettingSource renderWorkersSource);
            int renderWorkerCap = ReadExecutionIntegerOverride(
                XREngineEnvironmentVariables.RenderWorkerThreadCap,
                EffectiveSettings.RenderWorkerThreadCap,
                MapExecutionSettingSource(EffectiveSettings.GetRenderWorkerThreadCapSource()),
                out EEngineExecutionSettingSource renderWorkerCapSource);
            int foregroundReservation = ReadExecutionIntegerOverride(
                XREngineEnvironmentVariables.ReservedForegroundThreads,
                EffectiveSettings.ReservedForegroundThreadCount,
                MapExecutionSettingSource(EffectiveSettings.GetReservedForegroundThreadCountSource()),
                out EEngineExecutionSettingSource foregroundReservationSource);
            bool allowOversubscription = ReadExecutionBooleanOverride(
                XREngineEnvironmentVariables.AllowCpuOversubscription,
                EffectiveSettings.AllowCpuOversubscription,
                MapExecutionSettingSource(EffectiveSettings.GetAllowCpuOversubscriptionSource()),
                out EEngineExecutionSettingSource allowOversubscriptionSource);
            ERenderWorkerQos renderWorkerQos = ReadExecutionEnumOverride(
                XREngineEnvironmentVariables.RenderWorkerQos,
                EffectiveSettings.RenderWorkerQos,
                MapExecutionSettingSource(EffectiveSettings.GetRenderWorkerQosSource()),
                out EEngineExecutionSettingSource renderWorkerQosSource);

            var request = new EngineExecutionTopologyRequest
            {
                EffectiveProcessorCount = Environment.ProcessorCount,
                GeneralWorkerThreadCount = generalWorkers,
                GeneralWorkerThreadCap = generalWorkerCap,
                RenderWorkerThreadCount = renderWorkers,
                RenderWorkerThreadCap = renderWorkerCap,
                ReservedForegroundThreadCount = foregroundReservation,
                DedicatedBackgroundThreadCount = 0,
                AllowCpuOversubscription = allowOversubscription,
                RenderWorkerQos = renderWorkerQos,
                GeneralWorkerThreadCountSource = generalWorkersSource,
                GeneralWorkerThreadCapSource = generalWorkerCapSource,
                RenderWorkerThreadCountSource = renderWorkersSource,
                RenderWorkerThreadCapSource = renderWorkerCapSource,
                ReservedForegroundThreadCountSource = foregroundReservationSource,
                AllowCpuOversubscriptionSource = allowOversubscriptionSource,
                RenderWorkerQosSource = renderWorkerQosSource,
                ForegroundThreadNames =
                [
                    "render/window",
                    "update",
                    "fixed-update",
                    "collect-visible/swap",
                ],
            };

            EngineExecutionTopology topology = EngineExecutionTopology.Resolve(request);
            ExecutionTopology = topology;
            Debug.Out(topology.CreateDiagnosticSummary());
            return topology;
        }

        private static int ReadExecutionIntegerOverride(
            string variableName,
            int fallback,
            EEngineExecutionSettingSource fallbackSource,
            out EEngineExecutionSettingSource source)
        {
            string? raw = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                source = fallbackSource;
                return fallback;
            }

            if (!int.TryParse(raw.Trim(), out int value))
            {
                throw new InvalidOperationException(
                    $"Environment variable {variableName} must be an integer; received '{raw}'.");
            }

            source = EEngineExecutionSettingSource.Environment;
            return value;
        }

        private static bool ReadExecutionBooleanOverride(
            string variableName,
            bool fallback,
            EEngineExecutionSettingSource fallbackSource,
            out EEngineExecutionSettingSource source)
        {
            string? raw = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                source = fallbackSource;
                return fallback;
            }

            string normalized = raw.Trim();
            bool value = normalized switch
            {
                "1" => true,
                "0" => false,
                _ when bool.TryParse(normalized, out bool parsed) => parsed,
                _ => throw new InvalidOperationException(
                    $"Environment variable {variableName} must be true, false, 1, or 0; received '{raw}'."),
            };

            source = EEngineExecutionSettingSource.Environment;
            return value;
        }

        private static TEnum ReadExecutionEnumOverride<TEnum>(
            string variableName,
            TEnum fallback,
            EEngineExecutionSettingSource fallbackSource,
            out EEngineExecutionSettingSource source)
            where TEnum : struct, Enum
        {
            string? raw = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                source = fallbackSource;
                return fallback;
            }

            if (!Enum.TryParse(raw.Trim(), ignoreCase: true, out TEnum value) || !Enum.IsDefined(value))
            {
                throw new InvalidOperationException(
                    $"Environment variable {variableName} has unknown {typeof(TEnum).Name} value '{raw}'.");
            }

            source = EEngineExecutionSettingSource.Environment;
            return value;
        }

        private static EEngineExecutionSettingSource MapExecutionSettingSource(EffectiveSettings.SettingSource source)
            => source switch
            {
                EffectiveSettings.SettingSource.Project => EEngineExecutionSettingSource.Project,
                EffectiveSettings.SettingSource.User => EEngineExecutionSettingSource.User,
                _ => EEngineExecutionSettingSource.EngineDefault,
            };

        private static void ConfigureJobManagerHooks()
        {
            JobManager.LogMessage = LogJobManagerMessage;
            JobManager.ProfilerScopeFactory = static name => Engine.Profiler.Start(name);
            JobManager.JobDispatchObserver = static (affinity, label, kind) => ObserveJobDispatch(affinity, label, kind);
            JobManager.RenderThreadJobExecutionObserver =
                static (label, kind, durationMs, queueDelayMs, overBudgetMs) =>
                    ObserveRenderThreadJobExecution(label, kind, durationMs, queueDelayMs, overBudgetMs);
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
            // Bypass the JobManager for startup-time asset loads/saves. These are invoked on the editor
            // main thread before any windows or render threads exist, and the job worker pool is sized
            // very small at startup (ProcessorCount - reserved, capped at 16). Cascaded loads inside
            // LoadCore can themselves call RunOnJobThreadBlocking, which saturates the pool and deadlocks
            // the main thread waiting on tcs.Task.GetAwaiter().GetResult(). Running inline avoids that.
            T? asset = null;
            if (allowLoading)
            {
                asset = Assets.LoadGameAsset<T>(JobPriority.Normal, bypassJobThread: true, [.. folderNames, assetName]);
                if (asset != null)
                    return asset;
            }
            asset = generateFactory?.Invoke() ?? new T();
            asset.Name = assetName;
            string saveDirectory = System.IO.Path.Combine(Assets.GameAssetsPath, System.IO.Path.Combine(folderNames));
            Assets.SaveToImmediate(asset, saveDirectory);
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

                    // Preserve runtime-only bindings when swapping controller types so input devices,
                    // viewports, and already-constructed editor pawns stay wired up.
                    XRComponent? controlledPawn = existing.ControlledPawnComponent;
                    var viewportsToReassign = RuntimeEngine.Windows
                        .SelectMany(w => w.Viewports)
                        .Where(vp => vp.AssociatedPlayer == existing)
                        .ToArray();

                    RemoveLocalPlayer(index);

                    var replacement = AddLocalPlayer(index, desiredType);
                    if (controlledPawn is not null)
                        replacement.ControlledPawnComponent = controlledPawn;

                    foreach (var viewport in viewportsToReassign)
                        viewport.AssociatedPlayer = replacement;

                    replacement.OnPawnCameraChanged();
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

                if (!RuntimePlayerControllerServices.TryCreateLocalController(controllerType, index, out IPawnController? player) || player is null)
                {
                    if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                        throw new InvalidOperationException($"No registered local player controller factory for type {controllerType.FullName}.");

                    var ctorWithIndex = controllerType.GetConstructor([typeof(ELocalPlayerIndex)]);
                    player = (ctorWithIndex is not null
                        ? ctorWithIndex.Invoke([index]) as IPawnController
                        : Activator.CreateInstance(controllerType) as IPawnController)
                        ?? throw new InvalidOperationException($"Failed to instantiate controller of type {controllerType.FullName}");
                }

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

                if (!RuntimePlayerControllerServices.TryCreateRemoteController(remoteType, serverPlayerIndex, out IPawnController? player) || player is null)
                {
                    if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                        throw new InvalidOperationException($"No registered remote player controller factory for type {remoteType.FullName}.");

                    var ctor = remoteType.GetConstructor([typeof(int)]);
                    player = (ctor is not null
                        ? ctor.Invoke([serverPlayerIndex]) as IPawnController
                        : Activator.CreateInstance(remoteType) as IPawnController)
                        ?? throw new InvalidOperationException($"Failed to instantiate remote controller of type {remoteType.FullName}");
                }

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
