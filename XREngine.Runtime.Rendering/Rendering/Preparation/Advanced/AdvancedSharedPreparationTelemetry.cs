namespace XREngine.Rendering;

/// <summary>
/// Cumulative owner-side timings for shared Advanced preparation. Durations are
/// stopwatch ticks so the frame path does not perform floating-point conversion.
/// </summary>
public readonly record struct AdvancedSharedPreparationTelemetry(
    long AcquireCount,
    long CacheHitCount,
    long RebuildCount,
    long AcquireLockWaitTicks,
    long MaximumAcquireLockWaitTicks,
    long BuildTicks,
    long ExtractionTicks,
    long IndirectPlanningTicks,
    long InitialViewPlanningTicks,
    long CacheHitViewCheckTicks,
    long DeformationTicks,
    long CopyCount,
    long CopyFailureCount,
    long CopyLockWaitTicks,
    long MaximumCopyLockWaitTicks,
    long CopyTicks,
    long MaximumCopyTicks,
    long CopiedBytes,
    long BuildAllocatedBytes,
    long StopwatchFrequency);
