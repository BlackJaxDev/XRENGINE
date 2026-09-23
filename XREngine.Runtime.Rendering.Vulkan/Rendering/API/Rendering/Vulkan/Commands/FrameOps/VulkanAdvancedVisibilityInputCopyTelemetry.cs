using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Renderer-owned, allocation-free accumulator for immutable Advanced input
/// copies into the frame-slot plan ring.
/// </summary>
internal sealed class VulkanAdvancedVisibilityInputCopyTelemetry
{
    private long _copyCount;
    private long _copyBytes;
    private long _copyTicks;
    private long _maximumCopyTicks;
    private long _rejectedCopyCount;

    internal void RecordCopy(long bytes, long ticks)
    {
        Interlocked.Increment(ref _copyCount);
        Interlocked.Add(ref _copyBytes, bytes);
        Interlocked.Add(ref _copyTicks, ticks);
        RecordMaximumTicks(ticks);
    }

    internal void RecordRejectedCopy()
        => Interlocked.Increment(ref _rejectedCopyCount);

    internal VulkanAdvancedVisibilityInputCopyDiagnosticsSnapshot Capture()
    {
        double millisecondsPerTick = 1000.0 / Stopwatch.Frequency;
        return new(
            Interlocked.Read(ref _copyCount),
            Interlocked.Read(ref _copyBytes),
            Interlocked.Read(ref _copyTicks) * millisecondsPerTick,
            Interlocked.Read(ref _maximumCopyTicks) * millisecondsPerTick,
            Interlocked.Read(ref _rejectedCopyCount));
    }

    private void RecordMaximumTicks(long ticks)
    {
        long observed = Volatile.Read(ref _maximumCopyTicks);
        while (ticks > observed)
        {
            long prior = Interlocked.CompareExchange(
                ref _maximumCopyTicks,
                ticks,
                observed);
            if (prior == observed)
                return;
            observed = prior;
        }
    }
}
