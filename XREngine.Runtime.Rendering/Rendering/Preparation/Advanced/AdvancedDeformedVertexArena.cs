using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Frame-slot current/previous deformation storage with stable logical
/// offsets, explicit boundary growth, and completion-driven generation
/// retirement. Ordinary acquisition performs no allocation.
/// </summary>
public sealed class AdvancedDeformedVertexArena
{
    private readonly AdvancedDeformedVertexArenaOptions _options;
    private readonly AdvancedGpuHandle[] _owners;
    private readonly uint[] _offsets;
    private readonly uint[] _vertexCounts;
    private readonly uint[] _topologyGenerations;
    private readonly uint[] _lodGenerations;
    private readonly ulong[] _lastFrames;
    private readonly byte[] _historyProduced;
    private readonly ulong[] _slotSubmissionValues;
    private readonly byte[][][] _retiredStorage;
    private readonly ulong[] _retiredCompletionValues;
    private byte[][] _storage;
    private AdvancedFrameSlotPair _slots;
    private uint _vertexCapacity;
    private uint _nextVertexOffset;
    private uint _pendingVertexCapacity;
    private uint _allocationFailureCount;
    private uint _capacityGrowthCount;
    private uint _growthDeferralCount;
    private uint _slotReuseDeferralCount;
    private uint _velocityInvalidationCount;
    private int _retiredGenerationCount;
    private ulong _storageGeneration = 1UL;
    private ulong _frameId;
    private bool _frameOpen;

    public AdvancedDeformedVertexArena(
        AdvancedDeformedVertexArenaOptions options)
    {
        ValidateOptions(options);
        _options = options;
        _vertexCapacity = options.InitialVertexCapacity;
        _pendingVertexCapacity = _vertexCapacity;
        _storage = CreateStorage(options.FrameSlotCount, _vertexCapacity);
        _slotSubmissionValues = new ulong[options.FrameSlotCount];

        int ownerTableCapacity = NextPowerOfTwo(
            checked(options.OwnerCapacity * 2));
        _owners = new AdvancedGpuHandle[ownerTableCapacity];
        _offsets = new uint[ownerTableCapacity];
        _vertexCounts = new uint[ownerTableCapacity];
        _topologyGenerations = new uint[ownerTableCapacity];
        _lodGenerations = new uint[ownerTableCapacity];
        _lastFrames = new ulong[ownerTableCapacity];
        _historyProduced = new byte[ownerTableCapacity];

        _retiredStorage = new byte[options.RetiredGenerationCapacity][][];
        _retiredCompletionValues =
            new ulong[options.RetiredGenerationCapacity];
    }

    public uint VertexStride
        => checked((uint)Marshal.SizeOf<AdvancedDeformedVertex>());
    public uint VertexCapacity => _vertexCapacity;
    public ulong StorageGeneration => _storageGeneration;
    public uint CurrentFrameSlot => _slots.Current;
    public uint PreviousFrameSlot => _slots.Previous;
    public bool IsFrameOpen => _frameOpen;

    public bool TryBeginFrame(ulong frameId, ulong completedValue)
    {
        if (_frameOpen)
            throw new InvalidOperationException("The deformation arena frame is already open.");

        DrainRetired(completedValue);
        _slots = AdvancedFrameSlotContract.Resolve(
            frameId,
            checked((uint)_options.FrameSlotCount));
        if (_slotSubmissionValues[_slots.Current] > completedValue)
        {
            _slotReuseDeferralCount++;
            return false;
        }

        if (_pendingVertexCapacity > _vertexCapacity &&
            !TryGrowAtBoundary(_pendingVertexCapacity))
        {
            _growthDeferralCount++;
        }

        _frameId = frameId;
        _frameOpen = true;
        _allocationFailureCount = 0u;
        _velocityInvalidationCount = 0u;
        return true;
    }

