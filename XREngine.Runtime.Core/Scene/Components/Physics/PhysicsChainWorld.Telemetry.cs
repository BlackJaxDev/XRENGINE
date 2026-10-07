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
    private long _lateGpuRestPoseTicks;
    private long _lateGpuHierarchyTicks;
    private long _lateGpuParticleTransformReadTicks;
    private long _lateActivityScanTicks;
    private long _lateSelectedActivityTicks;

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
