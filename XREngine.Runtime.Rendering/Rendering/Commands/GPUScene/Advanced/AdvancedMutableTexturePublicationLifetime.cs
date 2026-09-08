using System.Threading;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Serializes in-place GPU writes against canonical publication readers without
/// calling back into the producer's lock from the scene database lock.
/// One instance belongs to one physical texture allocation for its entire life.
/// </summary>
internal sealed class AdvancedMutableTexturePublicationLifetime : IAdvancedGpuPublicationSourceLifetime
{
    private const long Published = 1L << 62;
    private const long Writing = 1L << 61;
    private const long Retired = 1L << 60;
    private const long ReferenceMask = Retired - 1;
    private long _state;
    private long _contentGeneration;

    public bool IsPublished => (Volatile.Read(ref _state) & Published) != 0;
    public long ReferenceCount => Volatile.Read(ref _state) & ReferenceMask;
    public bool CanWrite => Volatile.Read(ref _state) == 0;

    /// <summary>Reserves the only writer after withdrawal and the last reader release.</summary>
    public bool TryBeginWrite()
        => Interlocked.CompareExchange(ref _state, Writing, 0) == 0;

    /// <summary>Ends a settled writer; generation zero invalidates an unsuccessful output.</summary>
    public void EndWrite(ulong contentGeneration)
    {
        if (Volatile.Read(ref _state) != Writing)
            throw new InvalidOperationException("A mutable texture writer was not reserved.");
        Volatile.Write(ref _contentGeneration, unchecked((long)contentGeneration));
        if (Interlocked.CompareExchange(ref _state, 0, Writing) != Writing)
            throw new InvalidOperationException("A mutable texture writer changed ownership before completion.");
    }

    /// <summary>Keeps this completed texture canonical even between snapshot readers.</summary>
    public bool TryPublish()
    {
        if (Volatile.Read(ref _contentGeneration) == 0)
            return false;
        while (true)
        {
            long state = Volatile.Read(ref _state);
            if ((state & (Writing | Retired)) != 0)
                return false;
            if ((state & Published) != 0 || Interlocked.CompareExchange(ref _state, state | Published, state) == state)
                return true;
        }
    }

    /// <summary>Stops future publication readers while preserving all accepted readers.</summary>
    public void Withdraw()
        => Interlocked.And(ref _state, ~Published);

    /// <summary>Prevents stale bindings from retaining a destroyed physical allocation.</summary>
    public bool TryRetire()
        => Interlocked.CompareExchange(ref _state, Retired, 0) == 0;

    public bool TryRetainPublication()
        => TryRetainPublication(unchecked((ulong)Volatile.Read(ref _contentGeneration)));

    public bool TryRetainPublication(ulong expectedContentGeneration)
    {
        if (expectedContentGeneration == 0)
            return false;
        while (true)
        {
            long state = Volatile.Read(ref _state);
            if ((state & (Published | Writing | Retired)) != Published ||
                unchecked((ulong)Volatile.Read(ref _contentGeneration)) != expectedContentGeneration)
                return false;
            if ((state & ReferenceMask) == ReferenceMask)
                throw new InvalidOperationException("Mutable texture publication reference overflow.");
            if (Interlocked.CompareExchange(ref _state, state + 1, state) != state)
                continue;
            // Withdrawal, a write, and republication may have completed between
            // the earlier generation read and CAS. The retained count now blocks
            // further writes, so this second read rejects that stale description.
            if (unchecked((ulong)Volatile.Read(ref _contentGeneration)) == expectedContentGeneration)
                return true;
            ReleasePublication();
            return false;
        }
    }

    public void ReleasePublication()
    {
        while (true)
        {
            long state = Volatile.Read(ref _state);
            if ((state & ReferenceMask) == 0)
                throw new InvalidOperationException("Mutable texture publication reference underflow.");
            if (Interlocked.CompareExchange(ref _state, state - 1, state) == state)
                return;
        }
    }
}
