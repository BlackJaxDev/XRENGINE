using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>Cold-path evidence for one exact, submitted production frame.</summary>
public sealed record RenderBenchScenarioFrame
{
    public int Step { get; init; }
    /// <summary>Immutable real-scene fixture identity for this submitted frame.</summary>
    public string Workload { get; init; } = RenderBenchScenarioWorkloads.Default;
    /// <summary>For masked workloads, the actual material coverage condition submitted for this frame.</summary>
    public string MaskedCoverageMode { get; init; } = "not-applicable";
    /// <summary>White mask-border pixels from the raw-albedo color oracle.</summary>
    public int MaskedBorderPixelCount { get; init; }
    /// <summary>Green palette-target pixels immediately adjacent to the white cutout border.</summary>
    public int MaskedHoleAdjacentTargetPixelCount { get; init; }
    public string Mutation { get; init; } = string.Empty;
    public ulong EngineFrameId { get; init; }
    public long CollectGeneration { get; init; }
    public int[] VisibleCandidateIds { get; init; } = [];
    public int[] KeptCandidateIds { get; init; } = [];
    public uint[] EarlyDrawIds { get; init; } = [];
    public uint[] LateDrawIds { get; init; } = [];
    public RenderBenchDrawIdMapping[] EarlyDrawMappings { get; init; } = [];
    public RenderBenchDrawIdMapping[] LateDrawMappings { get; init; } = [];
    public int[] EarlyCandidateIds { get; init; } = [];
    public int[] LateCandidateIds { get; init; } = [];
    public uint GpuCandidateCount { get; init; }
    public int EarlyDrawCount { get; init; }
    public int LateDrawCount { get; init; }
    public int RasterizedDrawCount { get; init; }
    public int CandidateDrawCount { get; init; }
    public int KnownOccluderDrawCount { get; init; }
    public bool TwoPassExecuted { get; init; }
    public bool TemporalInvalidated { get; init; }
    public bool CameraCut { get; init; }
    public bool ProjectionDiscontinuity { get; init; }
    public bool UnsafeSceneRevision { get; init; }
    /// <summary>CPU planning time from the renderer; never interpreted as elapsed GPU time.</summary>
    public double OcclusionCpuMilliseconds { get; init; }
    public RenderBenchScenarioGpuTiming? GpuTiming { get; init; }
    public string ColorSha256 { get; init; } = string.Empty;
    public string? ImagePath { get; init; }
    public VulkanExplicitProductionSubmissionReceipt Submission { get; init; }
    public Dictionary<string, VulkanNativeBufferDiagnosticDescription> NativeBuffers { get; init; } = [];
    public Dictionary<string, string> ReadbackRoutes { get; init; } = [];
    public string? DiagnosticFailure { get; init; }
}
