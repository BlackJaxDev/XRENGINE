using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns frame-indexed, persistently mapped uniform-buffer chunks. Reservations are stable for
/// an arena generation; slices carry all identity needed to reject stale, foreign, misaligned,
/// or submitted-slot access before native memory is touched.
/// </summary>
internal unsafe sealed class VulkanMappedFrameArena
{
    private const int MaxFrameSlots = 8;
    private const int MaxReservations = 131_072;
    private static long s_nextArenaIdentity;

    private readonly VulkanMappedFrameArenaBackend _backend;
    private readonly object _reservationLock = new();
    private readonly Dictionary<VulkanMappedFrameReservationKey, VulkanMappedFrameReservation> _reservations = [];
    private readonly Dictionary<VulkanFrequencyAutoUniformReservationKey, VulkanFrequencyAutoUniformReservation> _frequencyReservations = [];
    private Chunk?[] _chunks = [];
    private ulong _reservedBytes;
    private ulong _generation;
    private ulong _capacity;
    private int _active;
    private int _writerThreadId;
    private int _writerDepth;
    private long _reservationHighWater;
    private long _mappedBytesHighWater;
    private long _flushExpansionBytes;

    internal VulkanMappedFrameArena(
        VulkanMappedFrameArenaBackend backend,
        ulong initialCapacity,
        uint dynamicOffsetAlignment)
    {
        _backend = backend;
        _capacity = Math.Max(initialCapacity, 1UL);
        DynamicOffsetAlignment = Math.Max(dynamicOffsetAlignment, 1U);
        Identity = unchecked((ulong)Interlocked.Increment(ref s_nextArenaIdentity));
        if (Identity == 0)
            Identity = unchecked((ulong)Interlocked.Increment(ref s_nextArenaIdentity));
    }

    internal ulong Identity { get; }
    internal uint DynamicOffsetAlignment { get; }
    internal bool IsActive => Volatile.Read(ref _active) != 0;
    internal int FrameSlotCount => _chunks.Length;
    internal ulong Capacity => _capacity;
    internal ulong ReservedBytes => Volatile.Read(ref _reservedBytes);
    internal ulong Generation => IsActive ? Volatile.Read(ref _generation) : 0UL;
    internal int ReservationCount
    {
        get
        {
            lock (_reservationLock)
                return _reservations.Count + _frequencyReservations.Count;
        }
    }
    internal long ReservationHighWater => Volatile.Read(ref _reservationHighWater);
    internal long MappedBytesHighWater => Volatile.Read(ref _mappedBytesHighWater);
    internal long FlushExpansionBytes => Volatile.Read(ref _flushExpansionBytes);

    internal void Initialize(int frameSlotCount)
    {
        if (IsActive)
            throw new InvalidOperationException("A mapped frame arena cannot be initialized while its generation is active.");

        EnsureFrameSlotCount(frameSlotCount);
        IncrementGeneration();
        for (int index = 0; index < _chunks.Length; index++)
            _chunks[index]?.InitializeGeneration(_generation);
        Volatile.Write(ref _active, 1);
        PublishTelemetry();
    }

    /// <summary>
    /// Adds frame slots without relocating existing chunks. Growth is allowed only before a
    /// generation becomes active, or by appending new slots to the active generation.
    /// </summary>
    internal void EnsureFrameSlotCount(int requiredFrameSlots)
    {
        if (requiredFrameSlots <= _chunks.Length)
            return;
        if ((uint)requiredFrameSlots > MaxFrameSlots)
            throw new InvalidOperationException($"Vulkan frame-data arena requested {requiredFrameSlots} frame slots; the explicit limit is {MaxFrameSlots}.");

        int oldLength = _chunks.Length;
        Array.Resize(ref _chunks, requiredFrameSlots);
        try
        {
            for (int index = oldLength; index < requiredFrameSlots; index++)
            {
                Chunk chunk = CreateChunk(_capacity);
                if (IsActive)
                    chunk.InitializeGeneration(_generation);
                _chunks[index] = chunk;
            }
        }
        catch
        {
            for (int index = oldLength; index < _chunks.Length; index++)
            {
                _chunks[index]?.Destroy(_backend, nativeDestroyAllowed: true);
                _chunks[index] = null;
            }
            Array.Resize(ref _chunks, oldLength);
            throw;
        }

        PublishTelemetry();
    }

