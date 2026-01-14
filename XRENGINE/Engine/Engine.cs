using ExCSS;
using Jitter2;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Audio;
using XREngine.Data.Core;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using static XREngine.Rendering.XRWorldInstance;

namespace XREngine
{
    /// <summary>
    /// The root static class for the engine.
    /// Contains all the necessary functions to run the engine and manage its components.
    /// Organized with several static subclasses for managing different parts of the engine.
    /// You can use these subclasses without typing the whole path every time in your code by adding "using static XREngine.Engine.<path>;" at the top of the file.
    /// </summary>
    public static partial class Engine
    {
        private static readonly EventList<XRWindow> _windows = [];
        private static readonly ConcurrentQueue<Action> _pendingUpdateThreadWork = new();
        private static readonly ConcurrentQueue<Action> _pendingPhysicsThreadWork = new();
        private static int _suppressedCleanupRequests;
        private static UserSettings _userSettings = null!;
        private static GameStartupSettings _gameSettings = null!;
        private static readonly List<IOverrideableSetting> _trackedUserOverrideableSettings = [];
        private static readonly List<IOverrideableSetting> _trackedGameOverrideableSettings = [];

        public static XREvent<UserSettings>? UserSettingsChanged;
        public static event Action<BuildSettings>? BuildSettingsChanged;

        static IDisposable ExternalProfilingHook(string sampleName) => Profiler.Start(sampleName);

        static Engine()
        {
            UserSettings = new UserSettings();
            GameSettings = new GameStartupSettings();
            BuildSettings = new BuildSettings();

            // Effective settings depend on these sources; forward changes.
            UserSettingsChanged += _ => EffectiveSettings.NotifyEffectiveSettingsChanged();
            Rendering.SettingsChanged += EffectiveSettings.NotifyEffectiveSettingsChanged;
            EffectiveSettings.EffectiveSettingsChanged += ApplyEffectiveSettingsRuntime;

            Time.Timer.PostUpdateFrame += Timer_PostUpdateFrame;

            XREvent.ProfilingHook = ExternalProfilingHook;
            IRenderTree.ProfilingHook = ExternalProfilingHook;

            // Snapshot restore can invalidate runtime-only bindings (viewport/world/camera).
            // Rebind right after restore (pre-BeginPlay) and once more after play begins.
            PlayMode.PostSnapshotRestore += OnPostSnapshotRestore_RebindRuntimeRendering;
            PlayMode.PostEnterPlay += OnPostEnterPlay_RebindRuntimeRendering;
        }

        private static void OnPostSnapshotRestore_RebindRuntimeRendering(XRWorld? restoredWorld)
            => RebindRuntimeRendering(restoredWorld, "PostSnapshotRestore");

        private static void OnPostEnterPlay_RebindRuntimeRendering()
            => RebindRuntimeRendering(ResolveStartupWorldForRebind(), "PostEnterPlay");

        private static void LogViewportRebindSummary(string phase, XRWorldInstance worldInstance)
        {

            string worldName = worldInstance.TargetWorld?.Name ?? "<unknown>";
            Debug.RenderingEvery(
                $"ViewportRebind.{phase}.Summary.{worldInstance.GetHashCode()}",
                TimeSpan.FromSeconds(0.5),
                "[ViewportDiag] {0}: PlayMode={1} World={2} Windows={3}",
                phase,
                PlayMode.State,
                worldName,
                _windows.Count);

            for (int i = 0; i < State.LocalPlayers.Length; i++)
            {
                var player = State.LocalPlayers[i];
                string playerType = player?.GetType().Name ?? "<null>";
                string pawnName = player?.ControlledPawn?.Name ?? "<null>";
                int playerHash = player?.GetHashCode() ?? 0;
                int viewportHash = player?.Viewport?.GetHashCode() ?? 0;
                bool hasCamera = player?.ControlledPawn?.GetCamera() is not null;

                Debug.RenderingEvery(
                    $"ViewportRebind.{phase}.Player.{worldInstance.GetHashCode()}.{i}",
                    TimeSpan.FromSeconds(0.5),
                    "[ViewportDiag] {0}: P{1} CtrlType={2} CtrlHash={3} ViewportHash={4} Pawn={5} PawnHasCamera={6}",
                    phase,
                    i + 1,
                    playerType,
                    playerHash,
                    viewportHash,
                    pawnName,
                    hasCamera);
            }
        }

        private static XRWorld? ResolveStartupWorldForRebind()
        {
            if (PlayMode.Configuration.StartupWorld is not null)
                return PlayMode.Configuration.StartupWorld;

            var firstWindowWorld = Windows.FirstOrDefault()?.TargetWorldInstance?.TargetWorld;
            if (firstWindowWorld is not null)
                return firstWindowWorld;

            var firstInstanceWorld = XRWorldInstance.WorldInstances.Values.FirstOrDefault()?.TargetWorld;
            return firstInstanceWorld;
        }

