using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stores one renderer-local deterministic fault request. The packed atomic
/// state keeps the steady-state probe allocation-free and permits tools or
/// tests to arm the next or a later occurrence safely from another thread.
/// </summary>
internal sealed class VulkanDesktopFrameFaultInjectionState
{
    private long _packedState;

    /// <summary>
    /// Arms <paramref name="point"/> to fail on its
    /// <paramref name="occurrence"/>th observation.
    /// </summary>
    internal void Arm(
        EVulkanDesktopFrameFaultPoint point,
        int occurrence = 1)
    {
        if (point == EVulkanDesktopFrameFaultPoint.None)
            throw new ArgumentOutOfRangeException(nameof(point));
        if (occurrence <= 0)
            throw new ArgumentOutOfRangeException(nameof(occurrence));

        Interlocked.Exchange(
            ref _packedState,
            Pack(point, occurrence));
    }

    /// <summary>
    /// Clears any pending injected failure.
    /// </summary>
    internal void Clear()
        => Interlocked.Exchange(ref _packedState, 0L);

    /// <summary>
    /// Consumes the matching request when its configured occurrence is reached.
    /// </summary>
    internal bool TryConsume(EVulkanDesktopFrameFaultPoint point)
    {
        while (true)
        {
            long observed = Volatile.Read(ref _packedState);
            if (observed == 0L || UnpackPoint(observed) != point)
                return false;

            int remaining = UnpackRemaining(observed);
            long replacement = remaining == 1
                ? 0L
                : Pack(point, remaining - 1);
            if (Interlocked.CompareExchange(
                    ref _packedState,
                    replacement,
                    observed) != observed)
            {
                continue;
            }

            return remaining == 1;
        }
    }

    private static long Pack(
        EVulkanDesktopFrameFaultPoint point,
        int remaining)
        => ((long)remaining << 32) | (uint)point;

    private static EVulkanDesktopFrameFaultPoint UnpackPoint(long packed)
        => (EVulkanDesktopFrameFaultPoint)(uint)packed;

    private static int UnpackRemaining(long packed)
        => checked((int)(packed >> 32));
}
