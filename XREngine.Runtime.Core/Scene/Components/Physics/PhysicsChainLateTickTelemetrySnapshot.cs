namespace XREngine.Components;

/// <summary>Contains cumulative physics chain late-tick time in Stopwatch ticks.</summary>
public readonly record struct PhysicsChainLateTickTelemetrySnapshot(
    bool Enabled,
    long StopwatchFrequency,
    long TickCount,
    long TickGateWaitTicks,
    long BodyTicks,
    long BoundaryTicks,
    long ComponentPreparationTicks,
    long CpuBatchTicks,
    long ParallelSolveTicks,
    long PublicationTicks,
    long DiagnosticsTicks,
    long GpuComponentPreparationTicks,
    long GpuInputPackingTicks,
    long GpuBridgeDispatchTicks,
    long StructuralBoundaryTicks,
    long QualityBudgetTicks,
    long GpuRestPoseTicks,
    long GpuHierarchyTicks,
    long GpuParticleTransformReadTicks,
    long ActivityScanTicks,
    long SelectedActivityTicks)
{
    /// <summary>Gets the serial world capture time before component GPU dispatch.</summary>
    public long GpuWorldInputGatherTicks { get; init; }
}
