using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>Cold diagnostic evidence; none of these copies feed production rendering.</summary>
public sealed record RenderBenchTextureStreamingScenarioEvidence
{
    public VulkanTextureStreamingDiagnosticSnapshot Baseline { get; init; }
    public VulkanTextureStreamingDiagnosticSnapshot Completion { get; init; }
    public long PayloadBytes { get; init; }
    public int SubmittedFrames { get; init; }
    public int VerifiedMipCount { get; init; }
    public long VerifiedBytes { get; init; }
    public string[] ExpectedMipSha256 { get; init; } = [];
    public string[] ActualMipSha256 { get; init; } = [];
    public VulkanTextureStreamingTicketSnapshot[] Tickets { get; init; } = [];
    public VulkanTextureStreamingDiagnosticSnapshot[] Boundaries { get; init; } = [];
    /// <summary>Published cumulative retirement drain durations captured before scenario teardown.</summary>
    public VulkanRetirementDiagnostic? Retirement { get; init; }
    public RenderBenchTextureStreamingCancellationEvidence? Cancellation { get; init; }
}
