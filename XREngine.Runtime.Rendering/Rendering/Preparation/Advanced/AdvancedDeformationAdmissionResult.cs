using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Per-frame aggregate deformation admission and overflow diagnostics.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedDeformationAdmissionResult(
    uint CandidateCount,
    uint DeduplicatedCount,
    uint AdmittedJobCount,
    uint RejectedJobCount,
    uint VisibleFallbackCount,
    ulong AdmittedVertexCount,
    ulong AdmittedOutputBytes,
    bool BudgetExceeded,
    EAdvancedDeformationOverflowBehavior OverflowBehavior);
