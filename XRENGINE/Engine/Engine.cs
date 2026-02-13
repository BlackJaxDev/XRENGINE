using System.Collections.Concurrent;
using XREngine.Audio;
using XREngine.Data.Core;
using XREngine.Data.Trees;
using XREngine.Rendering;
using static XREngine.Rendering.XRWorldInstance;

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
    ///   <item><description>Networking (server, client, P2P)</description></item>
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
    ///   <item><description><b>Engine.Windows.cs</b> - Window and viewport management</description></item>
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
        /// Collection of all active engine windows.
        /// </summary>
        private static readonly EventList<XRWindow> _windows = [];

        /// <summary>
        /// Counter for suppressed cleanup requests to prevent premature shutdown.
        /// </summary>
        private static int _suppressedCleanupRequests;

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // THREADING AND TASK QUEUES
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Queue for tasks that must execute on the update thread.
        /// </summary>
        private static readonly ConcurrentQueue<Action> _pendingUpdateThreadWork = new();

        /// <summary>
        /// Queue for tasks that must execute on the physics thread.
        /// </summary>
        private static readonly ConcurrentQueue<Action> _pendingPhysicsThreadWork = new();

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // SETTINGS BACKING FIELDS
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        private static UserSettings _userSettings = null!;
        private static GameStartupSettings _gameSettings = null!;
        private static EditorPreferences _globalEditorPreferences = null!;
        private static EditorPreferencesOverrides _editorPreferencesOverrides = null!;
        private static EditorPreferences _editorPreferences = null!;

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

        #endregion

        #region Static Constructor

        /// <summary>
        /// Provides a profiler hook for external systems.
        /// </summary>
        private static IDisposable ExternalProfilingHook(string sampleName) => Profiler.Start(sampleName);

        /// <summary>
        /// Static constructor that initializes default settings and wires up internal event handlers.
        /// </summary>
        static Engine()
        {
            // Initialize default settings objects
            UserSettings = new UserSettings();
            GameSettings = new GameStartupSettings();
            BuildSettings = new BuildSettings();
            GlobalEditorPreferences = new EditorPreferences();
            EditorPreferencesOverrides = new EditorPreferencesOverrides();
            _editorPreferences = new EditorPreferences();
            UpdateEffectiveEditorPreferences();

            // Wire up timer events for deferred processing
            Time.Timer.PostUpdateFrame += Timer_PostUpdateFrame;

            // Connect external profiling hooks for subsystems
            XREvent.ProfilingHook = ExternalProfilingHook;
            IRenderTree.ProfilingHook = ExternalProfilingHook;
            IRenderTree.OctreeStatsHook = (adds, moves, removes, skipped) =>
            {
                for (int i = 0; i < adds; i++) Rendering.Stats.RecordOctreeAdd();
                for (int i = 0; i < moves; i++) Rendering.Stats.RecordOctreeMove();
                for (int i = 0; i < removes; i++) Rendering.Stats.RecordOctreeRemove();
            };

            // Snapshot restore can invalidate runtime-only bindings (viewport/world/camera).
            // Rebind right after restore (pre-BeginPlay) and once more after play begins.
            PlayMode.PostSnapshotRestore += OnPostSnapshotRestore_RebindRuntimeRendering;
            PlayMode.PostEnterPlay += OnPostEnterPlay_RebindRuntimeRendering;
        }

        #endregion

        #region Public Properties - Engine State

        /// <summary>
        /// Indicates the engine is currently starting up and might be still initializing objects.
        /// </summary>
        /// <remarks>
        /// During startup, certain operations may be deferred or behave differently.
        /// Check this property when you need to handle startup-specific logic.
        /// </remarks>
        public static bool StartingUp { get; private set; }

        /// <summary>
        /// Indicates the engine is currently shutting down and might be disposing of objects.
        /// </summary>
        /// <remarks>
        /// During shutdown, avoid creating new resources or initiating long-running operations.
        /// </remarks>
        public static bool ShuttingDown { get; private set; }

        /// <summary>
        /// Gets whether any engine window currently has focus.
        /// </summary>
        public static bool LastFocusState { get; private set; } = true;

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
        public static AssetManager Assets { get; } = new();

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
        public static IReadOnlyCollection<XRWorldInstance> WorldInstances => XRWorldInstance.WorldInstances.Values;

        /// <summary>
        /// The list of currently active and rendering windows.
        /// </summary>
        public static IEventListReadOnly<XRWindow> Windows => _windows;

        public enum EViewportEnumerationMode
        {
            ExcludeVrEyeViewports,
            IncludeVrEyeViewports,
        }

        /// <summary>
        /// Enumerates all active viewports across all active windows.
        /// </summary>
        public static IEnumerable<XRViewport> EnumerateActiveViewports(EViewportEnumerationMode mode = EViewportEnumerationMode.ExcludeVrEyeViewports)
        {
            foreach (XRWindow window in _windows)
                foreach (XRViewport viewport in window.Viewports)
                    yield return viewport;

            if (mode != EViewportEnumerationMode.IncludeVrEyeViewports)
                yield break;

            XRViewport? leftEye = VRState.LeftEyeViewport;
            if (leftEye is not null && !IsViewportInAnyActiveWindow(leftEye))
                yield return leftEye;

            XRViewport? rightEye = VRState.RightEyeViewport;
            if (rightEye is not null && !IsViewportInAnyActiveWindow(rightEye))
                yield return rightEye;
        }

        /// <summary>
        /// Enumerates all active viewports from the render thread and returns a stable snapshot.
        /// </summary>
        /// <remarks>
        /// If called from a non-render thread, work is enqueued to the render thread and this method blocks
        /// until the snapshot has been produced.
        /// </remarks>
        public static IReadOnlyList<XRViewport> EnumerateActiveViewportsOnMainThread(EViewportEnumerationMode mode = EViewportEnumerationMode.ExcludeVrEyeViewports)
        {
            if (IsRenderThread)
                return [.. EnumerateActiveViewports(mode)];

            var completion = new TaskCompletionSource<IReadOnlyList<XRViewport>>(TaskCreationOptions.RunContinuationsAsynchronously);
            EnqueueMainThreadTask(() =>
            {
                try
                {
                    completion.TrySetResult([.. EnumerateActiveViewports(mode)]);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, "Engine.EnumerateActiveViewportsOnMainThread");

            return completion.Task.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Enumerates active viewports for a specific active window.
        /// </summary>
        public static IEnumerable<XRViewport> EnumerateActiveViewports(XRWindow? window, EViewportEnumerationMode mode = EViewportEnumerationMode.ExcludeVrEyeViewports)
        {
            if (window is null)
                yield break;

            foreach (XRViewport viewport in window.Viewports)
                yield return viewport;

            if (mode != EViewportEnumerationMode.IncludeVrEyeViewports)
                yield break;

            XRViewport? leftEye = VRState.LeftEyeViewport;
            if (leftEye is not null && ReferenceEquals(leftEye.Window, window) && !window.Viewports.Contains(leftEye))
                yield return leftEye;

            XRViewport? rightEye = VRState.RightEyeViewport;
            if (rightEye is not null && ReferenceEquals(rightEye.Window, window) && !window.Viewports.Contains(rightEye))
                yield return rightEye;
        }

        /// <summary>
        /// Enumerates all active (window, viewport) pairs across active windows.
        /// </summary>
        public static IEnumerable<(XRWindow Window, XRViewport Viewport)> EnumerateActiveWindowViewports(EViewportEnumerationMode mode = EViewportEnumerationMode.ExcludeVrEyeViewports)
        {
            foreach (XRWindow window in _windows)
                foreach (XRViewport viewport in window.Viewports)
                    yield return (window, viewport);

            if (mode != EViewportEnumerationMode.IncludeVrEyeViewports)
                yield break;

            XRViewport? leftEye = VRState.LeftEyeViewport;
            if (leftEye?.Window is XRWindow leftWindow && !leftWindow.Viewports.Contains(leftEye))
                yield return (leftWindow, leftEye);

            XRViewport? rightEye = VRState.RightEyeViewport;
            if (rightEye?.Window is XRWindow rightWindow && !rightWindow.Viewports.Contains(rightEye))
                yield return (rightWindow, rightEye);
        }

        private static bool IsViewportInAnyActiveWindow(XRViewport viewport)
        {
            foreach (XRWindow window in _windows)
            {
                if (window.Viewports.Contains(viewport))
                    return true;
            }

            return false;
        }

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
