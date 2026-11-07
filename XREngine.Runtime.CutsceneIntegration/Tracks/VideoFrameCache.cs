using XREngine.Rendering.VideoStreaming;

namespace XREngine.Cutscenes;

/// <summary>
/// A bounded cache of decoded RGB24 video frames of one source, keyed by the frame time. A backward seek or a return to a
/// frame that the cache holds shows the frame with no decode.
/// </summary>
/// <remarks>
/// <para>
/// The cache holds at most <see cref="Capacity"/> frames and at most <see cref="BudgetBytes"/> bytes of pixel buffers, and
/// always one frame. Slot buffers are made only when frames come. When the frame count or the byte budget is full, an
/// insert replaces the frame that was used least recently and uses its buffer again, so a full cache in steady playback
/// allocates nothing. A slot allocates a new buffer only when the frame size grows.
/// </para>
/// <para>
/// A frame that a lookup returns points into the buffer of its slot. It stays valid until the next <see cref="Insert"/>,
/// <see cref="Remove"/>, or <see cref="Clear"/> on this cache, so copy it before then. All members take one lock, so a
/// background loader can fill a cache that the update thread reads.
/// </para>
/// </remarks>
public sealed class VideoFrameCache
{
    /// <summary>The default number of frames: one second at 24 frames per second.</summary>
    public const int DefaultCapacity = 24;

    /// <summary>The default byte budget of the pixel buffers: 64 MiB, about ten 1080p frames or two 4K frames.</summary>
    public const long DefaultBudgetBytes = 64L * 1024 * 1024;

    private readonly Lock _sync = new();
    private readonly Slot[] _slots;
    private int _count;
    private long _useClock;
    private long _hitCount;
    private long _missCount;
    private long _insertCount;
    private long _evictionCount;
    private long _bufferAllocationCount;
    private long _allocatedBytes;

