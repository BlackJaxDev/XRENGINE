using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Per-frame ring buffer for dynamic uniform buffer offsets.
    /// Allocates aligned sub-ranges from a single large persistently-mapped host-visible buffer.
    /// Callers write uniform data at the returned offset and pass it as a dynamic offset
    /// to <c>vkCmdBindDescriptorSets</c>.
    /// </summary>
    internal sealed class VulkanDynamicUniformRingBuffer
    {
        private readonly VulkanRenderer _renderer;
        private Buffer _buffer;
        private DeviceMemory _memory;
        private readonly ulong _capacity;
        private readonly uint _alignment;
        private ulong _currentOffset;
        private void* _mappedPtr;
        private bool _destroyed;
        private VulkanDynamicDataDirtyRange _dirtyRange;

        internal Buffer VkBuffer => _buffer;
        internal DeviceMemory Memory => _memory;
        internal ulong Capacity => _capacity;
        internal ulong CurrentOffset => _currentOffset;
        internal uint Alignment => _alignment;
        internal VulkanDynamicDataDirtyRange DirtyRange => _dirtyRange;

        /// <summary>
        /// Creates a ring buffer backed by a persistently mapped host-visible coherent Vulkan buffer.
        /// </summary>
        /// <param name="renderer">Owning renderer.</param>
        /// <param name="capacity">Total ring buffer size in bytes (e.g. 4 MB).</param>
        internal VulkanDynamicUniformRingBuffer(VulkanRenderer renderer, ulong capacity)
        {
            _renderer = renderer;
            _capacity = capacity;

            renderer.Api!.GetPhysicalDeviceProperties(renderer._physicalDevice, out PhysicalDeviceProperties props);
            _alignment = (uint)Math.Max(props.Limits.MinUniformBufferOffsetAlignment, 1);

            (_buffer, _memory) = renderer.CreateDedicatedBufferRaw(
                capacity,
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mapped;
            if (!renderer.TryMapBufferMemory(_buffer, _memory, 0, capacity, out mapped))
            {
                renderer.DestroyBuffer(_buffer, _memory);
                throw new InvalidOperationException("Failed to persistently map dynamic UBO ring buffer.");
            }
            _mappedPtr = mapped;
        }

        /// <summary>
        /// Allocates <paramref name="size"/> bytes at the next aligned offset.
        /// Returns the byte offset, or <see cref="ulong.MaxValue"/> if the ring buffer
        /// is exhausted for this frame.
        /// </summary>
        internal ulong Allocate(uint size)
        {
            ulong aligned = AlignUp(_currentOffset, _alignment);
            if (aligned + size > _capacity)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDynamicUniformExhaustion();
                return ulong.MaxValue;
            }
            _currentOffset = aligned + size;
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDynamicUniformAllocation(size);
            return aligned;
        }

        /// <summary>
        /// Writes raw bytes at the given offset inside the ring buffer.
        /// The buffer is persistently mapped and host-coherent, so no flush is needed.
        /// </summary>
        internal void Write(ulong offset, ReadOnlySpan<byte> data)
        {
            data.CopyTo(new Span<byte>((byte*)_mappedPtr + offset, data.Length));
        }

        /// <summary>
        /// Writes a blittable struct at the given offset.
        /// </summary>
        internal void Write<T>(ulong offset, in T value) where T : unmanaged
        {
            *(T*)((byte*)_mappedPtr + offset) = value;
        }

        internal bool WriteIfChanged<T>(ulong offset, in T value) where T : unmanaged
        {
            uint size = checked((uint)sizeof(T));
            if (!TryGetMappedRange(offset, size, out void* destination))
                return false;

            ReadOnlySpan<byte> source = new(Unsafe.AsPointer(ref Unsafe.AsRef(in value)), checked((int)size));
            Span<byte> current = new(destination, checked((int)size));
            if (source.SequenceEqual(current))
                return true;

            source.CopyTo(current);
            _dirtyRange.Include(offset, size);
            return true;
        }

        internal bool TryGetMappedRange(ulong offset, uint size, out void* mappedPtr)
        {
            if (_destroyed || _mappedPtr == null || offset > _capacity || size > _capacity - offset)
            {
                mappedPtr = null;
                return false;
            }

            mappedPtr = (byte*)_mappedPtr + checked((nint)offset);
            return true;
        }

        /// <summary>Resets the allocation cursor. Call at the start of each frame.</summary>
        internal void ResetForFrame()
        {
            _currentOffset = 0;
            _dirtyRange.Clear();
        }

        internal void Destroy()
        {
            if (_destroyed) return;
            _destroyed = true;

            if (_mappedPtr != null)
            {
                _renderer.UnmapBufferMemory(_buffer, _memory);
                _mappedPtr = null;
            }

            if (_buffer.Handle != 0)
            {
                _renderer.DestroyBuffer(_buffer, _memory);
                _buffer = default;
                _memory = default;
            }
        }

        private static ulong AlignUp(ulong value, uint alignment)
            => (value + alignment - 1) & ~((ulong)alignment - 1);
    }

    private VulkanDynamicUniformRingBuffer?[] _dynamicUniformRingBuffers = Array.Empty<VulkanDynamicUniformRingBuffer>();

    private readonly object _meshFrameDataReservationLock = new();
    private readonly Dictionary<MeshFrameDataReservationKey, MeshFrameDataReservation> _meshFrameDataReservations = new();
    private readonly Dictionary<
        VulkanFrequencyAutoUniformReservationKey,
        VulkanFrequencyAutoUniformReservation> _frequencyAutoUniformReservations = [];
    private ulong _meshFrameDataReservedBytes;
    private long _meshFrameDataReservationGeneration;
    private int _meshFrameDataArenaActive;

    private readonly record struct MeshFrameDataReservationKey(
        VkMeshRenderer Owner,
        string Name,
        bool IsAutoUniform,
        int DrawSlot);

    private readonly record struct MeshFrameDataReservation(ulong Offset, uint Size, ulong Generation);
    private const string CpuDirectDynamicDataReservationName = "$CpuDirectDynamicData";

    /// <summary>
    /// Fixed capacity of each persistently mapped frame-data arena. Reservations use the
    /// same offset in every frame slot, so descriptor sets point at one stable arena buffer
    /// while draw-specific data is selected with a dynamic offset.
    /// </summary>
    internal const ulong DynamicUniformRingBufferCapacity =
        32 * 1024 * 1024;
    internal const int MaxDynamicUniformFrameSlots = 8;
    internal const int MaxMeshFrameDataReservations = 131_072;
    private static bool? DynamicUniformBufferEnabledOverride
        => XREnvironment.GetBooleanOverride(
            XREngineEnvironmentVariables.VulkanDynamicUniformBuffer);

    private bool DynamicUniformBufferEnabled =>
        DynamicUniformBufferEnabledOverride ??
        RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DynamicUniformBufferEnabled;

    internal bool MeshFrameDataArenaEnabled =>
        DynamicUniformBufferEnabled &&
        !IsDescriptorHeapDrawBindingActive &&
        _dynamicUniformRingBuffers.Length > 0;

    internal int MeshFrameDataReservationCount
    {
        get
        {
            lock (_meshFrameDataReservationLock)
                return _meshFrameDataReservations.Count +
                    _frequencyAutoUniformReservations.Count;
        }
    }

    internal ulong MeshFrameDataReservedBytes
    {
        get
        {
            lock (_meshFrameDataReservationLock)
                return _meshFrameDataReservedBytes;
        }
    }

    internal ulong MeshFrameDataReservationGeneration
        => Volatile.Read(ref _meshFrameDataArenaActive) == 0
            ? 0UL
            : unchecked((ulong)Volatile.Read(
                ref _meshFrameDataReservationGeneration));

    internal bool TryReserveMeshFrameDataRange(
        VkMeshRenderer owner,
        string name,
        bool isAutoUniform,
        int drawSlot,
        uint size,
        out ulong offset)
    {
        offset = 0;
        if (!MeshFrameDataArenaEnabled || size == 0 || drawSlot < 0)
            return false;

        MeshFrameDataReservationKey key = new(owner, name, isAutoUniform, drawSlot);
        lock (_meshFrameDataReservationLock)
        {
            bool hasExisting = _meshFrameDataReservations.TryGetValue(
                key,
                out MeshFrameDataReservation existing);
            if (hasExisting && existing.Size >= size)
            {
                offset = existing.Offset;
                return true;
            }
            if (!hasExisting &&
                !CanAddMeshFrameDataReservation_NoLock())
            {
                RuntimeEngine.Rendering.Stats.Vulkan
                    .RecordVulkanDynamicUniformExhaustion();
                return false;
            }

            uint alignment = _dynamicUniformRingBuffers[0]?.Alignment ?? 1u;
            ulong aligned = AlignUp(_meshFrameDataReservedBytes, alignment);
            if (aligned > DynamicUniformRingBufferCapacity || size > DynamicUniformRingBufferCapacity - aligned)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDynamicUniformExhaustion();
                return false;
            }

            ulong generation = MeshFrameDataReservationGeneration;
            if (generation == 0)
                return false;
            _meshFrameDataReservations[key] = new MeshFrameDataReservation(aligned, size, generation);
            _meshFrameDataReservedBytes = aligned + size;
            offset = aligned;
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDynamicUniformAllocation(size);
            PublishMeshFrameDataArenaGauges();
            return true;
        }
    }

    internal bool TryGetOrReserveFrequencyAutoUniformRange(
        VkRenderProgram program,
        AutoUniformBlockInfo block,
        ulong ownerIdentity,
        out VulkanFrequencyAutoUniformReservation reservation)
    {
        reservation = null!;
        if (!MeshFrameDataArenaEnabled ||
            block.Frequency == EVulkanBindingFrequency.Unknown ||
            ownerIdentity == 0 ||
            block.Size == 0)
        {
            return false;
        }

        ulong publicationLayoutSignature;
        if (program.BindingSchema?.TryGetAutoUniformBlock(
                block.InstanceName,
                out VulkanAutoUniformBindingSchema? schema) == true)
        {
            publicationLayoutSignature =
                schema.PublicationLayoutSignature;
        }
        else
        {
            // Legacy/fallback blocks cannot safely alias across linked
            // programs because they do not publish a compiled value schema.
            FrameOpSignatureHasher fallbackLayout = new();
            fallbackLayout.Add(program.BindingId);
            fallbackLayout.Add(program.LinkGeneration);
            fallbackLayout.Add(block.Set);
            fallbackLayout.Add(block.Binding);
            fallbackLayout.Add(block.Size);
            publicationLayoutSignature = fallbackLayout.ToHash();
        }

        VulkanFrequencyAutoUniformReservationKey key = new(
            publicationLayoutSignature,
            block.Frequency,
            ownerIdentity);
        lock (_meshFrameDataReservationLock)
        {
            if (_frequencyAutoUniformReservations.TryGetValue(
                    key,
                    out VulkanFrequencyAutoUniformReservation? existing))
            {
                if (existing.Size < block.Size)
                {
                    RuntimeEngine.Rendering.Stats.Vulkan
                        .RecordVulkanDynamicUniformExhaustion();
                    return false;
                }

                if (existing.PublicationStates.Length ==
                    _dynamicUniformRingBuffers.Length)
                {
                    reservation = existing;
                    return true;
                }

                reservation = new VulkanFrequencyAutoUniformReservation(
                    existing.Key,
                    existing.Offset,
                    existing.Size,
                    existing.RecordingVisibleGeneration,
                    _dynamicUniformRingBuffers.Length);
                _frequencyAutoUniformReservations[key] = reservation;
                return true;
            }
            if (!CanAddMeshFrameDataReservation_NoLock())
            {
                RuntimeEngine.Rendering.Stats.Vulkan
                    .RecordVulkanDynamicUniformExhaustion();
                return false;
            }

            uint alignment = _dynamicUniformRingBuffers[0]?.Alignment ?? 1u;
            ulong aligned = AlignUp(_meshFrameDataReservedBytes, alignment);
            if (aligned > DynamicUniformRingBufferCapacity ||
                block.Size > DynamicUniformRingBufferCapacity - aligned)
            {
                RuntimeEngine.Rendering.Stats.Vulkan
                    .RecordVulkanDynamicUniformExhaustion();
                return false;
            }

            ulong generation = MeshFrameDataReservationGeneration;
            if (generation == 0)
                return false;

            reservation = new VulkanFrequencyAutoUniformReservation(
                key,
                aligned,
                block.Size,
                generation,
                _dynamicUniformRingBuffers.Length);
            _frequencyAutoUniformReservations.Add(key, reservation);
            _meshFrameDataReservedBytes = aligned + block.Size;
            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanDynamicUniformAllocation(block.Size);
            PublishMeshFrameDataArenaGauges();
            return true;
        }
    }

    internal bool TryGetMeshFrameDataArenaRange(
        int frameIndex,
        ulong offset,
        uint size,
        out Buffer buffer,
        out DeviceMemory memory,
        out void* mappedPtr)
    {
        buffer = default;
        memory = default;
        mappedPtr = null;
        if ((uint)frameIndex >= (uint)_dynamicUniformRingBuffers.Length ||
            _dynamicUniformRingBuffers[frameIndex] is not { } arena ||
            !arena.TryGetMappedRange(offset, size, out mappedPtr))
        {
            return false;
        }

        buffer = arena.VkBuffer;
        memory = arena.Memory;
        return buffer.Handle != 0;
    }

    internal bool TryCaptureCpuDirectDynamicData(
        VkMeshRenderer owner,
        int frameIndex,
        int drawSlot,
        in PendingMeshDraw draw,
        uint passMask,
        uint viewId = 0u)
    {
        if (!MeshFrameDataArenaEnabled || (uint)frameIndex >= (uint)_dynamicUniformRingBuffers.Length)
            return false;

        uint size = checked((uint)VulkanCpuDirectDynamicData.Stride);
        if (!TryReserveMeshFrameDataRange(
                owner,
                CpuDirectDynamicDataReservationName,
                isAutoUniform: false,
                drawSlot,
                size,
                out ulong offset))
        {
            return false;
        }

        uint stableRendererId = unchecked((uint)owner.BindingId);
        VulkanCpuDirectDynamicData dynamicData = draw.CaptureDynamicData(
            viewId,
            passMask,
            owner.Mesh?.HasSkinning == true ? stableRendererId : 0u,
            owner.Mesh?.HasBlendshapes == true ? stableRendererId : 0u,
            draw.TransformId);
        return _dynamicUniformRingBuffers[frameIndex]!.WriteIfChanged(offset, dynamicData);
    }

    internal bool TryGetCpuDirectDynamicDataDirtyRange(
        int frameIndex,
        out VulkanDynamicDataDirtyRange dirtyRange)
    {
        if ((uint)frameIndex >= (uint)_dynamicUniformRingBuffers.Length ||
            _dynamicUniformRingBuffers[frameIndex] is not { } arena)
        {
            dirtyRange = default;
            return false;
        }

        dirtyRange = arena.DirtyRange;
        return true;
    }

    internal void ReleaseMeshFrameDataReservations(VkMeshRenderer owner)
    {
        _frameWideMeshFrameDataManifest.RemoveRenderer(owner);
        PublishFrameWideMeshFrameDataManifestGauges();

        // The arena is frame-slot owned and command buffers can retain its offsets. Do not
        // recycle released subranges inside the current arena generation. Removing the keys
        // releases the renderer object while keeping old offsets inert until arena teardown.
        lock (_meshFrameDataReservationLock)
        {
            if (_meshFrameDataReservations.Count == 0)
                return;

            List<MeshFrameDataReservationKey>? removed = null;
            foreach (MeshFrameDataReservationKey key in _meshFrameDataReservations.Keys)
            {
                if (!ReferenceEquals(key.Owner, owner))
                    continue;
                removed ??= [];
                removed.Add(key);
            }

            if (removed is null)
                return;
            for (int i = 0; i < removed.Count; i++)
                _meshFrameDataReservations.Remove(removed[i]);
            PublishMeshFrameDataArenaGauges();
        }
    }

    internal bool TryAcquireMeshFrameDataRecordingLease(
        CommandBuffer commandBuffer,
        VkMeshRenderer owner,
        int drawSlot,
        ulong sealedGeneration,
        out string reason)
    {
        reason = string.Empty;
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        ulong generation = MeshFrameDataReservationGeneration;
        if (commandBufferHandle == 0 || generation == 0)
            return true;

        VulkanMeshFrameDataReservationManifest manifest =
            _commandBufferRecordingScratch.Value!.MeshFrameDataManifest;
        bool manifestOwnsDraw = manifest.ContainsSealedDraw(owner, drawSlot, generation);
        bool workerOwnsSealedDraw = sealedGeneration != 0 && sealedGeneration == generation;
        if (!manifestOwnsDraw && !workerOwnsSealedDraw)
        {
            reason = $"late or unsealed frame-data request for generation {generation}, slot {drawSlot}";
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDynamicUniformExhaustion();
            return false;
        }

        lock (_resourceLifetimeTracker.SyncRoot)
        {
            if (!_resourceLifetimeTracker.CommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                _resourceLifetimeTracker.CommandBufferLifetimes[commandBufferHandle] = lifetime;
            }

            if (lifetime.QueuedSubmissionCount != 0)
            {
                reason = $"command buffer 0x{commandBufferHandle:X} is already queued";
                return false;
            }

            if (lifetime.FrameDataLease.Generation != 0 &&
                lifetime.FrameDataLease.Generation != generation)
            {
                reason = $"command buffer 0x{commandBufferHandle:X} captured frame-data generation {lifetime.FrameDataLease.Generation} before current generation {generation}";
                return false;
            }

            if (lifetime.FrameDataLease.TryAcquireRecording(
                generation,
                commandBufferQueued: lifetime.QueuedSubmissionCount != 0))
            {
                return true;
            }

            reason = $"command buffer 0x{commandBufferHandle:X} could not acquire frame-data generation {generation}";
            return false;
        }
    }

    /// <summary>
    /// Returns the dynamic uniform ring buffer for the given swapchain image, or null if disabled.
    /// </summary>
    internal VulkanDynamicUniformRingBuffer? GetDynamicUniformRingBuffer(uint imageIndex)
    {
        if (_dynamicUniformRingBuffers.Length == 0 || imageIndex >= _dynamicUniformRingBuffers.Length)
            return null;
        return _dynamicUniformRingBuffers[imageIndex];
    }

    private void InitializeDynamicUniformRingBuffers()
    {
        if (!DynamicUniformBufferEnabled)
        {
            if (DynamicUniformBufferEnabledOverride is false)
            {
                Debug.Vulkan(
                    "[Vulkan] Dynamic uniform arena disabled by {0}=0 for this process.",
                    XREngineEnvironmentVariables.VulkanDynamicUniformBuffer);
            }
            return;
        }

        int count = swapChainImages?.Length ?? 0;
        if (count == 0) return;
        ValidateDynamicUniformFrameSlotCount(count);

        _dynamicUniformRingBuffers = new VulkanDynamicUniformRingBuffer[count];
        for (int i = 0; i < count; i++)
            _dynamicUniformRingBuffers[i] = new VulkanDynamicUniformRingBuffer(this, DynamicUniformRingBufferCapacity);
        VulkanGeneration.IncrementNonZero(
            ref _meshFrameDataReservationGeneration);
        Volatile.Write(ref _meshFrameDataArenaActive, 1);
        PublishMeshFrameDataArenaGauges();

        Debug.Vulkan($"[Vulkan] Dynamic uniform ring buffers initialized: {count} x {DynamicUniformRingBufferCapacity / 1024} KB, alignment={_dynamicUniformRingBuffers[0]?.Alignment}");
    }

    private void EnsureDynamicUniformRingBufferCapacity(int count)
    {
        if (!DynamicUniformBufferEnabled ||
            count <= _dynamicUniformRingBuffers.Length)
        {
            return;
        }
        ValidateDynamicUniformFrameSlotCount(count);

        int oldLength = _dynamicUniformRingBuffers.Length;
        Array.Resize(ref _dynamicUniformRingBuffers, count);
        for (int i = oldLength; i < _dynamicUniformRingBuffers.Length; i++)
            _dynamicUniformRingBuffers[i] = new VulkanDynamicUniformRingBuffer(this, DynamicUniformRingBufferCapacity);

        PublishMeshFrameDataArenaGauges();

        Debug.Vulkan($"[Vulkan] Dynamic uniform ring buffers expanded: {oldLength}->{count} slots.");
    }

    private void DestroyDynamicUniformRingBuffers()
    {
        // Make the current generation unavailable before destroying its
        // storage. Keep the counter monotonic so a recreated arena can never
        // make a stale prepared handle valid again.
        Volatile.Write(ref _meshFrameDataArenaActive, 0);
        for (int i = 0; i < _dynamicUniformRingBuffers.Length; i++)
        {
            _dynamicUniformRingBuffers[i]?.Destroy();
            _dynamicUniformRingBuffers[i] = null;
        }
        _dynamicUniformRingBuffers = Array.Empty<VulkanDynamicUniformRingBuffer>();
        lock (_meshFrameDataReservationLock)
        {
            _meshFrameDataReservations.Clear();
            _frequencyAutoUniformReservations.Clear();
            _meshFrameDataReservedBytes = 0;
        }
        _frameWideMeshFrameDataManifest.Reset();
        Volatile.Write(ref _observedMeshFrameDataManifestGeneration, 0);
        PublishFrameWideMeshFrameDataManifestGauges();
        PublishMeshFrameDataArenaGauges();
    }

    /// <summary>
    /// Resets the ring buffer for the given swapchain image at frame start.
    /// </summary>
    internal void ResetDynamicUniformRingBuffer(uint imageIndex)
    {
        if (imageIndex < _dynamicUniformRingBuffers.Length)
            _dynamicUniformRingBuffers[imageIndex]?.ResetForFrame();
    }

    private static ulong AlignUp(ulong value, uint alignment)
        => (value + alignment - 1) & ~((ulong)alignment - 1);

    private bool CanAddMeshFrameDataReservation_NoLock()
        => _meshFrameDataReservations.Count +
            _frequencyAutoUniformReservations.Count <
            MaxMeshFrameDataReservations;

    internal static void ValidateDynamicUniformFrameSlotCount(int count)
    {
        if ((uint)count <= MaxDynamicUniformFrameSlots)
            return;

        RuntimeEngine.Rendering.Stats.Vulkan
            .RecordVulkanDynamicUniformExhaustion();
        throw new InvalidOperationException(
            $"Vulkan frame-data arena requested {count} frame slots; " +
            $"the explicit limit is {MaxDynamicUniformFrameSlots}.");
    }

    private void PublishMeshFrameDataArenaGauges()
    {
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanMeshFrameDataGauges(
            _dynamicUniformRingBuffers.Length,
            checked((long)((ulong)_dynamicUniformRingBuffers.Length * DynamicUniformRingBufferCapacity)),
            checked((long)Math.Min(MeshFrameDataReservedBytes, (ulong)long.MaxValue)),
            MeshFrameDataReservationCount,
            MeshFrameDataReservationGeneration,
            recordingLeases: 0,
            cachedLeases: 0,
            submittedLeases: 0,
            activeGenerationCount: MeshFrameDataReservationGeneration == 0 ? 0 : 1,
            leaseRetainedGenerationCount: 0);
    }
}
