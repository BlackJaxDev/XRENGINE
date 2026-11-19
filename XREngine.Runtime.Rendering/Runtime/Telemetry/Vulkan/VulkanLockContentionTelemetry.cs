using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Process-wide counters of contended waits on the Vulkan locks in
/// <see cref="EVulkanLockContentionSite"/>. Each site keeps a count, the total wait and the longest
/// wait, split by thread: the render thread (render lane 0) and every other thread (render worker
/// lanes, the collect thread and jobs).
/// </summary>
/// <remarks>
/// The lock scopes try the lock first and call <see cref="Record"/> only when that try fails, so an
/// uncontended acquisition does not read the clock or touch these counters. Recording uses
/// interlocked operations on fixed arrays and does not allocate. The counters only grow; readers
/// take differences between two snapshots.
/// </remarks>
public static class VulkanLockContentionTelemetry
{
    private const int ThreadClassCount = 2;
    private const int RenderThreadClass = 0;
    private const int OtherThreadClass = 1;
    private static readonly int SlotCount = (int)EVulkanLockContentionSite.Count * ThreadClassCount;
    private static readonly long[] s_counts = new long[SlotCount];
    private static readonly long[] s_waitTicks = new long[SlotCount];
    private static readonly long[] s_maxWaitTicks = new long[SlotCount];

    /// <summary>
    /// Adds one contended wait of <paramref name="elapsedTicks"/> stopwatch ticks at
    /// <paramref name="site"/> for the calling thread's class.
    /// </summary>
    public static void Record(EVulkanLockContentionSite site, long elapsedTicks)
    {
        if (site <= EVulkanLockContentionSite.None || site >= EVulkanLockContentionSite.Count)
            return;

        int slot = ResolveSlot(site, RuntimeEngine.IsRenderThread);
        Interlocked.Increment(ref s_counts[slot]);
        Interlocked.Add(ref s_waitTicks[slot], elapsedTicks);
        long max = Volatile.Read(ref s_maxWaitTicks[slot]);
        while (elapsedTicks > max)
        {
            long observed = Interlocked.CompareExchange(ref s_maxWaitTicks[slot], elapsedTicks, max);
            if (observed == max)
                break;
            max = observed;
        }
    }

    /// <summary>Number of contended waits at <paramref name="site"/> on the render thread or on other threads.</summary>
    public static long GetCount(EVulkanLockContentionSite site, bool renderThread)
        => IsCounted(site) ? Volatile.Read(ref s_counts[ResolveSlot(site, renderThread)]) : 0L;

    /// <summary>Total contended wait at <paramref name="site"/> in milliseconds.</summary>
    public static double GetWaitMilliseconds(EVulkanLockContentionSite site, bool renderThread)
        => IsCounted(site)
            ? Volatile.Read(ref s_waitTicks[ResolveSlot(site, renderThread)]) * 1000.0 / Stopwatch.Frequency
            : 0.0;

    /// <summary>Longest single contended wait at <paramref name="site"/> in microseconds.</summary>
    public static double GetMaxWaitMicroseconds(EVulkanLockContentionSite site, bool renderThread)
        => IsCounted(site)
            ? Volatile.Read(ref s_maxWaitTicks[ResolveSlot(site, renderThread)]) * 1_000_000.0 / Stopwatch.Frequency
            : 0.0;

    private static bool IsCounted(EVulkanLockContentionSite site)
        => site > EVulkanLockContentionSite.None && site < EVulkanLockContentionSite.Count;

    private static int ResolveSlot(EVulkanLockContentionSite site, bool renderThread)
        => (int)site * ThreadClassCount + (renderThread ? RenderThreadClass : OtherThreadClass);
}