    /// <summary>Creates a cache.</summary>
    /// <param name="capacity">The maximum number of frames. It must be at least one.</param>
    /// <param name="budgetBytes">The maximum number of bytes of pixel buffers. It must be at least one. One frame is always kept.</param>
    public VideoFrameCache(int capacity = DefaultCapacity, long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetBytes, 1L);
        BudgetBytes = budgetBytes;
        _slots = new Slot[capacity];
        for (int i = 0; i < capacity; i++)
            _slots[i] = new Slot();
    }

    /// <summary>The maximum number of frames.</summary>
    public int Capacity => _slots.Length;

    /// <summary>The maximum number of bytes of pixel buffers. One frame is always kept, also when it is larger.</summary>
    public long BudgetBytes { get; }

    /// <summary>The number of bytes of the pixel buffers that the slots hold now.</summary>
    public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);

    /// <summary>The number of frames in the cache now.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _count;
        }
    }

    /// <summary>The number of lookups that found a frame.</summary>
    public long HitCount => Interlocked.Read(ref _hitCount);

    /// <summary>The number of lookups that found no frame.</summary>
    public long MissCount => Interlocked.Read(ref _missCount);

    /// <summary>The number of frames that <see cref="Insert"/> stored.</summary>
    public long InsertCount => Interlocked.Read(ref _insertCount);

    /// <summary>The number of frames that an insert into a full cache replaced.</summary>
    public long EvictionCount => Interlocked.Read(ref _evictionCount);

    /// <summary>The number of pixel buffers that the slots allocated. It stops at the capacity when the frame size does not grow.</summary>
    public long BufferAllocationCount => Interlocked.Read(ref _bufferAllocationCount);

    /// <summary>
    /// Copies a frame into the cache. A frame with the same time replaces the frame that is there. A full cache replaces
    /// the frame that was used least recently. Frames of another pixel format or with no data are not stored.
    /// </summary>
    /// <param name="frame">The frame. The cache copies its data, so the caller can use its buffer again.</param>
    /// <returns>True if the cache stored the frame.</returns>
    public bool Insert(in DecodedVideoFrame frame)
    {
        if (frame.PixelFormat != VideoPixelFormat.Rgb24 || frame.PackedData.IsEmpty || frame.Width <= 0 || frame.Height <= 0)
            return false;

        lock (_sync)
        {
            int length = frame.PackedData.Length;
            Slot slot = FindSlotFor(frame.PresentationTimestampTicks, length);
            EnsureBuffer(slot, length);
            frame.PackedData.Span.CopyTo(slot.Buffer.AsSpan(0, length));
            slot.Used = true;
            slot.Length = length;
            slot.Width = frame.Width;
            slot.Height = frame.Height;
            slot.Pts = frame.PresentationTimestampTicks;
            slot.Duration = frame.DurationTicks;
            slot.LastUse = ++_useClock;
            _insertCount++;
            return true;
        }
    }

    /// <summary>
    /// Finds the frame that shows at a time: its time is at or before <paramref name="timeTicks"/>, and the time is before
    /// the end of the frame. A frame with no known duration covers only its own time.
    /// </summary>
    /// <param name="timeTicks">The stream time in 100 ns ticks.</param>
    /// <param name="frame">The frame. It points into the cache; see the remarks of the type.</param>
    public bool TryGetFrame(long timeTicks, out DecodedVideoFrame frame)
    {
        lock (_sync)
        {
            if (TryFindCovering(timeTicks, out Slot? slot))
            {
                _hitCount++;
                slot!.LastUse = ++_useClock;
                frame = View(slot);
                return true;
            }
            _missCount++;
            frame = default;
            return false;
        }
    }

    /// <summary>Finds the frame with exactly this time.</summary>
    /// <param name="ptsTicks">The frame time in 100 ns ticks.</param>
    /// <param name="frame">The frame. It points into the cache; see the remarks of the type.</param>
    public bool TryGetFrameAt(long ptsTicks, out DecodedVideoFrame frame)
    {
        lock (_sync)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];
                if (slot.Used && slot.Pts == ptsTicks)
                {
                    slot.LastUse = ++_useClock;
                    frame = View(slot);
                    return true;
                }
            }
            frame = default;
            return false;
        }
    }

    /// <summary>
    /// Copies the frame that shows at a time into another cache, with no decode. The two caches take their locks one after
    /// the other, never together.
    /// </summary>
    /// <param name="timeTicks">The stream time in 100 ns ticks.</param>
    /// <param name="target">The cache that receives the copy.</param>
    /// <param name="scratch">A buffer that holds the copy between the two locks. It grows when it is too small.</param>
    /// <returns>True if this cache had a frame for the time.</returns>
    public bool TryCopyFrameTo(long timeTicks, VideoFrameCache target, ref byte[]? scratch)
    {
        ArgumentNullException.ThrowIfNull(target);
        int width;
        int height;
        long pts;
        long duration;
        int length;
        lock (_sync)
        {
            if (!TryFindCovering(timeTicks, out Slot? slot))
                return false;
            slot!.LastUse = ++_useClock;
            length = slot.Length;
            if (scratch is null || scratch.Length < length)
                scratch = new byte[length];
            slot.Buffer.AsSpan(0, length).CopyTo(scratch);
            width = slot.Width;
            height = slot.Height;
            pts = slot.Pts;
            duration = slot.Duration;
        }

        DecodedVideoFrame copy = new(width, height, pts, duration, VideoPixelFormat.Rgb24, new ReadOnlyMemory<byte>(scratch, 0, length));
        return target.Insert(copy);
    }

    /// <summary>True if the cache holds the frame with exactly this time.</summary>
    /// <param name="ptsTicks">The frame time in 100 ns ticks.</param>
    public bool Contains(long ptsTicks)
    {
        lock (_sync)
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].Used && _slots[i].Pts == ptsTicks)
                    return true;
            return false;
        }
    }

    /// <summary>Removes the frame with exactly this time. The slot keeps its buffer.</summary>
    /// <param name="ptsTicks">The frame time in 100 ns ticks.</param>
    /// <returns>True if a frame was removed.</returns>
    public bool Remove(long ptsTicks)
    {
        lock (_sync)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];
                if (!slot.Used || slot.Pts != ptsTicks)
                    continue;
                slot.Used = false;
                _count--;
                return true;
            }
            return false;
        }
    }

    /// <summary>Removes every frame. The slots keep their buffers.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            for (int i = 0; i < _slots.Length; i++)
                _slots[i].Used = false;
            _count = 0;
        }
    }

    private bool TryFindCovering(long timeTicks, out Slot? found)
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (slot.Used && slot.Pts <= timeTicks && (timeTicks < slot.Pts + slot.Duration || timeTicks == slot.Pts))
            {
                found = slot;
                return true;
            }
        }
        found = null;
        return false;
    }

    // The slot of the same time, a free slot that fits in the byte budget, or the slot that was used least recently.
    private Slot FindSlotFor(long ptsTicks, int length)
    {
        Slot? free = null;
        Slot? oldest = null;
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (!slot.Used)
            {
                free ??= slot;
                continue;
            }
            if (slot.Pts == ptsTicks)
                return slot;
            if (oldest is null || slot.LastUse < oldest.LastUse)
                oldest = slot;
        }

        // A free slot needs a new buffer unless it has one. It takes one only inside the budget, or when the cache is empty.
        if (free is not null && (free.Buffer.Length >= length || oldest is null || _allocatedBytes - free.Buffer.Length + length <= BudgetBytes))
        {
            _count++;
            return free;
        }
        _evictionCount++;
        return oldest!;
    }

    private void EnsureBuffer(Slot slot, int length)
    {
        if (slot.Buffer.Length >= length)
            return;
        _allocatedBytes -= slot.Buffer.Length;
        slot.Buffer = new byte[length];
        _allocatedBytes += length;
        _bufferAllocationCount++;
    }

    private static DecodedVideoFrame View(Slot slot)
        => new(slot.Width, slot.Height, slot.Pts, slot.Duration, VideoPixelFormat.Rgb24, new ReadOnlyMemory<byte>(slot.Buffer, 0, slot.Length));

    /// <summary>One frame of the cache and its reusable pixel buffer.</summary>
    private sealed class Slot
    {
        public byte[] Buffer = [];
        public bool Used;
        public int Length;
        public int Width;
        public int Height;
        public long Pts;
        public long Duration;
        public long LastUse;
    }
}