        private static void RebindRuntimeRendering(XRWorld? world, string phase)
        {
            if (world is null)
                return;

            try
            {
                XRWorldInstance? worldInstance = XRWorldInstance.GetOrInitWorld(world);

                LogViewportRebindSummary(phase, worldInstance);

                //if (Environment.GetEnvironmentVariable("XRE_DEBUG_RENDER_DUMP") == "1")
                    DumpWorldRenderablesOncePerPhase(worldInstance, phase);

                //if (Environment.GetEnvironmentVariable("XRE_DEBUG_SNAPSHOT_HIERARCHY") == "1")
                    DumpWorldHierarchyRootsOncePerPhase(worldInstance, phase);

                foreach (var window in _windows)
                {
                    Debug.RenderingEvery(
                        $"ViewportRebind.{phase}.Window.{window.GetHashCode()}",
                        TimeSpan.FromSeconds(0.5),
                        "[ViewportDiag] {0}: WindowHash={1} TargetWorldMatch={2} Viewports={3} PresentationMode={4}",
                        phase,
                        window.GetHashCode(),
                        ReferenceEquals(window.TargetWorldInstance, worldInstance),
                        window.Viewports.Count,
                        Engine.Rendering.Settings.ViewportPresentationMode);

                    // Ensure the window is targeting the restored world instance.
                    if (!ReferenceEquals(window.TargetWorldInstance, worldInstance))
                        window.TargetWorldInstance = worldInstance;

                    // If a window ended up with zero viewports (runtime-only), log it loudly.
                    // We do not auto-create here unless explicitly needed; call sites can decide.
                    if (window.Viewports.Count == 0)
                    {
                        Debug.RenderingWarningEvery(
                            $"ViewportRebind.{phase}.WindowNoViewports.{window.GetHashCode()}",
                            TimeSpan.FromSeconds(0.5),
                            "[ViewportDiag] {0}: WindowHash={1} has 0 viewports. LocalPlayers={2}",
                            phase,
                            window.GetHashCode(),
                            State.LocalPlayers.Count(p => p is not null));
                    }

                    // Ensure viewports are linked to this window and have a world override.
                    foreach (var viewport in window.Viewports)
                    {
                        viewport.Window = window;

                        // Repair stale player-controller references.
                        // Snapshot restore (and controller type swaps) can leave XRViewport.AssociatedPlayer
                        // pointing at a controller instance that is no longer the one stored in Engine.State.LocalPlayers.
                        var associated = viewport.AssociatedPlayer;
                        if (associated is not null)
                        {
                            var current = State.GetLocalPlayer(associated.LocalPlayerIndex) ?? State.GetOrCreateLocalPlayer(associated.LocalPlayerIndex);
                            if (!ReferenceEquals(current, associated))
                            {
                                Debug.Out(
                                    "[{0}] Rebind: viewport {1} had stale AssociatedPlayer. OldHash={2} NewHash={3} Index={4}",
                                    phase,
                                    viewport.Index,
                                    associated.GetHashCode(),
                                    current.GetHashCode(),
                                    associated.LocalPlayerIndex);
                                viewport.AssociatedPlayer = current;
                            }
                        }
                        else
                        {
                            // Try to infer association from the player's Viewport pointer (also runtime-only).
                            // This helps when the viewport survived but the association was lost.
                            var inferred = State.LocalPlayers.FirstOrDefault(p => p is not null && ReferenceEquals(p.Viewport, viewport));
                            if (inferred is not null)
                            {
                                Debug.Out("[{0}] Rebind: inferred AssociatedPlayer for viewport {1} -> P{2}", phase, viewport.Index, (int)inferred.LocalPlayerIndex + 1);
                                viewport.AssociatedPlayer = inferred;
                            }
                        }

                        // LocalPlayerController.Viewport is runtime-only ([YamlIgnore]) and can be lost across snapshot restore.
                        // Without it, RefreshViewportCamera() cannot rebind the viewport's CameraComponent (resulting in black output).
                        if (viewport.AssociatedPlayer is not null && !ReferenceEquals(viewport.AssociatedPlayer.Viewport, viewport))
                            viewport.AssociatedPlayer.Viewport = viewport;

                        if (viewport.AssociatedPlayer is not null && viewport.WorldInstanceOverride is null)
                            viewport.WorldInstanceOverride = worldInstance;

                        // Rebind camera from controlled pawn (may have changed across restore / BeginPlay).
                        // NOTE: During snapshot restore (especially when exiting play), the ControlledPawn can be
                        // temporarily null or mid-destruction. Calling RefreshViewportCamera() in that window will
                        // actively clear VP.CameraComponent, causing black output until something later rebinds it.
                        var playerForRebind = viewport.AssociatedPlayer;
                        var playerPawnCamera = playerForRebind?.ControlledPawn?.GetCamera();
                        if (playerForRebind is not null && playerPawnCamera is not null)
                        {
                            playerForRebind.RefreshViewportCamera();
                        }
                        else
                        {
                            Debug.Out(
                                "[{0}] Rebind: skipping RefreshViewportCamera for VP[{1}] (player={2}) because ControlledPawn camera is null.",
                                phase,
                                viewport.Index,
                                playerForRebind?.LocalPlayerIndex.ToString() ?? "<null>");
                        }

                        var p = viewport.AssociatedPlayer;
                        var pawn = p?.ControlledPawn;
                        var pawnCam = pawn?.GetCamera();
                        var cam = viewport.ActiveCamera;
                        int camViewportCount = cam?.Viewports.Count ?? 0;
                        Debug.RenderingEvery(
                            $"ViewportRebind.{phase}.VP.{window.GetHashCode()}.{viewport.Index}",
                            TimeSpan.FromSeconds(0.5),
                            "[ViewportDiag] {0}: Win={1} VP[{2}] AssocP={3} PHash={4} P.ViewportMatch={5} Pawn={6} PawnCamNull={7} VP.CameraComponentNull={8} ActiveCameraNull={9} Camera.Viewports={10} WorldNull={11}",
                            phase,
                            window.GetHashCode(),
                            viewport.Index,
                            p is null ? "<null>" : $"P{(int)p.LocalPlayerIndex + 1}",
                            p?.GetHashCode() ?? 0,
                            p is not null && ReferenceEquals(p.Viewport, viewport),
                            pawn?.Name ?? "<null>",
                            pawnCam is null,
                            viewport.CameraComponent is null,
                            cam is null,
                            camViewportCount,
                            viewport.World is null);

                        // Snapshot restore can invalidate cached GPU resources (textures/FBOs/programs).
                        // If the pipeline keeps references to objects whose underlying API handles were destroyed,
                        // the final blit can end up sampling black.
                        if (phase is "PostSnapshotRestore" or "PostEnterPlay")
                        {
                            var capturedViewport = viewport;
                            EnqueueSwapTask(() =>
                            {
                                try
                                {
                                    capturedViewport.RenderPipelineInstance.DestroyCache();
                                }
                                catch (Exception ex)
                                {
                                    Debug.LogException(ex, $"[{phase}] Failed to destroy render cache for viewport {capturedViewport.Index}.");
                                }
                            });
                        }

                        // Only warn when we *expected* a camera/world to exist (i.e. the player has a pawn camera).
                        // During PostSnapshotRestore the pawn can be transiently null while higher-level systems (editor/game mode)
                        // re-possess or spawn the correct pawn.
                        if (viewport.ActiveCamera is null && viewport.AssociatedPlayer?.ControlledPawn?.GetCamera() is not null)
                            Debug.LogWarning($"[{phase}] Viewport {viewport.Index} has no ActiveCamera (player={viewport.AssociatedPlayer?.LocalPlayerIndex}).");
                        if (viewport.World is null && viewport.AssociatedPlayer?.ControlledPawn?.GetCamera() is not null)
                            Debug.LogWarning($"[{phase}] Viewport {viewport.Index} has no World (player={viewport.AssociatedPlayer?.LocalPlayerIndex}).");
                    }

                    // If viewports exist but no players are associated, keep at least player one wired.
                    if (window.Viewports.Count > 0 && window.Viewports.All(vp => vp.AssociatedPlayer is null))
                    {
                        var mainPlayer = State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One);
                        window.RegisterController(mainPlayer, autoSizeAllViewports: false);
                        mainPlayer.RefreshViewportCamera();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed runtime rendering rebind during {phase}");
            }
        }

        private static void DumpWorldRenderablesOncePerPhase(XRWorldInstance worldInstance, string phase)
        {
            Debug.RenderingEvery(
                $"RenderDump.World.{phase}.{worldInstance.GetHashCode()}",
                TimeSpan.FromDays(1),
                "[RenderDiag] World dump ({0}). World={1} Roots={2} VisualScene={3} TrackedRenderables={4}",
                phase,
                worldInstance.TargetWorld?.Name ?? "<unknown>",
                worldInstance.RootNodes.Count,
                worldInstance.VisualScene?.GetType().Name ?? "<null>",
                worldInstance.VisualScene?.Renderables?.Count ?? -1);

            int nodeCount = 0;
            int renderableComponentCount = 0;
            int meshCount = 0;
            int meshRenderCommandCount = 0;
            int iRenderableCount = 0;
            int iRenderableWithWorldCount = 0;
            int totalRenderInfoCount = 0;
            var componentTypes = new List<string>();

            static bool TryGetRenderedObjects(object? component, out System.Collections.IEnumerable? renderedObjects)
            {
                renderedObjects = null;
                if (component is null)
                    return false;

                var renderedObjectsProperty = component.GetType().GetProperty(
                    "RenderedObjects",
                    BindingFlags.Public | BindingFlags.Instance);

                if (renderedObjectsProperty is null)
                    return false;

                renderedObjects = renderedObjectsProperty.GetValue(component) as System.Collections.IEnumerable;
                return true;
            }

            foreach (var root in worldInstance.RootNodes)
            {
                foreach (var node in SceneNodePrefabUtility.EnumerateHierarchy(root))
                {
                    nodeCount++;

                    foreach (var component in node.Components)
                    {
                        componentTypes.Add(component.GetType().Name);

                        if (component is Components.Scene.Mesh.RenderableComponent rc)
                        {
                            renderableComponentCount++;
                            meshCount += rc.Meshes.Count;
                            foreach (var mesh in rc.Meshes)
                                meshRenderCommandCount += mesh.RenderInfo.RenderCommands.Count;
                        }

                        if (TryGetRenderedObjects(component, out var renderedObjects))
                        {
                            iRenderableCount++;
                            if (renderedObjects is not null)
                            {
                                foreach (var ri in renderedObjects)
                                {
                                    totalRenderInfoCount++;

                                    if (ri is null)
                                        continue;

                                    var worldInstanceProperty = ri.GetType().GetProperty(
                                        "WorldInstance",
                                        BindingFlags.Public | BindingFlags.Instance);

                                    if (worldInstanceProperty?.GetValue(ri) is not null)
                                        iRenderableWithWorldCount++;
                                }
                            }
                        }
                    }
                }
            }

            Debug.RenderingEvery(
                $"RenderDump.WorldCounts.{phase}.{worldInstance.GetHashCode()}",
                TimeSpan.FromDays(1),
                "[RenderDiag] World dump counts ({0}). Nodes={1} RenderableComponents={2} Meshes={3} MeshRenderCommands={4} IRenderables={5} WithWorld={6}/{7}",
                phase,
                nodeCount,
                renderableComponentCount,
                meshCount,
                meshRenderCommandCount,
                iRenderableCount,
                iRenderableWithWorldCount,
                totalRenderInfoCount);

            Debug.RenderingEvery(
                $"RenderDump.ComponentTypes.{phase}.{worldInstance.GetHashCode()}",
                TimeSpan.FromDays(1),
                "[RenderDiag] Component types ({0}): {1}",
                phase,
                string.Join(", ", componentTypes));
        }

        private static void DumpWorldHierarchyRootsOncePerPhase(XRWorldInstance worldInstance, string phase)
        {
            Debug.RenderingEvery(
                $"SnapshotHierarchy.{phase}.{worldInstance.GetHashCode()}",
                TimeSpan.FromDays(1),
                "[SnapshotDiag] Hierarchy roots ({0}). World={1} RootNodes={2}",
                phase,
                worldInstance.TargetWorld?.Name ?? "<unknown>",
                worldInstance.RootNodes.Count);

            int totalReachableNodes = 0;
            var visited = new HashSet<SceneNode>();

            foreach (var root in worldInstance.RootNodes)
            {
                if (root is null)
                    continue;

                int childCount = 0;
                foreach (var childTfm in root.Transform.Children)
                    if (childTfm?.SceneNode is not null)
                        childCount++;

                Debug.RenderingEvery(
                    $"SnapshotHierarchy.Root.{phase}.{worldInstance.GetHashCode()}.{root.GetHashCode()}",
                    TimeSpan.FromDays(1),
                    "[SnapshotDiag] Root '{0}' children={1} world={2}",
                    root.Name ?? "<unnamed>",
                    childCount,
                    root.World is null ? "<null>" : "set");

                totalReachableNodes += CountReachableNodes(root, visited);
            }

            Debug.RenderingEvery(
                $"SnapshotHierarchy.Totals.{phase}.{worldInstance.GetHashCode()}",
                TimeSpan.FromDays(1),
                "[SnapshotDiag] Reachable nodes via Transform.Children ({0}) = {1}",
                phase,
                totalReachableNodes);

            static int CountReachableNodes(SceneNode node, HashSet<SceneNode> visited)
            {
                if (node is null)
                    return 0;
                if (!visited.Add(node))
                    return 0;

                int count = 1;
                foreach (var childTfm in node.Transform.Children)
                {
                    if (childTfm?.SceneNode is SceneNode childNode)
                        count += CountReachableNodes(childNode, visited);
                }
                return count;
            }
        }

        private static void Timer_PostUpdateFrame()
        {
            XRObjectBase.ProcessPendingDestructions();
            TransformBase.ProcessParentReassignments();
        }

        public static bool IsRenderThread
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Environment.CurrentManagedThreadId == RenderThreadId;
        }

