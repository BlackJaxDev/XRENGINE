using Silk.NET.Vulkan;
using XREngine.Rendering.Commands;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retains native, append-only geometry arena images independently of the
/// frame-data arena. A native entry is pinned while any frame slot can still
/// reference it, so a later append never overwrites a prefix in flight.
/// </summary>
internal sealed class VulkanAdvancedGeometryResidentCache
{
    private const int Capacity = 256;
    private const ulong MaximumRequestedImageBytes = 1UL * 1024UL * 1024UL * 1024UL;
    private const ulong MaximumRetainedBytes = 2UL * 1024UL * 1024UL * 1024UL;
    private const ulong MinimumAllocationBytes = 16UL;
    private const ulong AllocationAlignment = 4UL * 1024UL * 1024UL;
    private const ulong CacheIdentityMask = 1UL << 63;

    private readonly object _gate = new();
    private readonly VulkanResourceRuntime _resources;
    private readonly Entry[] _entries = new Entry[Capacity];
    private ulong _nextIdentity;
    private ulong _useSerial;
    private ulong _residentBytes;
    private int _allocationCount;

    internal VulkanAdvancedGeometryResidentCache(VulkanResourceRuntime resources)
        => _resources = resources ?? throw new ArgumentNullException(nameof(resources));

    /// <summary>Native allocation bytes that remain live, including queued retirement.</summary>
    internal ulong ResidentBytes
    {
        get { lock (_gate) return _residentBytes; }
    }

    /// <summary>Native allocation count that remains live, including queued retirement.</summary>
    internal int AllocationCount
    {
        get { lock (_gate) return _allocationCount; }
    }

