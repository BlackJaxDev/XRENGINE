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
/// allocates nothing. The budget holds on every growth: when the frame size grows (a resolution change), the insert first
/// drops the buffers of free slots that are too small, and then the buffers of the frames that were used least recently,
/// until the new buffer fits. A buffer that is two or more times the frame size is made smaller. <see cref="Clear"/>
/// releases every buffer, so a new clip starts with no stale buffers.
/// </para>
/// <para>
/// A pinned frame (<see cref="Insert(in DecodedVideoFrame, bool)"/> with <c>pin</c> true) is never evicted, and its buffer
/// does not count toward the budget: the budget bounds the unpinned frames, and the pinned frames come on top.
/// <see cref="Unpin"/> ends the pin. The residency backend pins the frame of each level that it reports as resident.
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

    /// <summary>
    /// The maximum number of bytes of the pixel buffers of unpinned frames. One frame is always kept, also when it is larger.
    /// </summary>
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
        => Insert(frame, pin: false);

    /// <summary>
    /// Copies a frame into the cache, as <see cref="Insert(in DecodedVideoFrame)"/> does, and pins it if
    /// <paramref name="pin"/> is true. Each pin needs one <see cref="Unpin"/>.
    /// </summary>
    /// <param name="frame">The frame. The cache copies its data.</param>
    /// <param name="pin">True to keep the frame until <see cref="Unpin"/>.</param>
    /// <returns>True if the cache stored the frame. False if every slot holds a pinned frame.</returns>
    public bool Insert(in DecodedVideoFrame frame, bool pin)
    {
        if (frame.PixelFormat != VideoPixelFormat.Rgb24 || frame.PackedData.IsEmpty || frame.Width <= 0 || frame.Height <= 0)
            return false;

        lock (_sync)
        {
            int length = frame.PackedData.Length;
            ReleaseStaleBuffers(length);
            Slot? found = FindSlotFor(frame.PresentationTimestampTicks, length, pin);
            if (found is not { } slot)
                return false;
            EnsureBuffer(slot, length, pin || slot.PinCount > 0);
            if (pin)
                slot.PinCount++;
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

    /// <summary>Removes the frame with exactly this time, unless it is pinned. The slot keeps its buffer.</summary>
    /// <param name="ptsTicks">The frame time in 100 ns ticks.</param>
    /// <returns>True if a frame was removed.</returns>
    public bool Remove(long ptsTicks)
    {
        lock (_sync)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];
                if (!slot.Used || slot.Pts != ptsTicks || slot.PinCount > 0)
                    continue;
                slot.Used = false;
                _count--;
                return true;
            }
            return false;
        }
    }

    /// <summary>Ends one pin of the frame with exactly this time. The frame stays until it is evicted or removed.</summary>
    /// <param name="ptsTicks">The frame time in 100 ns ticks.</param>
    /// <returns>True if the frame had a pin.</returns>
    public bool Unpin(long ptsTicks)
    {
        lock (_sync)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];
                if (slot.Used && slot.Pts == ptsTicks && slot.PinCount > 0)
                {
                    slot.PinCount--;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>True if the frame with exactly this time is in the cache and pinned.</summary>
    /// <param name="ptsTicks">The frame time in 100 ns ticks.</param>
    public bool IsPinned(long ptsTicks)
    {
        lock (_sync)
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].Used && _slots[i].Pts == ptsTicks && _slots[i].PinCount > 0)
                    return true;
            return false;
        }
    }

    /// <summary>Removes every frame and every pin, and releases every pixel buffer.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            for (int i = 0; i < _slots.Length; i++)
                Drop(_slots[i]);
            _count = 0;
            _allocatedBytes = 0;
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

    // The slot of the same time, a free slot that fits in the byte budget, or the unpinned slot that was used least
    // recently. A pinned frame always gets a free slot. Null if no slot can take the frame: every slot holds a pinned
    // frame, or an unpinned frame does not fit in the budget and no unpinned frame can go.
    private Slot? FindSlotFor(long ptsTicks, int length, bool pin)
    {
        Slot? free = null;
        Slot? oldest = null;
        int unpinnedFrames = 0;
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (!slot.Used)
            {
                // A free slot with a buffer that fits comes first.
                if (free is null || (free.Buffer.Length < length && slot.Buffer.Length >= length))
                    free = slot;
                continue;
            }
            if (slot.Pts == ptsTicks)
                return slot;
            if (slot.PinCount > 0)
                continue;
            unpinnedFrames++;
            if (oldest is null || slot.LastUse < oldest.LastUse)
                oldest = slot;
        }

        if (free is not null)
        {
            long growth = Math.Max(0, length - free.Buffer.Length);
            if (pin || growth == 0 || UnpinnedBytes() + growth <= BudgetBytes || unpinnedFrames == 0)
            {
                _count++;
                return free;
            }
        }
        if (oldest is null)
            return null;
        _evictionCount++;
        return oldest;
    }

    // The bytes of the buffers of the slots that hold no pinned frame. The budget bounds these bytes.
    private long UnpinnedBytes()
    {
        long bytes = 0;
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i].PinCount == 0)
                bytes += _slots[i].Buffer.Length;
        return bytes;
    }

    // Free slots whose buffer is too small for frames of this size, or two or more times its size, hold memory that no
    // frame of this size uses.
    private void ReleaseStaleBuffers(int length)
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (!slot.Used && slot.Buffer.Length > 0 && (slot.Buffer.Length < length || slot.Buffer.Length >= 2L * length))
                Drop(slot);
        }
    }

    // Gives the slot a buffer of the frame size. Before a new unpinned buffer, it drops the buffers of other slots until the
    // unpinned total fits in the budget: free slots first, then unpinned frames that were used least recently. A pinned
    // buffer does not count toward the budget, and pinned frames stay.
    private void EnsureBuffer(Slot slot, int length, bool pinned)
    {
        if (slot.Buffer.Length >= length && slot.Buffer.Length < 2L * length)
            return;

        while (!pinned && UnpinnedBytes() - slot.Buffer.Length + length > BudgetBytes && FindVictim(slot) is { } victim)
            Drop(victim);

        _allocatedBytes -= slot.Buffer.Length;
        slot.Buffer = new byte[length];
        _allocatedBytes += length;
        _bufferAllocationCount++;
    }

    private Slot? FindVictim(Slot keep)
    {
        Slot? victim = null;
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            if (ReferenceEquals(slot, keep) || slot.Buffer.Length == 0 || slot.PinCount > 0)
                continue;
            if (!slot.Used)
                return slot;
            if (victim is null || slot.LastUse < victim.LastUse)
                victim = slot;
        }
        return victim;
    }

    // Releases the buffer of a slot and forgets its frame.
    private void Drop(Slot slot)
    {
        if (slot.Used)
        {
            slot.Used = false;
            _count--;
            _evictionCount++;
        }
        slot.PinCount = 0;
        _allocatedBytes -= slot.Buffer.Length;
        slot.Buffer = [];
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
        public int PinCount;
    }
}
