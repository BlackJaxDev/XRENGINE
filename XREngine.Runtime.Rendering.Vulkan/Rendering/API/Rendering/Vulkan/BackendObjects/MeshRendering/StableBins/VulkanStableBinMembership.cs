namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Render-thread-owned stable-bin membership. Template primary/variant slots
/// directly address intrusive links, while a fixed open-addressed numeric-key
/// table addresses bin heads. Publication, eviction, and a bin move are O(1)
/// and allocate nothing after capacity has been provisioned.
/// </summary>
internal sealed class VulkanStableBinMembership
{
    private struct SlotLink
    {
        internal VulkanResidentDrawTemplateHandle Handle;
        internal int BinSlot;
        internal int Previous;
        internal int Next;
        internal bool IsLinked;
    }

    private struct BinHead
    {
        internal VulkanRenderBinKey Key;
        internal int Head;
        internal int NextFree;
        internal bool IsOccupied;
    }

    private SlotLink[] _slots;
    private BinHead[] _bins;
    // 0 is empty, -1 is a deleted bucket, and positive values are bin slots.
    private int[] _binLookup;
    private readonly int _variantsPerPrimary;
    private int _freeBinHead;
    private int _memberCount;
    private int _binCount;
    private ulong _topologyGeneration;

    internal VulkanStableBinMembership(uint primaryCapacity, int variantsPerPrimary)
    {
        if (primaryCapacity == 0u || primaryCapacity >= int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(primaryCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThan(variantsPerPrimary, 1);

        _variantsPerPrimary = variantsPerPrimary;
        int membershipCapacity = checked((int)primaryCapacity * variantsPerPrimary);
        _slots = new SlotLink[checked(membershipCapacity + 1)];
        _bins = new BinHead[checked(membershipCapacity + 1)];
        _binLookup = new int[GetLookupCapacity(membershipCapacity)];
        InitializeFreeBins(1);
    }

    internal int MemberCount => _memberCount;
    internal int BinCount => _binCount;
    /// <summary>Monotonic generation for cached topology-only bin artifacts.</summary>
    internal ulong TopologyGeneration => _topologyGeneration;
    internal uint PrimaryCapacity => checked(
        (uint)(_slots.Length - 1) / (uint)_variantsPerPrimary);

    /// <summary>Boundary-only capacity growth; never call from frame recording.</summary>
    internal bool GrowAtBoundary(uint requiredCapacity)
    {
        if (requiredCapacity <= PrimaryCapacity)
            return true;
        if (requiredCapacity >= int.MaxValue)
            return false;

        int previousBinLength = _bins.Length;
        int membershipCapacity = checked((int)requiredCapacity * _variantsPerPrimary);
        Array.Resize(ref _slots, checked(membershipCapacity + 1));
        Array.Resize(ref _bins, checked(membershipCapacity + 1));
        InitializeFreeBins(previousBinLength);
        RebuildLookup(GetLookupCapacity(membershipCapacity));
        ++_topologyGeneration;
        return true;
    }

    /// <summary>
    /// Adds, removes, or moves one exact generation-checked template handle.
    /// Equal handle/key pairs are no-ops, so content-only generation updates do
    /// not perturb membership topology.
    /// </summary>
    internal bool TryUpdateTopology(
        in VulkanResidentDrawTemplateHandle handle,
        in VulkanRenderBinKey key,
        out VulkanStableBinMembershipFailure failure)
    {
        failure = VulkanStableBinMembershipFailure.None;
        if (!TryGetMembershipSlot(in handle, out int membershipSlot))
        {
            failure = VulkanStableBinMembershipFailure.SlotOutOfRange;
            return false;
        }
        if (!key.IsValid)
        {
            failure = VulkanStableBinMembershipFailure.InvalidKey;
            return false;
        }

        ref SlotLink slot = ref _slots[membershipSlot];
        if (slot.IsLinked && slot.Handle == handle &&
            _bins[slot.BinSlot].Key == key)
        {
            return true;
        }

        // Resolve the destination before disconnecting a live entry. Failure
        // therefore leaves the currently published resident template intact.
        int binSlot = FindBin(in key);
        if (binSlot == 0 && !TryCreateBin(in key, out binSlot))
        {
            failure = VulkanStableBinMembershipFailure.BinCapacityExceeded;
            return false;
        }

        // Replacing a resident entry can advance its generation without moving
        // it between bins. Keep the intrusive links intact while publishing
        // the new ABA token.
        if (slot.IsLinked && slot.BinSlot == binSlot)
        {
            slot.Handle = handle;
            ++_topologyGeneration;
            return true;
        }

        if (slot.IsLinked)
            Unlink(membershipSlot);

        Link(membershipSlot, binSlot, in handle);
        ++_topologyGeneration;
        return true;
    }

    /// <summary>
    /// Removes only the exact generation-checked member. A reused resident
    /// slot cannot be removed by an obsolete handle (ABA-safe eviction).
    /// </summary>
    internal void Remove(in VulkanResidentDrawTemplateHandle handle)
    {
        if (!TryGetMembershipSlot(in handle, out int membershipSlot))
            return;

        ref SlotLink slot = ref _slots[membershipSlot];
        if (!slot.IsLinked || slot.Handle != handle)
            return;

        Unlink(membershipSlot);
        ++_topologyGeneration;
    }

    internal bool TryGetHead(in VulkanRenderBinKey key, out int membershipSlot)
    {
        int binSlot = FindBin(in key);
        membershipSlot = binSlot == 0 ? 0 : _bins[binSlot].Head;
        return membershipSlot != 0;
    }

    internal int GetNext(int membershipSlot)
        => membershipSlot > 0 && membershipSlot < _slots.Length
            ? _slots[membershipSlot].Next
            : 0;

    internal bool TryGetHandle(int membershipSlot, out VulkanResidentDrawTemplateHandle handle)
    {
        handle = default;
        if (membershipSlot <= 0 || membershipSlot >= _slots.Length ||
            !_slots[membershipSlot].IsLinked)
        {
            return false;
        }

        handle = _slots[membershipSlot].Handle;
        return true;
    }

    internal bool Contains(in VulkanResidentDrawTemplateHandle handle)
    {
        if (!TryGetMembershipSlot(in handle, out int membershipSlot))
            return false;
        return _slots[membershipSlot].IsLinked &&
            _slots[membershipSlot].Handle == handle;
    }

    /// <summary>
    /// Copies an exact bin's members in direct membership-slot order. This is
    /// deterministic across equivalent publication histories and does not
    /// expose mutable intrusive link order to frame ingress.
    /// </summary>
    internal int CopyMembersDeterministically(
        in VulkanRenderBinKey key,
        Span<VulkanResidentDrawTemplateHandle> destination)
    {
        int binSlot = FindBin(in key);
        if (binSlot == 0)
            return 0;

        int count = 0;
        for (int slotIndex = 1; slotIndex < _slots.Length; ++slotIndex)
        {
            ref readonly SlotLink link = ref _slots[slotIndex];
            if (!link.IsLinked || link.BinSlot != binSlot)
                continue;
            if (count == destination.Length)
                return -1;
            destination[count++] = link.Handle;
        }
        return count;
    }

    /// <summary>Deterministic full membership enumeration for current-frame intersection.</summary>
    internal int CopyAllMembersDeterministically(
        Span<VulkanResidentDrawTemplateHandle> destination)
    {
        if (destination.Length < _memberCount)
            return -1;

        int count = 0;
        for (int slotIndex = 1; slotIndex < _slots.Length; ++slotIndex)
        {
            ref readonly SlotLink link = ref _slots[slotIndex];
            if (link.IsLinked)
                destination[count++] = link.Handle;
        }
        return count;
    }

    private void Link(
        int membershipSlot,
        int binSlot,
        in VulkanResidentDrawTemplateHandle handle)
    {
        ref BinHead bin = ref _bins[binSlot];
        ref SlotLink slot = ref _slots[membershipSlot];
        slot.Handle = handle;
        slot.BinSlot = binSlot;
        slot.Previous = 0;
        slot.Next = bin.Head;
        slot.IsLinked = true;
        if (bin.Head != 0)
            _slots[bin.Head].Previous = membershipSlot;
        bin.Head = membershipSlot;
        ++_memberCount;
    }

    private void Unlink(int membershipSlot)
    {
        ref SlotLink slot = ref _slots[membershipSlot];
        ref BinHead bin = ref _bins[slot.BinSlot];
        if (slot.Previous == 0)
            bin.Head = slot.Next;
        else
            _slots[slot.Previous].Next = slot.Next;
        if (slot.Next != 0)
            _slots[slot.Next].Previous = slot.Previous;

        if (bin.Head == 0)
            ReleaseBin(slot.BinSlot);
        slot = default;
        --_memberCount;
    }

    private bool TryCreateBin(in VulkanRenderBinKey key, out int binSlot)
    {
        binSlot = _freeBinHead;
        if (binSlot == 0)
            return false;

        ref BinHead bin = ref _bins[binSlot];
        _freeBinHead = bin.NextFree;
        bin.Key = key;
        bin.Head = 0;
        bin.NextFree = 0;
        bin.IsOccupied = true;
        InsertLookup(binSlot);
        ++_binCount;
        return true;
    }

    private void ReleaseBin(int binSlot)
    {
        ref BinHead bin = ref _bins[binSlot];
        int lookupIndex = FindLookupBucket(in bin.Key);
        if (lookupIndex < 0)
            throw new InvalidOperationException("Stable-bin membership lookup is corrupt.");

        _binLookup[lookupIndex] = -1;
        bin = default;
        bin.NextFree = _freeBinHead;
        _freeBinHead = binSlot;
        --_binCount;
    }

    private int FindBin(in VulkanRenderBinKey key)
    {
        int lookupIndex = FindLookupBucket(in key);
        return lookupIndex < 0 ? 0 : _binLookup[lookupIndex];
    }

    private int FindLookupBucket(in VulkanRenderBinKey key)
    {
        int mask = _binLookup.Length - 1;
        int index = (int)(GetKeyHash(in key) & (uint)mask);
        for (int probe = 0; probe < _binLookup.Length; ++probe)
        {
            int value = _binLookup[index];
            if (value == 0)
                return -1;
            if (value > 0 && _bins[value].Key == key)
                return index;
            index = (index + 1) & mask;
        }
        return -1;
    }

    private void InsertLookup(int binSlot)
    {
        VulkanRenderBinKey key = _bins[binSlot].Key;
        int mask = _binLookup.Length - 1;
        int index = (int)(GetKeyHash(in key) & (uint)mask);
        int tombstone = -1;
        for (int probe = 0; probe < _binLookup.Length; ++probe)
        {
            int value = _binLookup[index];
            if (value == 0)
            {
                _binLookup[tombstone >= 0 ? tombstone : index] = binSlot;
                return;
            }
            if (value < 0 && tombstone < 0)
                tombstone = index;
            index = (index + 1) & mask;
        }
        if (tombstone >= 0)
        {
            _binLookup[tombstone] = binSlot;
            return;
        }
        throw new InvalidOperationException("Stable-bin membership lookup capacity is exhausted.");
    }

    private void RebuildLookup(int lookupCapacity)
    {
        _binLookup = new int[lookupCapacity];
        for (int binSlot = 1; binSlot < _bins.Length; ++binSlot)
        {
            if (_bins[binSlot].IsOccupied)
                InsertLookup(binSlot);
        }
    }

    private void InitializeFreeBins(int firstBinSlot)
    {
        for (int binSlot = _bins.Length - 1; binSlot >= firstBinSlot; --binSlot)
        {
            _bins[binSlot].NextFree = _freeBinHead;
            _freeBinHead = binSlot;
        }
    }

    private static int GetLookupCapacity(int membershipCapacity)
    {
        uint required = checked((uint)membershipCapacity * 2u);
        uint capacity = 1u;
        while (capacity < required)
            capacity = checked(capacity << 1);
        return checked((int)capacity);
    }

    private static uint GetKeyHash(in VulkanRenderBinKey key)
    {
        ulong hash = 2166136261u;
        hash = MixHash(hash, key.PassCompatibility);
        hash = MixHash(hash, key.PipelineVariant);
        hash = MixHash(hash, key.GeometryPage);
        hash = MixHash(hash, key.TopologyAndIndexType);
        hash = MixHash(hash, key.DescriptorModel);
        hash = MixHash(hash, key.ViewMask);
        return unchecked((uint)MixHash(hash, (uint)key.OrderingClass));
    }

    private static ulong MixHash(ulong hash, ulong value)
        => (hash ^ value) * 16777619u;

    private bool TryGetMembershipSlot(
        in VulkanResidentDrawTemplateHandle handle,
        out int membershipSlot)
    {
        membershipSlot = 0;
        if (!handle.IsValid || handle.VariantOrdinal >= _variantsPerPrimary ||
            handle.PrimaryIndex == 0u || handle.PrimaryIndex > PrimaryCapacity)
        {
            return false;
        }
        membershipSlot = checked(
            ((int)handle.PrimaryIndex - 1) * _variantsPerPrimary +
            handle.VariantOrdinal + 1);
        return true;
    }
}

/// <summary>Exact fail-closed reason for a stable-bin membership rejection.</summary>
internal enum VulkanStableBinMembershipFailure : byte
{
    None = 0,
    SlotOutOfRange = 1,
    InvalidKey = 2,
    BinCapacityExceeded = 3,
}
