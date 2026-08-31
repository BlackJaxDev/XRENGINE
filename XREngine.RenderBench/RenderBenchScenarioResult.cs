namespace XREngine.RenderBench;

/// <summary>Machine-readable correctness evidence; never a performance measurement.</summary>
public sealed record RenderBenchScenarioResult
{
    public int SchemaVersion { get; init; } = 1;
    public string Scenario { get; init; } = string.Empty;
    public string Lane { get; init; } = string.Empty;
    public string Depth { get; init; } = string.Empty;
    public string Workload { get; init; } = RenderBenchScenarioWorkloads.Default;
    public string Status { get; init; } = "failed";
    public string? Failure { get; init; }
    public bool WindowCreated { get; init; }
    public bool DiagnosticReadbacks { get; init; }
    public bool PerformanceEvidence => false;
    public bool InFlightLifetimeProven { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public string ExecutableSha256 { get; init; } = string.Empty;
    public string InputSha256 { get; init; } = string.Empty;
    public string Adapter { get; init; } = string.Empty;
    public uint Driver { get; init; }
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    public Dictionary<string, string> ShaderSha256 { get; init; } = [];
    public Dictionary<string, string> EngineAssemblySha256 { get; init; } = [];
    public RenderBenchScenarioFrame[] Frames { get; init; } = [];
    public string[] Failures { get; init; } = [];
    public string[] ChildResults { get; init; } = [];
    public RenderBenchVisibilityAnalysisSummary[] VisibilityAnalysis { get; init; } = [];
    public RenderBenchColdRepeatAnalysisSummary? ColdRepeatAnalysis { get; init; }
    public RenderBenchNativeBufferStressEvidence? NativeBufferStress { get; init; }
    /// <summary>Fresh-process native pipeline cache and steady-state evidence for Phase 5.3.</summary>
    public RenderBenchPipelineScenarioEvidence? PipelineScenario { get; init; }
    /// <summary>Real chunked upload and cold native mip-content evidence.</summary>
    public RenderBenchTextureStreamingScenarioEvidence? TextureStreamingScenario { get; init; }
    /// <summary>Immutable material-row and descriptor-admission evidence for Phase 5.3.</summary>
    public RenderBenchMaterialScenarioEvidence? MaterialScenario { get; init; }
    /// <summary>Device-owned cumulative evidence through the completed lane, before teardown.</summary>
    public XREngine.Rendering.Vulkan.VulkanValidationDiagnosticSnapshot? NativeValidation { get; init; }
}
