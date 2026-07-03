using System;

namespace XREngine.Networking;

public enum RealtimePacketDropPolicy
{
    DropNewest,
    DropOldest,
}

public readonly record struct RealtimePacketSendRingStats(
    int Capacity,
    int SlotSizeBytes,
    int Count,
    long EnqueueCount,
    long DequeueCount,
    long DropCount,
    long OversizeDropCount);

public sealed class RealtimePacketSendRing
{
    private readonly byte[][] _slots;
    private readonly int[] _lengths;
    private int _head;
    private int _count;
    private long _enqueueCount;
    private long _dequeueCount;
    private long _dropCount;
    private long _oversizeDropCount;

    public RealtimePacketSendRing(int capacity, int slotSizeBytes)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (slotSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotSizeBytes));

        _slots = new byte[capacity][];
        _lengths = new int[capacity];
        for (int i = 0; i < _slots.Length; i++)
            _slots[i] = new byte[slotSizeBytes];
    }

    public int Capacity => _slots.Length;
    public int SlotSizeBytes => _slots[0].Length;
    public int Count => _count;

    public RealtimePacketSendRingStats Stats => new(
        Capacity,
        SlotSizeBytes,
        _count,
        _enqueueCount,
        _dequeueCount,
        _dropCount,
        _oversizeDropCount);

    public bool TryEnqueue(ReadOnlySpan<byte> payload, RealtimePacketDropPolicy dropPolicy = RealtimePacketDropPolicy.DropNewest)
    {
        if (payload.Length > SlotSizeBytes)
        {
            _oversizeDropCount++;
            return false;
        }

        if (_count == _slots.Length)
        {
            _dropCount++;
            if (dropPolicy == RealtimePacketDropPolicy.DropNewest)
                return false;

            _head++;
            if (_head >= _slots.Length)
                _head = 0;
            _count--;
        }

        int tail = (_head + _count) % _slots.Length;
        payload.CopyTo(_slots[tail]);
        _lengths[tail] = payload.Length;
        _count++;
        _enqueueCount++;
        return true;
    }

    public bool TryPeek(out ReadOnlyMemory<byte> payload)
    {
        if (_count == 0)
        {
            payload = default;
            return false;
        }

        payload = _slots[_head].AsMemory(0, _lengths[_head]);
        return true;
    }

    public bool TryDequeue(out ReadOnlyMemory<byte> payload)
    {
        if (!TryPeek(out payload))
            return false;

        _lengths[_head] = 0;
        _head++;
        if (_head >= _slots.Length)
            _head = 0;

        _count--;
        _dequeueCount++;
        return true;
    }

    public void ClearFast()
    {
        _head = 0;
        _count = 0;
        Array.Clear(_lengths);
    }
}

public readonly record struct PersistentReceiveSlabPoolStats(
    int Capacity,
    int SlabSizeBytes,
    int AvailableCount,
    long RentCount,
    long ReturnCount,
    long RentMissCount,
    long DoubleReturnCount);

public sealed class PersistentReceiveSlab
{
    internal PersistentReceiveSlab(int poolIndex, int sizeBytes)
    {
        PoolIndex = poolIndex;
        Buffer = new byte[sizeBytes];
    }

    internal int PoolIndex { get; }
    internal bool IsRented { get; set; }
    public byte[] Buffer { get; }
    public int Length { get; set; }
    public Span<byte> Span => Buffer.AsSpan(0, Length);
    public Memory<byte> Memory => Buffer.AsMemory(0, Length);
    public Span<byte> WritableSpan => Buffer;
}

public sealed class PersistentReceiveSlabPool
{
    private readonly PersistentReceiveSlab[] _slabs;
    private readonly int[] _free;
    private int _freeCount;
    private long _rentCount;
    private long _returnCount;
    private long _rentMissCount;
    private long _doubleReturnCount;

    public PersistentReceiveSlabPool(int capacity, int slabSizeBytes)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (slabSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(slabSizeBytes));

        _slabs = new PersistentReceiveSlab[capacity];
        _free = new int[capacity];
        for (int i = 0; i < capacity; i++)
        {
            _slabs[i] = new PersistentReceiveSlab(i, slabSizeBytes);
            _free[i] = capacity - 1 - i;
        }

        _freeCount = capacity;
    }

    public int Capacity => _slabs.Length;
    public int SlabSizeBytes => _slabs[0].Buffer.Length;
    public int AvailableCount => _freeCount;

    public PersistentReceiveSlabPoolStats Stats => new(
        Capacity,
        SlabSizeBytes,
        _freeCount,
        _rentCount,
        _returnCount,
        _rentMissCount,
        _doubleReturnCount);

    public bool TryRent(out PersistentReceiveSlab slab)
    {
        if (_freeCount == 0)
        {
            _rentMissCount++;
            slab = null!;
            return false;
        }

        int index = _free[--_freeCount];
        slab = _slabs[index];
        slab.IsRented = true;
        slab.Length = 0;
        _rentCount++;
        return true;
    }

    public void Return(PersistentReceiveSlab slab)
    {
        ArgumentNullException.ThrowIfNull(slab);
        if ((uint)slab.PoolIndex >= (uint)_slabs.Length || !ReferenceEquals(_slabs[slab.PoolIndex], slab))
            throw new InvalidOperationException("Receive slab does not belong to this pool.");

        if (!slab.IsRented)
        {
            _doubleReturnCount++;
            return;
        }

        slab.IsRented = false;
        slab.Length = 0;
        _free[_freeCount++] = slab.PoolIndex;
        _returnCount++;
    }
}
