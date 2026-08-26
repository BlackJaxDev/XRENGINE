using XREngine.Audio;
using XREngine.Components.Animation;
using XREngine.Components.Physics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Profiling;
using XREngine.Data.Trees;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.VideoStreaming;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;

namespace XREngine
{
    /// <summary>
    /// The root static class for the XREngine runtime.
    /// <para>
    /// This class serves as the central hub for all engine operations, managing:
    /// <list type="bullet">
    ///   <item><description>Engine lifecycle (initialization, game loop, shutdown)</description></item>
    ///   <item><description>Window and viewport management</description></item>
    ///   <item><description>Settings (user, game, editor preferences)</description></item>
    ///   <item><description>Threading and task scheduling</description></item>
    ///   <item><description>Networking (server, client)</description></item>
    ///   <item><description>VR initialization and state</description></item>
    ///   <item><description>World instance management</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The engine is organized with several static subclasses for managing different subsystems.
    /// You can use these subclasses without typing the whole path by adding 
    /// "using static XREngine.Engine.&lt;Subsystem&gt;;" at the top of your file.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para><b>Typical Usage:</b></para>
    /// <code>
    /// var settings = new GameStartupSettings { ... };
    /// var state = new GameState();
    /// Engine.Run(settings, state);
    /// </code>
    /// <para>
    /// For more control over the lifecycle:
    /// </para>
    /// <code>
    /// if (Engine.Initialize(settings, state))
    /// {
    ///     Engine.RunGameLoop();
    ///     Engine.BlockForRendering();
    /// }
    /// Engine.Cleanup();
    /// </code>
    /// <para>
    /// This partial class is split across multiple files:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Engine.cs</b> - Core fields, events, constructor, and basic properties</description></item>
    ///   <item><description><b>Engine.Threading.cs</b> - Threading properties and task scheduling</description></item>
    ///   <item><description><b>Engine.Lifecycle.cs</b> - Engine lifecycle (Run, Initialize, Cleanup)</description></item>
    ///   <item><description><b>RuntimeEngine.cs</b> - Runtime.Rendering-owned window and viewport registry</description></item>
    ///   <item><description><b>Engine.Settings.cs</b> - Settings properties and change handlers</description></item>
    ///   <item><description><b>Engine.Networking.cs</b> - Networking and VR initialization</description></item>
    ///   <item><description><b>Engine.ViewportRebind.cs</b> - Play mode diagnostics and viewport rebinding</description></item>
    /// </list>
    /// </remarks>
    public static partial class Engine
    {
        #region Private Fields

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // WINDOW MANAGEMENT
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Counter for suppressed cleanup requests to prevent premature shutdown.
        /// </summary>
        private static int _suppressedCleanupRequests;

        /// <summary>
        /// Set when bounded shutdown cannot establish a safe resource-destruction boundary.
        /// The process then exits without racing GPU/asset cleanup against live engine work.
        /// </summary>
        private static int _abandonProcessExitCleanup;
        private static IDisposable? _runtimeTimingLease;
        private static IDisposable? _runtimePhysicsLease;
        private static IDisposable? _runtimeStaticColliderAuthoringLease;

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // THREADING AND TASK QUEUES
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        private static int _isDispatchingRenderFrame;

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // SETTINGS BACKING FIELDS
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        private static UserSettings _userSettings = null!;
        private static GameStartupSettings _gameSettings = null!;
        private static EditorPreferences _globalEditorPreferences = null!;
        private static EditorPreferencesOverrides _editorPreferencesOverrides = null!;
        private static EditorPreferences _editorPreferences = null!;

        /// <summary>
        /// When true, settings cascades (Apply* methods) are suppressed.
        /// Used during static initialization when there are no worlds, viewports,
        /// or windows for the cascades to act on.
        /// </summary>
        private static bool _suppressSettingsCascades;
        private static int _settingsCascadeSuppressionDepth;
        private static bool _runtimeSettingsApplyPending;
        private static bool _editorPreferencesApplyPending;

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // OVERRIDEABLE SETTINGS TRACKING
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Tracks overrideable settings from <see cref="UserSettings"/>.
        /// </summary>
        private static readonly List<IOverrideableSetting> _trackedUserOverrideableSettings = [];

        /// <summary>
        /// Tracks overrideable settings from <see cref="GameSettings"/>.
        /// </summary>
        private static readonly List<IOverrideableSetting> _trackedGameOverrideableSettings = [];

        /// <summary>
        /// Tracks overrideable settings from <see cref="EditorPreferencesOverrides"/>.
        /// </summary>
        private static readonly List<IOverrideableSetting> _trackedEditorOverrideableSettings = [];

        /// <summary>
        /// Tracks overrideable theme settings from <see cref="EditorPreferencesOverrides.Theme"/>.
        /// </summary>
        private static readonly List<IOverrideableSetting> _trackedEditorThemeOverrideableSettings = [];

