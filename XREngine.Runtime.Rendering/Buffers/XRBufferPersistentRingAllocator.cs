namespace XREngine.Rendering;

public readonly ref struct XRBufferPersistentRingAllocation
{
    internal XRBufferPersistentRingAllocation(
        Span<byte> span,
        int slotIndex,
        uint byteOffset,
        uint byteCount,
        uint alignmentBytes,
        XRDataBuffer? backingBuffer)
    {
        Span = span;
        SlotIndex = slotIndex;
        ByteOffset = byteOffset;
        ByteCount = byteCount;
        AlignmentBytes = alignmentBytes;
        BackingBuffer = backingBuffer;
    }

    public Span<byte> Span { get; }
    public int SlotIndex { get; }
    public uint ByteOffset { get; }
    public uint ByteCount { get; }
    public uint AlignmentBytes { get; }
    public XRDataBuffer? BackingBuffer { get; }
}

/// <summary>
/// Backend-neutral ring allocator metadata for CPU-to-GPU dynamic data. Backends that expose
/// persistent mapped memory can provide the mapped byte span; compatibility users can pair this
/// with an upload/staging copy while preserving the same slot/fence contract.
/// </summary>
public sealed class XRBufferPersistentRingAllocator : IDisposable
{
    private readonly byte[] _fallbackStorage;
    private readonly XRGpuFence?[] _slotFences;
    private readonly uint _slotByteCapacity;
    private readonly uint _defaultAlignmentBytes;
    private readonly XRDataBuffer? _backingBuffer;
    private int _slotIndex;
    private uint _cursor;
    private bool _disposed;

    public XRBufferPersistentRingAllocator(
        uint slotByteCapacity,
        int slotCount = 3,
        uint defaultAlignmentBytes = 16u,
        XRDataBuffer? backingBuffer = null)
    {
        if (slotByteCapacity == 0u)
            throw new ArgumentOutOfRangeException(nameof(slotByteCapacity));
        if (slotCount < 3)
            throw new ArgumentOutOfRangeException(nameof(slotCount), "Persistent rings must keep at least three slots/frames in flight.");

        _slotByteCapacity = slotByteCapacity;
        _defaultAlignmentBytes = Math.Max(1u, defaultAlignmentBytes);
        _backingBuffer = backingBuffer;
        SlotCount = slotCount;
        _fallbackStorage = GC.AllocateUninitializedArray<byte>(checked((int)(slotByteCapacity * slotCount)));
        _slotFences = new XRGpuFence?[slotCount];
    }

    public int SlotCount { get; }
    public int CurrentSlotIndex => _slotIndex;
    public uint SlotByteCapacity => _slotByteCapacity;
    public uint CurrentSlotCursor => _cursor;

    public void BeginFrame(int slotIndex)
    {
        ThrowIfDisposed();

        if ((uint)slotIndex >= (uint)SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));

        XRGpuFence? fence = _slotFences[slotIndex];
        if (fence is not null)
        {
            EGpuFenceStatus status = fence.Poll();
            if (status == EGpuFenceStatus.Pending)
            {
                XRBufferWriteTelemetry.RecordPersistentRingFenceWait();
                throw new InvalidOperationException($"Persistent buffer ring slot {slotIndex} is still in use by the GPU.");
            }

            fence.Dispose();
            _slotFences[slotIndex] = null;
        }

        _slotIndex = slotIndex;
        _cursor = 0u;
    }

    public void EndFrame(XRGpuFence? fence)
    {
        ThrowIfDisposed();

        _slotFences[_slotIndex]?.Dispose();
        _slotFences[_slotIndex] = fence;
    }

    public bool TryAllocate(uint byteCount, out XRBufferPersistentRingAllocation allocation)
        => TryAllocate(byteCount, _defaultAlignmentBytes, out allocation);

    public bool TryAllocate(uint byteCount, uint alignmentBytes, out XRBufferPersistentRingAllocation allocation)
    {
        ThrowIfDisposed();
        allocation = default;

        if (byteCount == 0u)
            return true;

        uint alignment = Math.Max(1u, alignmentBytes);
        uint alignedOffset = AlignUp(_cursor, alignment);
        if (alignedOffset > _slotByteCapacity || byteCount > _slotByteCapacity - alignedOffset)
        {
            XRBufferWriteTelemetry.RecordPersistentRingExhaustion();
            return false;
        }

        uint absoluteOffset = checked((uint)(_slotIndex * _slotByteCapacity + alignedOffset));
        Span<byte> span = _fallbackStorage.AsSpan(checked((int)absoluteOffset), checked((int)byteCount));
        allocation = new XRBufferPersistentRingAllocation(
            span,
            _slotIndex,
            alignedOffset,
            byteCount,
            alignment,
            _backingBuffer);
        _cursor = alignedOffset + byteCount;
        XRBufferWriteTelemetry.RecordPersistentRingAllocation(byteCount);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        for (int i = 0; i < _slotFences.Length; i++)
        {
            _slotFences[i]?.Dispose();
            _slotFences[i] = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(XRBufferPersistentRingAllocator));
    }

    private static uint AlignUp(uint value, uint alignment)
        => alignment <= 1u ? value : (value + alignment - 1u) / alignment * alignment;
}