    internal bool TryPrepare(
        int frameSlot,
        ulong databaseEpoch,
        AdvancedGeometryPublicationSnapshot snapshot,
        out VulkanAdvancedGeometryPublication result,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        result = default;
        lock (_gate)
        {
            ReapRetiredNoLock();
            if (databaseEpoch == 0u || frameSlot is < 0 or >= 64)
            {
                failure = EVulkanAdvancedSceneResourceFailure.InvalidFrameOwner;
                reason = "The advanced geometry cache requires a non-zero database epoch and a frame slot from 0 through 63.";
                return false;
            }
            if (_resources.BackendObjectContext is not { IsDeviceOperational: true } context)
            {
                failure = EVulkanAdvancedSceneResourceFailure.RuntimeUnavailable;
                reason = "The Vulkan backend-object context is unavailable for advanced geometry publication.";
                return false;
            }

            if (!TryCalculateRequestedBytes(
                    snapshot,
                    out ulong totalRequestedBytes,
                    out ulong totalCapacityBytes))
            {
                failure = EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
                reason = $"The advanced geometry publication requests {totalRequestedBytes} bytes across its seven immutable streams and needs {totalCapacityBytes} bytes after resident-bank spare capacity and alignment, exceeding the {MaximumRetainedBytes}-byte cache limit.";
                return false;
            }

            Span<VulkanFrameDataSlice> preparedSlices = stackalloc VulkanFrameDataSlice[7];
            byte newlyPinned = 0;
            bool staticVerticesNew = false;
            bool indicesNew = false;
            bool preSkinnedCurrentNew = false;
            bool preSkinnedPreviousNew = false;
            bool meshletDescriptorsNew = false;
            bool meshletVertexIndicesNew = false;
            bool meshletTriangleWordsNew = false;
            if (!TryPrepareOneNoLock(frameSlot, databaseEpoch, snapshot.StaticVertices, context,
                    out preparedSlices[0], out staticVerticesNew, out failure, out reason) ||
                !TryPrepareOneNoLock(frameSlot, databaseEpoch, snapshot.Indices, context,
                    out preparedSlices[1], out indicesNew, out failure, out reason) ||
                !TryPrepareOneNoLock(frameSlot, databaseEpoch, snapshot.PreSkinnedCurrent, context,
                    out preparedSlices[2], out preSkinnedCurrentNew, out failure, out reason) ||
                !TryPrepareOneNoLock(frameSlot, databaseEpoch, snapshot.PreSkinnedPrevious, context,
                    out preparedSlices[3], out preSkinnedPreviousNew, out failure, out reason) ||
                !TryPrepareOneNoLock(frameSlot, databaseEpoch, snapshot.MeshletDescriptors, context,
                    out preparedSlices[4], out meshletDescriptorsNew, out failure, out reason) ||
                !TryPrepareOneNoLock(frameSlot, databaseEpoch, snapshot.MeshletVertexIndices, context,
                    out preparedSlices[5], out meshletVertexIndicesNew, out failure, out reason) ||
                !TryPrepareOneNoLock(frameSlot, databaseEpoch, snapshot.MeshletTriangleWords, context,
                    out preparedSlices[6], out meshletTriangleWordsNew, out failure, out reason))
            {
                newlyPinned = (byte)((staticVerticesNew ? VulkanAdvancedGeometryPublication.StaticVerticesPin : 0) |
                    (indicesNew ? VulkanAdvancedGeometryPublication.IndicesPin : 0) |
                    (preSkinnedCurrentNew ? VulkanAdvancedGeometryPublication.PreSkinnedCurrentPin : 0) |
                    (preSkinnedPreviousNew ? VulkanAdvancedGeometryPublication.PreSkinnedPreviousPin : 0) |
                    (meshletDescriptorsNew ? VulkanAdvancedGeometryPublication.MeshletDescriptorsPin : 0) |
                    (meshletVertexIndicesNew ? VulkanAdvancedGeometryPublication.MeshletVertexIndicesPin : 0) |
                    (meshletTriangleWordsNew ? VulkanAdvancedGeometryPublication.MeshletTriangleWordsPin : 0));
                RollbackNewPinsNoLock(frameSlot, preparedSlices, newlyPinned);
                return false;
            }

            newlyPinned = (byte)((staticVerticesNew ? VulkanAdvancedGeometryPublication.StaticVerticesPin : 0) |
                (indicesNew ? VulkanAdvancedGeometryPublication.IndicesPin : 0) |
                (preSkinnedCurrentNew ? VulkanAdvancedGeometryPublication.PreSkinnedCurrentPin : 0) |
                (preSkinnedPreviousNew ? VulkanAdvancedGeometryPublication.PreSkinnedPreviousPin : 0) |
                (meshletDescriptorsNew ? VulkanAdvancedGeometryPublication.MeshletDescriptorsPin : 0) |
                (meshletVertexIndicesNew ? VulkanAdvancedGeometryPublication.MeshletVertexIndicesPin : 0) |
                (meshletTriangleWordsNew ? VulkanAdvancedGeometryPublication.MeshletTriangleWordsPin : 0));

            result = new VulkanAdvancedGeometryPublication(
                preparedSlices[0], preparedSlices[1], preparedSlices[2], preparedSlices[3],
                preparedSlices[4], preparedSlices[5], preparedSlices[6], newlyPinned);
            failure = EVulkanAdvancedSceneResourceFailure.None;
            reason = "Ready";
            return true;
        }
    }

    /// <summary>Releases the cache pins that belong to a completed frame slot.</summary>
    internal void ReleaseFrameSlot(int frameSlot)
    {
        if (frameSlot is < 0 or >= 64)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        ulong pin = 1UL << frameSlot;
        lock (_gate)
        {
            for (int index = 0; index < _entries.Length; ++index)
                _entries[index].FrameSlotPins &= ~pin;
            RetireSupersededUnpinnedEntriesNoLock();
            ReapRetiredNoLock();
        }
    }

    /// <summary>
    /// Reverses only pins acquired by a failed higher-level publication. Pins
    /// that were already held by an accepted publication in the same slot are
    /// deliberately left intact.
    /// </summary>
    internal void Rollback(int frameSlot, in VulkanAdvancedGeometryPublication publication)
    {
        if (frameSlot is < 0 or >= 64)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        if (publication.NewlyPinnedStreams == 0u)
            return;

        Span<VulkanFrameDataSlice> slices = stackalloc VulkanFrameDataSlice[]
        {
            publication.StaticVertices, publication.Indices,
            publication.PreSkinnedCurrent, publication.PreSkinnedPrevious,
            publication.MeshletDescriptors, publication.MeshletVertexIndices,
            publication.MeshletTriangleWords,
        };
        lock (_gate)
            RollbackNewPinsNoLock(frameSlot, slices, publication.NewlyPinnedStreams);
    }