        public static int RenderThreadId { get; private set; }
    
        public static bool IsPhysicsThread
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Environment.CurrentManagedThreadId == PhysicsThreadId;
        }

        public static int PhysicsThreadId { get; private set; }

        internal static void SetPhysicsThreadId(int threadId)
            => PhysicsThreadId = threadId;

        /// <summary>
        /// These tasks will be executed during the collect-visible/render swap point.
        /// </summary>
        /// <param name="task"></param>
        public static void EnqueueSwapTask(Action task)
            => Jobs.Schedule(new ActionJob(task), JobPriority.Normal, JobAffinity.CollectVisibleSwap);

        /// <summary>
        /// These tasks will be executed during the collect-visible/render swap point.
        /// Calls repeatedly until the task returns true.
        /// </summary>
        public static void AddSwapCoroutine(Func<bool> task)
            => Jobs.Schedule(new CoroutineJob(task), JobPriority.Normal, JobAffinity.CollectVisibleSwap);
        
        /// <summary>
        /// These tasks will be executed on the main thread, and usually are rendering tasks.
        /// </summary>
        /// <param name="task"></param>
        public static void EnqueueMainThreadTask(Action task)
            => Jobs.Schedule(new ActionJob(task), JobPriority.Normal, JobAffinity.MainThread);

