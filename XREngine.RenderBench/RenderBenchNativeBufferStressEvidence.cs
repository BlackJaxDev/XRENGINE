using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>Serializable actual-allocation evidence for the bounded C-1/C/C+1 native growth probe.</summary>
public sealed record RenderBenchNativeBufferStressEvidence
{
    public VulkanExplicitProductionSubmissionReceipt CMinusOneReceipt { get; init; }
    public VulkanExplicitProductionSubmissionReceipt CReceipt { get; init; }
    public VulkanExplicitProductionSubmissionReceipt CPlusOneReceipt { get; init; }
    public VulkanExplicitProductionSubmissionReceipt LogicalSealRetryReceipt { get; init; }
    public VulkanExplicitProductionSubmissionReceipt ProbeReceipt { get; init; }
    public RenderBenchNativeBufferStressCapacity CMinusOne { get; init; } = new();
    public RenderBenchNativeBufferStressCapacity C { get; init; } = new();
    public RenderBenchNativeBufferStressCapacity CPlusOne { get; init; } = new();
    public string ProbeSource { get; init; } = string.Empty;
    public string LogicalSealProbeSource { get; init; } = string.Empty;
    public VulkanNativeBufferDiagnosticDescription LogicalSealProbeOriginalBinding { get; init; }
    public VulkanNativeBufferDiagnosticDescription ProbeOriginalBinding { get; init; }
    public VulkanNativeBufferDiagnosticDescription PostProbeBinding { get; init; }
    public bool CompletionQueryAccepted { get; init; }
    public bool CompletedBeforeWait { get; init; }
    public int DrainSubmissionCount { get; init; }
    public VulkanExplicitProductionBufferStressProbeEvidence? LogicalSealProbe { get; init; }
    public VulkanExplicitProductionBufferStressProbeEvidence? ProbeAfterCompletion { get; init; }
    public VulkanExplicitProductionBufferStressProbeEvidence? ProbeAfterSlotDrain { get; init; }
    public bool Passed { get; init; }
    public string[] Failures { get; init; } = [];
}