    /// <summary>Queues every cached allocation for normal lifetime-tracked retirement.</summary>
    internal void RetireAll()
    {
        lock (_gate)
        {
            for (int index = 0; index < _entries.Length; ++index)
            {
                ref Entry entry = ref _entries[index];
                entry.FrameSlotPins = 0u;
                RetireNoLock(ref entry, "AdvancedGeometry.CacheShutdown");
            }
            ReapRetiredNoLock();
        }
    }

    private bool TryPrepareOneNoLock(
        int frameSlot,
        ulong databaseEpoch,
        in AdvancedImmutableByteArenaPublicationSnapshot snapshot,
        VulkanBackendObjectContext context,
        out VulkanFrameDataSlice slice,
        out bool newlyPinned,
        out EVulkanAdvancedSceneResourceFailure failure,
        out string reason)
    {
        slice = default;
        newlyPinned = false;
        if (!snapshot.IsValid)
        {
            failure = EVulkanAdvancedSceneResourceFailure.InvalidPublication;
            reason = "The retained advanced geometry publication contains an invalid immutable arena image.";
            return false;
        }

        ulong requestedBytes = Math.Max((ulong)snapshot.ByteCount, MinimumAllocationBytes);
        if (requestedBytes > MaximumRequestedImageBytes)
        {
            failure = EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            reason = $"The advanced geometry image requests {requestedBytes} bytes, exceeding the {MaximumRequestedImageBytes}-byte per-image limit.";
            return false;
        }
        if (!TryValidateStorageRange(context, requestedBytes, out reason))
        {
            failure = EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
            return false;
        }

        ulong pin = 1UL << frameSlot;
        int matchIndex = FindCompatibleEntryNoLock(
            databaseEpoch, snapshot.BufferHandle, requestedBytes, snapshot.ByteCount);
        if (matchIndex >= 0)
        {
            ref Entry match = ref _entries[matchIndex];
            if (snapshot.ByteCount > match.PublishedBytes &&
                !TryWriteNoLock(ref match, match.PublishedBytes, snapshot.Data.Slice(checked((int)match.PublishedBytes)), context, out reason))
            {
                failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
                return false;
            }
            match.PublishedBytes = Math.Max(match.PublishedBytes, (ulong)snapshot.ByteCount);
            newlyPinned = (match.FrameSlotPins & pin) == 0u;
            match.FrameSlotPins |= pin;
            match.LastUseSerial = NextUseSerialNoLock();
            slice = match.CreateSlice(snapshot.ByteCount);
            failure = EVulkanAdvancedSceneResourceFailure.None;
            reason = "Ready";
            return true;
        }

        if (!TryFindVacantEntryNoLock(out int entryIndex))
        {
            failure = EVulkanAdvancedSceneResourceFailure.FrameSlotStillInUse;
            reason = $"The advanced geometry cache exhausted its {Capacity} allocation entries; retry after pinned frame slots or queued native retirements complete.";
            return false;
        }

        // A zero-length stream is bound as a 16-byte zero sentinel. Keeping
        // that bank exactly 16 bytes makes a later non-empty publication a
        // distinct replacement whenever the sentinel remains frame-pinned.
        ulong capacity = snapshot.ByteCount == 0u
            ? MinimumAllocationBytes
            : CalculateCapacity(requestedBytes);
        if (!CanAllocateNoLock(capacity))
        {
            bool queuedRetirement = RetireUnpinnedForPressureNoLock(capacity);
            failure = EVulkanAdvancedSceneResourceFailure.FrameSlotStillInUse;
            reason = queuedRetirement || HasPendingRetirementNoLock()
                ? $"The advanced geometry cache queued unpinned resident allocations for retirement under its {MaximumRetainedBytes}-byte live-allocation limit; retry after their frame-slot retirements complete. Current tracked bytes are {_residentBytes}, requested capacity is {capacity}."
                : $"The advanced geometry cache cannot retain another {capacity}-byte allocation within its {MaximumRetainedBytes}-byte live-allocation limit because active frame slots still pin {_residentBytes} tracked bytes.";
            return false;
        }

        try
        {
            (Buffer buffer, DeviceMemory memory) = _resources.Buffers.CreateDedicatedRaw(
                context,
                capacity,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit |
                BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferSrcBit |
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                owner: "AdvancedGeometry.ResidentCache");
            if (!_resources.Buffers.TryGetAllocation(buffer, out VulkanMemoryAllocation allocation))
            {
                _resources.Buffers.Retire(buffer, memory, "AdvancedGeometry.ResidentCache.Untracked");
                failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
                reason = "The advanced geometry allocation was not registered with Vulkan resource lifetime tracking.";
                return false;
            }
            if (allocation.Size < requestedBytes ||
                allocation.Size > MaximumRequestedImageBytes ||
                _residentBytes > MaximumRetainedBytes - allocation.Size)
            {
                // The allocation is already lifetime-tracked. Keep it in a
                // retiring entry until the resource service confirms that its
                // allocation vanished, so budget telemetry includes this
                // failed publication attempt as well.
                ref Entry overBudget = ref _entries[entryIndex];
                overBudget.Initialize(databaseEpoch, snapshot.BufferHandle, buffer, memory,
                    allocation.Size, NextIdentityNoLock());
                _residentBytes = checked(_residentBytes + allocation.Size);
                ++_allocationCount;
                RetireNoLock(ref overBudget, "AdvancedGeometry.ResidentCache.OverBudget");
                failure = EVulkanAdvancedSceneResourceFailure.FrameStorageCapacity;
                reason = $"The advanced geometry allocation consumed {allocation.Size} bytes and would exceed the {MaximumRetainedBytes}-byte live-allocation limit.";
                return false;
            }

            ref Entry entry = ref _entries[entryIndex];
            entry.Initialize(databaseEpoch, snapshot.BufferHandle, buffer, memory,
                allocation.Size, NextIdentityNoLock());
            _residentBytes = checked(_residentBytes + allocation.Size);
            ++_allocationCount;
            if (!TryWriteNoLock(ref entry, 0u, snapshot.Data, context, out reason))
            {
                RetireNoLock(ref entry, "AdvancedGeometry.ResidentCache.UploadFailure");
                failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
                return false;
            }
            if (snapshot.ByteCount == 0u &&
                !TryWriteZeroSentinelNoLock(ref entry, context, out reason))
            {
                RetireNoLock(ref entry, "AdvancedGeometry.ResidentCache.SentinelFailure");
                failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
                return false;
            }

            entry.PublishedBytes = snapshot.ByteCount;
            newlyPinned = true;
            entry.FrameSlotPins = pin;
            entry.LastUseSerial = NextUseSerialNoLock();
            slice = entry.CreateSlice(snapshot.ByteCount);
            Debug.Vulkan(
                "[VulkanAdvancedGeometry] Allocated resident bank: epoch={0}, handle={1}:{2}, requestedBytes={3}, publishedBytes={4}, allocationBytes={5}, residentBytes={6}, allocationCount={7}.",
                databaseEpoch,
                snapshot.BufferHandle.Index,
                snapshot.BufferHandle.Generation,
                requestedBytes,
                entry.PublishedBytes,
                entry.Capacity,
                _residentBytes,
                _allocationCount);
            failure = EVulkanAdvancedSceneResourceFailure.None;
            reason = "Ready";
            return true;
        }
        catch (Exception exception)
        {
            failure = EVulkanAdvancedSceneResourceFailure.NativeFault;
            reason = $"Advanced geometry native allocation failed: {exception.Message}";
            return false;
        }
    }

