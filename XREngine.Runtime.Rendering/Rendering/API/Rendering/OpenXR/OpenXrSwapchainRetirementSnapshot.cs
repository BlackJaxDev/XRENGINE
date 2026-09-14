namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Cold diagnostic snapshot of XR-owned generations, independent of desktop swapchains.</summary>
public sealed class OpenXrSwapchainRetirementSnapshot
{
    public bool Supported { get; init; }
    public int PendingGenerationCount { get; init; }
    public int GenerationCapacity { get; init; }
    public int GenerationHighWater { get; init; }
    public long QueuedGenerationCount { get; init; }
    public long DrainedGenerationCount { get; init; }
    public long DeferralCount { get; init; }
    public int RuntimeAcquiredSwapchainCount { get; init; }
    public EOpenXrSwapchainRetirementBlockers LastObservedBlockers { get; init; }
    public long AbandonedGenerationCount { get; init; }
    public long AbandonedSwapchainCount { get; init; }
    public bool DeviceLost { get; init; }
}
