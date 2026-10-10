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

    /// <summary>Gets the quality assignment time.</summary>
    public long QualityAssignmentTicks { get; init; }

    /// <summary>Gets the GPU rest input dependency preparation time.</summary>
    public long GpuRestDependencyPreparationTicks { get; init; }

    /// <summary>Sample ticks and counts cover only the selected stage calls, not all late ticks.</summary>
    public long GpuRestOpaqueSampleTicks { get; init; }
    public long GpuRestOpaqueSampleCount { get; init; }
    public long GpuRestOwnerMarkSampleTicks { get; init; }
    public long GpuRestOwnerMarkSampleCount { get; init; }
    public long GpuRestColliderSampleTicks { get; init; }
    public long GpuRestColliderSampleCount { get; init; }
    public long RigidRestRootSampleTicks { get; init; }
    public long RigidRestRootSampleCount { get; init; }
    public long RigidRestExpandSampleTicks { get; init; }
    public long RigidRestExpandSampleCount { get; init; }
    public long RigidRestPublishSampleTicks { get; init; }
    public long RigidRestPublishSampleCount { get; init; }

}
