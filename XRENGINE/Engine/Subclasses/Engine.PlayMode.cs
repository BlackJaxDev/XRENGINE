using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine
{
    public static partial class Engine
    {
        /// <summary>
        /// Manages play mode state and transitions for the engine.
        /// Handles entering/exiting play mode, physics control, and state management.
        /// </summary>
        public static class PlayMode
        {
            // Default to edit mode so the editor starts in a non-simulating state.
            private static EPlayModeState _state = EPlayModeState.Edit;
            private static PlayModeConfiguration _configuration = new();
            private static WorldStateSnapshot? _editModeSnapshot;
            private static readonly object _stateLock = new();
            private static GameMode? _activeGameMode;
            // TEMP flag previously forced physics on without transitions; keep it disabled by default
            // so editor play/edit toggles behave normally.
            private static bool _forcePlayWithoutTransitions = false;
            private static bool _editModeSimulationActive;

            #region Properties

            /// <summary>
            /// The current play mode state.
            /// </summary>
            public static EPlayModeState State
            {
                get
                {
                    lock (_stateLock)
                        return _state;
                }
                private set
                {
                    EPlayModeState oldState;
                    lock (_stateLock)
                    {
                        if (_state == value)
                            return;
                        oldState = _state;
                        _state = value;
                    }
                    Debug.Out($"PlayMode state changed: {oldState} -> {value}");
                    StateChanged?.Invoke(value);
                }
            }

            /// <summary>
            /// Whether the engine is currently in play mode (simulation running).
            /// </summary>
            public static bool IsPlaying => State == EPlayModeState.Play;

            /// <summary>
            /// Whether the engine is currently in edit mode (no simulation).
            /// </summary>
            public static bool IsEditing => State == EPlayModeState.Edit;

            /// <summary>
            /// Whether the engine is currently transitioning between modes.
            /// </summary>
            public static bool IsTransitioning => State == EPlayModeState.EnteringPlay
                                                || State == EPlayModeState.ExitingPlay;

            /// <summary>
            /// Whether the game is paused (in play mode but not simulating).
            /// </summary>
            public static bool IsPaused => State == EPlayModeState.Paused;

            /// <summary>
            /// Configuration for play mode behavior.
            /// </summary>
            public static PlayModeConfiguration Configuration
            {
                get => _configuration;
                set => _configuration = value ?? new PlayModeConfiguration();
            }

            /// <summary>
            /// If true, entering play mode skips the full edit->play transition and forces simulation on.
            /// Intended for unit testing or fast startup scenarios.
            /// </summary>
            public static bool ForcePlayWithoutTransitions
            {
                get => _forcePlayWithoutTransitions;
                set => _forcePlayWithoutTransitions = value;
            }

            /// <summary>
            /// The currently active GameMode during play.
            /// </summary>
            public static GameMode? ActiveGameMode => _activeGameMode;

            #endregion

            #region Events

            /// <summary>
            /// Fired when the play mode state changes.
            /// </summary>
            public static event Action<EPlayModeState>? StateChanged;

            /// <summary>
            /// Fired before entering play mode. Use to prepare for play.
            /// </summary>
            public static event Action? PreEnterPlay;

            /// <summary>
            /// Fired after entering play mode. Play is now active.
            /// </summary>
            public static event Action? PostEnterPlay;

            /// <summary>
            /// Fired after a play-mode snapshot has been restored (SerializeAndRestore), but before BeginPlay starts.
            /// Useful for rebuilding runtime-only wiring that is not serialized (e.g. viewports, caches).
            /// </summary>
            public static event Action<XRWorld>? PostSnapshotRestore;

            /// <summary>
            /// Fired before exiting play mode. Play is still active.
            /// </summary>
            public static event Action? PreExitPlay;

            /// <summary>
            /// Fired after exiting play mode. Now in edit mode.
            /// </summary>
            public static event Action? PostExitPlay;

            /// <summary>
            /// Fired when play mode is paused.
            /// </summary>
            public static event Action? Paused;

            /// <summary>
            /// Fired when play mode is resumed from pause.
            /// </summary>
            public static event Action? Resumed;

            #endregion

            #region Diagnostics

            private static void LogTransitionContext(string transition, string phase, XRWorld? targetWorld)
            {
                Debug.Out(
                    $"[PlayTransition] {transition}.{phase}: State={State} TargetWorld={targetWorld?.Name ?? "<null>"} " +
                    $"SnapshotMode={Configuration.StateRestorationMode} SnapshotAvailable={_editModeSnapshot is not null} " +
                    $"Windows={Windows.Count} WorldInstances={XRWorldInstance.WorldInstances.Count} " +
                    $"ActiveGameMode={_activeGameMode?.GetType().Name ?? "<null>"} TimerRunning={Time.Timer.IsRunning} TimerPaused={Time.Timer.Paused}");

                for (int windowIndex = 0; windowIndex < Windows.Count; windowIndex++)
                {
                    XRWindow window = Windows[windowIndex];
                    Debug.Out(
                        $"[PlayTransition] {transition}.{phase}: Window[{windowIndex}] Hash={window.GetHashCode()} " +
                        $"TargetWorld={window.TargetWorldInstance?.TargetWorld?.Name ?? "<null>"} Viewports={window.Viewports.Count}");

                    for (int viewportIndex = 0; viewportIndex < window.Viewports.Count; viewportIndex++)
                    {
                        XRViewport viewport = window.Viewports[viewportIndex];
                        Debug.Out(
                            $"[PlayTransition] {transition}.{phase}: Window[{windowIndex}] VP[{viewport.Index}] AssocPlayer={viewport.AssociatedPlayer?.LocalPlayerIndex?.ToString() ?? "<none>"} " +
                            $"World={viewport.World?.TargetWorld?.Name ?? "<null>"} CameraComponent={viewport.CameraComponent?.Name ?? "<null>"} " +
                            $"ActiveCamera={viewport.ActiveCamera?.GetHashCode().ToString() ?? "NULL"} Pipeline={viewport.RenderPipelineInstance.Pipeline?.DebugName ?? "<null>"} " +
                            $"Generation={viewport.RenderPipelineInstance.ResourceGeneration}");
                    }
                }

                for (int playerIndex = 0; playerIndex < Engine.State.LocalPlayers.Length; playerIndex++)
                {
                    var player = Engine.State.LocalPlayers[playerIndex];
                    if (player is null)
                    {
                        Debug.Out($"[PlayTransition] {transition}.{phase}: P{playerIndex + 1} Controller=<null>");
                        continue;
                    }

                    var pawn = player.ControlledPawnComponent as PawnComponent;
                    var pawnCamera = pawn?.GetCamera();
                    var playerViewport = player.Viewport as XRViewport;
                    Debug.Out(
                        $"[PlayTransition] {transition}.{phase}: P{playerIndex + 1} Controller={player.GetType().Name} " +
                        $"Pawn={pawn?.Name ?? "<null>"} PawnCamera={pawnCamera?.Name ?? "<null>"} " +
                        $"Viewport={playerViewport?.GetHashCode().ToString() ?? "NULL"} " +
                        $"ViewportCamera={playerViewport?.CameraComponent?.Name ?? "<null>"}");
                }
            }

            private static void LogWorldInstanceState(string transition, string phase, XRWorldInstance worldInstance)
            {
                Debug.Out(
                    $"[PlayTransition] {transition}.{phase}: WorldInstance={worldInstance.GetHashCode()} World={worldInstance.TargetWorld?.Name ?? "<null>"} " +
                    $"PlayState={worldInstance.PlayState} PhysicsEnabled={worldInstance.PhysicsEnabled} " +
                    $"VisualScene={worldInstance.VisualScene?.GetType().Name ?? "<null>"} RootNodes={worldInstance.RootNodes.Count} " +
                    $"GameMode={worldInstance.GameMode?.GetType().Name ?? "<null>"}");
            }

            #endregion

            #region Public Methods

            /// <summary>
            /// Enters play mode asynchronously.
            /// </summary>
            public static Task EnterPlayModeAsync()
            {
                XRWorld? targetWorld = null;

                // Play-mode transitions should never run on the render thread (ImGui draw callstack).
                if (Engine.IsRenderThread)
                {
                    Engine.EnqueueUpdateThreadTask(() => _ = EnterPlayModeAsync());
                    return Task.CompletedTask;
                }

                if (_forcePlayWithoutTransitions)
                    return BeginPlayWithoutTransitionsAsync();

                if (State != EPlayModeState.Edit)
                {
                    Debug.LogWarning($"Cannot enter play mode from state: {State}");
                    return Task.CompletedTask;
                }

                try
                {
                    // Ensure the engine loop is running before we flip into play so updates/physics/input advance
                    if (!Time.Timer.IsRunning)
                        Time.Timer.RunGameLoop();

                    targetWorld = ResolveStartupWorld();
                    LogTransitionContext("EnterPlay", "ResolvedStartupWorld", targetWorld);

                    State = EPlayModeState.EnteringPlay;
                    LogTransitionContext("EnterPlay", "StateSetEnteringPlay", targetWorld);

                    // Ensure the timer is unpaused so update/fixed threads run when play starts
                    Time.Timer.Paused = false;
                    LogTransitionContext("EnterPlay", "BeforePreEnterPlay", targetWorld);
                    PreEnterPlay?.Invoke();
                    LogTransitionContext("EnterPlay", "AfterPreEnterPlay", targetWorld);

                    // Step 1: Capture world state for restoration
                    if (Configuration.StateRestorationMode == EStateRestorationMode.SerializeAndRestore)
                    {
                        _editModeSnapshot = WorldStateSnapshot.Capture(targetWorld);
                        LogTransitionContext("EnterPlay", "AfterSnapshotCapture", targetWorld);

                        // IMPORTANT: play mode should run from a deserialized copy of the world state.
                        // This forces a clean object graph and ensures physics bodies are constructed from deserialized scene data.
                        _editModeSnapshot?.Restore();
                        LogTransitionContext("EnterPlay", "AfterSnapshotRestore", targetWorld);
                        if (targetWorld is not null)
                        {
                            var restoredTarget = targetWorld;
                            PostSnapshotRestore?.Invoke(restoredTarget);
                            LogTransitionContext("EnterPlay", "AfterPostSnapshotRestore", restoredTarget);
                        }
                    }

                    // Step 2: Reload gameplay assemblies if configured
                    if (Configuration.ReloadGameplayAssemblies)
                    {
                        LogTransitionContext("EnterPlay", "BeforeReloadGameplayAssemblies", targetWorld);
                        ReloadGameplayAssembliesAsync().GetAwaiter().GetResult();
                        LogTransitionContext("EnterPlay", "AfterReloadGameplayAssemblies", targetWorld);
                    }

                    // Step 3: Resolve and initialize GameMode
                    _activeGameMode = ResolveGameMode(targetWorld);
                    LogTransitionContext("EnterPlay", "AfterResolveGameMode", targetWorld);
                    
                    // Step 4: Begin play on all world instances
                    foreach (var worldInstance in XRWorldInstance.WorldInstances.Values)
                    {
                        LogWorldInstanceState("EnterPlay", "BeforeBeginPlay", worldInstance);

                        // Enable physics if configured
                        worldInstance.PhysicsEnabled = Configuration.SimulatePhysics;
                        
                        // Set the game mode
                        worldInstance.GameMode = _activeGameMode;
                        _activeGameMode?.WorldInstance = worldInstance;

                        // Begin play
                        worldInstance.BeginPlay().GetAwaiter().GetResult();
                        LogWorldInstanceState("EnterPlay", "AfterBeginPlay", worldInstance);
                    }

                    // Step 5: Call GameMode.OnBeginPlay
                    _activeGameMode?.OnBeginPlay();
                    LogTransitionContext("EnterPlay", "AfterGameModeBeginPlay", targetWorld);

                    State = EPlayModeState.Play;
                    LogTransitionContext("EnterPlay", "StateSetPlay", targetWorld);
                    PostEnterPlay?.Invoke();
                    LogTransitionContext("EnterPlay", "AfterPostEnterPlay", targetWorld);

                    Debug.Out("Entered play mode");
                }
                catch (Exception ex)
                {
                    LogTransitionContext("EnterPlay", "Exception", targetWorld);
                    Debug.LogException(ex, "Failed to enter play mode");
                    // Attempt recovery
                    State = EPlayModeState.Edit;
                }

                return Task.CompletedTask;
            }

            /// <summary>
            /// Exits play mode asynchronously.
            /// </summary>
            public static Task ExitPlayModeAsync()
            {
                // Play-mode transitions should never run on the render thread (ImGui draw callstack).
                if (Engine.IsRenderThread)
                {
                    Engine.EnqueueUpdateThreadTask(() => _ = ExitPlayModeAsync());
                    return Task.CompletedTask;
                }

                if (_forcePlayWithoutTransitions)
                {
                    Debug.LogWarning("Play mode exit ignored: transitions are disabled and physics stays enabled.");
                    return Task.CompletedTask;
                }

                if (State != EPlayModeState.Play && State != EPlayModeState.Paused)
                {
                    Debug.LogWarning($"Cannot exit play mode from state: {State}");
                    return Task.CompletedTask;
                }

                try
                {
                    State = EPlayModeState.ExitingPlay;
                    PreExitPlay?.Invoke();

                    // Make sure we leave the editor with the timer unpaused
                    Time.Timer.Paused = false;

                    // Step 1: Call GameMode.OnEndPlay
                    _activeGameMode?.OnEndPlay();

                    // Step 2: End play on all world instances
                    foreach (var worldInstance in XRWorldInstance.WorldInstances.Values)
                    {
                        // Disable physics
                        worldInstance.PhysicsEnabled = false;

                        // End play
                        worldInstance.EndPlay();

                        // Clear game mode reference
                        worldInstance.GameMode = null;
                    }

                    // Step 3: Clear active game mode
                    if (_activeGameMode is not null)
                    {
                        _activeGameMode.WorldInstance = null;
                        _activeGameMode = null;
                    }

                    // Step 4: Unload gameplay assemblies if they were loaded
                    if (Configuration.ReloadGameplayAssemblies)
                    {
                        UnloadGameplayAssembliesAsync().GetAwaiter().GetResult();
                    }

                    // Step 5: Restore world state based on configuration
                    XRWorld? restoredWorld = null;
                    switch (Configuration.StateRestorationMode)
                    {
                        case EStateRestorationMode.SerializeAndRestore:
                            _editModeSnapshot?.Restore();
                            restoredWorld = ResolveStartupWorld();
                            break;
                        case EStateRestorationMode.ReloadFromAsset:
                            ReloadWorldsFromAssetsAsync().GetAwaiter().GetResult();
                            restoredWorld = ResolveStartupWorld();
                            break;
                        case EStateRestorationMode.PersistChanges:
                            // Do nothing - changes persist
                            break;
                    }
                    _editModeSnapshot = null;

                    // Rebind runtime rendering after snapshot restore (same as entering play mode).
                    // This ensures viewports/cameras/world bindings are properly wired after deserialization.
                    if (restoredWorld is not null)
                    {
                        var restoredTarget = restoredWorld;
                        PostSnapshotRestore?.Invoke(restoredTarget);
                    }

                    State = EPlayModeState.Edit;
                    PostExitPlay?.Invoke();

                    Debug.Out("Exited play mode");
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, "Failed to exit play mode");
                    // Force back to edit mode
                    State = EPlayModeState.Edit;
                }

                return Task.CompletedTask;
            }

            /// <summary>
            /// Toggles between edit and play mode.
            /// </summary>
            public static void TogglePlayMode()
            {
                if (_forcePlayWithoutTransitions)
                {
                    _ = EnterPlayModeAsync();
                    return;
                }

                if (IsPlaying || IsPaused)
                    _ = ExitPlayModeAsync();
                else if (IsEditing)
                    _ = EnterPlayModeAsync();
            }

            /// <summary>
            /// Pauses the game while remaining in play context.
            /// </summary>
            public static void Pause()
            {
                if (_forcePlayWithoutTransitions)
                {
                    Debug.LogWarning("Pause ignored: physics simulation is forced on while transitions are disabled.");
                    return;
                }

                if (State != EPlayModeState.Play)
                {
                    Debug.LogWarning($"Cannot pause from state: {State}");
                    return;
                }

                // Pause physics on all worlds
                foreach (var worldInstance in XRWorldInstance.WorldInstances.Values)
                {
                    worldInstance.PhysicsEnabled = false;
                }

                // Pause the timer
                Time.Timer.Paused = true;

                State = EPlayModeState.Paused;
                Paused?.Invoke();

                Debug.Out("Game paused");
            }

            /// <summary>
            /// Resumes the game from pause.
            /// </summary>
            public static void Resume()
            {
                if (_forcePlayWithoutTransitions)
                {
                    Debug.LogWarning("Resume ignored: physics simulation is forced on while transitions are disabled.");
                    return;
                }

                if (State != EPlayModeState.Paused)
                {
                    Debug.LogWarning($"Cannot resume from state: {State}");
                    return;
                }

                // Resume the timer
                Time.Timer.Paused = false;

                // Resume physics on all worlds
                foreach (var worldInstance in XRWorldInstance.WorldInstances.Values)
                {
                    worldInstance.PhysicsEnabled = Configuration.SimulatePhysics;
                }

                State = EPlayModeState.Play;
                Resumed?.Invoke();

                Debug.Out("Game resumed");
            }

            /// <summary>
            /// Steps one frame while paused.
            /// </summary>
            public static void StepFrame()
            {
                if (_forcePlayWithoutTransitions)
                {
                    Debug.LogWarning("StepFrame ignored: physics simulation is forced on while transitions are disabled.");
                    return;
                }

                if (State != EPlayModeState.Paused)
                {
                    Debug.LogWarning($"Cannot step frame from state: {State}");
                    return;
                }

                // Temporarily resume for one frame
                Time.Timer.StepOneFrame();
                
                // Step physics once if enabled
                if (Configuration.SimulatePhysics)
                {
                    foreach (var worldInstance in XRWorldInstance.WorldInstances.Values)
                    {
                        worldInstance.PhysicsScene.StepSimulation();
                    }
                }
            }

            #endregion

            #region Private Methods

            /// <summary>
            /// Resolves which world should be used as the startup world.
            /// </summary>
            private static XRWorld? ResolveStartupWorld()
            {
                // Priority 1: Configuration override
                if (Configuration.StartupWorld is not null)
                    return Configuration.StartupWorld;

                // Priority 2: First window's target world
                var firstWindow = Windows.FirstOrDefault();
                if (firstWindow?.TargetWorldInstance?.TargetWorld is not null)
                    return firstWindow.TargetWorldInstance.TargetWorld;

                // Priority 3: First available world instance
                var firstInstance = XRWorldInstance.WorldInstances.Values.FirstOrDefault();
                if (firstInstance?.TargetWorld is not null)
                    return firstInstance.TargetWorld;

                // Priority 4: Create default (should rarely happen)
                Debug.LogWarning("No startup world found, creating default");
                return new XRWorld("Default");
            }

            /// <summary>
            /// Resolves which GameMode should be used.
            /// </summary>
            private static GameMode? ResolveGameMode(XRWorld? world)
            {
                // Priority 1: Configuration override
                if (Configuration.GameModeOverride is not null)
                    return Configuration.GameModeOverride;

                // Priority 2: World's default game mode
                if (world?.DefaultGameMode is not null)
                    return world.DefaultGameMode;

                // Priority 3: World settings' default game mode
                if (world?.Settings?.DefaultGameMode is not null)
                    return world.Settings.DefaultGameMode;

                // Priority 4: Create default
                return new CustomGameMode();
            }

            /// <summary>
            /// Forces the engine into a simulated state without performing edit->play transitions.
            /// </summary>
            private static async Task BeginPlayWithoutTransitionsAsync()
            {
                if (_editModeSimulationActive)
                    return;

                Configuration.SimulatePhysics = true;

                var targetWorld = ResolveStartupWorld();
                _activeGameMode = ResolveGameMode(targetWorld);

                foreach (var worldInstance in XRWorldInstance.WorldInstances.Values)
                {
                    worldInstance.PhysicsEnabled = true;
                    worldInstance.GameMode = _activeGameMode;
                    _activeGameMode?.WorldInstance = worldInstance;

                    await worldInstance.BeginPlay();
                }

                _activeGameMode?.OnBeginPlay();

                State = EPlayModeState.Play;
                _editModeSimulationActive = true;

                Debug.LogWarning("Play mode transitions are temporarily disabled; running with physics always simulated.");
            }

            private static void BeginPlayWithoutTransitions()
            {
                if (_editModeSimulationActive)
                    return;

                Configuration.SimulatePhysics = true;

                var targetWorld = ResolveStartupWorld();
                _activeGameMode = ResolveGameMode(targetWorld);

                foreach (var worldInstance in XRWorldInstance.WorldInstances.Values)
                {
                    worldInstance.PhysicsEnabled = true;
                    worldInstance.GameMode = _activeGameMode;
                    _activeGameMode?.WorldInstance = worldInstance;

                    worldInstance.BeginPlay().GetAwaiter().GetResult();
                }

                _activeGameMode?.OnBeginPlay();

                State = EPlayModeState.Play;
                _editModeSimulationActive = true;

                Debug.LogWarning("Play mode transitions are temporarily disabled; running with physics always simulated.");
            }

            /// <summary>
            /// Reloads gameplay assemblies into an isolated context.
            /// </summary>
            private static async Task ReloadGameplayAssembliesAsync()
            {
                // TODO: Integrate with GameCSProjLoader or GameplayAssemblyManager
                // For now, this is a placeholder
                await Task.CompletedTask;
            }

            /// <summary>
            /// Unloads gameplay assemblies.
            /// </summary>
            private static async Task UnloadGameplayAssembliesAsync()
            {
                // TODO: Integrate with GameCSProjLoader or GameplayAssemblyManager
                // For now, this is a placeholder
                await Task.CompletedTask;
            }

            /// <summary>
            /// Reloads all worlds from their asset files.
            /// </summary>
            private static async Task ReloadWorldsFromAssetsAsync()
            {
                // TODO: Implement world reload from assets
                await Task.CompletedTask;
            }

            #endregion
        }
    }
}