    private int FindCompatibleEntryNoLock(
        ulong databaseEpoch,
        AdvancedGpuHandle bufferHandle,
        ulong requestedBytes,
        uint requestedPublishedBytes)
    {
        int candidate = -1;
        ulong candidateCapacity = ulong.MaxValue;
        for (int index = 0; index < _entries.Length; ++index)
        {
            ref Entry entry = ref _entries[index];
            if (!entry.IsResident || entry.DatabaseEpoch != databaseEpoch ||
                entry.BufferHandle != bufferHandle || entry.Capacity < requestedBytes)
                continue;
            // The sentinel itself is part of the empty stream's observable
            // binding range. A later non-empty image would overwrite it, so
            // retain the sentinel bank until its frame-slot pins clear.
            if (entry.PublishedBytes == 0u && requestedPublishedBytes != 0u &&
                entry.FrameSlotPins != 0u)
                continue;
            if (entry.Capacity < candidateCapacity)
            {
                candidate = index;
                candidateCapacity = entry.Capacity;
            }
        }
        return candidate;
    }

    private static bool TryCalculateRequestedBytes(
        AdvancedGeometryPublicationSnapshot snapshot,
        out ulong totalRequestedBytes,
        out ulong totalCapacityBytes)
    {
        try
        {
            ulong staticVertices = Math.Max((ulong)snapshot.StaticVertices.ByteCount, MinimumAllocationBytes);
            ulong indices = Math.Max((ulong)snapshot.Indices.ByteCount, MinimumAllocationBytes);
            ulong preSkinnedCurrent = Math.Max((ulong)snapshot.PreSkinnedCurrent.ByteCount, MinimumAllocationBytes);
            ulong preSkinnedPrevious = Math.Max((ulong)snapshot.PreSkinnedPrevious.ByteCount, MinimumAllocationBytes);
            ulong meshletDescriptors = Math.Max((ulong)snapshot.MeshletDescriptors.ByteCount, MinimumAllocationBytes);
            ulong meshletVertexIndices = Math.Max((ulong)snapshot.MeshletVertexIndices.ByteCount, MinimumAllocationBytes);
            ulong meshletTriangleWords = Math.Max((ulong)snapshot.MeshletTriangleWords.ByteCount, MinimumAllocationBytes);
            totalRequestedBytes = checked(staticVertices + indices + preSkinnedCurrent + preSkinnedPrevious +
                meshletDescriptors + meshletVertexIndices + meshletTriangleWords);
            totalCapacityBytes = checked(
                CapacityForPreflight(staticVertices, snapshot.StaticVertices.ByteCount) +
                CapacityForPreflight(indices, snapshot.Indices.ByteCount) +
                CapacityForPreflight(preSkinnedCurrent, snapshot.PreSkinnedCurrent.ByteCount) +
                CapacityForPreflight(preSkinnedPrevious, snapshot.PreSkinnedPrevious.ByteCount) +
                CapacityForPreflight(meshletDescriptors, snapshot.MeshletDescriptors.ByteCount) +
                CapacityForPreflight(meshletVertexIndices, snapshot.MeshletVertexIndices.ByteCount) +
                CapacityForPreflight(meshletTriangleWords, snapshot.MeshletTriangleWords.ByteCount));
            return totalRequestedBytes <= MaximumRetainedBytes &&
                totalCapacityBytes <= MaximumRetainedBytes;
        }
        catch (OverflowException)
        {
            totalRequestedBytes = ulong.MaxValue;
            totalCapacityBytes = ulong.MaxValue;
            return false;
        }
    }

