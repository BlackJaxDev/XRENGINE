using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns one aligned native allocation for short-lived Vulkan ABI arrays. A reservation is
/// single-threaded and generation-validated, so a pointer derived by a caller cannot survive
/// the native call that consumes its span.
/// </summary>
internal sealed class VulkanNativeScratchArena<T> : IDisposable
    where T : unmanaged
{
    private nint _storage;
    private readonly object _gate = new();
    private int _capacity;
    private int _storageAlignment;
    private ulong _generation;
    private int _reservationThreadId;
    private int _reservationActive;
    private int _disposed;
    private long _reservationCount;
    private long _requestedBytes;
    private long _highWaterBytes;

    public VulkanNativeScratchArena(int initialCapacity = 4)
    {
        if (initialCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        if (initialCapacity > 0)
            Allocate(initialCapacity, IntPtr.Size);
    }

    /// <summary>Reserves a contiguous, aligned native-call lease.</summary>
    public VulkanNativeScratchReservation<T> Reserve(int count, int alignment = 0)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        int elementSize = Unsafe.SizeOf<T>();
        int resolvedAlignment = alignment == 0
            ? Math.Min(IntPtr.Size, GetLargestPowerOfTwoDivisor(elementSize))
            : alignment;
        if (resolvedAlignment <= 0 || (resolvedAlignment & (resolvedAlignment - 1)) != 0 || elementSize % resolvedAlignment != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a positive power of two that divides the element size.");

        lock (_gate)
        {
            ThrowIfDisposed();
            if (Interlocked.CompareExchange(ref _reservationActive, 1, 0) != 0)
                throw new InvalidOperationException("The Vulkan native scratch arena already has an active reservation.");

            try
            {
                EnsureCapacity(count, resolvedAlignment);
                int threadId = Environment.CurrentManagedThreadId;
                ulong generation = unchecked(++_generation);
                if (generation == 0)
                    generation = ++_generation;
                long requestedBytes = checked((long)count * elementSize);
                Volatile.Write(ref _reservationThreadId, threadId);
                Interlocked.Increment(ref _reservationCount);
                Interlocked.Add(ref _requestedBytes, requestedBytes);
                UpdateHighWater(requestedBytes);
                return new VulkanNativeScratchReservation<T>(this, count, generation, threadId, resolvedAlignment);
            }
            catch
            {
                Volatile.Write(ref _reservationThreadId, 0);
                Volatile.Write(ref _reservationActive, 0);
                throw;
            }
        }
    }

    internal unsafe Span<T> GetSpan(int count, ulong generation, int threadId, int alignment)
    {
        ThrowIfDisposed();
        if (generation == 0 || generation != _generation || Volatile.Read(ref _reservationActive) == 0)
            throw new InvalidOperationException("The Vulkan native scratch reservation has expired.");
        if (threadId != Environment.CurrentManagedThreadId || threadId != Volatile.Read(ref _reservationThreadId))
            throw new InvalidOperationException("A Vulkan native scratch reservation may only be used by its owning thread.");
        if ((uint)count > (uint)_capacity || _storage == 0 || _storageAlignment < alignment)
            throw new InvalidOperationException("The Vulkan native scratch reservation exceeds its backing store.");
        return new Span<T>((void*)_storage, count);
    }

    internal void Release(ulong generation, int threadId)
    {
        ThrowIfDisposed();
        if (generation == 0 || generation != _generation || Volatile.Read(ref _reservationActive) == 0)
            throw new InvalidOperationException("The Vulkan native scratch reservation has expired.");
        if (threadId != Environment.CurrentManagedThreadId || threadId != Volatile.Read(ref _reservationThreadId))
            throw new InvalidOperationException("A Vulkan native scratch reservation may only be released by its owning thread.");
        Volatile.Write(ref _reservationThreadId, 0);
        Volatile.Write(ref _reservationActive, 0);
    }

    internal long ReservationCount => Volatile.Read(ref _reservationCount);
    internal long RequestedBytes => Volatile.Read(ref _requestedBytes);
    internal long HighWaterBytes => Volatile.Read(ref _highWaterBytes);

    public unsafe void Reset()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _reservationActive) != 0)
                throw new InvalidOperationException("Cannot reset Vulkan native scratch while a reservation is active.");
            _generation = 0;
            if (_storage != 0 && _capacity > 0)
                new Span<T>((void*)_storage, _capacity).Clear();
        }
    }

    /// <summary>Frees the owned native allocation once all leases have completed.</summary>
    public unsafe void Dispose()
    {
        nint storage;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (Volatile.Read(ref _reservationActive) != 0)
                throw new InvalidOperationException("Cannot dispose Vulkan native scratch while a reservation is active.");

            Volatile.Write(ref _disposed, 1);
            storage = _storage;
            _storage = 0;
            _capacity = 0;
            _storageAlignment = 0;
        }
        if (storage != 0)
            NativeMemory.AlignedFree((void*)storage);
        GC.SuppressFinalize(this);
    }

    private void EnsureCapacity(int requiredCount, int requestedAlignment)
    {
        if (requiredCount <= _capacity && requestedAlignment <= _storageAlignment)
            return;

        int requiredCapacity = GetExpandedCapacity(requiredCount);
        int allocationAlignment = Math.Max(IntPtr.Size, requestedAlignment);
        Allocate(requiredCapacity, allocationAlignment);
    }

    private unsafe void Allocate(int capacity, int alignment)
    {
        nuint bytes = checked((nuint)capacity * (nuint)Unsafe.SizeOf<T>());
        // AlignedAlloc has implementation-defined behavior for zero-byte requests; zero uses no storage.
        if (bytes == 0)
            return;

        nuint allocationAlignment = checked((nuint)alignment);
        nuint allocationBytes = checked((bytes + allocationAlignment - 1) / allocationAlignment * allocationAlignment);
        void* replacement = NativeMemory.AlignedAlloc(allocationBytes, allocationAlignment);
        if (replacement is null)
            throw new OutOfMemoryException($"Failed to allocate {bytes} bytes of Vulkan native scratch.");

        nint previous = _storage;
        _storage = (nint)replacement;
        _capacity = capacity;
        _storageAlignment = alignment;
        if (previous != 0)
            NativeMemory.AlignedFree((void*)previous);
    }

    private int GetExpandedCapacity(int requiredCount)
    {
        int capacity = Math.Max(_capacity, 4);
        while (capacity < requiredCount)
        {
            if (capacity > int.MaxValue / 2)
                return requiredCount;
            capacity *= 2;
        }
        return capacity;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(VulkanNativeScratchArena<T>));
    }

    private void UpdateHighWater(long requestedBytes)
    {
        long observed;
        while (requestedBytes > (observed = Volatile.Read(ref _highWaterBytes)) &&
               Interlocked.CompareExchange(ref _highWaterBytes, requestedBytes, observed) != observed) { }
    }

    private static int GetLargestPowerOfTwoDivisor(int value) => value & -value;
}
