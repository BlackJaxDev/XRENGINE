using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free, generation-specific readiness truth. Scheduling queues may
/// reference a ticket, but only this monotonic state defines whether the accepted
/// frame dependency is complete.
/// </summary>
internal struct VulkanFrameDependencyTicket
{
    private int _state;
    private long _timelineValue;

    internal EVulkanFrameDependencyKind Kind { get; private set; }
    internal ulong ResourceKey { get; private set; }
    internal ulong Generation { get; private set; }
    internal EVulkanFrameDependencyState State
        => (EVulkanFrameDependencyState)Volatile.Read(ref _state);
    internal ulong TimelineValue
        => unchecked((ulong)Volatile.Read(ref _timelineValue));
    internal string? FailureDetail { get; private set; }

    internal void Declare(
        EVulkanFrameDependencyKind kind,
        ulong resourceKey,
        ulong generation)
    {
        Kind = kind;
        ResourceKey = resourceKey;
        Generation = generation;
        FailureDetail = null;
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
        if (timelineValue != 0UL)
            Volatile.Write(ref _timelineValue, unchecked((long)timelineValue));
        return Interlocked.CompareExchange(
                   ref _state,
                   (int)next,
                   (int)expected) == (int)expected;
    }

    internal void Fail(string detail)
    {
        FailureDetail = detail;
        Volatile.Write(ref _state, (int)EVulkanFrameDependencyState.TerminalFailed);
    }

    internal void Clear() => this = default;
}
