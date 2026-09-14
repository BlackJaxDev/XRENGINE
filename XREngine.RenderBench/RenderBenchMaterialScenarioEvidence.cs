namespace XREngine.RenderBench;

/// <summary>
/// Native material-table provenance retained by the Phase 5.3 presentationless
/// material scenario. Row values are supplied only by the Vulkan host seam.
/// </summary>
public sealed record RenderBenchMaterialScenarioEvidence
{
    public int SubmittedFrames { get; init; }
    public int BoundTextureCount { get; init; }
    public int PublishedTextureCount { get; init; }
    public int VisiblePriorityChunksSubmitted { get; init; }
    public int VisiblePriorityChunksCompleted { get; init; }
    /// <summary>All frame-admission retries, including cold native bank allocation.</summary>
    public int AdmissionRetryCount { get; init; }
    /// <summary>Accepted receipts whose subsequent ticket query still found an unpublished texture.</summary>
    public int AcceptedFramesBeforeTexturePublication { get; init; }
    /// <summary>The sampled material path declares published texture generations only.</summary>
    public string TexturePublicationPolicy { get; init; } = "PublishedGenerationsOnly";
    /// <summary>This lane does not prove mandatory admission of an explicitly pending generation.</summary>
    public bool StrictRequiredTextureAdmissionProven { get; init; }
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
