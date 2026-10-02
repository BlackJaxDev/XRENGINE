using System.Net;

namespace XREngine.Networking;

/// <summary>
/// Bounded hand-off queue for high-rate packets that are received on the transport loop and applied
/// on the simulation thread. Packets are copied into preallocated slabs, so enqueue and dequeue
/// allocate nothing. When every slab is in use the newest packet is dropped and counted.
/// </summary>
public sealed class DeferredRealtimePacketQueue
{
    private readonly object _sync = new();
    private readonly PersistentReceiveSlabPool _pool;
    private readonly PersistentReceiveSlab?[] _slabs;
    private readonly IPEndPoint?[] _senders;
    private readonly long[] _tags;
    private int _head;
    private int _count;
    private long _dropCount;

    public DeferredRealtimePacketQueue(int capacity, int slabSizeBytes)
    {
        _pool = new PersistentReceiveSlabPool(capacity, slabSizeBytes);
        _slabs = new PersistentReceiveSlab?[capacity];
        _senders = new IPEndPoint?[capacity];
        _tags = new long[capacity];
    }

    public int Capacity => _slabs.Length;
    public int SlabSizeBytes => _pool.SlabSizeBytes;

    public int Count
    {
        get { lock (_sync) return _count; }
    }

    /// <summary>Packets dropped because no slab was free or the packet exceeded the slab size.</summary>
    public long DropCount => Interlocked.Read(ref _dropCount);

    /// <summary>
    /// Copies <paramref name="packet"/> into a slab and queues it. <paramref name="tag"/> carries a
    /// caller-defined generation so stale packets can be discarded when they are dequeued.
    /// </summary>
    public bool TryEnqueue(ReadOnlySpan<byte> packet, IPEndPoint? sender, long tag)
    {
        lock (_sync)
        {
            if (packet.Length > _pool.SlabSizeBytes || !_pool.TryRent(out PersistentReceiveSlab slab))
            {
                _dropCount++;
                return false;
            }

            packet.CopyTo(slab.WritableSpan);
            slab.Length = packet.Length;
            int tail = (_head + _count) % _slabs.Length;
            _slabs[tail] = slab;
            _senders[tail] = sender;
            _tags[tail] = tag;
            _count++;
            return true;
        }
    }

    /// <summary>Removes the oldest packet. The caller must pass the slab to <see cref="Return"/> when done.</summary>
    public bool TryDequeue(out PersistentReceiveSlab slab, out IPEndPoint? sender, out long tag)
    {
        lock (_sync)
        {
            if (_count == 0)
            {
                slab = null!;
                sender = null;
                tag = 0;
                return false;
            }

            slab = _slabs[_head]!;
            sender = _senders[_head];
            tag = _tags[_head];
            _slabs[_head] = null;
            _senders[_head] = null;
            _head++;
            if (_head >= _slabs.Length)
                _head = 0;
            _count--;
            return true;
        }
    }

    public void Return(PersistentReceiveSlab slab)
    {
        lock (_sync)
            _pool.Return(slab);
    }

    /// <summary>Discards every queued packet and returns its slab.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            while (_count > 0)
            {
                PersistentReceiveSlab? slab = _slabs[_head];
                if (slab is not null)
                    _pool.Return(slab);
                _slabs[_head] = null;
                _senders[_head] = null;
                _head++;
                if (_head >= _slabs.Length)
                    _head = 0;
                _count--;
            }

            _head = 0;
        }
    }
}
