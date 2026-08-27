namespace XREngine.Animation;

/// <summary>
/// Identifies graph lifecycle boundaries at which temporal root placement must
/// establish a fresh baseline without reusing another state's first sample.
/// </summary>
public enum EAnimMotionContinuityChange : byte
{
    None,
    StateEntry,
    TransitionStarted,
    TransitionCompleted,
    TransitionInterrupted,
    Seek,
    Replay,
}
