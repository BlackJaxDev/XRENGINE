using XREngine;

namespace XREngine.Editor;

/// <summary>
/// Editor-specific state management that delegates to Engine.PlayMode.
/// Provides convenience methods and editor-specific behavior for play mode transitions.
/// </summary>
public static class EditorState
{
    /// <summary>
    /// The current play mode state.
    /// </summary>
    public static EPlayModeState CurrentState => Engine.PlayMode.State;
    
    /// <summary>
    /// Whether currently in edit mode (no simulation running).
    /// </summary>
    public static bool InEditMode => Engine.PlayMode.IsEditing;
    
    /// <summary>
    /// Whether currently in play mode (simulation running).
    /// </summary>
    public static bool InPlayMode => Engine.PlayMode.IsPlaying;
    
    /// <summary>
    /// Whether currently transitioning between modes.
    /// </summary>
    public static bool IsTransitioning => Engine.PlayMode.IsTransitioning;
    
    /// <summary>
    /// Whether the game is currently paused.
    /// </summary>
    public static bool IsPaused => Engine.PlayMode.IsPaused;

    /// <summary>
    /// Enters play mode. Editor state will be saved for restoration.
    /// </summary>
    public static void EnterPlayMode()
        => Engine.EnqueueUpdateThreadTask(() => _ = Engine.PlayMode.EnterPlayModeAsync());

    /// <summary>
    /// Requests entering play mode from an editor-facing interaction.
    /// Respects the configured enter-play confirmation preference.
    /// </summary>
    public static void RequestEnterPlayMode()
    {
        if (!Engine.PlayMode.IsEditing || Engine.PlayMode.IsTransitioning)
            return;

        if (Engine.EditorPreferences?.ConfirmBeforeEnteringPlayMode ?? true)
        {
            EditorPlayModeConfirmationPrompt.RequestEnterPlayMode();
            return;
        }

        EnterPlayMode();
    }
    
    /// <summary>
    /// Exits play mode. Editor state will be restored based on configuration.
    /// </summary>
    public static void ExitPlayMode()
        => Engine.EnqueueUpdateThreadTask(() => _ = Engine.PlayMode.ExitPlayModeAsync());

    /// <summary>
    /// Requests exiting play mode from an editor-facing interaction.
    /// Respects the configured exit-play confirmation preference.
    /// </summary>
    public static void RequestExitPlayMode()
    {
        if ((!Engine.PlayMode.IsPlaying && !Engine.PlayMode.IsPaused) || Engine.PlayMode.IsTransitioning)
            return;

        if (Engine.EditorPreferences?.ConfirmBeforeExitingPlayMode ?? true)
        {
            EditorPlayModeConfirmationPrompt.RequestExitPlayMode();
            return;
        }

        ExitPlayMode();
    }
    
    /// <summary>
    /// Toggles between edit and play mode.
    /// </summary>
    public static void TogglePlayMode()
        => Engine.EnqueueUpdateThreadTask(Engine.PlayMode.TogglePlayMode);
    
    /// <summary>
    /// Pauses the game while in play mode.
    /// </summary>
    public static void Pause()
        => Engine.EnqueueUpdateThreadTask(Engine.PlayMode.Pause);
    
    /// <summary>
    /// Resumes the game from pause.
    /// </summary>
    public static void Resume()
        => Engine.EnqueueUpdateThreadTask(Engine.PlayMode.Resume);
    
    /// <summary>
    /// Steps one frame while paused.
    /// </summary>
    public static void StepFrame()
        => Engine.EnqueueUpdateThreadTask(Engine.PlayMode.StepFrame);
}
