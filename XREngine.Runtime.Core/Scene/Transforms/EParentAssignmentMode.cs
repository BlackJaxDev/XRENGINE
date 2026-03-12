namespace XREngine.Scene.Transforms;

/// <summary>
/// Specifies how parent assignment should be performed.
/// </summary>
public enum EParentAssignmentMode
{
    /// <summary>
    /// Performs the parent assignment immediately on the calling thread with locking.
    /// Use this when you need the hierarchy to be updated synchronously and can tolerate blocking.
    /// </summary>
    Immediate,

    /// <summary>
    /// Queues the parent assignment to be processed during PostUpdate.
    /// This is the safest option for multi-threaded scenarios as it does not block the render thread.
    /// </summary>
    Deferred,
}
