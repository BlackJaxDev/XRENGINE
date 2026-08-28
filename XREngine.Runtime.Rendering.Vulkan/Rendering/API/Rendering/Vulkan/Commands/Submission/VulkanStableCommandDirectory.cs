namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Fixed command submission directory. All callers hold the lifetime tracker
/// lock, so publication and tombstoning are atomic with the sealed contract
/// that references an entry.
/// </summary>
internal sealed class VulkanStableCommandDirectory
{
    private const int Capacity = 4096;
    private const int HandleTableCapacity = Capacity * 2;
    private const uint EmptyHandleTableEntry = 0;
    private const uint TombstoneHandleTableEntry = uint.MaxValue;
    private readonly Entry[] _entries = new Entry[Capacity];
    private readonly uint[] _freeLinks = new uint[Capacity];
    private readonly uint[] _handleSlots = new uint[HandleTableCapacity];
    private uint _nextIndex = 1;
    private uint _freeHead;

    private sealed class Entry
    {
        internal ulong Handle;
        internal ulong RecordingGeneration;
        internal ulong Generation;
        internal VulkanCommandBufferLifetimeRecord? Lifetime;
        internal VulkanCommandBufferTrackingBatch? TrackingBatch;
    }

    internal bool TryPublish(
        ulong handle,
        ulong recordingGeneration,
        VulkanCommandBufferLifetimeRecord lifetime,
        VulkanCommandBufferTrackingBatch? trackingBatch,
        out VulkanStableCommandSlotHandle identity)
    {
        identity = VulkanStableCommandSlotHandle.Invalid;
        if (handle == 0 || lifetime is null)
            return false;

        Tombstone(lifetime.StableCommandIdentity);
        uint index;
        if (_freeHead != 0)
        {
            index = _freeHead;
            _freeHead = _freeLinks[index];
        }
        else if (_nextIndex < Capacity)
            index = _nextIndex++;
        else
            return false;

        Entry entry = _entries[index] ??= new Entry();
        ulong generation = unchecked(entry.Generation + 1UL);
        if (generation == 0)
            generation = 1;
        entry.Handle = handle;
        entry.RecordingGeneration = recordingGeneration;
        entry.Generation = generation;
        entry.Lifetime = lifetime;
        entry.TrackingBatch = trackingBatch;
        if (!TryInsertHandle(handle, index))
        {
            entry.Handle = 0;
            entry.RecordingGeneration = 0;
            entry.Lifetime = null;
            entry.TrackingBatch = null;
            _freeLinks[index] = _freeHead;
            _freeHead = index;
            return false;
        }
        identity = new VulkanStableCommandSlotHandle(index, generation);
        lifetime.StableCommandIdentity = identity;
        return true;
    }

    internal bool TryResolve(
        VulkanStableCommandSlotHandle identity,
        ulong handle,
        ulong recordingGeneration,
        out VulkanCommandBufferLifetimeRecord lifetime,
        out VulkanCommandBufferTrackingBatch? trackingBatch)
    {
        lifetime = null!;
        trackingBatch = null;
        if (!identity.IsValid || identity.Index >= _nextIndex)
            return false;

        Entry? entry = _entries[identity.Index];
        if (entry is null ||
            entry.Generation != identity.Generation ||
            entry.Handle != handle ||
            entry.RecordingGeneration != recordingGeneration ||
            entry.Lifetime is null ||
            entry.Lifetime.StableCommandIdentity != identity)
        {
            return false;
        }

        lifetime = entry.Lifetime;
        trackingBatch = entry.TrackingBatch;
        return true;
    }

    /// <summary>Expected O(1) handle lookup using the fixed open-addressed table.</summary>
    internal bool TryResolveByHandle(
        ulong handle,
        out VulkanStableCommandSlotHandle identity,
        out VulkanCommandBufferLifetimeRecord lifetime,
        out VulkanCommandBufferTrackingBatch? trackingBatch)
    {
        identity = VulkanStableCommandSlotHandle.Invalid;
        lifetime = null!;
        trackingBatch = null;
        if (handle == 0)
            return false;

        if (!TryFindHandleSlot(handle, out uint index))
            return false;

        Entry? entry = _entries[index];
        if (entry is null || entry.Lifetime is null)
            return false;

        VulkanStableCommandSlotHandle candidate = new(index, entry.Generation);
        return TryResolve(candidate, handle, entry.RecordingGeneration, out lifetime, out trackingBatch)
            && (identity = candidate).IsValid;
    }

