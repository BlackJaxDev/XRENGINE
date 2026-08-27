using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free, generation-specific readiness truth. Scheduling queues may
/// reference a ticket, but only this monotonic state defines whether the accepted
/// frame dependency is complete.
/// </summary>
internal struct VulkanFrameDependencyTicket
{
    private int _writeGate;
    private int _state;
    private long _timelineValue;
    private string? _failureDetail;

    internal EVulkanFrameDependencyKind Kind { get; private set; }
    internal ulong ResourceKey { get; private set; }
    internal ulong Generation { get; private set; }
    internal EVulkanFrameDependencyState State
        => (EVulkanFrameDependencyState)Volatile.Read(ref _state);
    internal ulong TimelineValue
        => unchecked((ulong)Volatile.Read(ref _timelineValue));
    internal string? FailureDetail => Volatile.Read(ref _failureDetail);

    internal void Declare(
        EVulkanFrameDependencyKind kind,
        ulong resourceKey,
        ulong generation)
    {
        Kind = kind;
        ResourceKey = resourceKey;
        Generation = generation;
        Volatile.Write(ref _failureDetail, null);
        Volatile.Write(ref _timelineValue, 0L);
        Volatile.Write(ref _state, (int)EVulkanFrameDependencyState.Declared);
    }

    internal bool TryAdvance(
        EVulkanFrameDependencyState expected,
        EVulkanFrameDependencyState next,
        ulong timelineValue = 0UL)
    {
        if (next <= expected || next == EVulkanFrameDependencyState.TerminalFailed)
            return false;

        EnterWriteGate();
        try
        {
            if ((EVulkanFrameDependencyState)Volatile.Read(ref _state) != expected)
                return false;

            // Publish the queue receipt before the state that makes it visible.
            // The writer gate prevents a losing transition from overwriting a
            // receipt belonging to the transition that actually won.
            if (timelineValue != 0UL)
                Volatile.Write(ref _timelineValue, unchecked((long)timelineValue));
            Volatile.Write(ref _state, (int)next);
            return true;
        }
        finally
        {
            Volatile.Write(ref _writeGate, 0);
        }
    }

    internal bool MarkCpuPrepared()
        => TryAdvance(EVulkanFrameDependencyState.Declared,
            EVulkanFrameDependencyState.CpuPrepared);

    internal bool MarkGpuSubmitted(ulong timelineValue = 0UL)
        => TryAdvance(EVulkanFrameDependencyState.CpuPrepared,
            EVulkanFrameDependencyState.GpuSubmitted,
            timelineValue);

    internal bool MarkReady(ulong timelineValue = 0UL)
    {
        if (TryAdvance(
                EVulkanFrameDependencyState.GpuSubmitted,
                EVulkanFrameDependencyState.Ready,
                timelineValue))
        {
            return true;
        }

        // Pipeline, descriptor, buffer, and already-published texture
        // dependencies can be proven ready by host-side creation/publication.
        // Do not invent a Vulkan timeline value for work that had no queue
        // submission.
        return TryAdvance(
            EVulkanFrameDependencyState.CpuPrepared,
            EVulkanFrameDependencyState.Ready,
            timelineValue);
    }

    internal void Fail(string detail)
    {
        EnterWriteGate();
        try
        {
            EVulkanFrameDependencyState current =
                (EVulkanFrameDependencyState)Volatile.Read(ref _state);
            if (current is EVulkanFrameDependencyState.Ready or
                EVulkanFrameDependencyState.TerminalFailed)
                return;

            // Publish the immutable diagnostic before the terminal state.
            // Readers that observe TerminalFailed must never see an empty
            // failure reason.
            Volatile.Write(ref _failureDetail, detail);
            Volatile.Write(
                ref _state,
                (int)EVulkanFrameDependencyState.TerminalFailed);
        }
        finally
        {
            Volatile.Write(ref _writeGate, 0);
        }
    }

    private void EnterWriteGate()
    {
        SpinWait spinner = default;
        while (Interlocked.CompareExchange(ref _writeGate, 1, 0) != 0)
            spinner.SpinOnce();
    }

    internal void Clear() => this = default;
}
