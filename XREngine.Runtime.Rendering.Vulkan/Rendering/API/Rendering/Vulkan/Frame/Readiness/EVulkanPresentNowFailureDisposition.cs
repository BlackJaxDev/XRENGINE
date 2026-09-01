namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Declares whether a foreground-readiness failure invalidates only the current
/// accepted frame or the renderer's ability to accept future frames.
/// </summary>
internal enum EVulkanPresentNowFailureDisposition : byte
{
    /// <summary>Discard the incomplete accepted frame and retry from a new producer epoch.</summary>
    RetryFrame,

    /// <summary>
    /// Stop accepting frames until one bounded probe is admitted by a relevant
    /// renderer-state change. The current device remains healthy, but the failed
    /// pipeline or output state must not be retried indefinitely while unchanged.
    /// </summary>
    RecoverAfterStateChange,

    /// <summary>Stop accepting foreground frames because renderer correctness is no longer known.</summary>
    RendererTerminal,
}
