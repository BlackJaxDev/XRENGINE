using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using XREngine.Rendering.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan.RenderGraph;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the Vulkan set-1 storage ABI for the first visibility producer lane.
/// Frame-local payload, counter, and indirect ranges are paired with one
/// device-generation persistent visibility-state buffer. The runtime neither
/// performs a CPU visibility fallback nor observes a same-frame GPU count.
/// </summary>
internal sealed partial class VulkanAdvancedVisibilityResourceRuntime
{
    private const ulong StorageCapacityPerFrameSlot = 2UL * 1024UL * 1024UL;
    private const uint StorageAlignment = 16u;
    // Sixteen counter words followed by eleven exact packed handle-lookup
    // segments. Keeping these in set 1 removes any mutable set-0 uniform
    // dependency from the preparation/raster family.
    private const uint CounterByteLength = 152u;
    private const uint IndexedIndirectArgumentByteLength = 20u;
    private const uint MeshIndirectArgumentByteLength = 12u;
    private const uint PersistentStateRecordByteLength = 32u;
    private const ulong PersistentStateCapacityBytes = 16UL * 1024UL * 1024UL;
    private const string PersistentStateOwner = "AdvancedVisibility.PersistentState";
    // The bounded Phase 5.2 pyramid is one tile-local level. Do not update a
    // descriptor set already captured by a recorded command buffer.
    private const uint LateDescriptorSetsPerView = 2u;
    private const uint MaxLateVisibilityViews = RenderFrameViewSet.MaxViewCount;
    // A frame plan has a bounded number of independently sealed late passes.
    // Each pass owns its descriptor table family for the entire frame
    // generation; a retry for the same operation reuses that family exactly.
    private const uint MaxLateVisibilityOperationsPerFrame = 8u;
    // Native compute tables are immutable per admitted closure. A frame slot
    // may seal a bounded number of independently recorded advanced stages.
    private const uint NativeComputeDescriptorSetsPerView = 8u;

    private readonly object _gate = new();
    private readonly VulkanResourceRuntime _resources;
    private readonly VulkanAdvancedVisibilityResourceState[] _states;
    private readonly VulkanAdvancedVisibilityFamilySeal[] _familySeals;
    private readonly bool[] _quarantinedFrameSlots;
    private VulkanDeviceContext? _device;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorPool _lateDescriptorPool;
    private DescriptorPool _nativeComputeDescriptorPool;
    private DescriptorSet[] _lateDescriptorSets = [];
    private ulong[] _lateDescriptorGenerations = [];
    private VulkanLateDepthPyramidDescriptorSignature[] _lateDescriptorSignatures = [];
    private int[] _lateOperationKeys = [];
    private ulong[] _lateOperationGenerations = [];
    private DescriptorSet[] _nativeComputeDescriptorSets = [];
    private ulong[] _nativeComputeDescriptorGenerations = [];
    private VulkanNativeComputeDescriptorSignature[] _nativeComputeDescriptorSignatures = [];
    private VkBufferHandle _persistentStateBuffer;
    private DeviceMemory _persistentStateMemory;
    private ulong _persistentStateTopologyGeneration;
    private ulong _persistentStateContentGeneration;
    private bool _ready;
    private string _availabilityReason =
        "The Vulkan advanced-visibility resource runtime has not been initialized.";

