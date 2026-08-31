namespace XREngine.RenderBench;

/// <summary>
/// Native material-table provenance retained by the Phase 5.3 presentationless
/// material scenario. Row values are supplied only by the Vulkan host seam.
/// </summary>
public sealed record RenderBenchMaterialScenarioEvidence
{
    public int SubmittedFrames { get; init; }
    public int RequiredVisibleTextureCount { get; init; }
    public int ReadyVisibleTextureCount { get; init; }
    public int RequiredVisibleChunksSubmitted { get; init; }
    public int RequiredVisibleChunksCompleted { get; init; }
    public int AdmissionRetryCount { get; init; }
    public string ScalarBefore { get; init; } = string.Empty;
    public string ScalarAfter { get; init; } = string.Empty;
    public string TextureBefore { get; init; } = string.Empty;
    public string TextureAfter { get; init; } = string.Empty;
    public string IdleSnapshot { get; init; } = string.Empty;
    /// <summary>Receipt-gated observations of the immutable CPU token and its native Vulkan backing.</summary>
    public RenderBenchMaterialPublicationEvidence[] Publications { get; init; } = [];
    public long IdlePageWritesBefore { get; init; }
    public long IdlePageWritesAfter { get; init; }
    public ulong IdleDescriptorWritesBefore { get; init; }
    public ulong IdleDescriptorWritesAfter { get; init; }
    public long IdleClosureLeaseAcquiresBefore { get; init; }
    public long IdleClosureLeaseAcquiresAfter { get; init; }
    public int MutationWarmupReceiptCount { get; init; }
    public int MaterialBankCount { get; init; }
    public int PendingMaterialBankAllocations { get; init; }
}