    private static ulong CapacityForPreflight(ulong requestedBytes, uint publishedBytes)
        => publishedBytes == 0u
            ? MinimumAllocationBytes
            : CalculateCapacity(requestedBytes);

    private void RollbackNewPinsNoLock(
        int frameSlot,
        ReadOnlySpan<VulkanFrameDataSlice> slices,
        byte newlyPinnedStreams)
    {
        ulong pin = 1UL << frameSlot;
        for (int sliceIndex = 0; sliceIndex < slices.Length; ++sliceIndex)
        {
            if ((newlyPinnedStreams & (1 << sliceIndex)) == 0)
                continue;
            VulkanFrameDataSlice slice = slices[sliceIndex];
            for (int entryIndex = 0; entryIndex < _entries.Length; ++entryIndex)
            {
                ref Entry entry = ref _entries[entryIndex];
                if (!entry.IsResident || !entry.Matches(in slice))
                    continue;
                entry.FrameSlotPins &= ~pin;
                break;
            }
        }
        RetireSupersededUnpinnedEntriesNoLock();
        ReapRetiredNoLock();
    }

    private bool TryFindVacantEntryNoLock(out int vacantIndex)
    {
        for (int index = 0; index < _entries.Length; ++index)
        {
            if (!_entries[index].IsOccupied)
            {
                vacantIndex = index;
                return true;
            }
        }

        int eviction = -1;
        ulong oldestUse = ulong.MaxValue;
        for (int index = 0; index < _entries.Length; ++index)
        {
            ref Entry entry = ref _entries[index];
            if (!entry.IsResident || entry.FrameSlotPins != 0u || entry.LastUseSerial >= oldestUse)
                continue;
            eviction = index;
            oldestUse = entry.LastUseSerial;
        }
        if (eviction >= 0)
            RetireNoLock(ref _entries[eviction], "AdvancedGeometry.ResidentCache.Eviction");

        vacantIndex = -1;
        return false;
    }

