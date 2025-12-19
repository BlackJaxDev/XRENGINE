using XREngine.Components;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>
/// Editor-specific controller for play mode that handles:
/// - Undo system integration (suppress during play, clear after)
/// - Editor tool state (disable gizmos during play)
/// - Keyboard shortcuts (F5 to play, Shift+F5 to stop)
/// - Visual indicators for play mode
/// - Editor pawn possession tracking and restoration
/// </summary>
public static class EditorPlayModeController
{
    private static WorldStateSnapshot? _editModeSnapshot;
    private static bool _initialized;
    private static readonly Dictionary<LocalInputInterface, ShortcutHandlers> _shortcutHandlers = new(ReferenceEqualityComparer.Instance);
    
    /// <summary>
    /// Stores the editor pawn possessions for each local player before entering play mode.
    /// Key is the local player index, value is the pawn that was controlled.
    /// </summary>
    private static readonly Dictionary<ELocalPlayerIndex, PawnComponent?> _editorPawnSnapshot = [];

    private record ShortcutHandlers(Action PlayPause, Action Stop, Action StepFrame);

    /// <summary>
    /// Initializes the editor play mode controller.
    /// Call this once during editor startup.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
            return;

        // Subscribe to play mode events
        Engine.PlayMode.PreEnterPlay += OnPreEnterPlay;
        Engine.PlayMode.PostEnterPlay += OnPostEnterPlay;
        Engine.PlayMode.PreExitPlay += OnPreExitPlay;
        Engine.PlayMode.PostExitPlay += OnPostExitPlay;
        Engine.PlayMode.Paused += OnPaused;
        Engine.PlayMode.Resumed += OnResumed;

        // Register keyboard shortcuts
        LocalInputInterface.GlobalRegisters.Add(RegisterPlayModeInput);

