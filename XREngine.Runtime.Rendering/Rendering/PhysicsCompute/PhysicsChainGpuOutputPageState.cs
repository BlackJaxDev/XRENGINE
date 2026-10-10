namespace XREngine.Rendering.Compute;

/// <summary>Reports one output page's ownership and reuse state.</summary>
public readonly record struct PhysicsChainGpuOutputPageState(
    int PageIndex,
    uint PageGeneration,
    uint ProducerEpoch,
    bool IsRetired,
    bool IsPublished,
    bool IsCurrent,
    bool IsHistory,
    bool IsWriting,
    int RetainCount,
    bool HasQueuedGpuWork,
    bool HasUnfencedGpuWork,
    bool ProducerIsCurrentRenderer,
    string? ProducerRendererType,
    EGpuFenceSubmissionStatus? FenceSubmissionStatus,
    bool? FenceIsSignaled,
    bool NativeReuseCapabilityAvailable,
    PhysicsChainGpuOutputPageNativeReuse NativeReuse,
    bool KnownProducerFailure,
    bool FailureRecoveryRequested);
