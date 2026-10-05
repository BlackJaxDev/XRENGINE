using System.Runtime.InteropServices;
using XREngine.Data.Runtime.Memory;

namespace XREngine.Core.Files;

/// <summary>
/// Bounded storage for temporary cooked payloads. Requests below <see cref="NativeThresholdBytes"/>
/// come from a small set of retained managed buffers, so repeated loads do not churn the heap.
/// Requests at or above the threshold come from native memory, so temporary payloads never reach
/// the large object heap. Every native allocation is balanced through
/// <see cref="NativeMemoryPressureTracker"/>.
/// </summary>
public static unsafe class CookedPayloadBufferPool
{
    /// <summary>
    /// Payloads larger than the last sub-LOH power-of-two bucket use native memory.
    /// A request below the CLR's LOH threshold must not round up into a 128 KiB managed array.
    /// </summary>
    public const int NativeThresholdBytes = 65_537;

    /// <summary>Upper bound on retained managed buffers across all size classes.</summary>
    public const int MaxRetainedBuffers = 32;

    /// <summary>Upper bound on retained managed bytes across all size classes.</summary>
    public const long MaxRetainedBytes = 2L * 1024L * 1024L;

    private const int MinimumBucketBytes = 4096;

    private static readonly object Sync = new();
    private static readonly Stack<byte[]>[] Buckets = CreateBuckets();
    private static int _retainedCount;
    private static long _retainedBytes;
    private static long _retainedBytesHighWater;
    private static long _rentCount;
    private static long _rentMissCount;
    private static long _nativeRentCount;
    private static long _returnCount;
    private static long _releaseOverflowCount;
    private static long _activeNativeBytes;
    private static long _activeNativeBytesHighWater;

    public static CookedPayloadPoolStatistics Statistics => new(
        Interlocked.Read(ref _rentCount),
        Interlocked.Read(ref _rentMissCount),
        Interlocked.Read(ref _nativeRentCount),
        Interlocked.Read(ref _returnCount),
        Interlocked.Read(ref _releaseOverflowCount),
        Interlocked.Read(ref _retainedBytes),
        Interlocked.Read(ref _retainedBytesHighWater),
        Interlocked.Read(ref _activeNativeBytes),
        Interlocked.Read(ref _activeNativeBytesHighWater));

    /// <summary>Rents storage for <paramref name="length"/> bytes and returns a lease that frees it on dispose.</summary>
    public static CookedPayloadLease Rent(int length)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        Interlocked.Increment(ref _rentCount);
        if (length == 0)
            return CookedPayloadLease.Pooled([], 0);

        if (length >= NativeThresholdBytes)
        {
            Interlocked.Increment(ref _nativeRentCount);
            void* pointer = NativeMemory.Alloc((nuint)length);
            NativeMemoryPressureLease pressure = NativeMemoryPressureTracker.Add(length);
            long active = Interlocked.Add(ref _activeNativeBytes, length);
            UpdateHighWater(ref _activeNativeBytesHighWater, active);
            return CookedPayloadLease.Native(pointer, length, pressure);
        }

        int bucket = BucketIndex(length);
        byte[]? buffer = null;
        lock (Sync)
        {
            Stack<byte[]> stack = Buckets[bucket];
            if (stack.Count > 0)
            {
                buffer = stack.Pop();
                _retainedCount--;
                _retainedBytes -= buffer.Length;
            }
        }

        if (buffer is null)
        {
            Interlocked.Increment(ref _rentMissCount);
            buffer = GC.AllocateUninitializedArray<byte>(BucketCapacity(bucket));
        }

        return CookedPayloadLease.Pooled(buffer, length);
    }

    /// <summary>
    /// Rents storage and exposes it for filling. The writable span is valid until the lease is
    /// disposed or transferred; after filling, read the bytes back through <see cref="CookedPayloadLease.Span"/>.
    /// </summary>
    public static CookedPayloadLease Rent(int length, out Span<byte> destination)
    {
        CookedPayloadLease lease = Rent(length);
        destination = lease.WritableSpan;
        return lease;
    }

    internal static void ReturnPooled(byte[] buffer)
    {
        if (buffer.Length == 0)
            return;

        Interlocked.Increment(ref _returnCount);
        int bucket = BucketIndex(buffer.Length);
        if (BucketCapacity(bucket) != buffer.Length)
        {
            Interlocked.Increment(ref _releaseOverflowCount);
            return;
        }

        lock (Sync)
        {
            if (_retainedCount >= MaxRetainedBuffers || _retainedBytes + buffer.Length > MaxRetainedBytes)
            {
                _releaseOverflowCount++;
                return;
            }

            Buckets[bucket].Push(buffer);
            _retainedCount++;
            _retainedBytes += buffer.Length;
            if (_retainedBytes > _retainedBytesHighWater)
                _retainedBytesHighWater = _retainedBytes;
        }
    }

    internal static void ReleaseNative(void* pointer, int length, NativeMemoryPressureLease pressure)
    {
        NativeMemory.Free(pointer);
        pressure.Dispose();
        Interlocked.Add(ref _activeNativeBytes, -length);
        Interlocked.Increment(ref _returnCount);
    }

    /// <summary>Drops every retained managed buffer. Native leases are owned by their holders and are unaffected.</summary>
    public static void TrimRetained()
    {
        lock (Sync)
        {
            for (int i = 0; i < Buckets.Length; i++)
                Buckets[i].Clear();
            _retainedCount = 0;
            _retainedBytes = 0;
        }
    }

    private static Stack<byte[]>[] CreateBuckets()
    {
        int count = BucketIndex(NativeThresholdBytes - 1) + 1;
        Stack<byte[]>[] buckets = new Stack<byte[]>[count];
        for (int i = 0; i < count; i++)
            buckets[i] = new Stack<byte[]>();
        return buckets;
    }

    private static int BucketIndex(int length)
    {
        int index = 0;
        int capacity = MinimumBucketBytes;
        while (capacity < length)
        {
            capacity <<= 1;
            index++;
        }

        return index;
    }

    private static int BucketCapacity(int bucket)
        => MinimumBucketBytes << bucket;

    private static void UpdateHighWater(ref long highWater, long candidate)
    {
        while (true)
        {
            long current = Interlocked.Read(ref highWater);
            if (candidate <= current || Interlocked.CompareExchange(ref highWater, candidate, current) == current)
                return;
        }
    }
}