        /// <summary>
        /// Tracks overrideable debug settings from <see cref="EditorPreferencesOverrides.Debug"/>.
        /// </summary>
        private static readonly List<IOverrideableSetting> _trackedEditorDebugOverrideableSettings = [];

        /// <summary>
        /// Maps overrideable setting instances to their property names for change notification routing.
        /// </summary>
        private static readonly Dictionary<IOverrideableSetting, string> _overrideableSettingPropertyMap = new();

        #endregion

        #region Events

        /// <summary>
        /// Raised when <see cref="UserSettings"/> changes.
        /// </summary>
        public static XREvent<UserSettings>? UserSettingsChanged;

        /// <summary>
        /// Raised when <see cref="BuildSettings"/> changes.
        /// </summary>
        public static event Action<BuildSettings>? BuildSettingsChanged;

        /// <summary>
        /// Raised when effective <see cref="EditorPreferences"/> changes (after applying overrides).
        /// </summary>
        public static event Action<EditorPreferences>? EditorPreferencesChanged;

        /// <summary>
        /// Raised when any window gains or loses focus.
        /// The boolean parameter indicates whether any window is currently focused.
        /// </summary>
        public static XREvent<bool>? FocusChanged { get; set; }

        /// <summary>
        /// Raised during <see cref="Initialize(GameStartupSettings, GameState, bool)"/>
        /// after sandbox settings are loaded and immediately before startup windows are created.
        /// </summary>
        public static event Action<GameStartupSettings, GameState>? BeforeCreateWindows;

        /// <summary>
        /// Raised during <see cref="Initialize(GameStartupSettings, GameState, bool)"/>
        /// immediately after startup windows are created.
        /// </summary>
        public static event Action<GameStartupSettings, GameState>? AfterCreateWindows;

        #endregion

        #region Static Constructor

        /// <summary>
        /// Provides a profiler hook for external systems.
        /// </summary>
        private static IDisposable ExternalProfilingHook(string sampleName)
            => StartPooledProfilerScope(sampleName);

        /// <summary>
        /// Bridges the value-type code-profiler scope through interfaces that expose
        /// <see cref="IDisposable"/> without boxing a new scope on every hot-path call.
        /// </summary>
        internal static IDisposable StartPooledProfilerScope(string sampleName)
            => PooledExternalProfilerScope.Rent(Profiler.Start(sampleName));

        private sealed class PooledExternalProfilerScope : IDisposable
        {
            [ThreadStatic]
            private static Stack<PooledExternalProfilerScope>? t_available;

            private CodeProfiler.ProfilerScope _scope;
            private bool _active;

            public static PooledExternalProfilerScope Rent(CodeProfiler.ProfilerScope scope)
            {
                Stack<PooledExternalProfilerScope> available = t_available ??= new();
                PooledExternalProfilerScope wrapper =
                    available.Count != 0 ? available.Pop() : new PooledExternalProfilerScope();
                wrapper._scope = scope;
                wrapper._active = true;
                return wrapper;
            }

            public void Dispose()
            {
                if (!_active)
                    return;

                _active = false;
                _scope.Dispose();
                _scope = default;
                (t_available ??= new()).Push(this);
            }
        }

        private static readonly RuntimeRenderThreadHost s_renderThreadHost = new(
            () => WindowPumpHost.IsRunning,
            runUntilPredicate => Time.Timer.BlockForRendering(runUntilPredicate),
            () => Time.Timer.WaitToRender(),
            () => Time.Timer.Stop());
        private static readonly EngineWindowPumpHost s_windowPumpHost = new();

        internal static RuntimeRenderThreadHost RenderThreadHost => s_renderThreadHost;
        internal static EngineWindowPumpHost WindowPumpHost => s_windowPumpHost;
        internal static bool StartupOpenXrRuntimeRequested { get; private set; }
        public static global::XREngine.Rendering.WindowMailboxDiagnostics WindowThreadMailboxDiagnostics
            => s_windowPumpHost.Diagnostics;

        private static bool ExternalProfilingEnabledHook()
            => Profiler.EnableFrameLogging;

        private static object ExternalLinkedProfilingContextHook()
            => Profiler.CaptureLinkedChildContext();

        private static IDisposable ExternalLinkedProfilingHook(object? context, string sampleName)
        {
            CodeProfiler.ProfilerScope scope = context is CodeProfiler.LinkedScopeContext linkedContext
                ? Profiler.StartLinkedChild(linkedContext, sampleName, ProfilerScopeKind.OneOffInvoke)
                : Profiler.Start(sampleName, ProfilerScopeKind.OneOffInvoke);
            return PooledExternalProfilerScope.Rent(scope);
        }