    private bool CanAllocateNoLock(ulong requestedCapacity)
        => requestedCapacity <= MaximumRetainedBytes &&
           _residentBytes <= MaximumRetainedBytes - requestedCapacity;

    private bool HasPendingRetirementNoLock()
    {
        for (int index = 0; index < _entries.Length; ++index)
            if (_entries[index].Retiring)
                return true;
        return false;
    }

    private static ulong CalculateCapacity(ulong requiredBytes)
    {
        ulong withSpare = Math.Min(
            checked(requiredBytes + requiredBytes / 8UL),
            MaximumRequestedImageBytes);
        ulong rounded = AlignUp(Math.Max(withSpare, MinimumAllocationBytes), AllocationAlignment);
        return Math.Min(rounded, MaximumRequestedImageBytes);
    }

    /// <summary>
    /// Superseded append-only banks are retained only until their frame-slot
    /// owners complete. Once unpinned, the larger current image subsumes their
    /// immutable prefix and owns future publications for that arena key.
    /// </summary>
    private void RetireSupersededUnpinnedEntriesNoLock()
    {
        for (int index = 0; index < _entries.Length; ++index)
        {
            ref Entry entry = ref _entries[index];
            if (!entry.IsResident || entry.FrameSlotPins != 0u ||
                !HasLargerResidentForSameKeyNoLock(index))
                continue;
            RetireNoLock(ref entry, "AdvancedGeometry.ResidentCache.Superseded");
        }
    }

