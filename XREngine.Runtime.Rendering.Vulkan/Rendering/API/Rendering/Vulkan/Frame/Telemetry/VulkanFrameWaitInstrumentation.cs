using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free render-thread collector for contended monitor acquisition.
/// Nested Vulkan subsystems can report the wait without retaining a ref to the
/// frame-owned <see cref="VulkanFrameTrace"/>.
/// </summary>
internal static class VulkanFrameWaitInstrumentation
{
    private static readonly TimeSpan CausalWaitThreshold =
        TimeSpan.FromMilliseconds(0.1);

    [ThreadStatic]
    private static VulkanThreadFrameWaitCapture s_current;

    internal static void BeginFrame(
        VulkanFrameTelemetry authority,
        in DesktopFrameIdentity identity)
    {
        s_current = default;
        s_current.Authority = authority;
        s_current.FrameId = identity.FrameNumber;
        s_current.FrameSlot = identity.FrameSlot;
        s_current.Stage = EVulkanFrameStage.SnapshotHandoff;
        s_current.Active = true;
    }

    internal static void SetStage(
        VulkanFrameTelemetry authority,
        EVulkanFrameStage stage)
    {
        if (s_current.Active && ReferenceEquals(s_current.Authority, authority))
            s_current.Stage = stage;
    }

    internal static void RecordCurrentThreadWait(
        EVulkanFrameWaitReason reason,
        TimeSpan elapsed)
    {
        if (!s_current.Active ||
            reason == EVulkanFrameWaitReason.None ||
            elapsed < CausalWaitThreshold)
        {
            return;
        }

        VulkanFrameCausalWait wait = new(
            reason,
            elapsed,
            s_current.FrameId,
            s_current.FrameSlot,
            ImageIndex: -1,
            SemaphoreTargetValue: 0,
            SemaphoreCompletedValue: 0,
            QueueFamily: 0,
            PendingCommandCount: 0,
            ConcurrentWorkerActivity: 0,
            s_current.Stage);
        s_current.Waits.Add(in wait);
    }

    internal static void CompleteFrame(
        VulkanFrameTelemetry authority,
        ref VulkanFrameTrace trace)
    {
        if (!s_current.Active || !ReferenceEquals(s_current.Authority, authority))
            return;

        try
        {
            for (int index = 0; index < s_current.Waits.Count; index++)
            {
                VulkanFrameCausalWait wait = s_current.Waits.Get(index);
                trace.ReclassifyStageWork(
                    wait.Stage,
                    wait.Elapsed,
                    EVulkanFrameIntervalClass.Wait,
                    wait.Reason);
                trace.RecordCausalWait(in wait);
            }

            trace.CausalWaits.AddDropped(s_current.Waits.DroppedCount);
        }
        finally
        {
            s_current = default;
        }
    }

    private struct VulkanThreadFrameWaitCapture
    {
        public VulkanFrameTelemetry? Authority;
        public ulong FrameId;
        public int FrameSlot;
        public EVulkanFrameStage Stage;
        public VulkanFrameCausalWaitSet Waits;
        public bool Active;
    }
}

/// <summary>
/// Monitor lease that takes the zero-timestamp fast path when uncontended and
/// publishes only waits above the causal-capture threshold.
/// </summary>
internal readonly ref struct VulkanFrameLockScope
{
    private readonly object _gate;

    private VulkanFrameLockScope(
        object gate,
        EVulkanFrameWaitReason reason)
    {
        _gate = gate;
        if (Monitor.TryEnter(gate))
            return;

        long started = Stopwatch.GetTimestamp();
        Monitor.Enter(gate);
        VulkanFrameWaitInstrumentation.RecordCurrentThreadWait(
            reason,
            Stopwatch.GetElapsedTime(started));
    }

    public static VulkanFrameLockScope Enter(
        object gate,
        EVulkanFrameWaitReason reason)
        => new(gate, reason);

    public static VulkanFrameThreadingLockScope Enter(
        Lock gate,
        EVulkanFrameWaitReason reason)
        => new(gate, reason);

    public void Dispose()
        => Monitor.Exit(_gate);
}

/// <summary>
/// Timed lease for the optimized <see cref="Lock"/> primitive. This preserves
/// its native locking semantics instead of converting it to an object monitor.
/// </summary>
internal readonly ref struct VulkanFrameThreadingLockScope
{
    private readonly Lock _gate;

    internal VulkanFrameThreadingLockScope(
        Lock gate,
        EVulkanFrameWaitReason reason)
    {
        _gate = gate;
        if (gate.TryEnter())
            return;

        long started = Stopwatch.GetTimestamp();
        gate.Enter();
        VulkanFrameWaitInstrumentation.RecordCurrentThreadWait(
            reason,
            Stopwatch.GetElapsedTime(started));
    }

    public void Dispose()
        => _gate.Exit();
}
