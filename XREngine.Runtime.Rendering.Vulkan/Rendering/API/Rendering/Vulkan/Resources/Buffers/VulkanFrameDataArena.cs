using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Canonical frame-slot-owned mapped transfer/storage arena. Chunk groups grow by appending
/// equally shaped buffers for every frame slot, so an issued buffer/offset is never relocated.
/// </summary>
internal unsafe sealed class VulkanFrameDataArena
{
    private const int LaneCount = (int)EVulkanFrameDataLane.Count;
    private const int MaxFrameSlots = 32;
    private const int MaxChunkGroupsPerLane = 16;
    private const ulong MaxMappedBytes = 1024UL * 1024UL * 1024UL;
    private static long s_nextIdentity;

    private readonly VulkanMappedFrameArenaBackend _backend;
    private readonly ulong _initialChunkCapacity;
    private readonly VulkanFrameDataChunk?[][][] _chunks = new VulkanFrameDataChunk?[LaneCount][][];
    private readonly ulong[][][] _nextOffsets = new ulong[LaneCount][][];
    private readonly int[] _chunkGroupCounts = new int[LaneCount];
    private readonly ulong[] _nextChunkCapacities = new ulong[LaneCount];
    private readonly BufferUsageFlags[] _usages = new BufferUsageFlags[LaneCount];
    private readonly string[] _labels = new string[LaneCount];
    private int _frameSlotCount;
    private int _active;
    private int _hostAccess;
    private int _hostThreadId;
    private ulong _generation;
    private long _allocatedBytes;
    private long _allocationCount;
    private long _allocatedBytesHighWater;
    private long _allocationHighWater;
    private long _flushExpansionBytes;

    internal VulkanFrameDataArena(VulkanMappedFrameArenaBackend backend, ulong initialChunkCapacity = 65_536UL)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _initialChunkCapacity = Math.Max(initialChunkCapacity, 1UL);
        Identity = unchecked((ulong)Interlocked.Increment(ref s_nextIdentity));
        if (Identity == 0)
            Identity = unchecked((ulong)Interlocked.Increment(ref s_nextIdentity));

