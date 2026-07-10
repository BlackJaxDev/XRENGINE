namespace XREngine;

/// <summary>
/// Counter readback decisions used by the engine.
/// </summary>
public enum ERvcCounterReadbackDecision
{
    /// <summary>
    /// Disabled counter readback decision.
    /// </summary>
    Disabled,
    /// <summary>
    /// Pending counter readback decision.
    /// </summary>
    Pending,
    /// <summary>
    /// Ready counter readback decision.
    /// </summary>
    Ready,
    /// <summary>
    /// Synchronous forbidden counter readback decision.
    /// </summary>
    SynchronousForbidden,
}
