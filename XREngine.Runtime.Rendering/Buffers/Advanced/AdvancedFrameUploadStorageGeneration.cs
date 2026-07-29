namespace XREngine.Rendering;

/// <summary>
/// One immutable-capacity storage generation. Pinned host storage supplies aligned,
/// stable frame-slot regions; backend code owns the corresponding native GPU mapping.
/// </summary>
internal sealed class AdvancedFrameUploadStorageGeneration : IDisposable
{
    private readonly byte[][] _storage;
    private readonly uint[] _cursors;
    private readonly AdvancedFrameUploadDirtyRangeSet _dirtyRanges;
    private uint _currentSlot;
    private bool _disposed;

    public AdvancedFrameUploadStorageGeneration(
        ulong generation,
        uint logicalSlotCount,
        in AdvancedFrameUploadCapacityProfile capacity,
        uint defaultAlignmentBytes,
        int maxDirtyRangesPerStream)
    {
        Generation = generation;
        LogicalSlotCount = logicalSlotCount;
        Capacity = capacity;
        DefaultAlignmentBytes = defaultAlignmentBytes;
        _storage = new byte[
            AdvancedFrameUploadCapacityProfile.StreamCount][];
        _cursors = new uint[AdvancedFrameUploadCapacityProfile.StreamCount];
        for (int i = 0; i < _storage.Length; i++)
        {
            EAdvancedFrameUploadStream stream = (EAdvancedFrameUploadStream)i;
            uint streamCapacity = capacity.Get(stream);
            int byteCount = checked((int)(streamCapacity * logicalSlotCount));
            _storage[i] = GC.AllocateUninitializedArray<byte>(
                byteCount,
                pinned: true);
        }

        _dirtyRanges = new AdvancedFrameUploadDirtyRangeSet(
            maxDirtyRangesPerStream);
    }

    public ulong Generation { get; }
    public uint LogicalSlotCount { get; }
    public uint DefaultAlignmentBytes { get; }
    public AdvancedFrameUploadCapacityProfile Capacity { get; }
    public ulong MappedByteCapacity =>
        Capacity.TotalBytesPerSlot * LogicalSlotCount;
    public int DirtyRangeCount => _dirtyRanges.TotalRangeCount;

    public void BeginFrame(uint slotIndex)
    {
        ThrowIfDisposed();
        _dirtyRanges.Clear();
        Array.Clear(_cursors);
        _currentSlot = slotIndex;
    }

    public bool TryAllocate(
        EAdvancedFrameUploadStream stream,
        uint byteCount,
        uint alignmentBytes,
        out Memory<byte> memory,
        out uint byteOffset)
    {
        ThrowIfDisposed();
        int streamIndex = ValidateStream(stream);
        uint alignment = Math.Max(1u, alignmentBytes);
        uint alignedOffset = AlignUp(_cursors[streamIndex], alignment);
        uint capacity = Capacity.Get(stream);
        if (alignedOffset > capacity ||
            byteCount > capacity - alignedOffset)
        {
            memory = default;
            byteOffset = 0u;
            return false;
        }

        uint absoluteOffset = checked((_currentSlot * capacity) + alignedOffset);
        memory = _storage[streamIndex].AsMemory(
            checked((int)absoluteOffset),
            checked((int)byteCount));
        byteOffset = alignedOffset;
        _cursors[streamIndex] = alignedOffset + byteCount;
        _dirtyRanges.Include(stream, alignedOffset, byteCount);
        return true;
    }

    public uint GetCurrentCursor(EAdvancedFrameUploadStream stream)
        => _cursors[ValidateStream(stream)];

    public int CopyDirtyRangesTo(
        Span<AdvancedUploadCopyRange> destination,
        int destinationOffset,
        uint frameSlot,
        bool isOverflow)
        => _dirtyRanges.CopyTo(
            destination,
            destinationOffset,
            Generation,
            frameSlot,
            isOverflow);

    public void EndFrame()
    {
        ThrowIfDisposed();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }

    private static int ValidateStream(EAdvancedFrameUploadStream stream)
    {
        int streamIndex = (int)stream;
        if ((uint)streamIndex >= AdvancedFrameUploadCapacityProfile.StreamCount)
            throw new ArgumentOutOfRangeException(nameof(stream), stream, null);
        return streamIndex;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AdvancedFrameUploadStorageGeneration));
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        if (alignment <= 1u)
            return value;

        ulong aligned = ((ulong)value + alignment - 1u) / alignment * alignment;
        if (aligned > uint.MaxValue)
            throw new InvalidOperationException("Advanced upload alignment exceeds the supported 32-bit buffer range.");
        return (uint)aligned;
    }
}
