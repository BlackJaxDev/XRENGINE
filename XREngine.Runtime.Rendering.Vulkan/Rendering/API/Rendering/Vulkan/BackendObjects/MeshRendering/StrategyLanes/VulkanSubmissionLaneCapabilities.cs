using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable capability and policy snapshot captured before a submission plan
/// is sealed. Workers must never consult live renderer capability state.
/// </summary>
internal readonly record struct VulkanSubmissionLaneCapabilities(
    bool SupportsIndirectCount,
    bool SupportsMeshletIndirectCount,
    bool AllowsInstrumentedDiagnostics,
    bool AllowsInstrumentedCpuSafetyNet,
    uint IndirectCrossoverSourceCount,
    uint MeshletCrossoverSourceCount)
{
    internal bool Supports(EMeshSubmissionStrategy strategy)
        => strategy switch
        {
            EMeshSubmissionStrategy.CpuDirect => true,
            EMeshSubmissionStrategy.GpuIndirectZeroReadback or
                EMeshSubmissionStrategy.GpuIndirectInstrumented => SupportsIndirectCount,
            EMeshSubmissionStrategy.GpuMeshletZeroReadback or
                EMeshSubmissionStrategy.GpuMeshletInstrumented => SupportsMeshletIndirectCount,
            _ => false,
        };
}
