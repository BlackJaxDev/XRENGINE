using System.Diagnostics;

namespace XREngine.Components;

public sealed partial class PhysicsChainWorld
{
    [ThreadStatic] private static PhysicsChainWorld? s_activeLateTick;
    private long _lateTickCount;
    private long _lateTickGateWaitTicks;
    private long _lateTickBodyTicks;
    private long _lateBoundaryTicks;
    private long _lateComponentPreparationTicks;
    private long _lateCpuBatchTicks;
    private long _lateParallelSolveTicks;
    private long _latePublicationTicks;
    private long _lateDiagnosticsTicks;
    private long _lateGpuComponentPreparationTicks;
    private long _lateGpuInputPackingTicks;
    private long _lateGpuWorldInputGatherTicks;
    private long _lateGpuBridgeDispatchTicks;
    private long _lateStructuralBoundaryTicks;
    private long _lateQualityBudgetTicks;
    private long _lateQualityAssignmentTicks;
    private long _lateGpuRestDependencyPreparationTicks;
    private long _lateGpuRestPoseTicks;
    private long _lateGpuHierarchyTicks;
    private long _lateGpuParticleTransformReadTicks;
    private long _lateActivityScanTicks;
    private long _lateSelectedActivityTicks;
    private long _gpuRestOpaqueSampleTicks;
    private long _gpuRestOpaqueSampleCount;
    private long _gpuRestOwnerMarkSampleTicks;
    private long _gpuRestOwnerMarkSampleCount;
    private long _gpuRestColliderSampleTicks;
    private long _gpuRestColliderSampleCount;
    private long _rigidRestRootSampleTicks;
    private long _rigidRestRootSampleCount;
    private long _rigidRestExpandSampleTicks;
    private long _rigidRestExpandSampleCount;
    private long _rigidRestPublishSampleTicks;
    private long _rigidRestPublishSampleCount;

    /// <summary>Reads cumulative late-tick timing without resetting it.</summary>
    public PhysicsChainLateTickTelemetrySnapshot LateTickTelemetry => new(
        RuntimeWorldTickTelemetry.Enabled,
        Stopwatch.Frequency,
        Interlocked.Read(ref _lateTickCount),
        Interlocked.Read(ref _lateTickGateWaitTicks),
        Interlocked.Read(ref _lateTickBodyTicks),
        Interlocked.Read(ref _lateBoundaryTicks),
        Interlocked.Read(ref _lateComponentPreparationTicks),
        Interlocked.Read(ref _lateCpuBatchTicks),
        Interlocked.Read(ref _lateParallelSolveTicks),
        Interlocked.Read(ref _latePublicationTicks),
        Interlocked.Read(ref _lateDiagnosticsTicks),
        Interlocked.Read(ref _lateGpuComponentPreparationTicks),
        Interlocked.Read(ref _lateGpuInputPackingTicks),
        Interlocked.Read(ref _lateGpuBridgeDispatchTicks),
        Interlocked.Read(ref _lateStructuralBoundaryTicks),
        Interlocked.Read(ref _lateQualityBudgetTicks),
        Interlocked.Read(ref _lateGpuRestPoseTicks),
        Interlocked.Read(ref _lateGpuHierarchyTicks),
        Interlocked.Read(ref _lateGpuParticleTransformReadTicks),
        Interlocked.Read(ref _lateActivityScanTicks),
        Interlocked.Read(ref _lateSelectedActivityTicks))
    {
        GpuWorldInputGatherTicks = Interlocked.Read(ref _lateGpuWorldInputGatherTicks),
        QualityAssignmentTicks = Interlocked.Read(ref _lateQualityAssignmentTicks),
        GpuRestDependencyPreparationTicks = Interlocked.Read(ref _lateGpuRestDependencyPreparationTicks),
        GpuRestOpaqueSampleTicks = Interlocked.Read(ref _gpuRestOpaqueSampleTicks),
        GpuRestOpaqueSampleCount = Interlocked.Read(ref _gpuRestOpaqueSampleCount),
        GpuRestOwnerMarkSampleTicks = Interlocked.Read(ref _gpuRestOwnerMarkSampleTicks),
        GpuRestOwnerMarkSampleCount = Interlocked.Read(ref _gpuRestOwnerMarkSampleCount),
        GpuRestColliderSampleTicks = Interlocked.Read(ref _gpuRestColliderSampleTicks),
        GpuRestColliderSampleCount = Interlocked.Read(ref _gpuRestColliderSampleCount),
        RigidRestRootSampleTicks = Interlocked.Read(ref _rigidRestRootSampleTicks),
        RigidRestRootSampleCount = Interlocked.Read(ref _rigidRestRootSampleCount),
        RigidRestExpandSampleTicks = Interlocked.Read(ref _rigidRestExpandSampleTicks),
        RigidRestExpandSampleCount = Interlocked.Read(ref _rigidRestExpandSampleCount),
        RigidRestPublishSampleTicks = Interlocked.Read(ref _rigidRestPublishSampleTicks),
        RigidRestPublishSampleCount = Interlocked.Read(ref _rigidRestPublishSampleCount),
    };

    internal static void RecordGpuComponentPreparation(long ticks)
    {
        if (s_activeLateTick is { } world)
            Interlocked.Add(ref world._lateGpuComponentPreparationTicks, ticks);
    }

    internal static void RecordGpuInputPacking(long ticks)
    {
        if (s_activeLateTick is { } world)
            Interlocked.Add(ref world._lateGpuInputPackingTicks, ticks);
    }

    internal static void RecordGpuBridgeDispatch(long ticks)
    {
        if (s_activeLateTick is { } world)
            Interlocked.Add(ref world._lateGpuBridgeDispatchTicks, ticks);
    }

    internal static void RecordGpuPrepareDetails(long restPoseTicks, long hierarchyTicks, long particleReadTicks)
    {
        if (s_activeLateTick is not { } world)
            return;
        Interlocked.Add(ref world._lateGpuRestPoseTicks, restPoseTicks);
        Interlocked.Add(ref world._lateGpuHierarchyTicks, hierarchyTicks);
        Interlocked.Add(ref world._lateGpuParticleTransformReadTicks, particleReadTicks);
    }
}