        /// <summary>
        /// Enqueues a task to run on the engine update thread.
        /// Use this for work that must not run on the render thread (e.g. play-mode transitions).
        /// </summary>
        public static void EnqueueUpdateThreadTask(Action task)
        {
            if (task is null)
                return;

            _pendingUpdateThreadWork.Enqueue(task);
        }

        /// <summary>
        /// Enqueues a task to run on the fixed update (physics) thread.
        /// Use this for PhysX scene mutations (add/remove/release) to avoid cross-thread access.
        /// </summary>
        public static void EnqueuePhysicsThreadTask(Action task)
        {
            if (task is null)
                return;
            _pendingPhysicsThreadWork.Enqueue(task);
        }

        /// <summary>
        /// These tasks will be executed on the main thread, and usually are rendering tasks.
        /// Calls repeatedly until the task returns true.
        /// </summary>
        /// <param name="task"></param>
        public static void AddMainThreadCoroutine(Func<bool> task)
            => Jobs.Schedule(new CoroutineJob(task), JobPriority.Normal, JobAffinity.MainThread);

        /// <summary>
        /// Invokes the task on the main thread if the current thread is not the render thread.
        /// Returns true if the task was enqueued, false if not.
        /// </summary>
        /// <param name="task"></param>
        /// <returns></returns>
        public static bool InvokeOnMainThread(Action task, bool executeNowIfAlreadyMainThread = false)
        {
            if (IsRenderThread)
            {
                if (executeNowIfAlreadyMainThread)
                    task();
                return false;
            }

            EnqueueMainThreadTask(task);
            return true;
        }

        /// <summary>
        /// Indicates the engine is currently starting up and might be still initializing objects.
        /// </summary>
        public static bool StartingUp { get; private set; }
        /// <summary>
        /// Indicates the engine is currently shutting down and might be disposing of objects.
        /// </summary>
        public static bool ShuttingDown { get; private set; }
        /// <summary>
        /// User-defined settings, such as graphical and audio options.
        /// </summary>
        public static UserSettings UserSettings
        {
            get => _userSettings;
            set
            {
                if (ReferenceEquals(_userSettings, value) && value is not null)
                    return;

                _userSettings?.PropertyChanged -= HandleUserSettingsChanged;
                _userSettings = value ?? new UserSettings();
                _userSettings.PropertyChanged += HandleUserSettingsChanged;
                TrackOverrideableSettings(_userSettings, _trackedUserOverrideableSettings);
                
                OnUserSettingsChanged();
            }
        }

        /// <summary>
        /// Project-level build settings describing how packaged builds should be produced.
        /// </summary>
        public static BuildSettings BuildSettings
        {
            get => GameSettings.BuildSettings;
            set
            {
                if (ReferenceEquals(GameSettings.BuildSettings, value) && value is not null)
                    return;

                if (GameSettings.BuildSettings is not null)
                    GameSettings.BuildSettings.PropertyChanged -= HandleBuildSettingsChanged;

                GameSettings.BuildSettings = value ?? new BuildSettings();
                GameSettings.BuildSettings.PropertyChanged += HandleBuildSettingsChanged;
                BuildSettingsChanged?.Invoke(GameSettings.BuildSettings);
            }
        }

        private static void OnUserSettingsChanged()
        {
            Rendering.ApplyGlobalIlluminationModePreference();
            UserSettingsChanged?.Invoke(_userSettings);
        }

