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
    public const float HighRefreshThresholdHz = 90.0f;

    private static readonly ManualResetEventSlim BackgroundAllowed = new(true);

    private static int s_exactForegroundDepth;
    private static int s_highRefreshConfigured;
    private static int s_highRefreshTargetMilliHertz;
    private static int s_highRefreshFrameDepth;
    private static int s_activeBackgroundSlices;
    private static int s_activeEditorJobSlices;
    private static int s_backgroundYielded;
    private static int s_editorJobYielded;
    private static long s_foregroundEpoch;
    private static long s_exactForegroundEntries;
    private static long s_exactForegroundWaitTicks;
    private static long s_highRefreshFrameEntries;
    private static long s_highRefreshFrameExits;
    private static long s_backgroundSlicesStarted;
    private static long s_backgroundSlicesCompleted;
    private static long s_backgroundYieldCount;
    private static long s_backgroundResumeCount;
    private static long s_editorJobSlicesStarted;
    private static long s_editorJobSlicesCompleted;
    private static long s_editorJobYieldCount;
    private static long s_editorJobResumeCount;

    internal static bool IsExactForegroundActive
        => Volatile.Read(ref s_exactForegroundDepth) != 0;

    internal static bool IsHighRefreshFrameActive
        => Volatile.Read(ref s_highRefreshFrameDepth) != 0;

    /// <summary>
    /// Prevents new deferable work from starting while one high-refresh frame
    /// executes its completion, build, record, submit, and present phases.
    /// Work already in a bounded slice is allowed to finish.
    /// </summary>
    internal static HighRefreshFrameScope EnterHighRefreshFrame(
        float targetRefreshHz)
    {
        bool highRefresh =
            float.IsFinite(targetRefreshHz) &&
            targetRefreshHz >= HighRefreshThresholdHz;
        Volatile.Write(
            ref s_highRefreshTargetMilliHertz,
            float.IsFinite(targetRefreshHz)
                ? Math.Max(0, (int)MathF.Round(targetRefreshHz * 1000.0f))
                : 0);
        if (!highRefresh)
        {
            if (Volatile.Read(ref s_highRefreshFrameDepth) == 0)
                Volatile.Write(ref s_highRefreshConfigured, 0);
            TryReleaseBackgroundPermission();
            return default;
        }

        Volatile.Write(ref s_highRefreshConfigured, 1);
        if (Interlocked.Increment(ref s_highRefreshFrameDepth) == 1)
        {
            BackgroundAllowed.Reset();
            Interlocked.Increment(ref s_foregroundEpoch);
        }

        Interlocked.Increment(ref s_highRefreshFrameEntries);
        return new HighRefreshFrameScope(acquired: true);
    }

    /// <summary>
    /// Clears the inter-frame high-refresh concurrency cap when presentation is
    /// inactive, such as a minimized or otherwise rejected desktop surface.
    /// </summary>
    internal static void MarkRenderingInactive()
    {
        if (Volatile.Read(ref s_highRefreshFrameDepth) != 0)
            return;

        Volatile.Write(ref s_highRefreshConfigured, 0);
        TryReleaseBackgroundPermission();
    }

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
        if (IsForegroundGateActive)
        {
            RecordBackgroundYield();
            scope = default;
            return false;
        }

        bool highRefreshConfigured =
            Volatile.Read(ref s_highRefreshConfigured) != 0;
        if (highRefreshConfigured)
        {
            if (Interlocked.CompareExchange(
                    ref s_activeBackgroundSlices,
                    1,
                    0) != 0)
            {
                RecordBackgroundYield();
                scope = default;
                return false;
            }
            BackgroundAllowed.Reset();
        }
        else
        {
            Interlocked.Increment(ref s_activeBackgroundSlices);
        }

        if (IsForegroundGateActive)
        {
            ReleaseBackgroundSliceReservation(completed: false);
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
    /// Admits one general editor job between high-refresh frame critical
    /// sections. Foreground-affine jobs use their owning engine threads and do
    /// not pass through this gate.
    /// </summary>
    public static bool TryEnterEditorJobSlice()
    {
        if (IsForegroundGateActive)
        {
            RecordEditorJobYield();
            return false;
        }

        bool highRefreshConfigured =
            Volatile.Read(ref s_highRefreshConfigured) != 0;
        if (highRefreshConfigured)
        {
            if (Interlocked.CompareExchange(
                    ref s_activeEditorJobSlices,
                    1,
                    0) != 0)
            {
                RecordEditorJobYield();
                return false;
            }
            BackgroundAllowed.Reset();
        }
        else
        {
            Interlocked.Increment(ref s_activeEditorJobSlices);
        }

        if (IsForegroundGateActive)
        {
            ReleaseEditorJobSliceReservation(completed: false);
            RecordEditorJobYield();
            return false;
        }

        Interlocked.Increment(ref s_editorJobSlicesStarted);
        if (Interlocked.Exchange(ref s_editorJobYielded, 0) != 0)
            Interlocked.Increment(ref s_editorJobResumeCount);
        return true;
    }

    /// <summary>
    /// Releases a general-job slice admitted by
    /// <see cref="TryEnterEditorJobSlice"/>.
    /// </summary>
    public static void ExitEditorJobSlice()
        => ReleaseEditorJobSliceReservation(completed: true);

    /// <summary>
    /// Parks a dedicated background worker briefly after a denied slice. The
    /// bounded wait also lets shutdown state be observed without requiring the
    /// foreground path to own or dispose worker synchronization primitives.
    /// </summary>
    public static void WaitForBackgroundPermission()
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
            Volatile.Read(ref s_backgroundResumeCount),
            Volatile.Read(ref s_highRefreshConfigured) != 0,
            Volatile.Read(ref s_highRefreshTargetMilliHertz) / 1000.0f,
            Volatile.Read(ref s_highRefreshFrameDepth),
            Volatile.Read(ref s_highRefreshFrameEntries),
            Volatile.Read(ref s_highRefreshFrameExits),
            Volatile.Read(ref s_activeEditorJobSlices),
            Volatile.Read(ref s_editorJobSlicesStarted),
            Volatile.Read(ref s_editorJobSlicesCompleted),
            Volatile.Read(ref s_editorJobYieldCount),
            Volatile.Read(ref s_editorJobResumeCount));

    private static void RecordBackgroundYield()
    {
        Interlocked.Exchange(ref s_backgroundYielded, 1);
        Interlocked.Increment(ref s_backgroundYieldCount);
    }

    private static void RecordEditorJobYield()
    {
        Interlocked.Exchange(ref s_editorJobYielded, 1);
        Interlocked.Increment(ref s_editorJobYieldCount);
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
            TryReleaseBackgroundPermission();
    }

    private static void ExitHighRefreshFrame()
    {
        int depth = Interlocked.Decrement(ref s_highRefreshFrameDepth);
        if (depth < 0)
        {
            Interlocked.Exchange(ref s_highRefreshFrameDepth, 0);
            throw new InvalidOperationException(
                "High-refresh render-work scope was released more than once.");
        }

        Interlocked.Increment(ref s_highRefreshFrameExits);
        if (depth == 0)
            TryReleaseBackgroundPermission();
    }

    private static void ExitBackgroundSlice()
        => ReleaseBackgroundSliceReservation(completed: true);

    private static void ReleaseBackgroundSliceReservation(bool completed)
    {
        int active = Interlocked.Decrement(ref s_activeBackgroundSlices);
        if (active < 0)
        {
            Interlocked.Exchange(ref s_activeBackgroundSlices, 0);
            throw new InvalidOperationException(
                "Background render-work slice was released more than once.");
        }
        if (completed)
            Interlocked.Increment(ref s_backgroundSlicesCompleted);
        if (active == 0)
            TryReleaseBackgroundPermission();
    }

    private static void ReleaseEditorJobSliceReservation(bool completed)
    {
        int active = Interlocked.Decrement(ref s_activeEditorJobSlices);
        if (active < 0)
        {
            Interlocked.Exchange(ref s_activeEditorJobSlices, 0);
            throw new InvalidOperationException(
                "Editor background-job slice was released more than once.");
        }
        if (completed)
            Interlocked.Increment(ref s_editorJobSlicesCompleted);
        if (active == 0)
            TryReleaseBackgroundPermission();
    }

    private static bool IsForegroundGateActive
        => IsExactForegroundActive || IsHighRefreshFrameActive;

    private static void TryReleaseBackgroundPermission()
    {
        if (IsForegroundGateActive)
            return;
        if (Volatile.Read(ref s_highRefreshConfigured) != 0 &&
            (Volatile.Read(ref s_activeBackgroundSlices) != 0 ||
             Volatile.Read(ref s_activeEditorJobSlices) != 0))
        {
            return;
        }

        BackgroundAllowed.Set();
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

    internal ref struct HighRefreshFrameScope
    {
        private bool _acquired;

        internal HighRefreshFrameScope(bool acquired)
            => _acquired = acquired;

        public void Dispose()
        {
            if (!_acquired)
                return;
            _acquired = false;
            ExitHighRefreshFrame();
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
