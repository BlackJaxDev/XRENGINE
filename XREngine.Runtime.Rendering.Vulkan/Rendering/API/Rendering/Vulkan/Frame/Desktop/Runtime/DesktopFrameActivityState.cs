using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Atomically publishes the immutable frame number and desktop in-flight slot
/// owned by the active desktop frame attempt.
/// </summary>
internal sealed class DesktopFrameActivityState
{
    private const int FrameSlotBitCount = 8;
    private const long InactivePublication = 0;
    private const int MaxEncodableFrameSlot = (1 << FrameSlotBitCount) - 2;
    private const ulong MaxEncodableFrameNumber = (ulong)(long.MaxValue >> FrameSlotBitCount);

    private long _publication;

    /// <summary>
    /// Attempts to publish a desktop frame attempt.
    /// </summary>
    /// <remarks>
    /// A failed claim does not alter the active publication. The returned token
    /// must be supplied to <see cref="TryExit"/> so a stale caller cannot clear
    /// a newer attempt.
    /// </remarks>
    internal bool TryEnter(ulong frameNumber, int frameSlot, out long publicationToken)
    {
        publicationToken = Encode(frameNumber, frameSlot);
        return Interlocked.CompareExchange(
            ref _publication,
            publicationToken,
            InactivePublication) == InactivePublication;
    }

    /// <summary>
    /// Clears the publication only when <paramref name="publicationToken"/>
    /// still identifies the active attempt.
    /// </summary>
    internal bool TryExit(long publicationToken)
        => publicationToken != InactivePublication &&
           Interlocked.CompareExchange(
               ref _publication,
               InactivePublication,
               publicationToken) == publicationToken;

    /// <summary>
    /// Captures one coherent activity observation.
    /// </summary>
    internal DesktopFrameActivitySnapshot Capture()
    {
        long publication = Volatile.Read(ref _publication);
        return publication == InactivePublication
            ? new DesktopFrameActivitySnapshot(false, 0, -1)
            : Decode(publication);
    }

    private static long Encode(ulong frameNumber, int frameSlot)
    {
        if (frameNumber == 0 || frameNumber > MaxEncodableFrameNumber)
            throw new ArgumentOutOfRangeException(nameof(frameNumber));
        if ((uint)frameSlot > MaxEncodableFrameSlot)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));

        return checked(
            (long)((frameNumber << FrameSlotBitCount) |
                   unchecked((uint)frameSlot + 1u)));
    }

    private static DesktopFrameActivitySnapshot Decode(long publication)
    {
        ulong encoded = unchecked((ulong)publication);
        ulong frameNumber = encoded >> FrameSlotBitCount;
        int frameSlot = unchecked((int)(encoded & ((1u << FrameSlotBitCount) - 1u))) - 1;
        return new DesktopFrameActivitySnapshot(true, frameNumber, frameSlot);
    }
}