    /// <summary>
    /// Replaces storage only while no arena generation is active. Existing slices therefore
    /// never silently retarget a different buffer/memory allocation.
    /// </summary>
    internal bool TryGrowCapacityOutsideActiveGeneration(ulong requiredCapacity)
    {
        if (requiredCapacity <= _capacity)
            return true;
        if (IsActive || Volatile.Read(ref _writerDepth) != 0)
            return false;

        ulong newCapacity = _capacity;
        while (newCapacity < requiredCapacity)
        {
            if (newCapacity > ulong.MaxValue / 2UL)
                return false;
            newCapacity *= 2UL;
        }

        Chunk?[] oldChunks = _chunks;
        _chunks = [];
        _capacity = newCapacity;
        try
        {
            EnsureFrameSlotCount(oldChunks.Length);
        }
        catch
        {
            foreach (Chunk? chunk in _chunks)
                chunk?.Destroy(_backend, nativeDestroyAllowed: true);
            _chunks = oldChunks;
            _capacity = oldChunks.Length > 0 ? oldChunks[0]!.Capacity : _capacity;
            throw;
        }

        bool nativeDestroyAllowed = _backend.TryEnterIdleTeardown();
        foreach (Chunk? chunk in oldChunks)
            chunk?.Destroy(_backend, nativeDestroyAllowed);
        return true;
    }