        /// <summary>
        /// Static constructor that initializes default settings and wires up internal event handlers.
        /// </summary>
        static Engine()
        {
            // Claim the current thread as the render thread immediately so that
            // InvokeOnRenderThread executes inline during static init instead of
            // queuing to the not-yet-created job system.  Without this,
            // RenderThreadId == 0 while the main thread has a positive ID, causing
            // IsRenderThread to return false, which queues tasks, prematurely
            // creates a JobManager with worker threads, and can deadlock on the
            // type-initializer lock. Initialize() will re-set this to the same value.
            int bootstrapThreadId = Environment.CurrentManagedThreadId;
            RuntimeEngine.AssignRenderThread(bootstrapThreadId);
            RuntimeEngine.AssignWindowThread(bootstrapThreadId);

            // Suppress all settings cascades during type initialization.
            // No worlds, viewports, windows, or audio devices exist yet, so Apply
            // methods have nothing to act on and can prematurely create the job
            // system, spawn worker threads, or probe audio hardware—any of which
            // risk a type-initializer deadlock.  Initialize() will set the real
            // settings through the property setters and cascade properly.
            using (SuppressSettingsCascades(applyOnDispose: false))
            {
                // Initialize default settings objects
                UserSettings = new UserSettings();
                GameSettings = new GameStartupSettings();
                BuildSettings = new BuildSettings();
                GlobalEditorPreferences = new EditorPreferences();
                EditorPreferencesOverrides = new EditorPreferencesOverrides();
                _editorPreferences = new EditorPreferences();
                UpdateEffectiveEditorPreferences();
            }

            EngineRenderingSettingsApplication.InitializeSettingsApplicationBoundary();
            Debug.InitializeExceptionTracing();

            // Wire up timer events for deferred processing
            Time.Timer.PostUpdateFrame += Timer_PostUpdateFrame;
            RuntimeWorldObjectServices.Current = new EngineRuntimeWorldObjectServices();
            RuntimeThreadServices.Current = new EngineRuntimeThreadServices();
            RuntimeSceneImportServices.Current = new UnityEditorImportBridge();

            InstallRuntimePhysicsServices();
            RuntimeMaintenanceServices.Current = new EngineRuntimeMaintenanceServices();
            XREngine.Networking.RuntimeNetworkDiscoveryHostServices.Current = new EngineRuntimeNetworkDiscoveryHostServices();
            XREngine.Scene.RuntimeSceneNodeServices.Current = new EngineRuntimeSceneNodeServices();
            XREngine.Components.Scene.Volumes.RuntimeSceneStreamingHostServices.Current = new EngineRuntimeSceneStreamingHostServices();
            RuntimeTransformServices.Current = new EngineRuntimeTransformServices();

            // Connect external profiling hooks for subsystems
            XREvent.ProfilingHook = ExternalProfilingHook;
            XREvent.IsProfilingEnabledHook = ExternalProfilingEnabledHook;
            XREvent.CaptureLinkedProfilingContextHook = ExternalLinkedProfilingContextHook;
            XREvent.LinkedProfilingHook = ExternalLinkedProfilingHook;
            IRenderTree.ProfilingHook = ExternalProfilingHook;
            IRenderTree.OctreeStatsHook = (adds, moves, removes, skipped) =>
            {
                for (int i = 0; i < adds; i++) RuntimeEngine.Rendering.Stats.Octree.RecordOctreeAdd();
                for (int i = 0; i < moves; i++) RuntimeEngine.Rendering.Stats.Octree.RecordOctreeMove();
                for (int i = 0; i < removes; i++) RuntimeEngine.Rendering.Stats.Octree.RecordOctreeRemove();
            };
            IRenderTree.OctreeSwapTimingHook = RuntimeEngine.Rendering.Stats.Octree.RecordOctreeSwapTiming;
            IRenderTree.OctreeRaycastTimingHook = RuntimeEngine.Rendering.Stats.Octree.RecordOctreeRaycastTiming;

            // Snapshot restore can invalidate runtime-only bindings (viewport/world/camera).
            // Rebind right after restore (pre-BeginPlay) and once more after play begins.
            PlayMode.PostSnapshotRestore += OnPostSnapshotRestore_RebindRuntimeRendering;
            PlayMode.PostEnterPlay += OnPostEnterPlay_RebindRuntimeRendering;
        }

        #endregion

        private static void InstallRuntimeTimingServices()
        {
            _runtimeTimingLease?.Dispose();
            _runtimeTimingLease = XREngine.Timers.RuntimeTimingServices.Install(
                new EngineRuntimeTimingServices(Time.Timer));
        }

        private static void UninstallRuntimeTimingServices()
        {
            _runtimeTimingLease?.Dispose();
            _runtimeTimingLease = null;
        }

