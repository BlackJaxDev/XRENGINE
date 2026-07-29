using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Whole-job aggregate limits. A zero limit means unbounded for that axis.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedDeformationBudget(
    uint MaximumJobs,
    ulong MaximumVertices,
    ulong MaximumOutputBytes,
    EAdvancedDeformationOverflowBehavior OverflowBehavior);