    public bool TryAcquireSlice(
        AdvancedGpuHandle owner,
        uint vertexCount,
        uint topologyGeneration,
        uint lodGeneration,
        bool newlyVisible,
        out AdvancedDeformedArenaSlice slice)
    {
        ThrowIfFrameClosed();
        if (!owner.IsValid)
            throw new ArgumentException("A deformation slice requires a stable owner handle.", nameof(owner));
        if (vertexCount == 0u)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));

        int ownerSlot = FindOrAddOwner(owner);
        if (ownerSlot < 0)
        {
            RecordCapacityRequirement(vertexCount);
            _allocationFailureCount++;
            slice = default;
            return false;
        }

        uint previousOffset = _offsets[ownerSlot];
        uint previousCount = _vertexCounts[ownerSlot];
        uint previousTopology = _topologyGenerations[ownerSlot];
        ulong previousFrame = _lastFrames[ownerSlot];
        bool firstUse = previousCount == 0u;
        bool topologyChanged =
            !firstUse && previousTopology != topologyGeneration;
        bool vertexCountChanged =
            !firstUse && previousCount != vertexCount;
        bool needsNewRange =
            firstUse || topologyChanged || vertexCountChanged;

        uint currentOffset = previousOffset;
        if (needsNewRange &&
            !TryAllocateVertices(vertexCount, out currentOffset))
        {
            RecordCapacityRequirement(vertexCount);
            _allocationFailureCount++;
            _velocityInvalidationCount++;
            slice = new AdvancedDeformedArenaSlice(
                owner,
                _slots.Current,
                _slots.Previous,
                previousOffset,
                previousOffset,
                vertexCount,
                VertexStride,
                topologyGeneration,
                lodGeneration,
                EAdvancedVelocityValidityReason.ArenaOverflow);
            return false;
        }

        EAdvancedVelocityValidityReason velocityValidity =
            ResolveVelocityValidity(
                firstUse,
                newlyVisible,
                _frameId,
                previousFrame,
                _historyProduced[ownerSlot] != 0,
                topologyChanged,
                vertexCountChanged);
        if (velocityValidity != EAdvancedVelocityValidityReason.Valid)
            _velocityInvalidationCount++;

        if (firstUse)
            previousOffset = currentOffset;

        _offsets[ownerSlot] = currentOffset;
        _vertexCounts[ownerSlot] = vertexCount;
        _topologyGenerations[ownerSlot] = topologyGeneration;
        _lodGenerations[ownerSlot] = lodGeneration;
        _lastFrames[ownerSlot] = _frameId;

        slice = new AdvancedDeformedArenaSlice(
            owner,
            _slots.Current,
            _slots.Previous,
            currentOffset,
            previousOffset,
            vertexCount,
            VertexStride,
            topologyGeneration,
            lodGeneration,
            velocityValidity);
        return true;
    }

    /// <summary>
    /// Marks the acquired owner as having produced current-frame vertices only
    /// after its deformation job is admitted for execution.
    /// </summary>
    public void ConfirmOwnerHistoryProduced(AdvancedGpuHandle owner)
    {
        int slot = FindOwner(owner);
        if (slot >= 0)
            _historyProduced[slot] = 1;
    }

    /// <summary>Invalidates history for an acquired owner whose job will not execute.</summary>
    public void InvalidateOwnerHistory(AdvancedGpuHandle owner)
    {
        int slot = FindOwner(owner);
        if (slot >= 0)
            _historyProduced[slot] = 0;
    }

    public Span<AdvancedDeformedVertex> GetCurrentVertices(
        in AdvancedDeformedArenaSlice slice)
    {
        ThrowIfFrameClosed();
        ValidateSlice(slice, _slots.Current);
        return MemoryMarshal.Cast<byte, AdvancedDeformedVertex>(
            _storage[_slots.Current].AsSpan())
            .Slice(
                checked((int)slice.CurrentVertexOffset),
                checked((int)slice.VertexCount));
    }

    public ReadOnlySpan<AdvancedDeformedVertex> GetPreviousVertices(
        in AdvancedDeformedArenaSlice slice)
    {
        ThrowIfFrameClosed();
        return MemoryMarshal.Cast<byte, AdvancedDeformedVertex>(
            _storage[slice.PreviousFrameSlot])
            .Slice(
                checked((int)slice.PreviousVertexOffset),
                checked((int)Math.Min(
                    slice.VertexCount,
                    _vertexCapacity - slice.PreviousVertexOffset)));
    }

    public void EndFrame(ulong submissionCompletionValue)
    {
        ThrowIfFrameClosed();
        _slotSubmissionValues[_slots.Current] = submissionCompletionValue;
        _frameOpen = false;
    }

    public AdvancedDeformedVertexArenaTelemetry GetTelemetry()
        => new(
            _storageGeneration,
            _vertexCapacity,
            _nextVertexOffset,
            _pendingVertexCapacity,
            _allocationFailureCount,
            _capacityGrowthCount,
            _growthDeferralCount,
            _slotReuseDeferralCount,
            _velocityInvalidationCount,
            _retiredGenerationCount);

    private bool TryAllocateVertices(uint vertexCount, out uint vertexOffset)
    {
        uint alignedOffset = AlignUp(_nextVertexOffset, 4u);
        ulong end = (ulong)alignedOffset + vertexCount;
        if (end > _vertexCapacity)
        {
            vertexOffset = 0u;
            return false;
        }

        vertexOffset = alignedOffset;
        _nextVertexOffset = checked((uint)end);
        return true;
    }

    private void RecordCapacityRequirement(uint additionalVertices)
    {
        ulong required = (ulong)AlignUp(_nextVertexOffset, 4u) +
            additionalVertices;
        uint clamped = required > uint.MaxValue
            ? uint.MaxValue
            : checked((uint)required);
        _pendingVertexCapacity = Math.Max(
            _pendingVertexCapacity,
            NextPowerOfTwo(clamped));
    }

    private bool TryGrowAtBoundary(uint requestedCapacity)
    {
        int retiredSlot = FindEmptyRetiredSlot();
        if (retiredSlot < 0)
            return false;

        byte[][] replacement = CreateStorage(
            _options.FrameSlotCount,
            requestedCapacity);
        int copyBytes = checked((int)((ulong)_vertexCapacity * VertexStride));
        for (int slot = 0; slot < _storage.Length; slot++)
            Buffer.BlockCopy(_storage[slot], 0, replacement[slot], 0, copyBytes);

        _retiredStorage[retiredSlot] = _storage;
        _retiredCompletionValues[retiredSlot] =
            MaximumSubmissionCompletionValue();
        _retiredGenerationCount++;
        _storage = replacement;
        _vertexCapacity = requestedCapacity;
        _pendingVertexCapacity = requestedCapacity;
        _storageGeneration++;
        _capacityGrowthCount++;
        return true;
    }

    private void DrainRetired(ulong completedValue)
    {
        for (int i = 0; i < _retiredStorage.Length; i++)
        {
            if (_retiredStorage[i] is null ||
                _retiredCompletionValues[i] > completedValue)
            {
                continue;
            }

            _retiredStorage[i] = null!;
            _retiredCompletionValues[i] = 0UL;
            _retiredGenerationCount--;
        }
    }

    private int FindEmptyRetiredSlot()
    {
        for (int i = 0; i < _retiredStorage.Length; i++)
            if (_retiredStorage[i] is null)
                return i;
        return -1;
    }

    private int FindOrAddOwner(AdvancedGpuHandle owner)
    {
        uint mask = checked((uint)_owners.Length - 1u);
        uint start = Hash(owner) & mask;
        for (uint probe = 0u; probe < (uint)_owners.Length; probe++)
        {
            int slot = checked((int)((start + probe) & mask));
            AdvancedGpuHandle existing = _owners[slot];
            if (existing == owner)
                return slot;
            if (existing.IsValid)
                continue;

            _owners[slot] = owner;
            return slot;
        }

        return -1;
    }

    private int FindOwner(AdvancedGpuHandle owner)
    {
        if (!owner.IsValid)
            return -1;
        uint mask = checked((uint)_owners.Length - 1u);
        uint start = Hash(owner) & mask;
        for (uint probe = 0u; probe < (uint)_owners.Length; probe++)
        {
            int slot = checked((int)((start + probe) & mask));
            AdvancedGpuHandle existing = _owners[slot];
            if (existing == owner)
                return slot;
            if (!existing.IsValid)
                return -1;
        }
        return -1;
    }

    private static EAdvancedVelocityValidityReason ResolveVelocityValidity(
        bool firstUse,
        bool newlyVisible,
        ulong currentFrame,
        ulong previousFrame,
        bool historyProduced,
        bool topologyChanged,
        bool vertexCountChanged)
    {
        if (firstUse || newlyVisible)
            return EAdvancedVelocityValidityReason.NewlyVisible;
        if (topologyChanged)
            return EAdvancedVelocityValidityReason.TopologyChanged;
        if (vertexCountChanged)
            return EAdvancedVelocityValidityReason.VertexCountChanged;
        return historyProduced && previousFrame + 1UL == currentFrame
            ? EAdvancedVelocityValidityReason.Valid
            : EAdvancedVelocityValidityReason.FrameGap;
    }

    private void ValidateSlice(
        in AdvancedDeformedArenaSlice slice,
        uint expectedFrameSlot)
    {
        if (!slice.Owner.IsValid ||
            slice.CurrentFrameSlot != expectedFrameSlot ||
            (ulong)slice.CurrentVertexOffset + slice.VertexCount > _vertexCapacity)
        {
            throw new ArgumentException(
                "The deformation slice does not belong to the current arena frame.",
                nameof(slice));
        }
    }

    private ulong MaximumSubmissionCompletionValue()
    {
        ulong maximum = 0UL;
        for (int i = 0; i < _slotSubmissionValues.Length; i++)
            maximum = Math.Max(maximum, _slotSubmissionValues[i]);
        return maximum;
    }

    private static byte[][] CreateStorage(
        int slotCount,
        uint vertexCapacity)
    {
        int byteCapacity = checked((int)(
            (ulong)vertexCapacity *
            (uint)Marshal.SizeOf<AdvancedDeformedVertex>()));
        byte[][] storage = new byte[slotCount][];
        for (int slot = 0; slot < slotCount; slot++)
            storage[slot] = GC.AllocateUninitializedArray<byte>(
                byteCapacity,
                pinned: true);
        return storage;
    }

    private static void ValidateOptions(
        in AdvancedDeformedVertexArenaOptions options)
    {
        if (options.InitialVertexCapacity == 0u)
            throw new ArgumentOutOfRangeException(nameof(options.InitialVertexCapacity));
        if (options.FrameSlotCount < 2)
            throw new ArgumentOutOfRangeException(nameof(options.FrameSlotCount));
        if (options.OwnerCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.OwnerCapacity));
        if (options.RetiredGenerationCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.RetiredGenerationCapacity));
    }

    private static uint AlignUp(uint value, uint alignment)
        => checked((value + alignment - 1u) & ~(alignment - 1u));

    private static uint Hash(AdvancedGpuHandle handle)
    {
        uint value = handle.Index * 0x9E3779B9u;
        value ^= handle.Generation * 0x85EBCA6Bu;
        value ^= value >> 16;
        return value;
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
            result = checked(result << 1);
        return result;
    }

    private static uint NextPowerOfTwo(uint value)
    {
        if (value <= 1u)
            return 1u;
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return checked(value + 1u);
    }

    private void ThrowIfFrameClosed()
    {
        if (!_frameOpen)
            throw new InvalidOperationException("The deformation arena frame is not open.");
    }
}