        private static void InstallRuntimePhysicsServices()
        {
            _runtimeStaticColliderAuthoringLease?.Dispose();
            _runtimePhysicsLease?.Dispose();

            var authoringServices = new EngineRuntimeStaticColliderAuthoringServices();
            _runtimePhysicsLease = RuntimePhysicsServices.Install(
                new EngineRuntimePhysicsServices(),
                new EngineConvexHullInputProvider());
            _runtimeStaticColliderAuthoringLease = RuntimeStaticColliderAuthoringServices.Install(authoringServices);
        }

        private static void UninstallRuntimePhysicsServices()
        {
            _runtimeStaticColliderAuthoringLease?.Dispose();
            _runtimeStaticColliderAuthoringLease = null;
            _runtimePhysicsLease?.Dispose();
            _runtimePhysicsLease = null;
        }

        #region Public Properties - Engine State

        /// <summary>
        /// Indicates the engine is currently starting up and might be still initializing objects.
        /// </summary>
        /// <remarks>
        /// During startup, certain operations may be deferred or behave differently.
        /// Check this property when you need to handle startup-specific logic.
        /// </remarks>
        public static bool StartingUp => RuntimeLifecycleState.Current.StartingUp;

        /// <summary>
        /// Indicates the engine is currently shutting down and might be disposing of objects.
        /// </summary>
        /// <remarks>
        /// During shutdown, avoid creating new resources or initiating long-running operations.
        /// </remarks>
        public static bool ShuttingDown => RuntimeLifecycleState.Current.ShuttingDown;

        /// <summary>
        /// Gets whether any engine window currently has focus.
        /// </summary>
        public static bool LastFocusState { get; private set; } = true;

        /// <summary>
        /// When enabled, windows render a minimal non-black startup presentation before heavier UI/world work is ready.
        /// </summary>
        public static bool StartupPresentationEnabled { get; set; }

        /// <summary>
        /// Clear color used for the temporary startup presentation.
        /// </summary>
        public static ColorF4 StartupPresentationClearColor { get; set; } = new(0.08f, 0.09f, 0.11f, 1.0f);

        #endregion

        #region Public Properties - Subsystems

        /// <summary>
        /// The networking manager for multiplayer communication.
        /// </summary>
        /// <remarks>
        /// Will be <c>null</c> for local-only games. Check <see cref="GameStartupSettings.NetworkingType"/>
        /// to determine the networking mode.
        /// </remarks>
        public static BaseNetworkingManager? Networking { get; private set; }

        /// <summary>
        /// Audio manager for playing and streaming sounds and music.
        /// </summary>
        public static AudioManager Audio { get; } = new();

        /// <summary>
        /// Manages all assets loaded into the engine.
        /// </summary>
        /// <remarks>
        /// Use this to load, cache, and manage the lifecycle of game assets.
        /// </remarks>
        public static AssetManager Assets { get; } = new(jobManagerProvider: static () => Jobs);

        /// <summary>
        /// Thread-safe random number generator for general use.
        /// </summary>
        public static Random Random { get; } = new();

        /// <summary>
        /// Code profiler for measuring performance and finding bottlenecks.
        /// </summary>
        /// <remarks>
        /// Use <c>using var scope = Engine.Profiler.Start("SampleName");</c> to profile code sections.
        /// </remarks>
        public static CodeProfiler Profiler { get; } = new();

        #endregion

        #region Public Properties - Collections

        /// <summary>
        /// All active world instances currently managed by the engine.
        /// </summary>
        /// <remarks>
        /// World instances are separate from windows, allowing multiple windows to display the same world.
        /// They are also distinct from <see cref="XRWorld"/>, which is just the serialized data for a world.
        /// </remarks>
        public static IReadOnlyCollection<RuntimeWorld> WorldInstances
            => RuntimeWorldRegistryServices.Current?.Snapshot().Values.ToArray() ?? [];

        #endregion

        #region Delegate Types

        /// <summary>
        /// Delegate for beginning a long-running operation with progress tracking.
        /// </summary>
        /// <param name="operationMessage">Message to display during the operation.</param>
        /// <param name="finishedMessage">Message to display when the operation completes.</param>
        /// <param name="progress">Progress reporter for tracking operation progress.</param>
        /// <param name="cancel">Cancellation token source to cancel the operation.</param>
        /// <param name="maxOperationTime">Optional maximum duration for the operation.</param>
        /// <returns>An operation ID for tracking.</returns>
        public delegate int DelBeginOperation(
            string operationMessage,
            string finishedMessage,
            out Progress<float> progress,
            out CancellationTokenSource cancel,
            TimeSpan? maxOperationTime = null);

        /// <summary>
        /// Delegate for ending a long-running operation.
        /// </summary>
        /// <param name="operationId">The operation ID returned by <see cref="DelBeginOperation"/>.</param>
        public delegate void DelEndOperation(int operationId);

        #endregion
    }
}