        for (int index = 0; index < LaneCount; index++)
        {
            EVulkanFrameDataLane lane = (EVulkanFrameDataLane)index;
            _chunks[index] = new VulkanFrameDataChunk?[MaxChunkGroupsPerLane][];
            _nextOffsets[index] = new ulong[MaxChunkGroupsPerLane][];
            for (int group = 0; group < MaxChunkGroupsPerLane; group++)
            {
                _chunks[index][group] = new VulkanFrameDataChunk?[MaxFrameSlots];
                _nextOffsets[index][group] = new ulong[MaxFrameSlots];
            }
            _usages[index] = GetUsage(lane);
            _labels[index] = $"FrameDataArena.{lane}";
            _nextChunkCapacities[index] = _initialChunkCapacity;
        }
    }

    internal ulong Identity { get; }
    internal ulong Generation => IsActive ? Volatile.Read(ref _generation) : 0UL;
    internal bool IsActive => Volatile.Read(ref _active) != 0;
    internal int FrameSlotCount => Volatile.Read(ref _frameSlotCount);
    internal long AllocatedBytes => Volatile.Read(ref _allocatedBytes);
    internal long AllocationCount => Volatile.Read(ref _allocationCount);
    internal long AllocatedBytesHighWater => Volatile.Read(ref _allocatedBytesHighWater);
    internal long AllocationHighWater => Volatile.Read(ref _allocationHighWater);
    internal long FlushExpansionBytes => Volatile.Read(ref _flushExpansionBytes);
    internal int DirtyRangeCount => CountDirtyRanges();
    internal long DirtyBytes => CountDirtyBytes();

    internal void Initialize(int frameSlotCount)
    {
        if (IsActive || frameSlotCount is <= 0 or > MaxFrameSlots)
            throw new InvalidOperationException($"Vulkan frame-data arena requires between 1 and {MaxFrameSlots} frame slots before activation.");

        _frameSlotCount = frameSlotCount;
        _generation = _generation == (ulong.MaxValue >> 2) ? 1UL : _generation + 1UL;
        for (int lane = 0; lane < LaneCount; lane++)
            InitializeLaneGeneration(lane);
        Volatile.Write(ref _active, 1);
    }

    internal bool TryAllocate(int frameSlot, EVulkanFrameDataLane lane, uint length, uint alignment, out VulkanFrameDataSlice slice)
        => TryAllocate(frameSlot, lane, (ulong)length, alignment, out slice);

    internal bool TryAllocate(int frameSlot, EVulkanFrameDataLane lane, ulong length, uint alignment, out VulkanFrameDataSlice slice)
    {
        slice = default;
        if (!IsActive || !_backend.IsOperational || !IsValidFrameSlot(frameSlot) || !IsValidLane(lane) ||
            length is 0 or > int.MaxValue || alignment == 0 || Volatile.Read(ref _hostAccess) != 0)
            return false;

        int laneIndex = (int)lane;
        ulong[][] offsets = _nextOffsets[laneIndex];
        VulkanFrameDataChunk?[][] groups = _chunks[laneIndex];
        for (int groupIndex = 0; groupIndex < _chunkGroupCounts[laneIndex]; groupIndex++)
        {
            VulkanFrameDataChunk?[] group = groups[groupIndex];
            if (group is null)
                continue;
            ulong offset = AlignUp(offsets[groupIndex][frameSlot], alignment);
            if (offset > group[frameSlot]!.Capacity || length > group[frameSlot]!.Capacity - offset)
                continue;
            offsets[groupIndex][frameSlot] = offset + length;
            return TryMakeSlice(frameSlot, lane, groupIndex, offset, (uint)length, alignment, out slice);
        }

        if (!TryAppendChunkGroup(laneIndex, length, alignment, out int appendedIndex))
            return false;
        offsets = _nextOffsets[laneIndex];
        offsets[appendedIndex][frameSlot] = length;
        return TryMakeSlice(frameSlot, lane, appendedIndex, 0, (uint)length, alignment, out slice);
    }

    internal bool TryAllocateWrite(int frameSlot, EVulkanFrameDataLane lane, ReadOnlySpan<byte> source, uint alignment, out VulkanFrameDataSlice slice)
    {
        if (!TryAllocate(frameSlot, lane, (uint)source.Length, alignment, out slice) || !TryBeginWrite(slice, out VulkanFrameDataWriteScope scope))
            return false;
        using (scope)
            source.CopyTo(scope.Bytes);
        return true;
    }

    internal bool TryAllocateWrite(int frameSlot, EVulkanFrameDataLane lane, void* source, ulong sourceOffset, uint length, uint alignment, out VulkanFrameDataSlice slice)
    {
        slice = default;
        if (source is null || length == 0 || sourceOffset > (ulong)nint.MaxValue ||
            !TryAllocate(frameSlot, lane, length, alignment, out slice) || !TryBeginWrite(slice, out VulkanFrameDataWriteScope scope))
            return false;
        using (scope)
            new ReadOnlySpan<byte>((byte*)source + (nint)sourceOffset, checked((int)length)).CopyTo(scope.Bytes);
        return true;
    }

    internal bool TryBeginWrite(in VulkanFrameDataSlice slice, out VulkanFrameDataWriteScope scope)
    {
        scope = default;
        if (!TryEnterHostAccess())
            return false;
        if (!TryValidateSlice(slice, VulkanFrameDataArenaSlotState.Writable, out VulkanFrameDataChunk chunk))
        {
            ExitHostAccess();
            return false;
        }
        scope = new VulkanFrameDataWriteScope(this, slice, new Span<byte>((byte*)chunk.MappedPointer + checked((nint)slice.Offset), checked((int)slice.Length)));
        return true;
    }

    internal bool TryBeginRead(in VulkanFrameDataSlice slice, out VulkanFrameDataReadScope scope)
    {
        scope = default;
        if (!TryEnterHostAccess())
            return false;
        if (!TryValidateSlice(slice, VulkanFrameDataArenaSlotState.Writable, out VulkanFrameDataChunk chunk))
        {
            ExitHostAccess();
            return false;
        }
        try
        {
            ExpandVisibilityRange(slice.Offset, slice.Length, chunk.AllocationLength, out ulong offset, out ulong length);
            if (!chunk.IsHostCoherent)
                _backend.Invalidate(chunk.Memory, offset, length);
            scope = new VulkanFrameDataReadScope(this, new Span<byte>((byte*)chunk.MappedPointer + checked((nint)slice.Offset), checked((int)slice.Length)));
            return true;
        }
        catch
        {
            ExitHostAccess();
            throw;
        }
    }

    /// <summary>
    /// Publishes host writes for a writable slice before an immediate synchronous
    /// transfer. The frame slot remains writable because the synchronous command
    /// boundary proves that the source range is no longer device-owned on return.
    /// </summary>
    internal bool TryFlushHostWrites(in VulkanFrameDataSlice slice)
    {
        if (Volatile.Read(ref _hostAccess) != 0 ||
            !TryValidateSlice(slice, VulkanFrameDataArenaSlotState.Writable, out VulkanFrameDataChunk chunk))
        {
            return false;
        }

        FlushDirtyRanges(chunk);
        return true;
    }

    internal bool TryPrepareFrameSlotForSubmission(uint frameSlot, ulong generation)
    {
        if (!_backend.IsOperational || generation == 0 || generation != Generation || !IsValidFrameSlot((int)frameSlot) || Volatile.Read(ref _hostAccess) != 0)
            return false;
        bool prepared = false;
        try
        {
            for (int lane = 0; lane < LaneCount; lane++)
                if (_chunks[lane] is { } groups)
                    for (int group = 0; group < groups.Length; group++)
                        if (groups[group][frameSlot] is { } chunk)
                        {
                            if (!chunk.TryTransition(generation, VulkanFrameDataArenaSlotState.Writable, VulkanFrameDataArenaSlotState.Prepared))
                            {
                                if (prepared)
                                    ReopenPreparedChunks((int)frameSlot, generation);
                                return false;
                            }
                            prepared = true;
                            FlushDirtyRanges(chunk);
                        }
            return true;
        }
        catch
        {
            if (prepared)
                ReopenPreparedChunks((int)frameSlot, generation);
            throw;
        }
    }

    internal bool TryCancelFrameSlotSubmission(uint frameSlot, ulong generation)
    {
        if (generation == 0 || generation != Generation || !IsValidFrameSlot((int)frameSlot))
            return false;
        ReopenPreparedChunks((int)frameSlot, generation);
        return true;
    }

    internal void MarkFrameSlotSubmitted(uint frameSlot, ulong generation)
    {
        if (generation == 0 || generation != Generation || !IsValidFrameSlot((int)frameSlot))
            return;
        for (int lane = 0; lane < LaneCount; lane++)
            if (_chunks[lane] is { } groups)
                for (int group = 0; group < groups.Length; group++)
                    if (groups[group][frameSlot] is { } chunk)
                        _ = chunk.PublishSubmitted(generation);
    }

    internal bool TryResetFrameSlot(uint frameSlot, ulong generation, bool submissionCompletionProven)
    {
        if (!_backend.IsOperational || generation == 0 || generation != Generation || !IsValidFrameSlot((int)frameSlot))
            return false;

        for (int lane = 0; lane < LaneCount; lane++)
            if (_chunks[lane] is { } groups)
                for (int group = 0; group < groups.Length; group++)
                    if (groups[group][frameSlot] is { } chunk)
                    {
                        VulkanFrameDataArenaSlotState state = chunk.GetState(generation);
                        if (state is VulkanFrameDataArenaSlotState.Invalid or VulkanFrameDataArenaSlotState.Prepared ||
                            state == VulkanFrameDataArenaSlotState.Submitted && !submissionCompletionProven)
                        {
                            return false;
                        }
                    }

        for (int lane = 0; lane < LaneCount; lane++)
            if (_chunks[lane] is { } groups)
                for (int group = 0; group < groups.Length; group++)
                    if (groups[group][frameSlot] is { } chunk)
                    {
                        if (chunk.GetState(generation) == VulkanFrameDataArenaSlotState.Submitted &&
                            !chunk.TryTransition(generation, VulkanFrameDataArenaSlotState.Submitted, VulkanFrameDataArenaSlotState.Writable))
                            return false;
                        chunk.DirtyRanges.Clear();
                        _nextOffsets[lane][group][frameSlot] = 0;
                    }
        return true;
    }

    internal void Destroy()
    {
        Volatile.Write(ref _active, 0);
        bool nativeDestroyAllowed = _backend.TryEnterIdleTeardown();
        for (int lane = 0; lane < LaneCount; lane++)
            if (_chunks[lane] is { } groups)
                foreach (VulkanFrameDataChunk?[] group in groups)
                    if (group is not null)
                        foreach (VulkanFrameDataChunk? chunk in group)
                            chunk?.Destroy(_backend, nativeDestroyAllowed);
    }

    internal void EndWrite(in VulkanFrameDataSlice slice)
    {
        if (TryValidateSlice(slice, VulkanFrameDataArenaSlotState.Writable, out VulkanFrameDataChunk chunk))
            chunk.DirtyRanges.Include(slice.Offset, slice.Length);
        ExitHostAccess();
    }

    internal void EndRead() => ExitHostAccess();

    private bool TryAppendChunkGroup(int laneIndex, ulong length, uint alignment, out int groupIndex)
    {
        groupIndex = -1;
        VulkanFrameDataChunk?[][] existingGroups = _chunks[laneIndex];
        int existingGroupCount = _chunkGroupCounts[laneIndex];
        if (existingGroupCount >= MaxChunkGroupsPerLane)
            return false;

        ulong capacity = _nextChunkCapacities[laneIndex];
        ulong required = AlignUp(length, alignment);
        while (capacity < required)
        {
            if (capacity > ulong.MaxValue / 2UL)
                return false;
            capacity *= 2UL;
        }

        if (capacity > (ulong)long.MaxValue / (ulong)_frameSlotCount)
            return false;
        ulong requestedMappedBytes = capacity * (ulong)_frameSlotCount;
        ulong currentMappedBytes = checked((ulong)Math.Max(AllocatedBytes, 0L));
        if (requestedMappedBytes > MaxMappedBytes || currentMappedBytes > MaxMappedBytes - requestedMappedBytes)
            return false;

        VulkanFrameDataChunk?[] group = existingGroups[existingGroupCount];
        ulong actualMappedBytes = 0;
        try
        {
            for (int slot = 0; slot < _frameSlotCount; slot++)
            {
                VulkanFrameDataChunk chunk = CreateChunk(
                    capacity,
                    _usages[laneIndex],
                    _labels[laneIndex]);
                if (chunk.AllocationLength > MaxMappedBytes ||
                    actualMappedBytes > MaxMappedBytes - chunk.AllocationLength ||
                    currentMappedBytes > MaxMappedBytes - actualMappedBytes - chunk.AllocationLength)
                {
                    chunk.Destroy(_backend, nativeDestroyAllowed: true);
                    for (int cleanupSlot = 0; cleanupSlot < slot; cleanupSlot++)
                    {
                        group[cleanupSlot]?.Destroy(_backend, nativeDestroyAllowed: true);
                        group[cleanupSlot] = null;
                    }
                    return false;
                }

                group[slot] = chunk;
                actualMappedBytes += chunk.AllocationLength;
            }
        }
        catch
        {
            for (int slot = 0; slot < _frameSlotCount; slot++)
            {
                group[slot]?.Destroy(_backend, nativeDestroyAllowed: true);
                group[slot] = null;
            }
            throw;
        }
        for (int slot = 0; slot < _frameSlotCount; slot++)
            group[slot]!.InitializeGeneration(Generation);
        _chunkGroupCounts[laneIndex] = existingGroupCount + 1;
        _nextChunkCapacities[laneIndex] = capacity > ulong.MaxValue / 2UL ? ulong.MaxValue : capacity * 2UL;
        groupIndex = existingGroupCount;
        Interlocked.Add(ref _allocatedBytes, checked((long)actualMappedBytes));
        Interlocked.Increment(ref _allocationCount);
        UpdateHighWater(ref _allocatedBytesHighWater, AllocatedBytes);
        UpdateHighWater(ref _allocationHighWater, AllocationCount);
        return true;
    }

    private VulkanFrameDataChunk CreateChunk(ulong capacity, BufferUsageFlags usage, string label)
    {
        if (!_backend.TryCreateChunk(capacity, usage, label, out Buffer buffer, out DeviceMemory memory, out void* pointer, out bool coherent, out ulong allocationLength) || allocationLength < capacity)
            throw new InvalidOperationException($"Failed to create mapped frame-data chunk '{label}'.");
        return new VulkanFrameDataChunk(buffer, memory, pointer, capacity, allocationLength, coherent);
    }

    private bool TryMakeSlice(int frameSlot, EVulkanFrameDataLane lane, int groupIndex, ulong offset, uint length, uint alignment, out VulkanFrameDataSlice slice)
    {
        slice = default;
        VulkanFrameDataChunk? chunk = _chunks[(int)lane]?[groupIndex]?[frameSlot];
        if (chunk is null || offset > chunk.Capacity || length > chunk.Capacity - offset)
            return false;
        slice = new VulkanFrameDataSlice(Identity, chunk.Buffer.Handle, chunk.Memory.Handle, lane, groupIndex, frameSlot, offset, length, alignment, Generation, chunk.Buffer, chunk.Memory);
        return true;
    }

    private bool TryValidateSlice(in VulkanFrameDataSlice slice, VulkanFrameDataArenaSlotState requiredState, out VulkanFrameDataChunk chunk)
    {
        chunk = null!;
        if (!_backend.IsOperational || !slice.IsValid || slice.ArenaIdentity != Identity || slice.Generation != Generation || !IsValidLane(slice.Lane) || !IsValidFrameSlot(slice.FrameSlot) || slice.ChunkIndex < 0 ||
            _chunks[(int)slice.Lane] is not { } groups || (uint)slice.ChunkIndex >= (uint)groups.Length || groups[slice.ChunkIndex] is not { } group || group[slice.FrameSlot] is not { } resolved ||
            resolved.Buffer.Handle != slice.BufferIdentity || resolved.Memory.Handle != slice.MemoryIdentity || slice.Offset % slice.Alignment != 0 || slice.Offset > resolved.Capacity || slice.Length > resolved.Capacity - slice.Offset || resolved.GetState(slice.Generation) != requiredState)
            return false;
        chunk = resolved;
        return true;
    }

    private void InitializeLaneGeneration(int lane)
    {
        if (_chunks[lane] is not { } groups)
            return;
        foreach (VulkanFrameDataChunk?[] group in groups)
            if (group is not null)
                foreach (VulkanFrameDataChunk? chunk in group)
                    chunk?.InitializeGeneration(_generation);
    }

    private void ReopenPreparedChunks(int frameSlot, ulong generation)
    {
        for (int lane = 0; lane < LaneCount; lane++)
            if (_chunks[lane] is { } groups)
                for (int group = 0; group < groups.Length; group++)
                    if (groups[group][frameSlot] is { } chunk)
                        _ = chunk.TryTransition(generation, VulkanFrameDataArenaSlotState.Prepared, VulkanFrameDataArenaSlotState.Writable);
    }

    private void FlushDirtyRanges(VulkanFrameDataChunk chunk)
    {
        for (int index = 0; index < chunk.DirtyRanges.Count; index++)
        {
            VulkanDynamicDataDirtyRange dirty = chunk.DirtyRanges.Get(index);
            ExpandVisibilityRange(dirty.Offset, dirty.Length, chunk.AllocationLength, out ulong expandedOffset, out ulong expandedLength);
            if (!chunk.IsHostCoherent)
                _backend.Flush(chunk.Memory, expandedOffset, expandedLength);
        }
        chunk.DirtyRanges.Clear();
    }

    private void ExpandVisibilityRange(ulong offset, ulong length, ulong allocationLength, out ulong expandedOffset, out ulong expandedLength)
    {
        ulong atom = _backend.NonCoherentAtomSize;
        expandedOffset = offset / atom * atom;
        ulong end = AlignUp(checked(offset + length), atom);
        end = Math.Min(end, allocationLength);
        expandedLength = end - expandedOffset;
        Interlocked.Add(ref _flushExpansionBytes, checked((long)(expandedLength - length)));
    }

    private bool TryEnterHostAccess()
    {
        if (Interlocked.CompareExchange(ref _hostAccess, 1, 0) != 0)
            return false;
        Volatile.Write(ref _hostThreadId, Environment.CurrentManagedThreadId);
        return true;
    }

    private void ExitHostAccess()
    {
        Volatile.Write(ref _hostThreadId, 0);
        Volatile.Write(ref _hostAccess, 0);
    }

    private bool IsValidFrameSlot(int frameSlot) => (uint)frameSlot < (uint)_frameSlotCount;
    private static bool IsValidLane(EVulkanFrameDataLane lane) => (uint)lane < LaneCount;
    private static ulong AlignUp(ulong value, ulong alignment) => alignment <= 1 ? value : checked(((value + alignment - 1UL) / alignment) * alignment);
    private static void UpdateHighWater(ref long highWater, long value)
    {
        long observed;
        while ((observed = Volatile.Read(ref highWater)) < value)
            if (Interlocked.CompareExchange(ref highWater, value, observed) == observed)
                return;
    }

    private int CountDirtyRanges()
    {
        int count = 0;
        for (int lane = 0; lane < LaneCount; lane++)
            if (_chunks[lane] is { } groups)
                for (int group = 0; group < groups.Length; group++)
                    for (int slot = 0; slot < _frameSlotCount; slot++)
                        if (groups[group][slot] is { } chunk)
                            count += chunk.DirtyRanges.Count;
        return count;
    }

    private long CountDirtyBytes()
    {
        ulong total = 0;
        for (int lane = 0; lane < LaneCount; lane++)
            if (_chunks[lane] is { } groups)
                for (int group = 0; group < groups.Length; group++)
                    for (int slot = 0; slot < _frameSlotCount; slot++)
                        if (groups[group][slot] is { } chunk)
                            total += chunk.DirtyRanges.TotalLength;
        return checked((long)Math.Min(total, (ulong)long.MaxValue));
    }

    private static BufferUsageFlags GetUsage(EVulkanFrameDataLane lane) => lane switch
    {
        EVulkanFrameDataLane.TransferUpload => BufferUsageFlags.TransferSrcBit,
        EVulkanFrameDataLane.TransferStaging => BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
        EVulkanFrameDataLane.Readback => BufferUsageFlags.TransferDstBit,
        EVulkanFrameDataLane.Uniform => BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
        EVulkanFrameDataLane.Storage => BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
        EVulkanFrameDataLane.Indirect => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unsupported Vulkan frame-data lane."),
    };
}
