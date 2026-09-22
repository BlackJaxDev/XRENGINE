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
    long BuildTicks,
    long ExtractionTicks,
    long DeformationTicks,
    long CopyCount,
    long CopyFailureCount,
    long CopyLockWaitTicks,
    long CopyTicks,
    long CopiedBytes,
    long BuildAllocatedBytes,
    long StopwatchFrequency);
