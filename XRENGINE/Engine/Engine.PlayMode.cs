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

            #region Public Methods

            /// <summary>
            /// Enters play mode asynchronously.
            /// </summary>
            public static async Task EnterPlayModeAsync()
            {
                if (_forcePlayWithoutTransitions)
                {
                    await ForcePlayWithoutTransitionsAsync();
                    return;
                }

                if (State != EPlayModeState.Edit)
                {
                    Debug.LogWarning($"Cannot enter play mode from state: {State}");
                    return;
                }

                try
                {
                    State = EPlayModeState.EnteringPlay;
                    PreEnterPlay?.Invoke();

                    // Step 1: Capture world state for restoration
                    var targetWorld = ResolveStartupWorld();
                    if (Configuration.StateRestorationMode == EStateRestorationMode.SerializeAndRestore)
                    {
                        _editModeSnapshot = WorldStateSnapshot.Capture(targetWorld);
                    }

                    // Step 2: Reload gameplay assemblies if configured
                    if (Configuration.ReloadGameplayAssemblies)
                    {
                        await ReloadGameplayAssembliesAsync();
                    }

                    // Step 3: Resolve and initialize GameMode
                    _activeGameMode = ResolveGameMode(targetWorld);
                    
                    // Step 4: Begin play on all world instances
                    foreach (var worldInstance in XRWorldInstance.WorldInstances.Values)
                    {
                        // Enable physics if configured
                        worldInstance.PhysicsEnabled = Configuration.SimulatePhysics;
                        
                        // Set the game mode
                        worldInstance.GameMode = _activeGameMode;
                        _activeGameMode?.WorldInstance = worldInstance;

                        // Begin play
                        await worldInstance.BeginPlay();
                    }

                    // Step 5: Call GameMode.OnBeginPlay
                    _activeGameMode?.OnBeginPlay();

                    State = EPlayModeState.Play;
                    PostEnterPlay?.Invoke();

                    Debug.Out("Entered play mode");
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, "Failed to enter play mode");
                    // Attempt recovery
                    State = EPlayModeState.Edit;
                }
            }

            /// <summary>
            /// Exits play mode asynchronously.
            /// </summary>
            public static async Task ExitPlayModeAsync()
            {
                if (_forcePlayWithoutTransitions)
                {
                    Debug.LogWarning("Play mode exit ignored: transitions are disabled and physics stays enabled.");
                    return;
                }

                if (State != EPlayModeState.Play && State != EPlayModeState.Paused)
                {
                    Debug.LogWarning($"Cannot exit play mode from state: {State}");
                    return;
                }

                try
                {
                    State = EPlayModeState.ExitingPlay;
                    PreExitPlay?.Invoke();

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
                        await UnloadGameplayAssembliesAsync();
                    }

                    // Step 5: Restore world state based on configuration
                    switch (Configuration.StateRestorationMode)
                    {
                        case EStateRestorationMode.SerializeAndRestore:
                            _editModeSnapshot?.Restore();
                            break;
                        case EStateRestorationMode.ReloadFromAsset:
                            await ReloadWorldsFromAssetsAsync();
                            break;
                        case EStateRestorationMode.PersistChanges:
                            // Do nothing - changes persist
                            break;
                    }
                    _editModeSnapshot = null;

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
            }

            /// <summary>
            /// Toggles between edit and play mode.
            /// </summary>
            public static void TogglePlayMode()
            {
                if (_forcePlayWithoutTransitions)
                {
                    _ = ForcePlayWithoutTransitionsAsync();
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
                return new GameMode();
            }

            /// <summary>
            /// Forces the engine into a simulated state without performing edit->play transitions.
            /// </summary>
            private static async Task ForcePlayWithoutTransitionsAsync()
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
                    if (_activeGameMode is not null)
                        _activeGameMode.WorldInstance = worldInstance;

                    await worldInstance.BeginPlay();
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
