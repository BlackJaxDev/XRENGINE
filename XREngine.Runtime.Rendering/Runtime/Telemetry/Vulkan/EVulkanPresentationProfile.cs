namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Selects a deliberate desktop presentation and pacing policy. Profiles are
/// benchmark identities; callers must not infer them from the selected native
/// present mode alone.
/// </summary>
public enum EVulkanPresentationProfile
{
    /// <summary>FIFO display pacing with bounded application latency.</summary>
    Stable,

    /// <summary>Mailbox presentation with a target-rate limiter and at most one queued frame.</summary>
    LowLatency,

    /// <summary>Immediate presentation without an application frame limiter.</summary>
    Uncapped,

    /// <summary>Streamline/DLSS-compatible presentation with explicit frame-generation accounting.</summary>
    FrameGeneration,
}
