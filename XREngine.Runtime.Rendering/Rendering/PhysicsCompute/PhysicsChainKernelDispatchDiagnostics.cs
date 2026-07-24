namespace XREngine.Rendering.Compute;

/// <summary>
/// CPU-visible command-submission counters for the explicitly named GPU solver families.
/// Dynamic active-tree counts remain GPU resident to preserve strict zero-readback frames.
/// </summary>
public readonly record struct PhysicsChainKernelDispatchDiagnostics(
    int ShortLinearIndirectDispatchCount,
    int BranchedOrLongIndirectDispatchCount,
    int SimulationBarrierCount,
    bool DynamicWorkgroupCountsRemainGpuAuthored);