    internal VulkanAdvancedVisibilityResourceRuntime(
        VulkanResourceRuntime resources,
        int frameSlotCount)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        if (frameSlotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSlotCount));

        _states = new VulkanAdvancedVisibilityResourceState[frameSlotCount];
        _familySeals = new VulkanAdvancedVisibilityFamilySeal[frameSlotCount];
        _quarantinedFrameSlots = new bool[frameSlotCount];
    }

    internal bool IsReady => _ready;
    internal string AvailabilityReason => _availabilityReason;
    internal DescriptorSetLayout DescriptorSetLayout => _descriptorSetLayout;

    internal bool TryGetProgramDescriptorSetLayout(
        uint setIndex,
        out DescriptorSetLayout layout)
    {
        layout = setIndex == VulkanAdvancedSceneProgramBindingContract.VisibilitySetIndex
            ? _descriptorSetLayout
            : default;
        return _ready && layout.Handle != 0;
    }

    internal bool TryInitialize(VulkanDeviceContext device, out string reason)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            if (_ready)
            {
                reason = "Ready";
                return true;
            }

            if (_resources.FrameDataArena is not { IsActive: true } arena ||
                !arena.TryReserveLaneCapacity(
                    EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    StorageCapacityPerFrameSlot,
                    StorageAlignment))
            {
                return SetUnavailable(
                    "The frame-data arena could not reserve the fixed advanced-visibility set-1 lane.",
                    out reason);
            }

            _device = device;
            if (!TryCreatePersistentStateStorage(out reason) ||
                !TryCreateDescriptorStorage(device, out reason))
            {
                RetireNativeStorageNoLock();
                return SetUnavailable(reason, out reason);
            }

            _ready = true;
            _availabilityReason = "Ready";
            reason = "Ready";
            return true;
        }
    }

    /// <summary>
    /// Allocates the exact frame-slot set-1 ranges required by the prepared
    /// visibility publication. Counts remain GPU-owned; this method rejects
    /// any plan that would require CPU count observation or readback.
    /// </summary>
    internal bool TryPrepare(
        int frameSlot,
        ulong frameGeneration,
        in AdvancedPreparationPublication publication,
        in AdvancedIndirectPreparationResult indirect,
        in VulkanAdvancedSceneLookupSegments lookupSegments,
        VulkanAdvancedVisibilityInputStorage input,
        ReadOnlySpan<AdvancedVisibilityPayload> sourcePayloads,
        ReadOnlySpan<AdvancedPreparedDrawDeformationRecord> deformationOverlay,
        in VulkanAdvancedVisibilityGeometrySlices geometry,
        uint viewCount,
        in VulkanAdvancedVisibilityFamilySeal familySeal,
        out VulkanAdvancedVisibilityResourceState state,
        out EVulkanAdvancedVisibilityResourceFailure failure,
        out string reason)
    {
        state = default;
        lock (_gate)
        {
            if (!_ready || _device is not { IsOperational: true } ||
                _resources.FrameDataArena is not { IsActive: true } arena)
            {
                failure = EVulkanAdvancedVisibilityResourceFailure.RuntimeUnavailable;
                reason = _availabilityReason;
                return false;
            }
            if ((uint)frameSlot >= (uint)_states.Length || frameGeneration == 0u)
            {
                failure = EVulkanAdvancedVisibilityResourceFailure.InvalidFrameOwner;
                reason = "The visibility producer has no valid frame-slot generation.";
                return false;
            }
            if (_quarantinedFrameSlots[frameSlot])
            {
                failure = EVulkanAdvancedVisibilityResourceFailure.TransactionIntegrityFailure;
                reason = "The visibility frame slot is quarantined because its frame-data transaction could not be rolled back.";
                return false;
            }
            // Raster currently records one view segment inside one target
            // scope. Do not admit stereo until layer-specific scopes or a
            // true gl_ViewIndex-based indirect ABI is sealed end to end.
            if (!familySeal.IsValid ||
                input is null ||
                !input.MatchesPublication(in publication, in indirect) ||
                publication.DrawCount == 0u || viewCount != 1u ||
                publication.RequiresCpuReadback ||
                indirect.RequiresCpuCount ||
                sourcePayloads.Length != publication.DrawCount ||
                deformationOverlay.IsEmpty || !geometry.HasValidSources)
            {
                failure = EVulkanAdvancedVisibilityResourceFailure.InvalidPreparation;
                reason = "The visibility producer currently requires one exact mono view, non-empty GPU-only payload data, canonical mesh-geometry slices, and no CPU count or readback dependency.";
                return false;
            }

            ref VulkanAdvancedVisibilityResourceState current = ref _states[frameSlot];
            if (current.IsValid && current.FrameGeneration == frameGeneration)
            {
                VulkanAdvancedVisibilityFamilySeal reusedSeal =
                    familySeal with { Geometry = current.Geometry };
                if (!_familySeals[frameSlot].Matches(in reusedSeal) ||
                    current.PayloadCapacity != publication.DrawCount ||
                    current.ViewCount != viewCount ||
                    current.RangeCapacity != Math.Max(1u, indirect.RangeCount) ||
                    !current.Geometry.MatchesSources(in geometry))
                {
                    failure = EVulkanAdvancedVisibilityResourceFailure.InvalidPreparation;
                    reason = "The immutable frame-slot visibility family does not match the exact publication shape.";
                    return false;
                }
                state = current;
                failure = EVulkanAdvancedVisibilityResourceFailure.None;
                reason = "Ready (immutable family reuse)";
                return true;
            }
            uint payloadBytes;
            uint candidateBytes;
            uint producerBytes;
            uint deformationOverlayBytes;
            uint perViewIndexBytes;
            uint perViewRangeBytes;
            uint perViewIndirectBytes;
            uint perViewMeshArgumentBytes;
            uint totalIndexBytes;
            uint totalRangeBytes;
            uint totalIndirectBytes;
            uint totalMeshArgumentBytes;
            uint totalCounterBytes;
            uint totalPayloadCapacity;
            uint persistentStateBytes;
            try
            {
                payloadBytes = checked(publication.DrawCount * (uint)Unsafe.SizeOf<AdvancedVisibilityPayload>());
                candidateBytes = checked(publication.DrawCount * (uint)Unsafe.SizeOf<AdvancedVisibilityCandidate>());
                producerBytes = checked(publication.DrawCount * sizeof(uint));
                deformationOverlayBytes = checked(
                    (uint)deformationOverlay.Length *
                    (uint)Unsafe.SizeOf<AdvancedPreparedDrawDeformationRecord>());
                perViewIndexBytes = checked(publication.DrawCount * sizeof(uint));
                perViewRangeBytes = checked(Math.Max(1u, indirect.RangeCount) * sizeof(uint));
                perViewIndirectBytes = checked(publication.DrawCount * IndexedIndirectArgumentByteLength);
                perViewMeshArgumentBytes = checked(publication.DrawCount * MeshIndirectArgumentByteLength);
                totalPayloadCapacity = checked(publication.DrawCount * viewCount);
                totalIndexBytes = checked(perViewIndexBytes * viewCount);
                totalRangeBytes = checked(perViewRangeBytes * viewCount);
                totalIndirectBytes = checked(perViewIndirectBytes * viewCount);
                totalMeshArgumentBytes = checked(perViewMeshArgumentBytes * viewCount);
                totalCounterBytes = checked(CounterByteLength * viewCount);
                persistentStateBytes = checked(
                    publication.DrawCount * viewCount *
                    PersistentStateRecordByteLength);
            }
            catch (OverflowException)
            {
                failure = EVulkanAdvancedVisibilityResourceFailure.CapacityExceeded;
                reason = "Prepared visibility counts overflow the set-1 byte contract.";
                return false;
            }
            if (persistentStateBytes > PersistentStateCapacityBytes)
            {
                failure = EVulkanAdvancedVisibilityResourceFailure.CapacityExceeded;
                reason = $"Prepared visibility requires {persistentStateBytes} persistent-state bytes, exceeding the fixed capacity {PersistentStateCapacityBytes}.";
                return false;
            }
            // Immutable candidate/payload metadata remains shared; every
            // GPU-produced stream owns an exact contiguous segment per view.
            // This is the capacity proof used by the set-1 frame-slot lane.
            ulong requiredBytes = AlignedStorageBytes(payloadBytes) +
                AlignedStorageBytes(candidateBytes) +
                AlignedStorageBytes(producerBytes) +
                AlignedStorageBytes(deformationOverlayBytes) +
                AlignedStorageBytes(perViewIndexBytes) +
                AlignedStorageBytes(perViewRangeBytes) +
                AlignedStorageBytes(totalIndexBytes) * 5UL +
                AlignedStorageBytes(totalRangeBytes) * 2UL +
                AlignedStorageBytes(totalCounterBytes) +
                AlignedStorageBytes(totalIndirectBytes) * 2UL +
                AlignedStorageBytes(totalMeshArgumentBytes) * 2UL;
            if (requiredBytes > StorageCapacityPerFrameSlot)
            {
                failure = EVulkanAdvancedVisibilityResourceFailure.CapacityExceeded;
                reason = $"Prepared visibility requires {requiredBytes} bytes, exceeding the fixed set-1 capacity {StorageCapacityPerFrameSlot}.";
                return false;
            }
            if (!arena.TryCaptureReservedLaneCursor(
                    frameSlot,
                    EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    out ulong rollbackCursor))
            {
                failure = EVulkanAdvancedVisibilityResourceFailure.CapacityExceeded;
                reason = "The set-1 visibility allocation cursor is unavailable.";
                return false;
            }
            if (!arena.TryAllocate(
                    frameSlot,
                    EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    payloadBytes,
                    StorageAlignment,
                    out VulkanFrameDataSlice payloads) ||
                !arena.TryAllocate(
                    frameSlot,
                    EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    totalCounterBytes,
                    StorageAlignment,
                    out VulkanFrameDataSlice counters) ||
                !arena.TryAllocate(
                    frameSlot,
                    EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    totalIndirectBytes,
                    StorageAlignment,
                    out VulkanFrameDataSlice arguments))
            {
                if (!TryRollbackFrameStorageTransaction(
                        arena, frameSlot, rollbackCursor, out string rollbackReason))
                {
                    failure = EVulkanAdvancedVisibilityResourceFailure.TransactionIntegrityFailure;
                    reason = rollbackReason;
                    return false;
                }
                failure = EVulkanAdvancedVisibilityResourceFailure.CapacityExceeded;
                reason = "The exact set-1 allocation did not fit the frame-slot visibility lane.";
                return false;
            }
            if (!arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, candidateBytes, StorageAlignment, out VulkanFrameDataSlice candidates) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, totalIndexBytes, StorageAlignment, out VulkanFrameDataSlice deferredIndices) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, totalIndexBytes, StorageAlignment, out VulkanFrameDataSlice visibleIndices) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, producerBytes, StorageAlignment, out VulkanFrameDataSlice producers) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, deformationOverlayBytes, StorageAlignment, out VulkanFrameDataSlice overlay) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, perViewIndexBytes, StorageAlignment, out VulkanFrameDataSlice rangeIndices) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, perViewRangeBytes, StorageAlignment, out VulkanFrameDataSlice rangeOffsets) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, totalRangeBytes, StorageAlignment, out VulkanFrameDataSlice rangeCounts) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, totalMeshArgumentBytes, StorageAlignment, out VulkanFrameDataSlice meshArguments) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, totalIndexBytes, StorageAlignment, out VulkanFrameDataSlice meshPayloads) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, totalIndexBytes, StorageAlignment, out VulkanFrameDataSlice lateVisibleIndices) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, totalRangeBytes, StorageAlignment, out VulkanFrameDataSlice lateRangeCounts) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, totalIndirectBytes, StorageAlignment, out VulkanFrameDataSlice lateIndirectArguments) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, totalMeshArgumentBytes, StorageAlignment, out VulkanFrameDataSlice lateMeshArguments) ||
                !arena.TryAllocate(frameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage, totalIndexBytes, StorageAlignment, out VulkanFrameDataSlice lateMeshPayloads))
            {
                if (!TryRollbackFrameStorageTransaction(
                        arena, frameSlot, rollbackCursor, out string rollbackReason))
                {
                    failure = EVulkanAdvancedVisibilityResourceFailure.TransactionIntegrityFailure;
                    reason = rollbackReason;
                    return false;
                }
                failure = EVulkanAdvancedVisibilityResourceFailure.CapacityExceeded;
                reason = "The complete set-1 visibility ABI did not fit the frame-slot lane.";
                return false;
            }
            if (!TryWritePayloads(arena, payloads, sourcePayloads) ||
                !TryWritePayloads(arena, candidates, input.Candidates) ||
                !TryWritePayloads(arena, producers, input.Producers) ||
                !TryWritePayloads(arena, overlay, deformationOverlay) ||
                !TryWriteRangeMetadata(
                    arena,
                    rangeIndices,
                    rangeOffsets,
                    input.IndirectRanges,
                    input.IndirectPayloadIndices,
                    publication.DrawCount) ||
                !TryClear(arena, deferredIndices) ||
                !TryClear(arena, visibleIndices) || !TryClear(arena, rangeCounts) ||
                !TryInitializeCounters(arena, counters, in lookupSegments, viewCount) ||
                !TryClear(arena, arguments) ||
                !TryClear(arena, meshArguments) || !TryClear(arena, meshPayloads) ||
                !TryClear(arena, lateVisibleIndices) ||
                !TryClear(arena, lateRangeCounts) ||
                !TryClear(arena, lateIndirectArguments) || !TryClear(arena, lateMeshArguments) ||
                !TryClear(arena, lateMeshPayloads))
            {
                if (!TryRollbackFrameStorageTransaction(
                        arena, frameSlot, rollbackCursor, out string rollbackReason))
                {
                    failure = EVulkanAdvancedVisibilityResourceFailure.TransactionIntegrityFailure;
                    reason = rollbackReason;
                    return false;
                }
                failure = EVulkanAdvancedVisibilityResourceFailure.NativeFault;
                reason = "The frame-slot visibility payload, counters, or indirect arguments could not be initialized.";
                return false;
            }
            if (!input.MatchesPublication(in publication, in indirect))
            {
                if (!TryRollbackFrameStorageTransaction(
                        arena, frameSlot, rollbackCursor, out string rollbackReason))
                {
                    failure = EVulkanAdvancedVisibilityResourceFailure.TransactionIntegrityFailure;
                    reason = rollbackReason;
                    return false;
                }
                failure = EVulkanAdvancedVisibilityResourceFailure.InvalidPreparation;
                reason = "The frame-owned visibility input changed while its immutable set-1 payload was being copied.";
                return false;
            }
            VulkanAdvancedVisibilityGeometrySlices realizedGeometry =
                geometry with { DeformationOverlay = overlay };
            VulkanAdvancedVisibilityFamilySeal realizedSeal =
                familySeal with { Geometry = realizedGeometry };
            if (!realizedSeal.IsValid)
            {
                if (!TryRollbackFrameStorageTransaction(
                        arena, frameSlot, rollbackCursor, out string rollbackReason))
                {
                    failure = EVulkanAdvancedVisibilityResourceFailure.TransactionIntegrityFailure;
                    reason = rollbackReason;
                    return false;
                }
                failure = EVulkanAdvancedVisibilityResourceFailure.InvalidPreparation;
                reason = "The realized deformation overlay does not complete the visibility family.";
                return false;
            }
            VulkanAdvancedVisibilityResourceState candidateState = new(
                FrameSlot: frameSlot, FrameGeneration: frameGeneration,
                DescriptorSet: current.DescriptorSet, Payloads: payloads,
                Candidates: candidates, PersistentStateBuffer: _persistentStateBuffer,
                PersistentStateByteLength: PersistentStateCapacityBytes,
                PersistentStateTopologyGeneration: _persistentStateTopologyGeneration,
                PersistentStateContentGeneration: ++_persistentStateContentGeneration,
                DeferredIndices: deferredIndices, VisibleIndices: visibleIndices,
                Producers: producers, RangeIndices: rangeIndices, RangeOffsets: rangeOffsets,
                RangeCounts: rangeCounts, Counters: counters,
                IndirectArguments: arguments, MeshArguments: meshArguments,
                MeshPayloads: meshPayloads, Geometry: realizedGeometry,
                LateVisibleIndices: lateVisibleIndices,
                LateRangeCounts: lateRangeCounts,
                LateIndirectArguments: lateIndirectArguments,
                LateMeshArguments: lateMeshArguments,
                LateMeshPayloads: lateMeshPayloads,
                ViewCount: viewCount,
                PayloadCapacity: publication.DrawCount,
                RangeCapacity: Math.Max(1u, indirect.RangeCount),
                IndirectArgumentCapacity: totalPayloadCapacity);
            if (!TryUpdateDescriptorSet(
                    candidateState.DescriptorSet,
                    in candidateState,
                    out reason))
            {
                if (!TryRollbackFrameStorageTransaction(
                        arena, frameSlot, rollbackCursor, out string rollbackReason))
                {
                    failure = EVulkanAdvancedVisibilityResourceFailure.TransactionIntegrityFailure;
                    reason = rollbackReason;
                    return false;
                }
                failure = EVulkanAdvancedVisibilityResourceFailure.NativeFault;
                return false;
            }
            current = candidateState;
            _familySeals[frameSlot] = realizedSeal;
            state = current;
            failure = EVulkanAdvancedVisibilityResourceFailure.None;
            reason = "Ready";
            return true;
        }
    }

    /// <summary>
    /// Restores the allocation transaction before another set-1 family can
    /// observe the lane. A failed restore leaves ownership ambiguous, so the
    /// frame slot is permanently rejected for this runtime generation.
    /// </summary>
    private bool TryRollbackFrameStorageTransaction(
        VulkanFrameDataArena arena,
        int frameSlot,
        ulong rollbackCursor,
        out string reason)
    {
        if (arena.TryRestoreReservedLaneCursor(
                frameSlot,
                EVulkanFrameDataLane.AdvancedVisibilityStorage,
                rollbackCursor))
        {
            reason = string.Empty;
            return true;
        }

        _quarantinedFrameSlots[frameSlot] = true;
        _states[frameSlot] = default;
        _familySeals[frameSlot] = default;
        reason = "The visibility frame-data transaction could not restore its reserved-lane cursor; the frame slot was quarantined.";
        return false;
    }

    internal void RetireAll()
    {
        lock (_gate)
        {
            _ready = false;
            _availabilityReason = "The Vulkan advanced-visibility resource runtime is retired.";
            RetireNativeStorageNoLock();
        }
    }

    private static bool TryClear(VulkanFrameDataArena arena, in VulkanFrameDataSlice slice)
    {
        if (!arena.TryBeginWrite(slice, out VulkanFrameDataWriteScope scope))
            return false;
        using (scope)
            scope.Bytes.Clear();
        return true;
    }

    private static ulong AlignedStorageBytes(uint byteCount)
        => ((ulong)byteCount + StorageAlignment - 1u) & ~(StorageAlignment - 1u);

    private static bool TryInitializeCounters(
        VulkanFrameDataArena arena,
        in VulkanFrameDataSlice slice,
        in VulkanAdvancedSceneLookupSegments segments,
        uint viewCount)
    {
        if (!arena.TryBeginWrite(slice, out VulkanFrameDataWriteScope scope))
            return false;
        using (scope)
        {
            scope.Bytes.Clear();
            Span<uint> words = MemoryMarshal.Cast<byte, uint>(scope.Bytes);
            uint wordsPerView = CounterByteLength / sizeof(uint);
            if (viewCount == 0u || words.Length < checked(wordsPerView * viewCount))
                return false;
            for (uint viewIndex = 0u; viewIndex < viewCount; ++viewIndex)
            {
                int offset = checked((int)(viewIndex * wordsPerView) + 16);
                WriteSegment(words, ref offset, segments.Draws);
                WriteSegment(words, ref offset, segments.Instances);
                WriteSegment(words, ref offset, segments.Transforms);
                WriteSegment(words, ref offset, segments.RenderStates);
                WriteSegment(words, ref offset, segments.EditorIdentities);
                WriteSegment(words, ref offset, segments.Geometry);
                WriteSegment(words, ref offset, segments.Materials);
                WriteSegment(words, ref offset, segments.ShadingKernels);
                WriteSegment(words, ref offset, segments.Textures);
                WriteSegment(words, ref offset, segments.Samplers);
                WriteSegment(words, ref offset, segments.Shadows);
            }
        }
        return true;
    }

    private static void WriteSegment(
        Span<uint> words,
        ref int offset,
        in AdvancedGpuLookupSegment segment)
    {
        words[offset++] = segment.Offset;
        words[offset++] = segment.Count;
    }

    private bool TryCreatePersistentStateStorage(out string reason)
    {
        VulkanBackendObjectContext? context = _resources.BackendObjectContext;
        if (context is null || !context.IsDeviceOperational)
        {
            reason = "The Vulkan backend context is unavailable for persistent visibility-state allocation.";
            return false;
        }

        try
        {
            (_persistentStateBuffer, _persistentStateMemory) = _resources.Buffers.CreateDedicatedRaw(
                context,
                PersistentStateCapacityBytes,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                owner: PersistentStateOwner);
            if (!_resources.Buffers.TryCreateMappedSlice(
                    context,
                    _persistentStateBuffer,
                    _persistentStateMemory,
                    0u,
                    PersistentStateCapacityBytes,
                    out VulkanMappedMemorySlice slice) ||
                !_resources.Buffers.TryAcquireWrite(context, in slice, out VulkanMappedMemoryWriteLease lease))
            {
                reason = "The persistent visibility-state buffer could not be mapped for initialization.";
                return false;
            }

            using (lease)
                lease.Bytes.Clear();
            _persistentStateTopologyGeneration++;
            _persistentStateContentGeneration++;
            reason = "Ready";
            return true;
        }
        catch (Exception exception)
        {
            reason = $"The persistent visibility-state buffer could not be created: {exception.Message}";
            return false;
        }
    }

    private static bool TryWritePayloads<T>(
        VulkanFrameDataArena arena,
        in VulkanFrameDataSlice destination,
        ReadOnlySpan<T> source)
        where T : unmanaged
    {
        if (!arena.TryBeginWrite(destination, out VulkanFrameDataWriteScope scope))
            return false;
        using (scope)
            MemoryMarshal.AsBytes(source).CopyTo(scope.Bytes);
        return true;
    }

    private static bool TryWriteRangeMetadata(
        VulkanFrameDataArena arena,
        in VulkanFrameDataSlice rangeIndices,
        in VulkanFrameDataSlice rangeOffsets,
        ReadOnlySpan<AdvancedIndirectRange> ranges,
        ReadOnlySpan<int> payloadIndices,
        uint payloadCount)
    {
        if (ranges.IsEmpty || payloadIndices.Length != payloadCount ||
            !arena.TryBeginWrite(rangeIndices, out VulkanFrameDataWriteScope indexScope))
        {
            return false;
        }

        using (indexScope)
        {
            Span<uint> indices = MemoryMarshal.Cast<byte, uint>(indexScope.Bytes);
            if (indices.Length < payloadCount)
                return false;
            indices.Clear();
            for (int rangeIndex = 0; rangeIndex < ranges.Length; rangeIndex++)
            {
                AdvancedIndirectRange range = ranges[rangeIndex];
                uint rangeEnd = checked(range.FirstPayloadIndex + range.PayloadCapacity);
                if (rangeEnd > payloadIndices.Length)
                    return false;
                for (uint orderedIndex = range.FirstPayloadIndex;
                     orderedIndex < rangeEnd;
                     orderedIndex++)
                {
                    int payloadIndex = payloadIndices[(int)orderedIndex];
                    if ((uint)payloadIndex >= payloadCount)
                        return false;
                    indices[payloadIndex] = checked((uint)rangeIndex);
                }
            }
        }

        if (!arena.TryBeginWrite(rangeOffsets, out VulkanFrameDataWriteScope offsetScope))
            return false;
        using (offsetScope)
        {
            Span<uint> offsets = MemoryMarshal.Cast<byte, uint>(offsetScope.Bytes);
            if (offsets.Length < ranges.Length)
                return false;
            offsets.Clear();
            for (int rangeIndex = 0; rangeIndex < ranges.Length; rangeIndex++)
                offsets[rangeIndex] = ranges[rangeIndex].FirstPayloadIndex;
        }
        return true;
    }

    private unsafe bool TryCreateDescriptorStorage(VulkanDeviceContext device, out string reason)
    {
        ReadOnlySpan<uint> storageBindingNumbers = [
            VulkanAdvancedSceneProgramBindingContract.VisibilityCandidatesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityPersistentStateBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityDeferredIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityVisibleIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityPayloadBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityProducersBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityRangeIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityRangeOffsetsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityRangeCountsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityCountersBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityIndexedArgumentsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityMeshArgumentsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityMeshPayloadsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityStaticVerticesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityCurrentVerticesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityPreviousVerticesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityMeshletDescriptorsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityMeshletVertexIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityMeshletTriangleWordsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityLateVisibleIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityLateRangeCountsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityLateIndexedArgumentsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityLateMeshArgumentsBinding,
VulkanAdvancedSceneProgramBindingContract.VisibilityLateMeshPayloadsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityDeformationOverlayBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityCanonicalIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityReconstructionCountersBinding];
        ReadOnlySpan<uint> nativeStorageBindingNumbers = [
            VulkanAdvancedSceneProgramBindingContract.NativeActiveTilesBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeKernelTilesBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeCountersBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeDispatchArgumentsBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeKernelCountsBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeFroxelGridBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeLightIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeLightingCountersBinding];
        ReadOnlySpan<uint> nativeSampledBindingNumbers = [
            VulkanAdvancedSceneProgramBindingContract.NativeIdentityBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeMetadataBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeDepthBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeAmbientOcclusionSampledBinding];
        ReadOnlySpan<uint> nativeStorageImageBindingNumbers = [
            VulkanAdvancedSceneProgramBindingContract.NativeHdrBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeVelocityBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeReactiveBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeShadingDiagnosticsBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeAmbientOcclusionStorageBinding];
        const int imageBindingCount = 2;
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[
            storageBindingNumbers.Length + imageBindingCount +
            nativeStorageBindingNumbers.Length + nativeSampledBindingNumbers.Length +
            nativeStorageImageBindingNumbers.Length];
        for (int index = 0; index < storageBindingNumbers.Length; ++index)
            bindings[index] = new DescriptorSetLayoutBinding
            {
                Binding = storageBindingNumbers[index],
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1u,
                StageFlags = ShaderStageFlags.ComputeBit | ShaderStageFlags.VertexBit |
                    ShaderStageFlags.FragmentBit |
                    (device.SupportsMeshTaskIndirectCount ? ShaderStageFlags.MeshBitExt : 0),
            };
        bindings[storageBindingNumbers.Length] = new DescriptorSetLayoutBinding
        {
            Binding = VulkanAdvancedSceneProgramBindingContract
                .VisibilityDepthPyramidSampledBinding,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1u,
            StageFlags = ShaderStageFlags.ComputeBit,
        };
        bindings[storageBindingNumbers.Length + 1] = new DescriptorSetLayoutBinding
        {
            Binding = VulkanAdvancedSceneProgramBindingContract
                .VisibilityDepthPyramidStorageBinding,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1u,
            StageFlags = ShaderStageFlags.ComputeBit,
        };
        int nativeBindingOffset = storageBindingNumbers.Length + imageBindingCount;
        for (int index = 0; index < nativeStorageBindingNumbers.Length; ++index)
            bindings[nativeBindingOffset + index] = new DescriptorSetLayoutBinding
            {
                Binding = nativeStorageBindingNumbers[index],
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1u,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        nativeBindingOffset += nativeStorageBindingNumbers.Length;
        for (int index = 0; index < nativeSampledBindingNumbers.Length; ++index)
            bindings[nativeBindingOffset + index] = new DescriptorSetLayoutBinding
            {
                Binding = nativeSampledBindingNumbers[index],
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1u,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        nativeBindingOffset += nativeSampledBindingNumbers.Length;
        for (int index = 0; index < nativeStorageImageBindingNumbers.Length; ++index)
            bindings[nativeBindingOffset + index] = new DescriptorSetLayoutBinding
            {
                Binding = nativeStorageImageBindingNumbers[index],
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1u,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)(storageBindingNumbers.Length + imageBindingCount +
                nativeStorageBindingNumbers.Length + nativeSampledBindingNumbers.Length +
                nativeStorageImageBindingNumbers.Length),
            PBindings = bindings,
        };
        Result result = device.Api.CreateDescriptorSetLayout(device.Device, ref layoutInfo, null, out _descriptorSetLayout);
        if (result != Result.Success)
        {
            reason = $"Failed to create advanced-visibility set-1 descriptor layout ({result}).";
            return false;
        }
        _resources.RegisterDescriptorSetLayout(_descriptorSetLayout, "AdvancedVisibility.Set1DescriptorSetLayout");

        DescriptorPoolSize* poolSizes = stackalloc DescriptorPoolSize[3]
        {
            new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = checked((uint)_states.Length *
                    (uint)(storageBindingNumbers.Length + nativeStorageBindingNumbers.Length)),
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = checked((uint)_states.Length *
                    (1u + (uint)nativeSampledBindingNumbers.Length)),
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = checked((uint)_states.Length *
                    (1u + (uint)nativeStorageImageBindingNumbers.Length)),
            },
        };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 3u,
            PPoolSizes = poolSizes,
            MaxSets = (uint)_states.Length,
        };
        result = device.Api.CreateDescriptorPool(device.Device, ref poolInfo, null, out _descriptorPool);
        if (result != Result.Success)
        {
            reason = $"Failed to create advanced-visibility set-1 descriptor pool ({result}).";
            return false;
        }
        _resources.Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.DescriptorPool, _descriptorPool.Handle),
            "AdvancedVisibility.Set1DescriptorPool",
            externallyOwned: false);

        DescriptorSetLayout[] layouts = new DescriptorSetLayout[_states.Length];
        DescriptorSet[] sets = new DescriptorSet[_states.Length];
        layouts.AsSpan().Fill(_descriptorSetLayout);
        fixed (DescriptorSetLayout* layoutPointer = layouts)
        fixed (DescriptorSet* setPointer = sets)
        {
            DescriptorSetAllocateInfo allocation = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = (uint)sets.Length,
                PSetLayouts = layoutPointer,
            };
            result = device.Api.AllocateDescriptorSets(device.Device, ref allocation, setPointer);
        }
        if (result != Result.Success)
        {
            reason = $"Failed to allocate frame-slot advanced-visibility set-1 descriptor sets ({result}).";
            return false;
        }
        _resources.DescriptorLifetime.RegisterDescriptorSets(
            _descriptorPool, sets, usesUpdateAfterBind: false, owner: "AdvancedVisibility.Set1DescriptorSet");
        for (int index = 0; index < sets.Length; ++index)
            _states[index] = new VulkanAdvancedVisibilityResourceState(
                FrameSlot: -1, FrameGeneration: 0u, DescriptorSet: sets[index],
                Payloads: default, Candidates: default, PersistentStateBuffer: default,
                PersistentStateByteLength: 0u, PersistentStateTopologyGeneration: 0u,
                PersistentStateContentGeneration: 0u,
                DeferredIndices: default, VisibleIndices: default, Producers: default,
                RangeIndices: default, RangeOffsets: default, RangeCounts: default,
                Counters: default, IndirectArguments: default, MeshArguments: default,
                MeshPayloads: default, Geometry: default,
                LateVisibleIndices: default, LateRangeCounts: default,
                LateIndirectArguments: default, LateMeshArguments: default,
                LateMeshPayloads: default, ViewCount: 0u,
                PayloadCapacity: 0u, RangeCapacity: 0u,
                IndirectArgumentCapacity: 0u);

        // The normal frame-slot table is rewritten only before early work is
        // recorded. Late pyramid work needs one immutable table per mip, so
        // allocate those tables separately rather than recycling the normal
        // table while a primary command buffer may still reference it.
        uint lateSetCount = checked((uint)_states.Length *
            MaxLateVisibilityOperationsPerFrame * LateDescriptorSetsPerView *
            MaxLateVisibilityViews);
        DescriptorPoolSize* latePoolSizes = stackalloc DescriptorPoolSize[3]
        {
            new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = checked(lateSetCount *
                    (uint)(storageBindingNumbers.Length + nativeStorageBindingNumbers.Length)),
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = checked(lateSetCount *
                    (1u + (uint)nativeSampledBindingNumbers.Length)),
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = checked(lateSetCount *
                    (1u + (uint)nativeStorageImageBindingNumbers.Length)),
            },
        };
        DescriptorPoolCreateInfo latePoolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 3u,
            PPoolSizes = latePoolSizes,
            MaxSets = lateSetCount,
        };
        result = device.Api.CreateDescriptorPool(device.Device, ref latePoolInfo, null, out _lateDescriptorPool);
        if (result != Result.Success)
        {
            reason = $"Failed to create immutable late-visibility descriptor pool ({result}).";
            return false;
        }
        _resources.Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.DescriptorPool, _lateDescriptorPool.Handle),
            "AdvancedVisibility.LateSet1DescriptorPool",
            externallyOwned: false);

        _lateDescriptorSets = new DescriptorSet[lateSetCount];
        _lateDescriptorGenerations = new ulong[lateSetCount];
        _lateDescriptorSignatures = new VulkanLateDepthPyramidDescriptorSignature[lateSetCount];
        int lateOperationCount = checked((int)((uint)_states.Length *
            MaxLateVisibilityOperationsPerFrame));
        _lateOperationKeys = new int[lateOperationCount];
        _lateOperationGenerations = new ulong[lateOperationCount];
        DescriptorSetLayout[] lateLayouts = new DescriptorSetLayout[lateSetCount];
        lateLayouts.AsSpan().Fill(_descriptorSetLayout);
        fixed (DescriptorSetLayout* layoutPointer = lateLayouts)
        fixed (DescriptorSet* setPointer = _lateDescriptorSets)
        {
            DescriptorSetAllocateInfo allocation = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _lateDescriptorPool,
                DescriptorSetCount = lateSetCount,
                PSetLayouts = layoutPointer,
            };
            result = device.Api.AllocateDescriptorSets(device.Device, ref allocation, setPointer);
        }
        if (result != Result.Success)
        {
            reason = $"Failed to allocate immutable late-visibility descriptor sets ({result}).";
            return false;
        }
        _resources.DescriptorLifetime.RegisterDescriptorSets(
            _lateDescriptorPool,
            _lateDescriptorSets,
            usesUpdateAfterBind: false,
            owner: "AdvancedVisibility.LateSet1DescriptorSet");

        uint nativeSetCount = checked((uint)_states.Length * MaxLateVisibilityViews *
            NativeComputeDescriptorSetsPerView);
        DescriptorPoolSize* nativePoolSizes = stackalloc DescriptorPoolSize[3]
        {
            new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = checked(nativeSetCount * (uint)(storageBindingNumbers.Length +
                    nativeStorageBindingNumbers.Length)),
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = checked(nativeSetCount * (1u +
                    (uint)nativeSampledBindingNumbers.Length)),
            },
            new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = checked(nativeSetCount * (1u +
                    (uint)nativeStorageImageBindingNumbers.Length)),
            },
        };
        DescriptorPoolCreateInfo nativePoolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 3u,
            PPoolSizes = nativePoolSizes,
            MaxSets = nativeSetCount,
        };
        result = device.Api.CreateDescriptorPool(device.Device, ref nativePoolInfo, null,
            out _nativeComputeDescriptorPool);
        if (result != Result.Success)
        {
            reason = $"Failed to create immutable native-compute descriptor pool ({result}).";
            return false;
        }
        _resources.Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.DescriptorPool, _nativeComputeDescriptorPool.Handle),
            "AdvancedVisibility.NativeComputeSet1DescriptorPool", externallyOwned: false);
        _nativeComputeDescriptorSets = new DescriptorSet[nativeSetCount];
        _nativeComputeDescriptorGenerations = new ulong[nativeSetCount];
        _nativeComputeDescriptorSignatures = new VulkanNativeComputeDescriptorSignature[nativeSetCount];
        DescriptorSetLayout[] nativeLayouts = new DescriptorSetLayout[nativeSetCount];
        nativeLayouts.AsSpan().Fill(_descriptorSetLayout);
        fixed (DescriptorSetLayout* layoutPointer = nativeLayouts)
        fixed (DescriptorSet* setPointer = _nativeComputeDescriptorSets)
        {
            DescriptorSetAllocateInfo allocation = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _nativeComputeDescriptorPool,
                DescriptorSetCount = nativeSetCount,
                PSetLayouts = layoutPointer,
            };
            result = device.Api.AllocateDescriptorSets(device.Device, ref allocation, setPointer);
        }
        if (result != Result.Success)
        {
            reason = $"Failed to allocate immutable native-compute descriptor sets ({result}).";
            return false;
        }
        _resources.DescriptorLifetime.RegisterDescriptorSets(
            _nativeComputeDescriptorPool, _nativeComputeDescriptorSets,
            usesUpdateAfterBind: false, owner: "AdvancedVisibility.NativeComputeSet1DescriptorSet");

        _resources.RecordDescriptorTableGeneration();
        reason = "Ready";
        return true;
    }

    private unsafe bool TryUpdateDescriptorSet(
        DescriptorSet descriptorSet,
        in VulkanAdvancedVisibilityResourceState state,
        out string reason)
    {
        if (descriptorSet.Handle == 0 || _device is not { IsOperational: true })
        {
            reason = "The frame-slot advanced-visibility descriptor set is unavailable.";
            return false;
        }
        VulkanVisibilityPreparedVertexSource currentVertices =
            state.Geometry.CurrentVertices;
        VulkanVisibilityPreparedVertexSource previousVertices =
            state.Geometry.PreviousVertices;
        if (!currentVertices.TryValidate(_resources, out reason) ||
            !previousVertices.TryValidate(_resources, out reason))
        {
            return false;
        }

        Span<VulkanFrameDataSlice> slices = stackalloc VulkanFrameDataSlice[]
        {
            state.Candidates, default, state.DeferredIndices,
            state.VisibleIndices, state.Payloads, state.Producers,
            state.RangeIndices, state.RangeOffsets, state.RangeCounts,
            state.Counters, state.IndirectArguments, state.MeshArguments,
            state.MeshPayloads, state.Geometry.StaticVertices,
            default, default, state.Geometry.MeshletDescriptors,
            state.Geometry.MeshletVertexIndices,
            state.Geometry.MeshletTriangleWords,
            state.LateVisibleIndices, state.LateRangeCounts,
            state.LateIndirectArguments, state.LateMeshArguments,
            state.LateMeshPayloads, state.Geometry.DeformationOverlay,
            state.Geometry.Indices, state.Counters
        };
        ReadOnlySpan<uint> storageBindingNumbers = [
            VulkanAdvancedSceneProgramBindingContract.VisibilityCandidatesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityPersistentStateBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityDeferredIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityVisibleIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityPayloadBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityProducersBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityRangeIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityRangeOffsetsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityRangeCountsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityCountersBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityIndexedArgumentsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityMeshArgumentsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityMeshPayloadsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityStaticVerticesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityCurrentVerticesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityPreviousVerticesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityMeshletDescriptorsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityMeshletVertexIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityMeshletTriangleWordsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityLateVisibleIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityLateRangeCountsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityLateIndexedArgumentsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityLateMeshArgumentsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityLateMeshPayloadsBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityDeformationOverlayBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityCanonicalIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.VisibilityReconstructionCountersBinding];
        if (slices.Length != storageBindingNumbers.Length)
        {
            reason = "The advanced-visibility storage binding map is incomplete.";
            return false;
        }

        DescriptorBufferInfo* infos =
            stackalloc DescriptorBufferInfo[slices.Length];
        WriteDescriptorSet* writes =
            stackalloc WriteDescriptorSet[slices.Length];
        for (int index = 0; index < slices.Length; ++index)
        {
            infos[index] = index switch
            {
                1 => new DescriptorBufferInfo
                {
                    Buffer = state.PersistentStateBuffer,
                    Offset = 0u,
                    Range = state.PersistentStateByteLength,
                },
                14 => new DescriptorBufferInfo
                {
                    Buffer = state.Geometry.CurrentVertices.Buffer,
                    Offset = state.Geometry.CurrentVertices.Offset,
                    Range = state.Geometry.CurrentVertices.Length,
                },
                15 => new DescriptorBufferInfo
                {
                    Buffer = state.Geometry.PreviousVertices.Buffer,
                    Offset = state.Geometry.PreviousVertices.Offset,
                    Range = state.Geometry.PreviousVertices.Length,
                },
                _ => new DescriptorBufferInfo
                {
                    Buffer = slices[index].Buffer,
                    Offset = slices[index].Offset,
                    Range = slices[index].Length,
                },
            };
            writes[index] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = storageBindingNumbers[index],
                DescriptorCount = 1u,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = infos + index,
            };
        }
        if (!_resources.DescriptorLifetime.TryUpdateDescriptorSets(
                (uint)slices.Length,
                writes,
                out reason))
        {
            return false;
        }
        _resources.RecordDescriptorTableGeneration();
        reason = "Ready";
        return true;
    }
    /// <summary>
    /// Writes the exact sampled/source and storage/destination mip views used
    /// by one sealed late-visibility dispatch. The caller owns the image-view
    /// lifetime through the matching frame slot and must never update a set
    /// after its command buffer has begun recording.
    /// </summary>
    internal unsafe bool TryUpdateLateDepthPyramidDescriptors(
        DescriptorSet descriptorSet,
        in DescriptorImageInfo sampled,
        in DescriptorImageInfo storage,
        out string reason)
    {
        if (descriptorSet.Handle == 0 || sampled.ImageView.Handle == 0 ||
            sampled.Sampler.Handle == 0 || storage.ImageView.Handle == 0 ||
            _device is not { IsOperational: true })
        {
            reason = "The sealed late-visibility depth-pyramid descriptor closure is incomplete.";
            return false;
        }

        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[2]
        {
            sampled,
            storage,
        };
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2]
        {
            new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = VulkanAdvancedSceneProgramBindingContract
                    .VisibilityDepthPyramidSampledBinding,
                DescriptorCount = 1u,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = images,
            },
            new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = VulkanAdvancedSceneProgramBindingContract
                    .VisibilityDepthPyramidStorageBinding,
                DescriptorCount = 1u,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = images + 1,
            },
        };
        if (!_resources.DescriptorLifetime.TryUpdateDescriptorSets(
                2u,
                writes,
                out reason))
        {
            return false;
        }
        _resources.RecordDescriptorTableGeneration();
        reason = "Ready";
        return true;
    }

    /// <summary>
    /// Seals a unique set-1 descriptor table for one coarse build/test role. Its
    /// storage bindings mirror the exact frame-slot visibility allocation,
    /// while its two image bindings are permanently written for this frame
    /// generation. Late operations have independent bounded table families;
    /// a retry is idempotent only when it presents the identical closure.
    /// </summary>
    internal unsafe bool TryAcquireLateDepthPyramidDescriptorSet(
        in VulkanAdvancedVisibilityResourceState state,
        int lateOperationKey,
        uint viewIndex,
        uint descriptorRole,
        in DescriptorImageInfo sampled,
        in DescriptorImageInfo storage,
        out DescriptorSet descriptorSet,
        out string reason)
    {
        descriptorSet = default;
        if (!state.IsValid || lateOperationKey < 0 || viewIndex >= state.ViewCount ||
            descriptorRole >= LateDescriptorSetsPerView ||
            sampled.ImageView.Handle == 0 || sampled.Sampler.Handle == 0 ||
            storage.ImageView.Handle == 0 || _device is not { IsOperational: true } device)
        {
            reason = "The sealed late-visibility descriptor request is incomplete.";
            return false;
        }

        lock (_gate)
        {
            if (!TryGetLateOperationSlotNoLock(
                    state.FrameSlot,
                    state.FrameGeneration,
                    lateOperationKey,
                    out int operationSlot))
            {
                reason = "The bounded late-visibility descriptor operation slots are exhausted for this frame generation.";
                return false;
            }

            int setsPerOperation = checked(
                (int)(LateDescriptorSetsPerView * MaxLateVisibilityViews));
            int index = checked(operationSlot * setsPerOperation +
                (int)(viewIndex * LateDescriptorSetsPerView + descriptorRole));
            if ((uint)index >= (uint)_lateDescriptorSets.Length ||
                _lateDescriptorSets[index].Handle == 0)
            {
                reason = "The frame-slot late-visibility descriptor table is unavailable.";
                return false;
            }
            VulkanLateDepthPyramidDescriptorSignature signature = new(
                lateOperationKey,
                sampled.ImageView,
                sampled.Sampler,
                sampled.ImageLayout,
                storage.ImageView,
                storage.ImageLayout);
            if (_lateDescriptorGenerations[index] == state.FrameGeneration)
            {
                if (_lateDescriptorSignatures[index] != signature)
                {
                    reason = "The late-visibility descriptor table was already sealed with a different immutable closure.";
                    return false;
                }

                descriptorSet = _lateDescriptorSets[index];
                reason = "Ready";
                return true;
            }

            DescriptorSet candidate = _lateDescriptorSets[index];
            if (!TryUpdateDescriptorSet(candidate, in state, out reason) ||
                !TryUpdateLateDepthPyramidDescriptors(candidate, in sampled, in storage, out reason))
            {
                return false;
            }

            _lateDescriptorGenerations[index] = state.FrameGeneration;
            _lateDescriptorSignatures[index] = signature;
            descriptorSet = candidate;
            reason = "Ready";
            return true;
        }
    }

    /// <summary>
    /// Resolves one stable operation identity to a frame-slot-local descriptor
    /// family. Generations make old owners immediately reusable only after the
    /// owning frame slot itself has advanced.
    /// </summary>
    private bool TryGetLateOperationSlotNoLock(
        int frameSlot,
        ulong frameGeneration,
        int lateOperationKey,
        out int operationSlot)
    {
        operationSlot = -1;
        int baseSlot = checked(frameSlot * (int)MaxLateVisibilityOperationsPerFrame);
        for (int relativeSlot = 0;
             relativeSlot < (int)MaxLateVisibilityOperationsPerFrame;
             ++relativeSlot)
        {
            int candidate = baseSlot + relativeSlot;
            if (_lateOperationGenerations[candidate] == frameGeneration &&
                _lateOperationKeys[candidate] == lateOperationKey)
            {
                operationSlot = candidate;
                return true;
            }
        }

        for (int relativeSlot = 0;
             relativeSlot < (int)MaxLateVisibilityOperationsPerFrame;
             ++relativeSlot)
        {
            int candidate = baseSlot + relativeSlot;
            if (_lateOperationGenerations[candidate] == frameGeneration)
                continue;

            _lateOperationGenerations[candidate] = frameGeneration;
            _lateOperationKeys[candidate] = lateOperationKey;
            operationSlot = candidate;
            return true;
        }

        return false;
    }

    private readonly record struct VulkanLateDepthPyramidDescriptorSignature(
        int OperationKey,
        ImageView SampledImageView,
        Sampler SampledSampler,
        ImageLayout SampledLayout,
        ImageView StorageImageView,
        ImageLayout StorageLayout);

    /// <summary>
    /// Seals an immutable set-1 table for one frozen native-compute closure.
    /// Tables are selected by frame-slot generation and view, so retrying the
    /// same closure is idempotent while a different closure cannot mutate a
    /// descriptor set that an admitted command buffer may already reference.
    /// </summary>
    internal bool TryPrepareNativeComputeDescriptors(
        in VulkanAdvancedVisibilityResourceState state,
        in VulkanAdvancedNativeComputeClosure closure,
        out DescriptorSet descriptorSet,
        out string reason)
    {
        descriptorSet = default;
        if (!state.IsValid || !closure.IsValid || closure.ViewIndex >= state.ViewCount ||
            closure.IdentityDescriptor.Sampler.Handle == 0 ||
            closure.MetadataDescriptor.Sampler.Handle == 0 ||
            closure.DepthDescriptor.Sampler.Handle == 0 || _device is not { IsOperational: true })
        {
            reason = "The immutable native-compute descriptor closure is incomplete.";
            return false;
        }

        lock (_gate)
        {
            int baseIndex = checked((state.FrameSlot * (int)MaxLateVisibilityViews +
                (int)closure.ViewIndex) * (int)NativeComputeDescriptorSetsPerView);
            VulkanNativeComputeDescriptorSignature signature = new(closure);
            int availableIndex = -1;
            for (int relativeIndex = 0;
                 relativeIndex < (int)NativeComputeDescriptorSetsPerView;
                 ++relativeIndex)
            {
                int index = baseIndex + relativeIndex;
                if (_nativeComputeDescriptorGenerations[index] != state.FrameGeneration)
                {
                    if (availableIndex < 0)
                        availableIndex = index;
                    continue;
                }
                if (_nativeComputeDescriptorSignatures[index] != signature)
                    continue;

                descriptorSet = _nativeComputeDescriptorSets[index];
                reason = "Ready";
                return true;
            }

            if (availableIndex < 0 || _nativeComputeDescriptorSets[availableIndex].Handle == 0)
            {
                reason = "The bounded native-compute descriptor table family is exhausted for this frame generation.";
                return false;
            }

            DescriptorSet candidate = _nativeComputeDescriptorSets[availableIndex];
            if (!TryUpdateDescriptorSet(candidate, in state, out reason) ||
                !TryUpdateNativeComputeDescriptors(candidate, in closure, out reason))
            {
                return false;
            }

            _nativeComputeDescriptorGenerations[availableIndex] = state.FrameGeneration;
            _nativeComputeDescriptorSignatures[availableIndex] = signature;
            descriptorSet = candidate;
            reason = "Ready";
            return true;
        }
    }

    private unsafe bool TryUpdateNativeComputeDescriptors(
        DescriptorSet descriptorSet,
        in VulkanAdvancedNativeComputeClosure closure,
        out string reason)
    {
        VulkanFrozenBufferBarrier[] buffers =
        {
            closure.ActiveTiles, closure.KernelTiles, closure.ClassificationCounters,
            closure.DispatchArguments, closure.KernelCounts, closure.FroxelGrid,
            closure.LightIndices, closure.LightingCounters,
        };
        ReadOnlySpan<uint> bufferBindings = [
            VulkanAdvancedSceneProgramBindingContract.NativeActiveTilesBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeKernelTilesBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeCountersBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeDispatchArgumentsBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeKernelCountsBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeFroxelGridBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeLightIndicesBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeLightingCountersBinding];
        DescriptorBufferInfo* bufferInfos = stackalloc DescriptorBufferInfo[buffers.Length];
        const int imageCount = 9;
        DescriptorImageInfo* imageInfos = stackalloc DescriptorImageInfo[imageCount]
        {
            closure.IdentityDescriptor, closure.MetadataDescriptor, closure.DepthDescriptor,
            closure.HdrDescriptor, closure.VelocityDescriptor, closure.ReactiveDescriptor,
            closure.ShadingDiagnosticsDescriptor, closure.AmbientOcclusionStorageDescriptor,
            closure.AmbientOcclusionSampledDescriptor,
        };
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[buffers.Length + imageCount];
        for (int index = 0; index < buffers.Length; ++index)
        {
            VulkanFrozenBufferBarrier buffer = buffers[index];
            if (buffer.NativeBuffer.Handle == 0 || buffer.NativeSize == 0u)
            {
                reason = "A frozen native-compute storage buffer is unavailable.";
                return false;
            }
            bufferInfos[index] = new DescriptorBufferInfo
            {
                Buffer = buffer.NativeBuffer,
                Offset = buffer.NativeOffset,
                Range = buffer.NativeSize,
            };
            writes[index] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet,
                DstBinding = bufferBindings[index], DescriptorCount = 1u,
                DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = bufferInfos + index,
            };
        }

        ReadOnlySpan<uint> imageBindings = [
            VulkanAdvancedSceneProgramBindingContract.NativeIdentityBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeMetadataBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeDepthBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeHdrBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeVelocityBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeReactiveBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeShadingDiagnosticsBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeAmbientOcclusionStorageBinding,
            VulkanAdvancedSceneProgramBindingContract.NativeAmbientOcclusionSampledBinding];
        for (int index = 0; index < imageCount; ++index)
        {
            DescriptorImageInfo image = imageInfos[index];
            if (image.ImageView.Handle == 0 || ((index < 3 || index == 8) && image.Sampler.Handle == 0))
            {
                reason = "A frozen native-compute image descriptor is unavailable.";
                return false;
            }
            writes[buffers.Length + index] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet,
                DstBinding = imageBindings[index], DescriptorCount = 1u,
                DescriptorType = index < 3 || index == 8 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage,
                PImageInfo = imageInfos + index,
            };
        }
        if (!_resources.DescriptorLifetime.TryUpdateDescriptorSets(
                (uint)(buffers.Length + imageCount), writes, out reason))
        {
            return false;
        }
        _resources.RecordDescriptorTableGeneration();
        reason = "Ready";
        return true;
    }

    private readonly record struct VulkanNativeComputeDescriptorSignature(
        VulkanAdvancedNativeComputeClosure Closure);

    /// <summary>
    /// Resolves the depth source and pyramid image from the exact published
    /// resource-planner generation that produced <paramref name="graph"/>.
    /// A generation mismatch is rejected: late visibility must never resolve
    /// its logical names against a newer graph while an older plan records.
    /// </summary>
    internal bool TryCaptureLateTargetClosure(
        VulkanCompiledRenderGraph graph,
        string depthTargetName,
        string pyramidTargetName,
        uint viewCount,
        VulkanAdvancedVisibilityLateClosureStorage storage,
        out VulkanAdvancedVisibilityLateTargetClosure closure,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(storage);
        closure = default;
        if (_resources.BackendObjectContext is not { IsDeviceOperational: true } context ||
            !_resources.Descriptors.TryGetCanonicalImmutableSampler(
                VulkanCanonicalSampler.NearestClamp,
                out Sampler sampler))
        {
            reason = "The Vulkan late-visibility target closure has no live backend context or depth sampler.";
            return false;
        }

        ResourcePlannerRuntimeGeneration generation =
            _resources.PlannerPublications.GetPublishedGeneration();
        if (!ReferenceEquals(generation.State.CompiledRenderGraph, graph))
        {
            reason =
                "The published physical-resource generation does not match the frozen render graph.";
            return false;
        }
        // Render-graph pass edges use the tex:: namespace, while the physical
        // allocator is keyed by the registry's declared texture names.
        if (!generation.State.ResourceAllocator.TryGetPhysicalGroupForResource(
                depthTargetName,
                out VulkanPhysicalImageGroup? depthGroup) ||
            depthGroup is null ||
            !generation.State.ResourceAllocator.TryGetPhysicalGroupForResource(
                pyramidTargetName,
                out VulkanPhysicalImageGroup? pyramidGroup) ||
            pyramidGroup is null)
        {
            reason =
                $"The frozen physical-resource generation has no allocation for depth '{depthTargetName}' or pyramid '{pyramidTargetName}'.";
            return false;
        }
        uint expectedPyramidWidth = DivideRoundUp(depthGroup.ResolvedExtent.Width, 64u);
        uint expectedPyramidHeight = DivideRoundUp(depthGroup.ResolvedExtent.Height, 64u);
        if (!depthGroup.IsAllocated || !pyramidGroup.IsAllocated ||
            depthGroup.Image.Handle == pyramidGroup.Image.Handle ||
            depthGroup.Samples != SampleCountFlags.Count1Bit ||
            pyramidGroup.Samples != SampleCountFlags.Count1Bit ||
            !VulkanBarrierUsageMapper.IsDepthFormat(depthGroup.Format) ||
            pyramidGroup.Format != Format.R32Sfloat ||
            depthGroup.ResolvedExtent.Depth != pyramidGroup.ResolvedExtent.Depth ||
            expectedPyramidWidth != pyramidGroup.ResolvedExtent.Width ||
            expectedPyramidHeight != pyramidGroup.ResolvedExtent.Height ||
            depthGroup.Template.Layers != pyramidGroup.Template.Layers ||
            pyramidGroup.MipLevels < 1u ||
            viewCount == 0u || viewCount > MaxLateVisibilityViews ||
            viewCount > Math.Max(1u, depthGroup.Template.Layers) ||
            viewCount > Math.Max(1u, pyramidGroup.Template.Layers) ||
            (depthGroup.Usage & ImageUsageFlags.SampledBit) == 0 ||
            (pyramidGroup.Usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit)) !=
                (ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit))
        {
            reason = "The frozen late-visibility images do not provide distinct depth/R32F coarse-tile resources with ceil(depthExtent / 64) extent, matching layers, and sampled/storage usage.";
            return false;
        }

        bool captureSucceeded = false;
        try
        {
            const int dispatchCount = 1;
            int descriptorCount = checked((int)viewCount);
            DescriptorImageInfo[] sampled = storage.PyramidSampled;
            DescriptorImageInfo[] storageDescriptors = storage.PyramidStorage;
            DescriptorImageInfo[] lateSampled = storage.LateSampled;
            DescriptorImageInfo[] lateStorage = storage.LateStorage;
            sampled.AsSpan(0, descriptorCount).Clear();
            storageDescriptors.AsSpan(0, descriptorCount).Clear();
            lateSampled.AsSpan(0, checked((int)viewCount)).Clear();
            lateStorage.AsSpan(0, checked((int)viewCount)).Clear();
            for (uint viewIndex = 0u; viewIndex < viewCount; ++viewIndex)
            {
                if (!TryAcquireTrackedView(context, storage, depthGroup, Format.Undefined,
                        ImageAspectFlags.DepthBit, 0u, 1u, viewIndex, out ImageView sourceView))
                {
                    reason = "The frozen depth source image view could not be acquired.";
                    return false;
                }
                if (!TryAcquireTrackedView(context, storage, pyramidGroup, pyramidGroup.Format,
                        ImageAspectFlags.ColorBit, 0u, 1u, viewIndex, out ImageView storageView))
                {
                    reason = "The frozen coarse depth-pyramid storage view could not be acquired.";
                    return false;
                }
                int index = checked((int)viewIndex);
                sampled[index] = new DescriptorImageInfo
                {
                    Sampler = sampler,
                    ImageView = sourceView,
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                };
                storageDescriptors[index] = new DescriptorImageInfo
                {
                    ImageView = storageView,
                    ImageLayout = ImageLayout.General,
                };
            }

            // The late test samples the exact coarse tile level written by
            // the build dispatch. Its sampled view is identical to the
            // already-acquired storage view (mip zero, one layer), so reuse
            // that acquisition rather than taking an unbalanced third view.
            for (uint viewIndex = 0u; viewIndex < viewCount; ++viewIndex)
            {
                lateSampled[viewIndex] = new DescriptorImageInfo
                {
                    Sampler = sampler,
                    ImageView = storageDescriptors[checked((int)viewIndex)].ImageView,
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                };
                lateStorage[viewIndex] = storageDescriptors[
                    checked((int)viewIndex * dispatchCount)];
            }
            closure = new VulkanAdvancedVisibilityLateTargetClosure(
                depthGroup,
                pyramidGroup,
                sampled,
                storageDescriptors,
                lateSampled,
                lateStorage,
                dispatchCount,
                null,
                0,
                viewCount);
            captureSucceeded = true;
            reason = "Ready";
            return true;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return false;
        }
        finally
        {
            if (!captureSucceeded)
                storage.ReleaseAcquiredViews(context.Resources.Images);
        }
    }

    /// <summary>
    /// Captures the complete native input/output closure used by classification,
    /// reconstruction and native opaque shading.  This deliberately resolves
    /// both images and SSBOs from the published graph generation once, before
    /// command recording begins.  It is invalid to recover these handles from
    /// a mutable resource registry during recording because resize can replace
    /// a same-named allocation between admission and submission.
    /// </summary>
    internal bool TryCaptureNativeComputeClosure(
        VulkanRenderGraphPlan graphPlan,
        uint frameSlot,
        uint viewIndex,
        string ambientOcclusionTargetName,
        VulkanAdvancedNativeComputeClosureStorage storage,
        out VulkanAdvancedNativeComputeClosure closure,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(ambientOcclusionTargetName);
        closure = default;
        if (_resources.BackendObjectContext is not { IsDeviceOperational: true } context ||
            !_resources.Descriptors.TryGetCanonicalImmutableSampler(
                VulkanCanonicalSampler.NearestClamp, out Sampler sampler))
        {
            reason = "The advanced native compute closure has no live backend context or canonical sampler.";
            return false;
        }

        ResourcePlannerRuntimeGeneration generation =
            _resources.PlannerPublications.GetPublishedGeneration();
        if (!ReferenceEquals(generation.State.CompiledRenderGraph, graphPlan.CompiledGraph) ||
            graphPlan.Revision == 0u ||
            graphPlan.Revision != generation.State.ResourcePlannerRevision ||
            graphPlan.Revision != generation.State.RenderGraphPlan.Revision)
        {
            reason = "The advanced native compute closure does not match the currently published frozen render-graph revision.";
            return false;
        }

        string activeTiles = AdvancedClassificationResourceNames.ActiveTiles(frameSlot);
        string kernelTiles = AdvancedClassificationResourceNames.KernelTiles(frameSlot);
        string counters = AdvancedClassificationResourceNames.Counters(frameSlot);
        string dispatchArgs = AdvancedClassificationResourceNames.DispatchArgs(frameSlot);
        string kernelCounts = AdvancedClassificationResourceNames.KernelCounts(frameSlot);
        string froxels = AdvancedClusteredLightingResourceNames.FroxelGrid(frameSlot);
        string lightIndices = AdvancedClusteredLightingResourceNames.LightIndexList(frameSlot);
        string lightingCounters = AdvancedClusteredLightingResourceNames.LightingCounters(frameSlot);
        if (!TryGetNativeComputeImageGroups(generation.State.ResourceAllocator,
                out VulkanPhysicalImageGroup? identity, out VulkanPhysicalImageGroup? metadata,
                out VulkanPhysicalImageGroup? depth, out VulkanPhysicalImageGroup? hdr,
                out VulkanPhysicalImageGroup? velocity, out VulkanPhysicalImageGroup? reactive,
                out VulkanPhysicalImageGroup? shadingDiagnostics, out VulkanPhysicalImageGroup? ambientOcclusion,
                ambientOcclusionTargetName) ||
            !TryFindFrozenBuffer(graphPlan.Barriers.BufferBarriers, activeTiles, out VulkanFrozenBufferBarrier active) ||
            !TryFindFrozenBuffer(graphPlan.Barriers.BufferBarriers, kernelTiles, out VulkanFrozenBufferBarrier kernels) ||
            !TryFindFrozenBuffer(graphPlan.Barriers.BufferBarriers, counters, out VulkanFrozenBufferBarrier classificationCounters) ||
            !TryFindFrozenBuffer(graphPlan.Barriers.BufferBarriers, dispatchArgs, out VulkanFrozenBufferBarrier dispatch) ||
            !TryFindFrozenBuffer(graphPlan.Barriers.BufferBarriers, kernelCounts, out VulkanFrozenBufferBarrier counts) ||
            !TryFindFrozenBuffer(graphPlan.Barriers.BufferBarriers, froxels, out VulkanFrozenBufferBarrier froxelGrid) ||
            !TryFindFrozenBuffer(graphPlan.Barriers.BufferBarriers, lightIndices, out VulkanFrozenBufferBarrier indices) ||
            !TryFindFrozenBuffer(graphPlan.Barriers.BufferBarriers, lightingCounters, out VulkanFrozenBufferBarrier lighting))
        {
            reason = "The frozen graph is missing one or more required advanced native compute images or buffers.";
            return false;
        }

        if (!identity!.IsAllocated || !metadata!.IsAllocated || !depth!.IsAllocated ||
            !hdr!.IsAllocated || !velocity!.IsAllocated || !reactive!.IsAllocated ||
            !shadingDiagnostics!.IsAllocated || !ambientOcclusion!.IsAllocated ||
            viewIndex >= Math.Max(1u, identity.Template.Layers) ||
            viewIndex >= Math.Max(1u, metadata.Template.Layers) ||
            viewIndex >= Math.Max(1u, depth.Template.Layers) ||
            viewIndex >= Math.Max(1u, hdr.Template.Layers) ||
            viewIndex >= Math.Max(1u, velocity.Template.Layers) ||
            viewIndex >= Math.Max(1u, reactive.Template.Layers) ||
            viewIndex >= Math.Max(1u, shadingDiagnostics.Template.Layers) ||
            viewIndex >= Math.Max(1u, ambientOcclusion.Template.Layers))
        {
            reason = "The frozen advanced native compute image closure is unallocated or does not contain the requested view layer.";
            return false;
        }
        if (!TryValidateNativeComputeResources(
                in generation.State,
                identity, metadata, depth, hdr, velocity, reactive, shadingDiagnostics,
                ambientOcclusion,
                frameSlot,
                activeTiles, kernelTiles, counters, dispatchArgs, kernelCounts, froxels,
                lightIndices, lightingCounters,
                ref active, ref kernels, ref classificationCounters, ref dispatch, ref counts, ref froxelGrid,
                ref indices, ref lighting,
                out reason))
        {
            return false;
        }

        bool captured = false;
        try
        {
            if (!TryAcquireNativeComputeView(context, storage, identity, ImageAspectFlags.ColorBit, viewIndex, out ImageView identityView) ||
                !TryAcquireNativeComputeView(context, storage, metadata, ImageAspectFlags.ColorBit, viewIndex, out ImageView metadataView) ||
                !TryAcquireNativeComputeView(context, storage, depth, ImageAspectFlags.DepthBit, viewIndex, out ImageView depthView) ||
                !TryAcquireNativeComputeView(context, storage, hdr, ImageAspectFlags.ColorBit, viewIndex, out ImageView hdrView) ||
                !TryAcquireNativeComputeView(context, storage, velocity, ImageAspectFlags.ColorBit, viewIndex, out ImageView velocityView) ||
                !TryAcquireNativeComputeView(context, storage, reactive, ImageAspectFlags.ColorBit, viewIndex, out ImageView reactiveView) ||
                !TryAcquireNativeComputeView(context, storage, shadingDiagnostics, ImageAspectFlags.ColorBit, viewIndex, out ImageView shadingDiagnosticsView) ||
                !TryAcquireNativeComputeView(context, storage, ambientOcclusion, ImageAspectFlags.ColorBit, viewIndex, out ImageView ambientOcclusionView))
            {
                reason = "The frozen advanced native compute image view closure could not be acquired.";
                return false;
            }

            closure = new VulkanAdvancedNativeComputeClosure(
                graphPlan.Revision, identity, metadata, depth, hdr, velocity, reactive, shadingDiagnostics, ambientOcclusion,
                active, kernels, classificationCounters, dispatch, counts, froxelGrid,
                indices, lighting,
                new DescriptorImageInfo { Sampler = sampler, ImageView = identityView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal },
                new DescriptorImageInfo { Sampler = sampler, ImageView = metadataView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal },
                new DescriptorImageInfo { Sampler = sampler, ImageView = depthView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal },
                new DescriptorImageInfo { ImageView = hdrView, ImageLayout = ImageLayout.General },
                new DescriptorImageInfo { ImageView = velocityView, ImageLayout = ImageLayout.General },
                new DescriptorImageInfo { ImageView = reactiveView, ImageLayout = ImageLayout.General },
                new DescriptorImageInfo { ImageView = shadingDiagnosticsView, ImageLayout = ImageLayout.General },
                new DescriptorImageInfo { ImageView = ambientOcclusionView, ImageLayout = ImageLayout.General },
                new DescriptorImageInfo { Sampler = sampler, ImageView = ambientOcclusionView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal },
                viewIndex);
            captured = closure.IsValid;
            reason = captured ? "Ready" : "The advanced native compute closure is incomplete.";
            return captured;
        }
        finally
        {
            if (!captured)
                storage.Release(context.Resources.Images);
        }
    }

    private static bool TryGetNativeComputeImageGroups(
        VulkanResourceAllocator allocator,
        out VulkanPhysicalImageGroup? identity,
        out VulkanPhysicalImageGroup? metadata,
        out VulkanPhysicalImageGroup? depth,
        out VulkanPhysicalImageGroup? hdr,
        out VulkanPhysicalImageGroup? velocity,
        out VulkanPhysicalImageGroup? reactive,
        out VulkanPhysicalImageGroup? shadingDiagnostics,
        out VulkanPhysicalImageGroup? ambientOcclusion,
        string ambientOcclusionTargetName)
    {
        bool found = allocator.TryGetPhysicalGroupForResource(AdvancedVisibilityResourceNames.Identity, out identity);
        found &= allocator.TryGetPhysicalGroupForResource(AdvancedVisibilityResourceNames.Metadata, out metadata);
        found &= allocator.TryGetPhysicalGroupForResource(AdvancedVisibilityResourceNames.DepthStencil, out depth);
        found &= allocator.TryGetPhysicalGroupForResource(DefaultRenderPipeline.HDRSceneTextureName, out hdr);
        found &= allocator.TryGetPhysicalGroupForResource(DefaultRenderPipeline.VelocityTextureName, out velocity);
        found &= allocator.TryGetPhysicalGroupForResource(AdvancedShadingResourceNames.ReactiveMask, out reactive);
        found &= allocator.TryGetPhysicalGroupForResource(AdvancedShadingResourceNames.ShadingDiagnostics, out shadingDiagnostics);
        found &= allocator.TryGetPhysicalGroupForResource(ambientOcclusionTargetName, out ambientOcclusion);
        return found;
    }

    private static bool TryFindFrozenBuffer(
        ReadOnlySpan<VulkanFrozenBufferBarrier> barriers,
        string name,
        out VulkanFrozenBufferBarrier result)
    {
        for (int index = 0; index < barriers.Length; ++index)
            if (string.Equals(barriers[index].LogicalResourceName, name, StringComparison.Ordinal))
            {
                result = barriers[index];
                return result.NativeBuffer.Handle != 0 && result.NativeSize != 0u;
            }
        result = default;
        return false;
    }

    private static bool TryAcquireNativeComputeView(
        VulkanBackendObjectContext context,
        VulkanAdvancedNativeComputeClosureStorage storage,
        VulkanPhysicalImageGroup group,
        ImageAspectFlags aspect,
        uint layer,
        out ImageView view)
    {
        if (!TryAcquireView(context, group, Format.Undefined, aspect, 0u, 1u, layer, out view))
            return false;
        if (storage.TryTrack(context.Resources.Images, view))
            return true;
        _ = context.Resources.Images.ReleaseInternedView(view);
        view = default;
        return false;
    }

    private static bool TryAcquireView(
        VulkanBackendObjectContext context,
        VulkanPhysicalImageGroup group,
        Format format,
        ImageAspectFlags aspect,
        uint baseMipLevel,
        uint levelCount,
        uint arrayLayer,
        out ImageView view)
    {
        ImageViewCreateInfo createInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = group.Image,
            ViewType = ImageViewType.Type2D,
            Format = format == Format.Undefined ? group.Format : format,
            SubresourceRange = new ImageSubresourceRange(
                aspect,
                baseMipLevel,
                levelCount,
                arrayLayer,
                1u),
        };
        return context.Resources.Images.TryAcquireInternedView(
            context,
            in createInfo,
            "AdvancedVisibility.LateDepthPyramid",
            out view);
    }

    private static uint DivideRoundUp(uint value, uint divisor)
        => checked((Math.Max(value, 1u) + divisor - 1u) / divisor);

    private static bool TryAcquireTrackedView(
        VulkanBackendObjectContext context,
        VulkanAdvancedVisibilityLateClosureStorage storage,
        VulkanPhysicalImageGroup group,
        Format format,
        ImageAspectFlags aspect,
        uint baseMipLevel,
        uint levelCount,
        uint arrayLayer,
        out ImageView view)
    {
        if (!TryAcquireView(
                context,
                group,
                format,
                aspect,
                baseMipLevel,
                levelCount,
                arrayLayer,
                out view))
        {
            return false;
        }
        if (storage.TryTrackAcquiredView(view))
            return true;

        _ = context.Resources.Images.ReleaseInternedView(view);
        view = default;
        return false;
    }

    private bool SetUnavailable(string reason, out string result)
    {
        _ready = false;
        _availabilityReason = reason;
        result = reason;
        return false;
    }

    private void RetireNativeStorageNoLock()
    {
        if (_nativeComputeDescriptorPool.Handle != 0)
        {
            _resources.DescriptorLifetime.RetireDescriptorPool(_nativeComputeDescriptorPool);
            _nativeComputeDescriptorPool = default;
        }
        _nativeComputeDescriptorSets = [];
        _nativeComputeDescriptorGenerations = [];
        _nativeComputeDescriptorSignatures = [];
        if (_lateDescriptorPool.Handle != 0)
        {
            _resources.DescriptorLifetime.RetireDescriptorPool(_lateDescriptorPool);
            _lateDescriptorPool = default;
        }
        _lateDescriptorSets = [];
        _lateDescriptorGenerations = [];
        _lateDescriptorSignatures = [];
        _lateOperationKeys = [];
        _lateOperationGenerations = [];
        if (_descriptorPool.Handle != 0)
        {
            _resources.DescriptorLifetime.RetireDescriptorPool(_descriptorPool);
            _descriptorPool = default;
        }
        if (_descriptorSetLayout.Handle != 0 && _device is { } device)
        {
            _resources.DestroyDescriptorSetLayout(device.Api, device.Device,
                _resources.FramebufferRetirementFrameSlot, _descriptorSetLayout,
                "AdvancedVisibility.Set1DescriptorSetLayout");
            _descriptorSetLayout = default;
        }
        if (_persistentStateBuffer.Handle != 0 && _resources.BackendObjectContext is { } context)
        {
            _resources.Buffers.Destroy(
                context,
                _persistentStateBuffer,
                _persistentStateMemory,
                PersistentStateOwner);
            _persistentStateBuffer = default;
            _persistentStateMemory = default;
            _persistentStateTopologyGeneration = 0u;
            _persistentStateContentGeneration = 0u;
        }
        _states.AsSpan().Clear();
        _familySeals.AsSpan().Clear();
        _device = null;
    }
}
