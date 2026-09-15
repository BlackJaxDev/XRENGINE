namespace XREngine.ControlPlane;

/// <summary>
/// Represents the possible states of a multiplayer instance.
/// </summary>
public enum MultiplayerInstanceState
{
    /// <summary>
    /// The multiplayer instance is being allocated.
    /// </summary>
    Allocating = 0,

    /// <summary>
    /// The multiplayer instance is being staged.
    /// </summary>
    Staging,

    /// <summary>
    /// The multiplayer instance is starting.
    /// </summary>
    Starting,

    /// <summary>
    /// The multiplayer instance is ready.
    /// </summary>
    Ready,

    /// <summary>
    /// The multiplayer instance is draining.
    /// </summary>
    Draining,

    /// <summary>
    /// The multiplayer instance is stopping.
    /// </summary>
    Stopping,

    /// <summary>
    /// The multiplayer instance has stopped.
    /// </summary>
    Stopped,

    /// <summary>
    /// The multiplayer instance has failed.
    /// </summary>
    Failed,

    /// <summary>
    /// Compatibility name for ready instances created by the explicit development path.
    /// </summary>
    Running = Ready,
}
