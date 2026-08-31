using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>
/// Cancellation controls distinguish queued work from a submitted chunk whose
/// completion has not yet been observed. They do not infer native GPU overlap.
/// </summary>
public sealed record RenderBenchTextureStreamingCancellationEvidence
{
    public VulkanTextureStreamingTicketSnapshot QueuedCancellation { get; init; }
    public VulkanTextureStreamingTicketSnapshot BeforeSubmittedCancellation { get; init; }
    public VulkanTextureStreamingTicketSnapshot SubmittedCancellation { get; init; }
    public long FinalPublicationDelta { get; init; }
    public int DrainBoundaries { get; init; }
}
