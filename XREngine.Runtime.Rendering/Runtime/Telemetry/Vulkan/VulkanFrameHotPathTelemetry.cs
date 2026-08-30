using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Process-lifetime counters for Phase 4.4 hot-path closure. Recording uses
/// only thread-local allocation counters and interlocked scalar updates.
/// </summary>
public static class VulkanFrameHotPathTelemetry
{
    public static readonly long LockWaitThresholdTicks =
        Math.Max(1L, Stopwatch.Frequency / 10_000L);

    private static long s_submissionInvocationCount;
    private static long s_submissionAllocatedBytes;
    private static long s_submissionAllocationHighWaterBytes;
    private static long s_presentInvocationCount;
    private static long s_presentAllocatedBytes;
    private static long s_presentAllocationHighWaterBytes;
    private static long s_lifetimeLockWaitCount;
    private static long s_lifetimeLockWaitTicks;
    private static long s_lifetimeLockWaitPeakTicks;
    private static long s_lifetimeLockWaitOverThresholdCount;
    private static long s_layoutLockWaitCount;
    private static long s_layoutLockWaitTicks;
    private static long s_layoutLockWaitPeakTicks;
    private static long s_layoutLockWaitOverThresholdCount;

    public static void RecordSubmission(long allocationBefore)
        => RecordAllocation(
            allocationBefore,
            ref s_submissionInvocationCount,
            ref s_submissionAllocatedBytes,
            ref s_submissionAllocationHighWaterBytes);

    public static void RecordPresent(long allocationBefore)
        => RecordAllocation(
            allocationBefore,
            ref s_presentInvocationCount,
            ref s_presentAllocatedBytes,
            ref s_presentAllocationHighWaterBytes);

    public static void RecordLifetimeLockWait(long waitTicks)
        => RecordLockWait(
            waitTicks,
            ref s_lifetimeLockWaitCount,
            ref s_lifetimeLockWaitTicks,
            ref s_lifetimeLockWaitPeakTicks,
            ref s_lifetimeLockWaitOverThresholdCount);

    public static void RecordLayoutLockWait(long waitTicks)
        => RecordLockWait(
            waitTicks,
            ref s_layoutLockWaitCount,
            ref s_layoutLockWaitTicks,
            ref s_layoutLockWaitPeakTicks,
            ref s_layoutLockWaitOverThresholdCount);

    public static VulkanFrameHotPathSnapshot CaptureSnapshot()
        => new(
            Interlocked.Read(ref s_submissionInvocationCount),
            Interlocked.Read(ref s_submissionAllocatedBytes),
            Interlocked.Read(ref s_submissionAllocationHighWaterBytes),
            Interlocked.Read(ref s_presentInvocationCount),
            Interlocked.Read(ref s_presentAllocatedBytes),
            Interlocked.Read(ref s_presentAllocationHighWaterBytes),
            Interlocked.Read(ref s_lifetimeLockWaitCount),
            Interlocked.Read(ref s_lifetimeLockWaitTicks),
            Interlocked.Read(ref s_lifetimeLockWaitPeakTicks),
            Interlocked.Read(ref s_lifetimeLockWaitOverThresholdCount),
            Interlocked.Read(ref s_layoutLockWaitCount),
            Interlocked.Read(ref s_layoutLockWaitTicks),
            Interlocked.Read(ref s_layoutLockWaitPeakTicks),
            Interlocked.Read(ref s_layoutLockWaitOverThresholdCount),
            LockWaitThresholdTicks);

    private static void RecordAllocation(
        long allocationBefore,
        ref long invocationCount,
        ref long allocatedBytes,
        ref long allocationHighWaterBytes)
    {
        long delta = Math.Max(
            0L,
            GC.GetAllocatedBytesForCurrentThread() - allocationBefore);
        Interlocked.Increment(ref invocationCount);
        Interlocked.Add(ref allocatedBytes, delta);
        UpdateMaximum(ref allocationHighWaterBytes, delta);
    }

    private static void RecordLockWait(
        long waitTicks,
        ref long waitCount,
        ref long totalWaitTicks,
        ref long peakWaitTicks,
        ref long overThresholdCount)
    {
        if (waitTicks <= 0L)
            return;

        Interlocked.Increment(ref waitCount);
        Interlocked.Add(ref totalWaitTicks, waitTicks);
        UpdateMaximum(ref peakWaitTicks, waitTicks);
        if (waitTicks > LockWaitThresholdTicks)
            Interlocked.Increment(ref overThresholdCount);
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        while (true)
        {
            long current = Volatile.Read(ref target);
            if (candidate <= current)
                return;
            if (Interlocked.CompareExchange(ref target, candidate, current) ==
                current)
            {
                return;
            }
        }
    }
}
