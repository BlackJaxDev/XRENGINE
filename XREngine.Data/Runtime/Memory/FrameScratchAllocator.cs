using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Data.Runtime.Memory;

public readonly record struct FrameScratchStatistics(
    long CapacityBytes,
    long UsedBytes,
    long HighWaterBytes,
    long OverflowBytes,
    long OverflowCount,
    long FallbackAllocationCount);

public unsafe sealed class FrameScratchAllocator : IDisposable
{
    private const byte ResetPoisonByte = 0xCD;
    private readonly nuint _capacity;
    private byte* _base;
    private nuint _offset;
    private nuint _highWater;
    private long _overflowBytes;
    private long _overflowCount;
    private long _fallbackAllocationCount;
    private int _generation;
    private bool _disposed;
    private OverflowBlock? _overflowHead;

    public FrameScratchAllocator(nuint capacityBytes)
    {
        if (capacityBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));

        _capacity = capacityBytes;
        _base = (byte*)NativeMemory.AlignedAlloc(capacityBytes, 64);
        if (_base is null)
            throw new OutOfMemoryException("Unable to allocate frame scratch memory.");

#if DEBUG
        new Span<byte>(_base, checked((int)capacityBytes)).Fill(ResetPoisonByte);
#endif
    }

    ~FrameScratchAllocator()
        => Dispose();

    public int Generation => _generation;

    public FrameScratchStatistics Statistics => new(
        checked((long)_capacity),
        checked((long)_offset),
        checked((long)_highWater),
        Interlocked.Read(ref _overflowBytes),
        Interlocked.Read(ref _overflowCount),
        Interlocked.Read(ref _fallbackAllocationCount));

    public Span<T> Allocate<T>(int count, int alignmentBytes = 16)
        where T : unmanaged
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (count == 0)
            return Span<T>.Empty;

        ThrowIfDisposed();
        nuint byteCount = checked((nuint)count * (nuint)sizeof(T));
        byte* ptr = AllocateBytes(byteCount, NormalizeAlignment(alignmentBytes));
        return new Span<T>(ptr, count);
    }

    public FrameScratchLease<T> Rent<T>(int count, int alignmentBytes = 16)
        where T : unmanaged
        => new(this, Generation, Allocate<T>(count, alignmentBytes));

    public void Reset()
    {
        ThrowIfDisposed();

#if DEBUG
        if (_offset > 0)
            new Span<byte>(_base, checked((int)_offset)).Fill(ResetPoisonByte);
#endif

        _offset = 0;
        FreeOverflowBlocks();
        unchecked
        {
            _generation++;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        FreeOverflowBlocks();

        byte* ptr = _base;
        _base = null;
        if (ptr is not null)
            NativeMemory.AlignedFree(ptr);

        GC.SuppressFinalize(this);
    }

    internal void ThrowIfGenerationMismatch(int generation)
    {
        ThrowIfDisposed();
        if (generation != _generation)
            throw new InvalidOperationException("Frame scratch lease was used after the allocator reset.");
    }

    private byte* AllocateBytes(nuint byteCount, nuint alignment)
    {
        nuint alignedOffset = AlignUp(_offset, alignment);
        nuint nextOffset = checked(alignedOffset + byteCount);
        if (nextOffset <= _capacity)
        {
            _offset = nextOffset;
            if (nextOffset > _highWater)
                _highWater = nextOffset;

            return _base + alignedOffset;
        }

        Interlocked.Increment(ref _overflowCount);
        Interlocked.Increment(ref _fallbackAllocationCount);
        Interlocked.Add(ref _overflowBytes, checked((long)byteCount));
        return AllocateOverflow(byteCount, alignment);
    }

    private byte* AllocateOverflow(nuint byteCount, nuint alignment)
    {
        nuint allocationSize = checked(byteCount + alignment);
        byte* ptr = (byte*)NativeMemory.AlignedAlloc(allocationSize, alignment);
        if (ptr is null)
            throw new OutOfMemoryException("Unable to allocate frame scratch overflow memory.");

        _overflowHead = new OverflowBlock(ptr, allocationSize, _overflowHead);
        return ptr;
    }

    private void FreeOverflowBlocks()
    {
        OverflowBlock? block = _overflowHead;
        _overflowHead = null;
        while (block is not null)
        {
            OverflowBlock? next = block.Next;
            NativeMemory.AlignedFree(block.Pointer);
            block = next;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nuint AlignUp(nuint value, nuint alignment)
        => (value + alignment - 1) & ~(alignment - 1);

    private static nuint NormalizeAlignment(int alignmentBytes)
    {
        if (alignmentBytes <= 0)
            return (nuint)nint.Size;

        uint alignment = (uint)alignmentBytes;
        if ((alignment & (alignment - 1)) != 0)
            throw new ArgumentException("Alignment must be a power of two.", nameof(alignmentBytes));

        return Math.Max(alignment, (uint)nint.Size);
    }

    private sealed unsafe class OverflowBlock(byte* pointer, nuint size, OverflowBlock? next)
    {
        public byte* Pointer { get; } = pointer;
        public nuint Size { get; } = size;
        public OverflowBlock? Next { get; } = next;
    }
}

public readonly ref struct FrameScratchLease<T>
    where T : unmanaged
{
    private readonly FrameScratchAllocator _owner;
    private readonly int _generation;
    private readonly Span<T> _span;

    internal FrameScratchLease(FrameScratchAllocator owner, int generation, Span<T> span)
    {
        _owner = owner;
        _generation = generation;
        _span = span;
    }

    public int Length => _span.Length;

    public Span<T> Span
    {
        get
        {
            _owner.ThrowIfGenerationMismatch(_generation);
            return _span;
        }
    }

    public void Dispose()
    {
    }
}

public static class FrameScratch
{
    public const int DefaultThreadCapacityBytes = 1024 * 1024;

    [ThreadStatic]
    private static FrameScratchAllocator? t_current;

    public static FrameScratchAllocator Current
        => t_current ??= new FrameScratchAllocator(DefaultThreadCapacityBytes);

    public static void ResetCurrent()
        => Current.Reset();

    public static Span<T> Allocate<T>(int count, int alignmentBytes = 16)
        where T : unmanaged
        => Current.Allocate<T>(count, alignmentBytes);
}

public sealed class FrameScratchRing : IDisposable
{
    private readonly FrameScratchAllocator[] _lanes;
    private int _index;

    public FrameScratchRing(int laneCount, nuint laneCapacityBytes)
    {
        if (laneCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(laneCount));

        _lanes = new FrameScratchAllocator[laneCount];
        for (int i = 0; i < _lanes.Length; i++)
            _lanes[i] = new FrameScratchAllocator(laneCapacityBytes);
    }

    public FrameScratchAllocator Current => _lanes[_index];

    public int CurrentLane => _index;

    public void AdvanceFrame()
    {
        _index++;
        if (_index >= _lanes.Length)
            _index = 0;

        _lanes[_index].Reset();
    }

    public void Dispose()
    {
        for (int i = 0; i < _lanes.Length; i++)
            _lanes[i].Dispose();
    }
}