        _initialized = true;
        Debug.Out("EditorPlayModeController initialized");
    }

    /// <summary>
    /// Shuts down the editor play mode controller.
    /// </summary>
    public static void Shutdown()
    {
        if (!_initialized)
            return;

        Engine.PlayMode.PreEnterPlay -= OnPreEnterPlay;
        Engine.PlayMode.PostEnterPlay -= OnPostEnterPlay;
        Engine.PlayMode.PreExitPlay -= OnPreExitPlay;
        Engine.PlayMode.PostExitPlay -= OnPostExitPlay;
        Engine.PlayMode.Paused -= OnPaused;
        Engine.PlayMode.Resumed -= OnResumed;

        _initialized = false;
    }

    #region Event Handlers

    private static void OnPreEnterPlay()
    {
        Debug.Out("EditorPlayModeController: PreEnterPlay");

        // Disable undo recording during play
        // Undo.SuppressRecording(); // TODO: Implement in Undo class

        // Save current editor pawn possessions for restoration when exiting play mode
        SaveEditorPawnSnapshot();

        // Save current world state if using SerializeAndRestore mode
        if (Engine.PlayMode.Configuration.StateRestorationMode == EStateRestorationMode.SerializeAndRestore)
        {
            var currentWorld = GetCurrentEditorWorld();
            if (currentWorld is not null)
            {
                _editModeSnapshot = WorldStateSnapshot.Capture(currentWorld);
                Debug.Out($"Captured world state snapshot for {currentWorld.Name}");
            }
        }

        // Clear selection to avoid editing during play
        Selection.Clear();

        // Disable transform gizmos
        // TransformTool.Disable(); // TODO: Implement

        // Notify UI to update
        PlayModeUIChanged?.Invoke(true);
    }

    private static void OnPostEnterPlay()
    {
        Debug.Out("EditorPlayModeController: PostEnterPlay - Now in play mode");
    }

    private static void OnPreExitPlay()
    {
        Debug.Out("EditorPlayModeController: PreExitPlay");
    }

    private static void OnPostExitPlay()
    {
        Debug.Out("EditorPlayModeController: PostExitPlay");

        // Re-enable undo recording
        // Undo.ResumeRecording(); // TODO: Implement in Undo class

        // Restore world state based on configuration
        if (Engine.PlayMode.Configuration.StateRestorationMode == EStateRestorationMode.SerializeAndRestore
            && _editModeSnapshot is not null)
        {
            _editModeSnapshot.Restore();
            Debug.Out("Restored world state from snapshot");
        }
        _editModeSnapshot = null;

        // Re-enable transform gizmos
        // TransformTool.Enable(); // TODO: Implement

        // Clear undo history from play session
        Undo.ClearHistory();

        // Restore editor pawn possessions
        RestoreEditorPawnSnapshot();

        // Notify UI to update
        PlayModeUIChanged?.Invoke(false);
    }

    private static void OnPaused()
    {
        Debug.Out("EditorPlayModeController: Game paused");
        PlayModePausedChanged?.Invoke(true);
    }

    private static void OnResumed()
    {
        Debug.Out("EditorPlayModeController: Game resumed");
        PlayModePausedChanged?.Invoke(false);
    }

    #endregion

    #region Keyboard Shortcuts

    private static void RegisterPlayModeInput(InputInterface inputInterface)
    {
        if (inputInterface is not LocalInputInterface local)
            return;

        if (inputInterface.Unregister)
        {
            // Unregistration is handled automatically by the input system
            // when Unregister is true, just remove our handlers reference
            _shortcutHandlers.Remove(local);
            return;
        }

        if (_shortcutHandlers.ContainsKey(local))
            return;

        Action playPauseHandler = () => HandlePlayPauseShortcut(local);
        Action stopHandler = () => HandleStopShortcut(local);
        Action stepFrameHandler = () => HandleStepFrameShortcut(local);

        _shortcutHandlers[local] = new ShortcutHandlers(playPauseHandler, stopHandler, stepFrameHandler);

        // F5 = Play/Pause toggle, Shift+F5 = Stop
        local.RegisterKeyEvent(EKey.F5, EButtonInputType.Pressed, playPauseHandler);
        // F6 = Step frame (when paused)
        local.RegisterKeyEvent(EKey.F6, EButtonInputType.Pressed, stepFrameHandler);
    }

    private static void HandlePlayPauseShortcut(LocalInputInterface input)
    {
        bool shiftHeld = IsShiftDown(input);

        if (shiftHeld)
        {
            // Shift+F5 = Stop
            if (Engine.PlayMode.IsPlaying || Engine.PlayMode.IsPaused)
            {
                EditorState.ExitPlayMode();
            }
        }
        else
        {
            // F5 = Toggle play/pause
            if (Engine.PlayMode.IsEditing)
            {
                EditorState.EnterPlayMode();
            }
            else if (Engine.PlayMode.IsPlaying)
            {
                EditorState.Pause();
            }
            else if (Engine.PlayMode.IsPaused)
            {
                EditorState.Resume();
            }
        }
    }

    private static void HandleStopShortcut(LocalInputInterface input)
    {
        if (Engine.PlayMode.IsPlaying || Engine.PlayMode.IsPaused)
        {
            EditorState.ExitPlayMode();
        }
    }

    private static void HandleStepFrameShortcut(LocalInputInterface input)
    {
        if (Engine.PlayMode.IsPaused)
        {
            EditorState.StepFrame();
        }
    }

    private static bool IsShiftDown(LocalInputInterface input)
        => input.GetKeyState(EKey.ShiftLeft, EButtonInputType.Held)
        || input.GetKeyState(EKey.ShiftRight, EButtonInputType.Held)
        || input.GetKeyState(EKey.ShiftLeft, EButtonInputType.Pressed)
        || input.GetKeyState(EKey.ShiftRight, EButtonInputType.Pressed);

    #endregion

    #region Helpers

    private static XRWorld? GetCurrentEditorWorld()
    {
        // Get the world currently being viewed in the editor
        // First try the first window's target world
        var window = Engine.Windows.FirstOrDefault();
        return window?.TargetWorldInstance?.TargetWorld;
    }

    /// <summary>
    /// Saves the current editor pawn possessions for all local players.
    /// Called before entering play mode to allow restoration when exiting.
    /// </summary>
    private static void SaveEditorPawnSnapshot()
    {
        _editorPawnSnapshot.Clear();
        
        foreach (var player in Engine.State.LocalPlayers)
        {
            if (player is LocalPlayerController localPlayer)
            {
                _editorPawnSnapshot[localPlayer.LocalPlayerIndex] = localPlayer.ControlledPawn;
                Debug.Out($"Saved editor pawn for player {localPlayer.LocalPlayerIndex}: {localPlayer.ControlledPawn?.Name ?? "null"}");
            }
        }
    }

    /// <summary>
    /// Restores the editor pawn possessions that were saved before entering play mode.
    /// Called after exiting play mode.
    /// </summary>
    private static void RestoreEditorPawnSnapshot()
    {
        foreach (var (playerIndex, editorPawn) in _editorPawnSnapshot)
        {
            if (editorPawn is null)
                continue;

            // Check if the editor pawn still exists (wasn't destroyed during play)
            if (editorPawn.SceneNode is null || editorPawn.IsDestroyed)
            {
                Debug.Out($"Editor pawn for player {playerIndex} was destroyed during play, cannot restore");
                continue;
            }

            var localPlayer = Engine.State.GetOrCreateLocalPlayer(playerIndex);
            if (localPlayer is not null)
            {
                localPlayer.ControlledPawn = editorPawn;
                Debug.Out($"Restored editor pawn for player {playerIndex}: {editorPawn.Name}");
            }
        }
        
        _editorPawnSnapshot.Clear();
    }

    #endregion

    #region Events

    /// <summary>
    /// Fired when play mode UI state should change.
    /// Parameter is true when entering play, false when exiting.
    /// </summary>
    public static event Action<bool>? PlayModeUIChanged;

    /// <summary>
    /// Fired when pause state changes during play mode.
    /// </summary>
    public static event Action<bool>? PlayModePausedChanged;

    #endregion
}
