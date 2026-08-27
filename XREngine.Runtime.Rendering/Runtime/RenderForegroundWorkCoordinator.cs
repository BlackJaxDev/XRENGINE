using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering;

/// <summary>
/// Gives exact foreground readiness writer priority over bounded background
/// render-work slices. Background callers must keep a slice small and release
/// it between independently useful steps; foreground callers then wait only for
/// an already-running step, never an entire import or compile backlog.
/// </summary>
public static class RenderForegroundWorkCoordinator
{
    private static readonly ManualResetEventSlim BackgroundAllowed = new(true);

    private static int s_exactForegroundDepth;
    private static int s_activeBackgroundSlices;
    private static int s_backgroundYielded;
    private static long s_foregroundEpoch;
    private static long s_exactForegroundEntries;
    private static long s_exactForegroundWaitTicks;
    private static long s_backgroundSlicesStarted;
    private static long s_backgroundSlicesCompleted;
    private static long s_backgroundYieldCount;
    private static long s_backgroundResumeCount;

    internal static bool IsExactForegroundActive
        => Volatile.Read(ref s_exactForegroundDepth) != 0;

    internal static ExactForegroundScope EnterExactForeground()
    {
        long waitStart = Stopwatch.GetTimestamp();
        if (Interlocked.Increment(ref s_exactForegroundDepth) == 1)
        {
            BackgroundAllowed.Reset();
            Interlocked.Increment(ref s_foregroundEpoch);
        }

        Interlocked.Increment(ref s_exactForegroundEntries);
        SpinWait spinner = default;
        while (Volatile.Read(ref s_activeBackgroundSlices) != 0)
            spinner.SpinOnce();

        long waitedTicks = Stopwatch.GetTimestamp() - waitStart;
        if (waitedTicks > 0L)
            Interlocked.Add(ref s_exactForegroundWaitTicks, waitedTicks);
        return new ExactForegroundScope(acquired: true);
    }

    internal static bool TryEnterBackgroundSlice(out BackgroundSlice scope)
    {
        if (IsExactForegroundActive)
        {
            RecordBackgroundYield();
            scope = default;
            return false;
        }

        Interlocked.Increment(ref s_activeBackgroundSlices);
        if (IsExactForegroundActive)
        {
            Interlocked.Decrement(ref s_activeBackgroundSlices);
            RecordBackgroundYield();
            scope = default;
            return false;
        }

        Interlocked.Increment(ref s_backgroundSlicesStarted);
        if (Interlocked.Exchange(ref s_backgroundYielded, 0) != 0)
            Interlocked.Increment(ref s_backgroundResumeCount);
        scope = new BackgroundSlice(acquired: true);
        return true;
    }

    /// <summary>
    /// Parks a dedicated background worker briefly after a denied slice. The
    /// bounded wait also lets shutdown state be observed without requiring the
    /// foreground path to own or dispose worker synchronization primitives.
    /// </summary>
    internal static void WaitForBackgroundPermission()
        => BackgroundAllowed.Wait(millisecondsTimeout: 5);

    public static RenderForegroundWorkSnapshot CaptureSnapshot()
        => new(
            Volatile.Read(ref s_foregroundEpoch),
            Volatile.Read(ref s_exactForegroundDepth),
            Volatile.Read(ref s_activeBackgroundSlices),
            Volatile.Read(ref s_exactForegroundEntries),
            Volatile.Read(ref s_exactForegroundWaitTicks),
            Volatile.Read(ref s_backgroundSlicesStarted),
            Volatile.Read(ref s_backgroundSlicesCompleted),
            Volatile.Read(ref s_backgroundYieldCount),
            Volatile.Read(ref s_backgroundResumeCount));

    private static void RecordBackgroundYield()
    {
        Interlocked.Exchange(ref s_backgroundYielded, 1);
        Interlocked.Increment(ref s_backgroundYieldCount);
    }

    private static void ExitExactForeground()
    {
        int depth = Interlocked.Decrement(ref s_exactForegroundDepth);
        if (depth < 0)
        {
            Interlocked.Exchange(ref s_exactForegroundDepth, 0);
            throw new InvalidOperationException(
                "Exact foreground render-work scope was released more than once.");
        }
        if (depth == 0)
            BackgroundAllowed.Set();
    }

    private static void ExitBackgroundSlice()
    {
        int active = Interlocked.Decrement(ref s_activeBackgroundSlices);
        if (active < 0)
        {
            Interlocked.Exchange(ref s_activeBackgroundSlices, 0);
            throw new InvalidOperationException(
                "Background render-work slice was released more than once.");
        }
        Interlocked.Increment(ref s_backgroundSlicesCompleted);
    }

    internal ref struct ExactForegroundScope
    {
        private bool _acquired;

        internal ExactForegroundScope(bool acquired)
            => _acquired = acquired;

        public void Dispose()
        {
            if (!_acquired)
                return;
            _acquired = false;
            ExitExactForeground();
        }
    }

    internal ref struct BackgroundSlice
    {
        private bool _acquired;

        internal BackgroundSlice(bool acquired)
            => _acquired = acquired;

        public void Dispose()
        {
            if (!_acquired)
                return;
            _acquired = false;
            ExitBackgroundSlice();
        }
    }
}