    internal void Tombstone(VulkanStableCommandSlotHandle identity)
    {
        if (!identity.IsValid || identity.Index >= _nextIndex)
            return;

        Entry? entry = _entries[identity.Index];
        if (entry is null || entry.Generation != identity.Generation)
            return;

        if (entry.Lifetime is not null &&
            entry.Lifetime.StableCommandIdentity == identity)
        {
            entry.Lifetime.StableCommandIdentity = VulkanStableCommandSlotHandle.Invalid;
        }
        RemoveHandle(entry.Handle, identity.Index);
        entry.Handle = 0;
        entry.RecordingGeneration = 0;
        entry.Lifetime = null;
        entry.TrackingBatch = null;
        _freeLinks[identity.Index] = _freeHead;
        _freeHead = identity.Index;
        RebuildHandleIndex();
    }

    /// <summary>Rare destruction cleanup. Stable submission never calls this scan.</summary>
    internal void TombstoneByHandle(ulong handle)
    {
        if (handle == 0)
            return;

        if (TryFindHandleSlot(handle, out uint index) && _entries[index] is { } entry)
            Tombstone(new VulkanStableCommandSlotHandle(index, entry.Generation));
    }

    private bool TryInsertHandle(ulong handle, uint slot)
    {
        int mask = HandleTableCapacity - 1;
        int start = (int)(MixHandle(handle) & (uint)mask);
        int firstTombstone = -1;
        for (int probe = 0; probe < HandleTableCapacity; ++probe)
        {
            int tableIndex = (start + probe) & mask;
            uint existing = _handleSlots[tableIndex];
            if (existing == EmptyHandleTableEntry)
            {
                _handleSlots[firstTombstone >= 0 ? firstTombstone : tableIndex] = slot;
                return true;
            }
            if (existing == TombstoneHandleTableEntry)
            {
                if (firstTombstone < 0)
                    firstTombstone = tableIndex;
                continue;
            }
            if (_entries[existing]?.Handle == handle)
                return false;
        }

        if (firstTombstone >= 0)
        {
            _handleSlots[firstTombstone] = slot;
            return true;
        }
        return false;
    }

    private bool TryFindHandleSlot(ulong handle, out uint slot)
    {
        int mask = HandleTableCapacity - 1;
        int start = (int)(MixHandle(handle) & (uint)mask);
        for (int probe = 0; probe < HandleTableCapacity; ++probe)
        {
            uint entry = _handleSlots[(start + probe) & mask];
            if (entry == EmptyHandleTableEntry)
                break;
            if (entry != TombstoneHandleTableEntry && _entries[entry]?.Handle == handle)
            {
                slot = entry;
                return true;
            }
        }

        slot = 0;
        return false;
    }

    private void RemoveHandle(ulong handle, uint slot)
    {
        if (handle == 0)
            return;

        int mask = HandleTableCapacity - 1;
        int start = (int)(MixHandle(handle) & (uint)mask);
        for (int probe = 0; probe < HandleTableCapacity; ++probe)
        {
            int tableIndex = (start + probe) & mask;
            uint entry = _handleSlots[tableIndex];
            if (entry == EmptyHandleTableEntry)
                return;
            if (entry == slot)
            {
                _handleSlots[tableIndex] = TombstoneHandleTableEntry;
                return;
            }
        }
    }

    /// <summary>
    /// Reclaims probe tombstones on the cold recording/destruction path so a
    /// long-lived renderer cannot turn a stable miss into a table-length walk.
    /// </summary>
    private void RebuildHandleIndex()
    {
        Array.Clear(_handleSlots);
        for (uint index = 1; index < _nextIndex; ++index)
        {
            Entry? entry = _entries[index];
            if (entry is not null && entry.Handle != 0)
                _ = TryInsertHandle(entry.Handle, index);
        }
    }

    private static ulong MixHandle(ulong value)
    {
        value ^= value >> 33;
        value *= 0xff51afd7ed558ccdUL;
        value ^= value >> 33;
        return value;
    }
}
