using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Canonical play-mode state and event owner. The application host performs world snapshot,
/// assembly reload, rendering, and input composition around these transitions.
/// </summary>
public sealed class RuntimePlayModeController
{
    private readonly object _stateSync = new();
    private int _state = (int)EPlayModeState.Edit;
    private PlayModeConfiguration _configuration = new();
    private int _forcePlayWithoutTransitions;
    private GameMode? _activeGameMode;

    public static RuntimePlayModeController Current { get; } = new();

    public EPlayModeState State => (EPlayModeState)Volatile.Read(ref _state);
    public bool IsPlaying => State == EPlayModeState.Play;
    public bool IsEditing => State == EPlayModeState.Edit;
    public bool IsPaused => State == EPlayModeState.Paused;
    public bool IsTransitioning => State is EPlayModeState.EnteringPlay or EPlayModeState.ExitingPlay;

    public PlayModeConfiguration Configuration
    {
        get => _configuration;
        set => _configuration = value ?? new PlayModeConfiguration();
    }

    public bool ForcePlayWithoutTransitions
    {
        get => Volatile.Read(ref _forcePlayWithoutTransitions) != 0;
        set => Volatile.Write(ref _forcePlayWithoutTransitions, value ? 1 : 0);
    }

    public GameMode? ActiveGameMode => Volatile.Read(ref _activeGameMode);

    public event Action<EPlayModeState>? StateChanged;
    public event Action? PreEnterPlay;
    public event Action? PostEnterPlay;
    public event Action<XRWorld>? PostSnapshotRestore;
    public event Action? PreExitPlay;
    public event Action? PostExitPlay;
    public event Action? Paused;
    public event Action? Resumed;

    public bool TransitionTo(EPlayModeState state)
    {
        EPlayModeState previous;
        lock (_stateSync)
        {
            previous = (EPlayModeState)_state;
            if (previous == state)
                return false;
            Volatile.Write(ref _state, (int)state);
        }

        StateChanged?.Invoke(state);
        return true;
    }

    public void SetActiveGameMode(GameMode? gameMode)
        => Volatile.Write(ref _activeGameMode, gameMode);

    public void RaisePreEnterPlay() => PreEnterPlay?.Invoke();
    public void RaisePostEnterPlay() => PostEnterPlay?.Invoke();
    public void RaisePostSnapshotRestore(XRWorld world) => PostSnapshotRestore?.Invoke(world);
    public void RaisePreExitPlay() => PreExitPlay?.Invoke();
    public void RaisePostExitPlay() => PostExitPlay?.Invoke();
    public void RaisePaused() => Paused?.Invoke();
    public void RaiseResumed() => Resumed?.Invoke();
}