        private static void HandleUserSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(UserSettings.GlobalIlluminationMode):
                    Rendering.ApplyGlobalIlluminationModePreference();
                    break;
                case null:
                case "":
                    Rendering.ApplyGlobalIlluminationModePreference();
                    break;
            }

            TrackOverrideableSettings(_userSettings, _trackedUserOverrideableSettings);
            EffectiveSettings.NotifyEffectiveSettingsChanged();
        }

        private static void ApplyEffectiveSettingsRuntime()
        {
            Rendering.ApplyRenderPipelinePreference();
            Rendering.ApplyGlobalIlluminationModePreference();
            Rendering.ApplyGpuRenderDispatchPreference();
            Rendering.ApplyGpuBvhPreference();
            Rendering.ApplyNvidiaDlssPreference();
            Rendering.ApplyIntelXessPreference();
            ApplyTimerSettings();
        }

        private static void ApplyTimerSettings()
        {
            float targetRenderFrequency = UserSettings.TargetFramesPerSecond ?? 0.0f;
            float targetUpdateFrequency = EffectiveSettings.TargetUpdatesPerSecond ?? 0.0f;
            float fixedUpdateFrequency = EffectiveSettings.FixedFramesPerSecond;
            EVSyncMode vSync = EffectiveSettings.VSync;

            Time.UpdateTimer(
                targetRenderFrequency,
                targetUpdateFrequency,
                fixedUpdateFrequency,
                vSync);
        }

        private static void TrackOverrideableSettings<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(T settingsRoot, List<IOverrideableSetting> cache)
        {
            foreach (var tracked in cache)
            {
                if (tracked is IXRNotifyPropertyChanged notify)
                    notify.PropertyChanged -= HandleOverrideableSettingChanged;
            }

            cache.Clear();

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var property in properties)
            {
                if (!typeof(IOverrideableSetting).IsAssignableFrom(property.PropertyType))
                    continue;

                IOverrideableSetting? overrideable = null;
                try
                {
                    overrideable = property.GetValue(settingsRoot) as IOverrideableSetting;
                }
                catch
                {
                    continue;
                }

                if (overrideable is null)
                    continue;

                cache.Add(overrideable);

                if (overrideable is IXRNotifyPropertyChanged notify)
                    notify.PropertyChanged += HandleOverrideableSettingChanged;
            }
        }

        private static void HandleOverrideableSettingChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            EffectiveSettings.NotifyEffectiveSettingsChanged();
        }

        private static void HandleBuildSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            BuildSettingsChanged?.Invoke(GameSettings.BuildSettings);
        }

        private static void HandleGameSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            EffectiveSettings.NotifyEffectiveSettingsChanged();
            TrackOverrideableSettings(_gameSettings, _trackedGameOverrideableSettings);
        }
        /// <summary>
        /// Game-defined settings, such as initial world and libraries.
        /// </summary>
        public static GameStartupSettings GameSettings
        {
            get => _gameSettings;
            set
            {
                if (ReferenceEquals(_gameSettings, value) && value is not null)
                    return;

                if (_gameSettings is not null)
                    _gameSettings.PropertyChanged -= HandleGameSettingsChanged;

                if (_gameSettings?.BuildSettings is not null)
                    _gameSettings.BuildSettings.PropertyChanged -= HandleBuildSettingsChanged;

                _gameSettings = value ?? new GameStartupSettings();

                _gameSettings.BuildSettings ??= new BuildSettings();

                _gameSettings.BuildSettings.PropertyChanged += HandleBuildSettingsChanged;
                _gameSettings.PropertyChanged += HandleGameSettingsChanged;
                TrackOverrideableSettings(_gameSettings, _trackedGameOverrideableSettings);
                BuildSettingsChanged?.Invoke(_gameSettings.BuildSettings);

                EffectiveSettings.NotifyEffectiveSettingsChanged();
            }
        }
        /// <summary>
        /// All networking-related functions.
        /// </summary>
        public static BaseNetworkingManager? Networking { get; private set; }
        /// <summary>
        /// Audio manager for playing and streaming sounds and music.
        /// </summary>
        public static AudioManager Audio { get; } = new();
        /// <summary>
        /// All active world instances.
        /// These are separate from the windows to allow for multiple windows to display the same world.
        /// They are also not the same as XRWorld, which is just the data for a world.
        /// </summary>
        public static IReadOnlyCollection<XRWorldInstance> WorldInstances => XRWorldInstance.WorldInstances.Values;
        /// <summary>
        /// The list of currently active and rendering windows.
        /// </summary>
        public static IEventListReadOnly<XRWindow> Windows => _windows;
        ///// <summary>
        ///// The list of all active render objects being utilized for rendering.
        ///// Each generic render object has a list of API-specific render objects that represent it for each window.
        ///// </summary>
        //public static IEventListReadOnly<GenericRenderObject> RenderObjects => _renderObjects;
        /// <summary>
        /// Manages all assets loaded into the engine.
        /// </summary>
        public static AssetManager Assets { get; } = new();
        /// <summary>
        /// Easily accessible random number generator.
        /// </summary>
        public static Random Random { get; } = new();
        /// <summary>
        /// This class is used to profile the speed of engine and game code to find performance bottlenecks.
        /// </summary>
        public static CodeProfiler Profiler { get; } = new();

        /// <summary>
        /// The sole method needed to run the engine.
        /// Calls Initialize, Run, and ShutDown in order.
        /// </summary>
        /// <param name="startupSettings"></param>
        /// <param name="state"></param>
        public static void Run(GameStartupSettings startupSettings, GameState state)
        {
            if (Initialize(startupSettings, state))
            {
                RunGameLoop();
                BlockForRendering();
            }
            Cleanup();
        }

        /// <summary>
        /// Initializes the engine with settings for the game it will run.
        /// </summary>
        public static bool Initialize(
            GameStartupSettings startupSettings,
            GameState state,
            bool beginPlayingAllWorlds = true)
        {
            bool success = false;
            try
            {
                StartingUp = true;
                RenderThreadId = Environment.CurrentManagedThreadId;
                GameSettings = startupSettings;
                UserSettings = GameSettings.DefaultUserSettings;

                ConfigureJobManager(GameSettings);

                //Creating windows first is most important, because they will initialize the render context and graphics API.
                CreateWindows(startupSettings.StartupWindows);
                Rendering.SecondaryContext.InitializeIfSupported(Windows.FirstOrDefault());
                XRWindow.AnyWindowFocusChanged += WindowFocusChanged;

                //VR is allowed to initialize async in the background.
                //Windows need to be created first if initializing VR in place.
                if (startupSettings is IVRGameStartupSettings vrSettings)
                    Task.Run(async () => await InitializeVR(vrSettings, startupSettings.RunVRInPlace));

                //Run the tick timer for the engine.
                Time.Initialize(GameSettings, UserSettings);

                //Start processing network in/out for what type of client this is
                InitializeNetworking(startupSettings);

                //Attach event callbacks for processing async and main thread tasks
                Time.Timer.SwapBuffers += SwapBuffers;
                //Time.Timer.RenderFrame += DequeueMainThreadTasks;
                success = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error during engine initialization: {e.Message}\n{e.StackTrace}");
                success = false;
            }
            finally
            {
                StartingUp = false;
            }

            if (beginPlayingAllWorlds && success)
                BeginPlayAllWorlds();

            return success;
        }

        public static bool LastFocusState { get; private set; } = true;

        private static void WindowFocusChanged(XRWindow window, bool isFocused)
        {
            bool anyWindowFocused = isFocused;
            if (!anyWindowFocused)
            {
                foreach (var w in _windows)
                {
                    if (w == null || w.Window == null)
                        continue; // Skip if the window is null or has been disposed

                    if (w.IsFocused)
                    {
                        anyWindowFocused = true;
                        break;
                    }
                }
            }

            if (LastFocusState == anyWindowFocused)
                return;

            LastFocusState = anyWindowFocused;
            if (anyWindowFocused)
                OnGainedFocus();
            else
                OnLostFocus();
        }

        public static XREvent<bool>? FocusChanged { get; set; }

        private static void OnLostFocus()
        {
            Debug.Out("No windows are focused.");

            //Disable audio if disabled on defocus
            if (UserSettings.DisableAudioOnDefocus)
            {
                if (UserSettings.AudioDisableFadeSeconds > 0.0f)
                    Audio.FadeOut(UserSettings.AudioDisableFadeSeconds);
                else
                    Audio.Enabled = false; // Disable audio immediately
            }

            //Set target FPS to unfocused value
            //If in VR, the headset will handle this instead of window focus
            if (UserSettings.UnfocusedTargetFramesPerSecond is not null && !VRState.IsInVR)
            {
                Time.Timer.TargetRenderFrequency = UserSettings.UnfocusedTargetFramesPerSecond.Value;
                Debug.Out($"Unfocused target FPS set to {UserSettings.UnfocusedTargetFramesPerSecond}.");
            }

            FocusChanged?.Invoke(false);
        }

        private static void OnGainedFocus()
        {
            Debug.Out("At least one window is focused.");

            //Enable audio if it was disabled on defocus
            if (UserSettings.DisableAudioOnDefocus)
            {
                if (UserSettings.AudioDisableFadeSeconds > 0.0f)
                    Audio.FadeIn(UserSettings.AudioDisableFadeSeconds);
                else
                    Audio.Enabled = true; // Enable audio immediately
            }

            //Set target FPS to focused value
            //If in VR, the headset will handle this instead of window focus
            if (UserSettings.UnfocusedTargetFramesPerSecond is not null && !VRState.IsInVR)
            {
                Time.Timer.TargetRenderFrequency = UserSettings.TargetFramesPerSecond ?? 0.0f;
                Debug.Out($"Focused target FPS set to {UserSettings.TargetFramesPerSecond}.");
            }

            FocusChanged?.Invoke(true);
        }

        private static async Task<bool> InitializeVR(IVRGameStartupSettings vrSettings, bool runVRInPlace)
        {
            bool result;
            if (runVRInPlace)
            {
                var window = _windows.FirstOrDefault();

                // OpenXR can be initialized without OpenVR manifests.
                // OpenVR requires both the action manifest and vrmanifest.
                if (vrSettings.VRRuntime == EVRRuntime.OpenXR)
                {
                    result = VRState.InitializeOpenXR(window);
                    if (!result)
                        Debug.LogWarning("Failed to initialize OpenXR (forced). VR will not be started.");
                }
                else if (vrSettings.VRRuntime == EVRRuntime.OpenVR)
                {
                    if (vrSettings.VRManifest is null || vrSettings.ActionManifest is null)
                    {
                        Debug.LogWarning("VR settings are not properly initialized for OpenVR. VR will not be started.");
                        return false;
                    }

                    result = await VRState.InitializeLocal(vrSettings.ActionManifest, vrSettings.VRManifest, window ?? _windows[0]);
                }
                else
                {
                    // Auto: try OpenXR first, then fall back to OpenVR if configured.
                    result = VRState.InitializeOpenXR(window);
                    if (!result)
                    {
                        if (vrSettings.VRManifest is null || vrSettings.ActionManifest is null)
                        {
                            Debug.LogWarning("VR settings are not properly initialized. VR will not be started.");
                            return false;
                        }

                        result = await VRState.InitializeLocal(vrSettings.ActionManifest, vrSettings.VRManifest, window ?? _windows[0]);
                    }
                }
            }
            else
            {
                // Client mode currently only supports OpenVR-based transport.
                if (vrSettings.VRRuntime == EVRRuntime.OpenXR)
                {
                    Debug.LogWarning("OpenXR is not supported in client VR mode. VR will not be started.");
                    return false;
                }

                if (vrSettings.VRManifest is null || vrSettings.ActionManifest is null)
                {
                    Debug.LogWarning("VR settings are not properly initialized. VR will not be started.");
                    return false;
                }

                result = await VRState.IninitializeClient(vrSettings.ActionManifest, vrSettings.VRManifest);
            }

            return result;
        }

        private static void InitializeNetworking(GameStartupSettings startupSettings)
        {
            if (Networking is BaseNetworkingManager previousNet)
            {
                previousNet.RemoteJobRequestReceived -= HandleRemoteJobRequestAsync;
            }

            var appType = startupSettings.NetworkingType;
            switch (appType)
            {
                default:
                case GameStartupSettings.ENetworkingType.Local:
                    Networking = null;
                    break;
                case GameStartupSettings.ENetworkingType.Server:
                    var server = new ServerNetworkingManager();
                    Networking = server;
                    server.Start(IPAddress.Parse(startupSettings.UdpMulticastGroupIP), startupSettings.UdpMulticastPort, startupSettings.UdpClientRecievePort);
                    break;
                case GameStartupSettings.ENetworkingType.Client:
                    var client = new ClientNetworkingManager();
                    Networking = client;
                    client.Start(IPAddress.Parse(startupSettings.UdpMulticastGroupIP), startupSettings.UdpMulticastPort, IPAddress.Parse(startupSettings.ServerIP), startupSettings.UdpServerSendPort);
                    break;
                case GameStartupSettings.ENetworkingType.P2PClient:
                    var p2pClient = new PeerToPeerNetworkingManager();
                    Networking = p2pClient;
                    p2pClient.Start(IPAddress.Parse(startupSettings.UdpMulticastGroupIP), startupSettings.UdpMulticastPort, IPAddress.Parse(startupSettings.ServerIP));
                    break;
            }

            if (Networking is BaseNetworkingManager net)
            {
                Jobs.RemoteTransport = new RemoteJobNetworkingTransport(net);
                net.RemoteJobRequestReceived += HandleRemoteJobRequestAsync;
            }
            else
            {
                Jobs.RemoteTransport = null;
            }
        }

        private static Task<RemoteJobResponse?> HandleRemoteJobRequestAsync(RemoteJobRequest request)
            => HandleRemoteJobRequestInternalAsync(request);

        private static async Task<RemoteJobResponse?> HandleRemoteJobRequestInternalAsync(RemoteJobRequest request)
        {
            if (request is null)
                return null;

            return request.Operation switch
            {
                RemoteJobOperations.AssetLoad => await HandleRemoteAssetLoadAsync(request).ConfigureAwait(false),
                _ => RemoteJobResponse.FromError(request.JobId, $"Unsupported remote job operation '{request.Operation}'."),
            };
        }

        private static async Task<RemoteJobResponse?> HandleRemoteAssetLoadAsync(RemoteJobRequest request)
        {
            string? path = null;
            request.Metadata?.TryGetValue("path", out path);
            Guid assetId = Guid.Empty;
            if (request.Metadata?.TryGetValue("id", out var idText) == true)
                Guid.TryParse(idText, out assetId);

            try
            {
                byte[]? payload = null;
                string? resolvedPath = null;

                if (request.TransferMode == RemoteJobTransferMode.PushDataToRemote && request.Payload is { Length: > 0 })
                {
                    payload = request.Payload;
                }
                else if (assetId != Guid.Empty)
                {
                    if (Assets.TryGetAssetByID(assetId, out var existing) && !string.IsNullOrWhiteSpace(existing.FilePath) && File.Exists(existing.FilePath))
                    {
                        resolvedPath = existing.FilePath;
                        payload = await File.ReadAllBytesAsync(existing.FilePath).ConfigureAwait(false);
                    }
                    else if (Assets.TryResolveAssetPathById(assetId, out var resolvedByMeta) && !string.IsNullOrWhiteSpace(resolvedByMeta) && File.Exists(resolvedByMeta))
                    {
                        resolvedPath = resolvedByMeta;
                        payload = await File.ReadAllBytesAsync(resolvedByMeta).ConfigureAwait(false);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    resolvedPath = path;
                    payload = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                }

                if (payload is null)
                    return RemoteJobResponse.FromError(request.JobId, assetId != Guid.Empty
                        ? $"Asset not found for remote load with id '{assetId}'."
                        : $"Asset not found for remote load at '{path}'.");

                IReadOnlyDictionary<string, string>? responseMetadata = null;
                if (!string.IsNullOrWhiteSpace(resolvedPath))
                {
                    responseMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["path"] = resolvedPath,
                    };
                }

                return new RemoteJobResponse
                {
                    JobId = request.JobId,
                    Success = true,
                    Payload = payload,
                    Metadata = responseMetadata,
                    SenderId = Networking is BaseNetworkingManager net ? net.LocalPeerId : null,
                    TargetId = request.SenderId,
                };
            }
            catch (Exception ex)
            {
                return new RemoteJobResponse
                {
                    JobId = request.JobId,
                    Success = false,
                    Error = ex.Message,
                    SenderId = Networking is BaseNetworkingManager net ? net.LocalPeerId : null,
                    TargetId = request.SenderId,
                };
            }
        }

        /// <summary>
        /// Starts play for all worlds.
        /// This means all scene nodes will become active, and thus their components, and all ticking events will register, etc.
        /// </summary>
        public static void BeginPlayAllWorlds()
        {
            foreach (var world in XRWorldInstance.WorldInstances.Values)
                world.BeginPlay().Wait();
        }

        /// <summary>
        /// Stops play for all worlds.
        /// </summary>
        public static void EndPlayAllWorlds()
        {
            foreach (var world in XRWorldInstance.WorldInstances.Values)
                world.EndPlay();
        }

        private static void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("Engine.SwapBuffers");
        }

        private static void DequeueMainThreadTasks()
            => ProcessPendingMainThreadWork();

        internal static void ProcessMainThreadTasks()
            => ProcessPendingMainThreadWork();

        internal static void ProcessUpdateThreadTasks(int maxTasks = 1024)
        {
            int processed = 0;
            while (processed < maxTasks && _pendingUpdateThreadWork.TryDequeue(out var task))
            {
                try
                {
                    task();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
                processed++;
            }
        }

        internal static void ProcessPhysicsThreadTasks(int maxTasks = 4096)
        {
            int processed = 0;
            while (processed < maxTasks && _pendingPhysicsThreadWork.TryDequeue(out var task))
            {
                try
                {
                    task();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
                processed++;
            }
        }

        private static void ProcessPendingMainThreadWork()
        {
            //using var scope = Engine.Profiler.Start();

            // Execute main-thread-affinity jobs scheduled via the job system
            //Jobs.ProcessMainThreadJobs();
        }

        public static void CreateWindows(List<GameWindowStartupSettings> windows)
        {
            foreach (var windowSettings in windows)
                CreateWindow(windowSettings);
        }

        public static XRWindow CreateWindow(GameWindowStartupSettings windowSettings)
        {
            bool preferHdrOutput = windowSettings.OutputHDR ?? Rendering.Settings.OutputHDR;
            var options = GetWindowOptions(windowSettings, preferHdrOutput);

            XRWindow window;
            try
            {
                window = new XRWindow(options, windowSettings.UseNativeTitleBar);
            }
            catch (Exception ex) when (options.API.API == ContextAPI.Vulkan)
            {
                Debug.LogWarning($"Vulkan initialization failed, falling back to OpenGL: {ex.Message}");
                // Fallback to OpenGL context
                options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
                window = new XRWindow(options, windowSettings.UseNativeTitleBar);
            }

            window.PreferHDROutput = preferHdrOutput;
            CreateViewports(windowSettings.LocalPlayers, window);
            window.UpdateViewportSizes();
            _windows.Add(window);

            Rendering.ApplyRenderPipelinePreference();

            /*Task.Run(() => */window.SetWorld(windowSettings.TargetWorld);

            return window;
        }

        private static void CreateViewports(ELocalPlayerIndexMask localPlayerMask, XRWindow window)
        {
            if (localPlayerMask == 0)
                return;
            
            for (int i = 0; i < 4; i++)
                if (((int)localPlayerMask & (1 << i)) > 0)
                    window.RegisterLocalPlayer((ELocalPlayerIndex)i, false);
            
            window.ResizeAllViewportsAccordingToPlayers();
        }

        private static WindowOptions GetWindowOptions(GameWindowStartupSettings windowSettings, bool preferHdrOutput)
        {
            WindowState windowState;
            WindowBorder windowBorder;
            Vector2D<int> position = new(windowSettings.X, windowSettings.Y);
            Vector2D<int> size = new(windowSettings.Width, windowSettings.Height);
            switch (windowSettings.WindowState)
            {
                case EWindowState.Fullscreen:
                    windowState = WindowState.Fullscreen;
                    windowBorder = WindowBorder.Hidden;
                    break;
                default:
                case EWindowState.Windowed:
                    windowState = WindowState.Normal;
                    windowBorder = WindowBorder.Resizable;
                    break;
                case EWindowState.Borderless:
                    windowState = WindowState.Normal;
                    windowBorder = WindowBorder.Hidden;
                    position = new Vector2D<int>(0, 0);
                    int primaryX = Native.NativeMethods.GetSystemMetrics(0);
                    int primaryY = Native.NativeMethods.GetSystemMetrics(1);
                    size = new Vector2D<int>(primaryX, primaryY);
                    break;
            }

            if (!windowSettings.UseNativeTitleBar && windowState == WindowState.Normal)
                windowBorder = WindowBorder.Hidden;

            bool requestHdrSurface = preferHdrOutput && UserSettings.RenderLibrary != ERenderLibrary.Vulkan;
            int preferredBitDepth = requestHdrSurface ? 64 : 24;

            return new(
                true,
                position,
                size,
                0.0,
                0.0,
                UserSettings.RenderLibrary == ERenderLibrary.Vulkan
                    ? new GraphicsAPI(ContextAPI.Vulkan, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(1, 1))
                    : new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6)),
                windowSettings.WindowTitle ?? string.Empty,
                windowState,
                windowBorder,
                windowSettings.VSync,
                true,
                VideoMode.Default,
                preferredBitDepth,
                8,
                null,
                windowSettings.TransparentFramebuffer,
                false,
                false,
                null,
                1);
        }

        /// <summary>
        /// This method will check the condition for the engine to continue running.
        /// </summary>
        /// <returns></returns>
        private static bool IsEngineStillActive()
            => Windows.Count > 0;

        /// <summary>
        /// This will initialize all parallel threads and start the game loop.
        /// The main rendering thread must call BlockForRendering() separately to start rendering.
        /// </summary>
        public static void RunGameLoop()
            => Time.Timer.RunGameLoop();

        /// <summary>
        /// This will block the current thread and submit renders to the graphics API until the engine is shut down.
        /// </summary>
        public static void BlockForRendering()
            => Time.Timer.BlockForRendering(IsEngineStillActive);

        /// <summary>
        /// Closes all windows, resulting in the engine shutting down and the process ending.
        /// </summary>
        public static void ShutDown()
        {
            var windows = _windows.ToArray();
            foreach (var window in windows)
                window.Window.Close();
        }   

        /// <summary>
        /// Stops the engine and disposes of all allocated data.
        /// Called internally once no windows remain active.
        /// </summary>
        internal static void Cleanup()
        {
            //TODO: clean shutdown. Each window should dispose of assets its allocated upon its own closure

            //ShuttingDown = true;
            Rendering.SecondaryContext.Dispose();
            Time.Timer.Stop();
            Jobs.Shutdown();
            Assets.Dispose();
            //ShuttingDown = false;
        }

        /// <summary>
        /// Prevents the engine from shutting down the next time the final window is closed.
        /// Useful for temporary utility windows (e.g., file dialogs) that should not tear down the host application.
        /// </summary>
        public static void SuppressNextCleanup()
            => Interlocked.Increment(ref _suppressedCleanupRequests);

        public static void RemoveWindow(XRWindow window)
        {
            _windows.Remove(window);
            if (_windows.Count != 0)
                return;
            
            if (Interlocked.CompareExchange(ref _suppressedCleanupRequests, 0, 0) > 0)
            {
                Interlocked.Decrement(ref _suppressedCleanupRequests);
                return;
            }

            Cleanup();
        }

        public delegate int DelBeginOperation(
            string operationMessage,
            string finishedMessage,
            out Progress<float> progress,
            out CancellationTokenSource cancel,
            TimeSpan? maxOperationTime = null);

        public delegate void DelEndOperation(int operationId);
    }
}