    private bool HasLargerResidentForSameKeyNoLock(int entryIndex)
    {
        ref Entry entry = ref _entries[entryIndex];
        for (int candidateIndex = 0; candidateIndex < _entries.Length; ++candidateIndex)
        {
            if (candidateIndex == entryIndex)
                continue;
            ref Entry candidate = ref _entries[candidateIndex];
            if (!candidate.IsResident || candidate.DatabaseEpoch != entry.DatabaseEpoch ||
                candidate.BufferHandle != entry.BufferHandle)
                continue;
            if (candidate.PublishedBytes >= entry.PublishedBytes &&
                candidate.Capacity > entry.Capacity)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Starts normal lifetime-tracked retirement for the least-recently used
    /// unpinned entries. Their bytes remain charged until reaped, so callers
    /// retry after the owning submissions complete rather than oversubscribing
    /// the cache during a frame burst.
    /// </summary>
    private bool RetireUnpinnedForPressureNoLock(ulong requiredCapacity)
    {
        bool retiredAny = false;
        ulong projectedResidentBytes = _residentBytes;
        while (projectedResidentBytes > MaximumRetainedBytes - requiredCapacity)
        {
            int eviction = -1;
            ulong oldestUse = ulong.MaxValue;
            for (int index = 0; index < _entries.Length; ++index)
            {
                ref Entry entry = ref _entries[index];
                if (!entry.IsResident || entry.FrameSlotPins != 0u || entry.LastUseSerial >= oldestUse)
                    continue;
                eviction = index;
                oldestUse = entry.LastUseSerial;
            }
            if (eviction < 0)
                return retiredAny;
            ulong retiredCapacity = _entries[eviction].Capacity;
            RetireNoLock(ref _entries[eviction], "AdvancedGeometry.ResidentCache.BudgetPressure");
            retiredAny = true;
            projectedResidentBytes -= retiredCapacity;
        }
        return retiredAny;
    }

    private static bool TryValidateStorageRange(
        VulkanBackendObjectContext context,
        ulong requestedBytes,
        out string reason)
    {
        VulkanPhysicalDeviceCapabilitySnapshot? capabilities =
            context.DeviceContext.PhysicalDeviceCapabilities;
        ulong reportedStorageRange = capabilities is null
            ? 0UL
            : capabilities.Properties.Limits.MaxStorageBufferRange;
        ulong maximumStorageRange = reportedStorageRange == 0UL
            ? ulong.MaxValue
            : reportedStorageRange;
        if (requestedBytes <= maximumStorageRange)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"The advanced geometry descriptor range requests {requestedBytes} bytes, exceeding this device's {maximumStorageRange}-byte storage-buffer range.";
        return false;
    }

    private bool TryWriteNoLock(
        ref Entry entry,
        ulong offset,
        ReadOnlySpan<byte> source,
        VulkanBackendObjectContext context,
        out string reason)
    {
        if (source.IsEmpty)
        {
            reason = string.Empty;
            return true;
        }
        if (offset > entry.Capacity || (ulong)source.Length > entry.Capacity - offset ||
            !_resources.Buffers.TryCreateMappedSlice(
                context, entry.Buffer, entry.Memory, offset, checked((ulong)source.Length), out VulkanMappedMemorySlice mapped) ||
            !_resources.Buffers.TryAcquireWrite(context, in mapped, out VulkanMappedMemoryWriteLease lease))
        {
            reason = "The resident advanced geometry buffer could not acquire a writable mapped range.";
            return false;
        }
        using (lease)
            source.CopyTo(lease.Bytes);
        reason = string.Empty;
        return true;
    }

    private bool TryWriteZeroSentinelNoLock(
        ref Entry entry,
        VulkanBackendObjectContext context,
        out string reason)
    {
        if (!_resources.Buffers.TryCreateMappedSlice(
                context, entry.Buffer, entry.Memory, 0u, MinimumAllocationBytes, out VulkanMappedMemorySlice mapped) ||
            !_resources.Buffers.TryAcquireWrite(context, in mapped, out VulkanMappedMemoryWriteLease lease))
        {
            reason = "The resident advanced geometry zero sentinel could not acquire a writable mapped range.";
            return false;
        }
        using (lease)
            lease.Bytes.Clear();
        reason = string.Empty;
        return true;
    }

    private void RetireNoLock(ref Entry entry, string owner)
    {
        if (!entry.IsResident)
            return;
        entry.FrameSlotPins = 0u;
        _resources.Buffers.Retire(entry.Buffer, entry.Memory, owner);
        entry.Retiring = true;
    }

    private void ReapRetiredNoLock()
    {
        for (int index = 0; index < _entries.Length; ++index)
        {
            ref Entry entry = ref _entries[index];
            if (!entry.Retiring || _resources.Buffers.TryGetAllocation(entry.Buffer, out _))
                continue;
            _residentBytes -= entry.Capacity;
            --_allocationCount;
            entry = default;
        }
    }

    private ulong NextIdentityNoLock()
    {
        _nextIdentity = _nextIdentity == ulong.MaxValue
            ? 1u
            : _nextIdentity + 1u;
        return CacheIdentityMask | _nextIdentity;
    }

    private ulong NextUseSerialNoLock()
    {
        _useSerial = _useSerial == ulong.MaxValue ? 1u : _useSerial + 1u;
        return _useSerial;
    }

    private static ulong AlignUp(ulong value, ulong alignment)
        => checked((value + alignment - 1UL) / alignment * alignment);

    private struct Entry
    {
        internal ulong DatabaseEpoch;
        internal AdvancedGpuHandle BufferHandle;
        internal Buffer Buffer;
        internal DeviceMemory Memory;
        internal ulong Capacity;
        internal ulong PublishedBytes;
        internal ulong Identity;
        internal ulong FrameSlotPins;
        internal ulong LastUseSerial;
        internal bool Retiring;

        internal bool IsOccupied => Buffer.Handle != 0 || Memory.Handle != 0;
        internal bool IsResident => IsOccupied && !Retiring;

        internal void Initialize(
            ulong databaseEpoch,
            AdvancedGpuHandle bufferHandle,
            Buffer buffer,
            DeviceMemory memory,
            ulong capacity,
            ulong identity)
        {
            DatabaseEpoch = databaseEpoch;
            BufferHandle = bufferHandle;
            Buffer = buffer;
            Memory = memory;
            Capacity = capacity;
            Identity = identity;
            PublishedBytes = 0u;
            FrameSlotPins = 0u;
            LastUseSerial = 0u;
            Retiring = false;
        }

        internal VulkanFrameDataSlice CreateSlice(uint byteCount)
        {
            uint length = byteCount == 0u ? checked((uint)MinimumAllocationBytes) : byteCount;
            return new VulkanFrameDataSlice(
                Identity,
                Buffer.Handle,
                Memory.Handle,
                EVulkanFrameDataLane.AdvancedSceneStorage,
                0,
                0,
                0u,
                length,
                16u,
                Identity,
                Buffer,
                Memory);
        }

        internal bool Matches(in VulkanFrameDataSlice slice)
            => Identity == slice.ArenaIdentity && Identity == slice.Generation &&
               Buffer.Handle == slice.Buffer.Handle && Memory.Handle == slice.Memory.Handle;
    }
}
