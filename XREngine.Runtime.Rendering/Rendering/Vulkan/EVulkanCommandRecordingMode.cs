namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Selects how Vulkan records stable rendering work.
/// </summary>
public enum EVulkanCommandRecordingMode
{
    /// <summary>
    /// Uses hybrid primary/secondary recording for validated desktop workloads
    /// and keeps explicit safety quarantines for unsupported target modes.
    /// </summary>
    Auto,

    /// <summary>
    /// Records frame operations directly into the primary command buffer.
    /// Intended as a correctness and performance bisection mode.
    /// </summary>
    Inline,

    /// <summary>
    /// Requests hybrid primary/secondary command recording. Unsupported
    /// workloads remain visibly diagnosed and use their Vulkan inline path.
    /// </summary>
    Hybrid,
}
