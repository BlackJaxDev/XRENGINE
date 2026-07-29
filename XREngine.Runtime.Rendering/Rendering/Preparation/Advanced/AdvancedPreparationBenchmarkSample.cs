using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// One bounded aggregate preparation benchmark sample.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedPreparationBenchmarkSample(
    uint SkeletalInstanceCount,
    EAdvancedPreparationBenchmarkScenario Scenario,
    uint AdmittedJobCount,
    uint DispatchCount,
    ulong VertexCount,
    long ElapsedTicks,
    long ManagedBytesAllocated);