    internal bool TryReserve(
        VkMeshRenderer owner,
        string name,
        bool isAutoUniform,
        int drawSlot,
        uint size,
        out VulkanMappedFrameReservation reservation)
    {
        reservation = default;
        if (!IsActive || !_backend.IsOperational || owner is null || string.IsNullOrEmpty(name) || size == 0 || drawSlot < 0)
            return false;

        VulkanMappedFrameReservationKey key = new(owner, name, isAutoUniform, drawSlot);
        lock (_reservationLock)
        {
            if (_reservations.TryGetValue(key, out VulkanMappedFrameReservation existing))
            {
                if (existing.Length < size)
                    return false;
                reservation = existing;
                return true;
            }

            if (_reservations.Count + _frequencyReservations.Count >= MaxReservations ||
                !TryAllocateReservation_NoLock(size, out reservation))
            {
                return false;
            }

            _reservations.Add(key, reservation);
            UpdateHighWater(ref _reservationHighWater, _reservations.Count + _frequencyReservations.Count);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDynamicUniformAllocation(size);
        PublishTelemetry();
        return true;
    }

    internal bool TryGetOrReserveFrequencyAutoUniformRange(
        VkRenderProgram program,
        AutoUniformBlockInfo block,
        ulong ownerIdentity,
        out VulkanFrequencyAutoUniformReservation reservation)
    {
        reservation = null!;
        if (!IsActive || !_backend.IsOperational || program is null || block.Frequency == EVulkanBindingFrequency.Unknown || ownerIdentity == 0 || block.Size == 0)
            return false;

        ulong layoutSignature = ResolvePublicationLayoutSignature(program, block);
        VulkanFrequencyAutoUniformReservationKey key = new(layoutSignature, block.Frequency, ownerIdentity);
        lock (_reservationLock)
        {
            if (_frequencyReservations.TryGetValue(key, out VulkanFrequencyAutoUniformReservation? existing))
            {
                if (existing.Size < block.Size)
                    return false;
                if (existing.PublicationStates.Length == _chunks.Length)
                {
                    reservation = existing;
                    return true;
                }

                reservation = new VulkanFrequencyAutoUniformReservation(
                    existing.Key,
                    existing.Offset,
                    existing.Size,
                    existing.RecordingVisibleGeneration,
                    _chunks.Length);
                _frequencyReservations[key] = reservation;
                return true;
            }

            if (_reservations.Count + _frequencyReservations.Count >= MaxReservations ||
                !TryAllocateReservation_NoLock(block.Size, out VulkanMappedFrameReservation range))
            {
                return false;
            }

            reservation = new VulkanFrequencyAutoUniformReservation(
                key,
                range.Offset,
                range.Length,
                range.Generation,
                _chunks.Length);
            _frequencyReservations.Add(key, reservation);
            UpdateHighWater(ref _reservationHighWater, _reservations.Count + _frequencyReservations.Count);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDynamicUniformAllocation(block.Size);
        PublishTelemetry();
        return true;
    }

    internal bool TryGetSlice(int frameSlot, in VulkanMappedFrameReservation reservation, out VulkanMappedFrameSlice slice)
    {
        slice = default;
        if (!_backend.IsOperational || !reservation.IsValid || reservation.Generation != Generation ||
            (uint)frameSlot >= (uint)_chunks.Length || _chunks[frameSlot] is not { } chunk ||
            reservation.Offset % reservation.Alignment != 0 ||
            reservation.Offset % DynamicOffsetAlignment != 0 ||
            reservation.Offset > chunk.Capacity || reservation.Length > chunk.Capacity - reservation.Offset)
        {
            return false;
        }

        slice = new VulkanMappedFrameSlice(
            Identity,
            chunk.Buffer.Handle,
            chunk.Memory.Handle,
            reservation.Offset,
            reservation.Length,
            reservation.Alignment,
            frameSlot,
            reservation.Generation,
            chunk.Buffer,
            chunk.Memory);
        return true;
    }

    internal bool TryGetSlice(int frameSlot, ulong offset, uint length, out VulkanMappedFrameSlice slice)
    {
        VulkanMappedFrameReservation reservation = new(offset, length, DynamicOffsetAlignment, Generation);
        return TryGetSlice(frameSlot, reservation, out slice);
    }

    internal bool TryBeginWrite(VulkanMappedFrameSlice slice, out VulkanMappedFrameWriteScope scope)
    {
        scope = default;
        int currentThreadId = Environment.CurrentManagedThreadId;
        if (Interlocked.CompareExchange(ref _writerDepth, 1, 0) != 0)
            return false;

        Volatile.Write(ref _writerThreadId, currentThreadId);
        if (!TryValidateWritableSlice(slice, out Chunk? chunk) || chunk is null)
        {
            ReleaseHostAccessGate();
            return false;
        }

        scope = new VulkanMappedFrameWriteScope(
            this,
            slice,
            new Span<byte>((byte*)chunk.MappedPtr + checked((nint)slice.Offset), checked((int)slice.Length)));
        return true;
    }

    internal bool TryWriteIfChanged<T>(in VulkanMappedFrameSlice slice, in T value) where T : unmanaged
    {
        if (slice.Length < sizeof(T) || !TryBeginWrite(slice, out VulkanMappedFrameWriteScope scope))
            return false;

        using (scope)
        {
            ReadOnlySpan<byte> source = new(Unsafe.AsPointer(ref Unsafe.AsRef(in value)), sizeof(T));
            if (source.SequenceEqual(scope.Bytes[..sizeof(T)]))
                return true;
            source.CopyTo(scope.Bytes);
        }

        return true;
    }

    /// <summary>
    /// Reopens a frame slot after its previous device read has completed. A slot that was
    /// submitted cannot be reopened without the caller's fence/timeline completion proof.
    /// </summary>
    internal bool TryResetFrameSlot(
        uint frameSlot,
        ulong generation,
        bool submissionCompletionProven)
    {
        if (!_backend.IsOperational || frameSlot >= _chunks.Length ||
            _chunks[frameSlot] is not { } chunk)
            return false;
        if (generation == 0 || generation != Generation)
            return false;
        VulkanMappedFrameSlotState state = chunk.GetState(generation);
        if (state == VulkanMappedFrameSlotState.Writable)
            return true;
        if (state != VulkanMappedFrameSlotState.Submitted ||
            !submissionCompletionProven)
            return false;

        if (!chunk.TryTransition(
                generation,
                VulkanMappedFrameSlotState.Submitted,
                VulkanMappedFrameSlotState.Writable))
            return false;

        return true;
    }

    /// <summary>
    /// Flushes all writes for a frame slot and seals it against further host access before
    /// native submission. Empty/coherent dirty ranges remain valid successful preparation.
    /// </summary>
    internal bool TryPrepareFrameSlotForSubmission(
        uint frameSlot,
        ulong generation)
    {
        if (!_backend.IsOperational || generation == 0 || generation != Generation ||
            frameSlot >= _chunks.Length ||
            _chunks[frameSlot] is not { } chunk ||
            !chunk.TryTransition(
                generation,
                VulkanMappedFrameSlotState.Writable,
                VulkanMappedFrameSlotState.Prepared))
            return false;

        if (Volatile.Read(ref _writerDepth) != 0)
        {
            _ = chunk.TryTransition(
                generation,
                VulkanMappedFrameSlotState.Prepared,
                VulkanMappedFrameSlotState.Writable);
            return false;
        }

        if (chunk.DirtyRange.IsEmpty)
            return true;

        try
        {
            if (FlushDirtyRange(checked((int)frameSlot), out _, out _))
                return true;
        }
        catch
        {
            _ = chunk.TryTransition(
                generation,
                VulkanMappedFrameSlotState.Prepared,
                VulkanMappedFrameSlotState.Writable);
            throw;
        }

        _ = chunk.TryTransition(
            generation,
            VulkanMappedFrameSlotState.Prepared,
            VulkanMappedFrameSlotState.Writable);
        return false;
    }

    /// <summary>
    /// Reopens a prepared slot when native submission is rejected before queue ownership
    /// transfers. Already-flushed bytes remain visible and later writes establish new dirtiness.
    /// </summary>
    internal bool TryCancelFrameSlotSubmission(uint frameSlot, ulong generation)
    {
        if (generation == 0 || generation != Generation ||
            frameSlot >= _chunks.Length ||
            _chunks[frameSlot] is not { } chunk)
            return false;

        return chunk.TryTransition(
            generation,
            VulkanMappedFrameSlotState.Prepared,
            VulkanMappedFrameSlotState.Writable);
    }

    /// <summary>
    /// Publishes device ownership after native queue acceptance. This operation is deliberately
    /// non-throwing: an invariant violation seals the current generation as submitted so an
    /// accepted GPU read can never be mistaken for rejected work or reopened for host writes.
    /// </summary>
    internal void MarkFrameSlotSubmitted(uint frameSlot, ulong generation)
    {
        if (generation == 0 || generation != Generation ||
            frameSlot >= _chunks.Length ||
            _chunks[frameSlot] is not { } chunk)
        {
            Debug.VulkanWarning(
                $"[Vulkan] Accepted submit could not publish mapped frame slot {frameSlot} for stale generation {generation}.");
            return;
        }

        if (chunk.PublishSubmitted(generation, out VulkanMappedFrameSlotState previousState) &&
            previousState == VulkanMappedFrameSlotState.Prepared)
            return;

        Debug.VulkanWarning(
            $"[Vulkan] Accepted submit sealed mapped frame slot {frameSlot} from unexpected {previousState} state for generation {generation}.");
    }

    internal bool TryGetDirtyRange(int frameSlot, out VulkanDynamicDataDirtyRange dirtyRange)
    {
        if ((uint)frameSlot >= (uint)_chunks.Length || _chunks[frameSlot] is not { } chunk)
        {
            dirtyRange = default;
            return false;
        }

        dirtyRange = chunk.DirtyRange;
        return true;
    }

    internal bool FlushDirtyRange(int frameSlot, out ulong flushOffset, out ulong flushLength)
    {
        flushOffset = 0;
        flushLength = 0;
        if ((uint)frameSlot >= (uint)_chunks.Length || _chunks[frameSlot] is not { } chunk || chunk.DirtyRange.IsEmpty)
            return false;

        ulong offset = chunk.DirtyRange.Offset;
        ulong length = chunk.DirtyRange.Length;
        if (offset > chunk.Capacity || length > chunk.Capacity - offset)
            return false;

        ExpandVisibilityRange(offset, length, chunk.AllocationLength, out flushOffset, out flushLength);
        if (!chunk.IsHostCoherent)
            _backend.Flush(chunk.Memory, flushOffset, flushLength);
        chunk.DirtyRange.Clear();
        return true;
    }

    /// <summary>
    /// Invalidates the atom-expanded device-write range before host reads it. The arena does
    /// not expose a pointer here; callers must still enter a bounded write/read boundary.
    /// </summary>
    internal bool TryGetInvalidateRange(
        in VulkanMappedFrameSlice slice,
        out ulong invalidateOffset,
        out ulong invalidateLength)
    {
        invalidateOffset = 0;
        invalidateLength = 0;
        if (Interlocked.CompareExchange(ref _writerDepth, 1, 0) != 0)
            return false;

        Volatile.Write(
            ref _writerThreadId,
            Environment.CurrentManagedThreadId);
        try
        {
            if (!TryValidateWritableSlice(slice, out Chunk? chunk) || chunk is null)
                return false;

            ExpandVisibilityRange(
                slice.Offset,
                slice.Length,
                chunk.AllocationLength,
                out invalidateOffset,
                out invalidateLength);
            if (!chunk.IsHostCoherent)
                _backend.Invalidate(chunk.Memory, invalidateOffset, invalidateLength);
            return true;
        }
        finally
        {
            ReleaseHostAccessGate();
        }
    }

    internal void ReleaseReservations(VkMeshRenderer owner)
    {
        if (owner is null)
            return;
        lock (_reservationLock)
        {
            List<VulkanMappedFrameReservationKey>? removed = null;
            foreach (VulkanMappedFrameReservationKey key in _reservations.Keys)
            {
                if (!ReferenceEquals(key.Owner, owner))
                    continue;
                (removed ??= []).Add(key);
            }
            if (removed is not null)
                for (int index = 0; index < removed.Count; index++)
                    _reservations.Remove(removed[index]);
        }
        PublishTelemetry();
    }

    internal void Destroy()
    {
        if (Interlocked.CompareExchange(ref _writerDepth, 1, 0) != 0)
            throw new InvalidOperationException("Cannot destroy a mapped frame arena while a host write is active.");

        Volatile.Write(
            ref _writerThreadId,
            Environment.CurrentManagedThreadId);
        Volatile.Write(ref _active, 0);
        try
        {
            bool nativeDestroyAllowed = _backend.TryEnterIdleTeardown();
            foreach (Chunk? chunk in _chunks)
                chunk?.Destroy(_backend, nativeDestroyAllowed);
            _chunks = [];
            lock (_reservationLock)
            {
                _reservations.Clear();
                _frequencyReservations.Clear();
                _reservedBytes = 0;
            }
            PublishTelemetry();
        }
        finally
        {
            ReleaseHostAccessGate();
        }
    }

    internal void EndWrite(in VulkanMappedFrameSlice slice)
    {
        if ((uint)slice.FrameSlot >= (uint)_chunks.Length)
            throw new InvalidOperationException("Mapped-frame write scope ended with an invalid frame slot.");
        if (Volatile.Read(ref _writerDepth) != 1 ||
            Volatile.Read(ref _writerThreadId) != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Mapped-frame arena permits exactly one active host writer.");
        MarkDirty(slice);
        ReleaseHostAccessGate();
    }

    private bool TryAllocateReservation_NoLock(uint size, out VulkanMappedFrameReservation reservation)
    {
        reservation = default;
        ulong aligned = AlignUp(_reservedBytes, DynamicOffsetAlignment);
        if (aligned > _capacity || size > _capacity - aligned)
            return false;

        ulong generation = Generation;
        if (generation == 0)
            return false;
        reservation = new VulkanMappedFrameReservation(aligned, size, DynamicOffsetAlignment, generation);
        _reservedBytes = aligned + size;
        return true;
    }

    private bool TryValidateSlice(in VulkanMappedFrameSlice slice, out Chunk? chunk)
    {
        chunk = null;
        if (!_backend.IsOperational || !slice.IsValid || slice.ArenaIdentity != Identity || slice.Generation != Generation ||
            slice.Alignment < DynamicOffsetAlignment ||
            slice.Offset % slice.Alignment != 0 ||
            slice.Offset % DynamicOffsetAlignment != 0 ||
            (uint)slice.FrameSlot >= (uint)_chunks.Length ||
            _chunks[slice.FrameSlot] is not { } resolvedChunk ||
            slice.BufferIdentity != resolvedChunk.Buffer.Handle ||
            slice.MemoryIdentity != resolvedChunk.Memory.Handle ||
            slice.Offset > resolvedChunk.Capacity ||
            slice.Length > resolvedChunk.Capacity - slice.Offset)
        {
            return false;
        }

        chunk = resolvedChunk;
        return true;
    }

    private bool TryValidateWritableSlice(in VulkanMappedFrameSlice slice, out Chunk? chunk)
    {
        if (!TryValidateSlice(slice, out chunk) || chunk is null)
            return false;

        return chunk.GetState(slice.Generation) == VulkanMappedFrameSlotState.Writable;
    }

    private void ReleaseHostAccessGate()
    {
        Volatile.Write(ref _writerThreadId, 0);
        Volatile.Write(ref _writerDepth, 0);
    }

    private Chunk CreateChunk(ulong capacity)
    {
        if (!_backend.TryCreateChunk(
                capacity,
                out Buffer buffer,
                out DeviceMemory memory,
                out void* mappedPtr,
                out bool isHostCoherent,
                out ulong allocationLength) ||
            buffer.Handle == 0 || memory.Handle == 0 || mappedPtr is null)
        {
            throw new InvalidOperationException("Failed to create a persistently mapped Vulkan frame-arena chunk.");
        }
        if (allocationLength < capacity)
        {
            _backend.DestroyChunk(buffer, memory, mappedPtr, nativeDestroyAllowed: true);
            throw new InvalidOperationException("Mapped frame-arena allocation is smaller than its buffer capacity.");
        }

        Chunk chunk = new(
            buffer,
            memory,
            mappedPtr,
            capacity,
            allocationLength,
            isHostCoherent);
        UpdateHighWater(
            ref _mappedBytesHighWater,
            checked((long)((ulong)(_chunks.Length + 1) * allocationLength)));
        return chunk;
    }

    private static ulong ResolvePublicationLayoutSignature(VkRenderProgram program, AutoUniformBlockInfo block)
    {
        if (program.BindingSchema?.TryGetAutoUniformBlock(block.InstanceName, out VulkanAutoUniformBindingSchema? schema) == true)
            return schema.PublicationLayoutSignature;

        FrameOpSignatureHasher fallbackLayout = new();
        fallbackLayout.Add(program.BindingId);
        fallbackLayout.Add(program.LinkGeneration);
        fallbackLayout.Add(block.Set);
        fallbackLayout.Add(block.Binding);
        fallbackLayout.Add(block.Size);
        return fallbackLayout.ToHash();
    }

    private void MarkDirty(in VulkanMappedFrameSlice slice)
    {
        if (_chunks[slice.FrameSlot] is { } chunk)
            chunk.DirtyRange.Include(slice.Offset, slice.Length);
    }

    private void ExpandVisibilityRange(ulong offset, ulong length, ulong capacity, out ulong expandedOffset, out ulong expandedLength)
    {
        ulong atom = _backend.NonCoherentAtomSize;
        expandedOffset = offset / atom * atom;
        ulong end = AlignUp(checked(offset + length), atom);
        if (end > capacity)
            end = capacity;
        expandedLength = end - expandedOffset;
        Interlocked.Add(ref _flushExpansionBytes, checked((long)(expandedLength - length)));
    }

    private void IncrementGeneration()
    {
        const ulong maxEncodedGeneration = ulong.MaxValue >> 2;
        _generation = _generation >= maxEncodedGeneration
            ? 1UL
            : _generation + 1UL;
    }

    private void PublishTelemetry()
    {
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanMeshFrameDataGauges(
            _chunks.Length,
            checked((long)((ulong)_chunks.Length * _capacity)),
            checked((long)Math.Min(ReservedBytes, (ulong)long.MaxValue)),
            ReservationCount,
            Generation);
    }

    private static ulong AlignUp(ulong value, ulong alignment)
        => alignment <= 1 ? value : checked(((value + alignment - 1UL) / alignment) * alignment);

    private static void UpdateHighWater(ref long highWater, long value)
    {
        long observed;
        while ((observed = Volatile.Read(ref highWater)) < value)
        {
            if (Interlocked.CompareExchange(ref highWater, value, observed) == observed)
                return;
        }
    }

    private sealed class Chunk(
        Buffer buffer,
        DeviceMemory memory,
        void* mappedPtr,
        ulong capacity,
        ulong allocationLength,
        bool isHostCoherent)
    {
        internal Buffer Buffer { get; } = buffer;
        internal DeviceMemory Memory { get; } = memory;
        internal void* MappedPtr { get; } = mappedPtr;
        internal ulong Capacity { get; } = capacity;
        internal ulong AllocationLength { get; } = allocationLength;
        internal bool IsHostCoherent { get; } = isHostCoherent;
        internal VulkanDynamicDataDirtyRange DirtyRange;
        private long _stateToken;

        internal void InitializeGeneration(ulong generation)
            => Volatile.Write(
                ref _stateToken,
                EncodeState(generation, VulkanMappedFrameSlotState.Writable));

        internal VulkanMappedFrameSlotState GetState(ulong generation)
        {
            long token = Volatile.Read(ref _stateToken);
            return DecodeGeneration(token) == generation
                ? DecodeState(token)
                : VulkanMappedFrameSlotState.Invalid;
        }

        internal bool TryTransition(
            ulong generation,
            VulkanMappedFrameSlotState from,
            VulkanMappedFrameSlotState to)
        {
            long expected = EncodeState(generation, from);
            long replacement = EncodeState(generation, to);
            return Interlocked.CompareExchange(
                ref _stateToken,
                replacement,
                expected) == expected;
        }

        internal bool PublishSubmitted(
            ulong generation,
            out VulkanMappedFrameSlotState previousState)
        {
            while (true)
            {
                long observed = Volatile.Read(ref _stateToken);
                if (DecodeGeneration(observed) != generation)
                {
                    previousState = VulkanMappedFrameSlotState.Invalid;
                    return false;
                }

                previousState = DecodeState(observed);
                if (previousState == VulkanMappedFrameSlotState.Submitted)
                    return true;

                long submitted = EncodeState(
                    generation,
                    VulkanMappedFrameSlotState.Submitted);
                if (Interlocked.CompareExchange(
                        ref _stateToken,
                        submitted,
                        observed) == observed)
                    return true;
            }
        }

        private static long EncodeState(
            ulong generation,
            VulkanMappedFrameSlotState state)
            => unchecked((long)((generation << 2) | (byte)state));

        private static ulong DecodeGeneration(long token)
            => unchecked((ulong)token) >> 2;

        private static VulkanMappedFrameSlotState DecodeState(long token)
            => (VulkanMappedFrameSlotState)(unchecked((ulong)token) & 0x3UL);

        internal void Destroy(
            VulkanMappedFrameArenaBackend backend,
            bool nativeDestroyAllowed)
            => backend.DestroyChunk(Buffer, Memory, MappedPtr, nativeDestroyAllowed);
    }

    private readonly record struct VulkanMappedFrameReservationKey(
        VkMeshRenderer Owner,
        string Name,
        bool IsAutoUniform,
        int DrawSlot);
}
