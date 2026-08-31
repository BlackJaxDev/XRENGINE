using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>Pipeline persistence evidence from one fresh, presentationless process.</summary>
public sealed record RenderBenchPipelineScenarioEvidence
{
    public int PreparationFrameCount { get; init; }
    public int SteadyFrameCount { get; init; }
    /// <summary>Unsubmitted explicit-admission retries for late, history-dependent native pipelines.</summary>
    public int PipelineAdmissionRetryCount { get; init; }
    public double PipelineAdmissionRetryMilliseconds { get; init; }
    public VulkanPipelineCacheDiagnostic Preparation { get; init; } = new();
    public VulkanPipelineCacheDiagnostic Completion { get; init; } = new();
    public VulkanPipelineTelemetrySnapshot SteadyStateTelemetry { get; init; } = new();
}
