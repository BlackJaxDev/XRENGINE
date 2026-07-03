using System.Buffers;
using System.Runtime.CompilerServices;

namespace XREngine.Data.Runtime.Memory;

public readonly record struct PooledArrayStatistics(
    long RentCount,
    long ReturnCount,
    long ActiveCount,
    long PoolMissCount,
    long DiscardedOnReturnCount);

public static class PooledArray
{
    private static long _rentCount;
    private static long _returnCount;
    private static long _poolMissCount;
    private static long _discardedOnReturnCount;

    public static int DefaultMaxRetainedElements { get; set; } = 1024 * 1024;

    public static PooledArray<T> Rent<T>(
        int length,
        bool clearOnReturn = false,
        int? maxRetainedElements = null)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        T[] array = ArrayPool<T>.Shared.Rent(length);
        Interlocked.Increment(ref _rentCount);
        if (array.Length < length)
            Interlocked.Increment(ref _poolMissCount);

        return new PooledArray<T>(
            array,
            length,
            clearOnReturn,
            maxRetainedElements ?? DefaultMaxRetainedElements);
    }

    public static PooledArrayStatistics GetStatistics()
    {
        long rent = Interlocked.Read(ref _rentCount);
        long returned = Interlocked.Read(ref _returnCount);
        return new PooledArrayStatistics(
            rent,
            returned,
            Math.Max(0L, rent - returned),
            Interlocked.Read(ref _poolMissCount),
            Interlocked.Read(ref _discardedOnReturnCount));
    }

    internal static void RecordReturn(bool discarded)
    {
        Interlocked.Increment(ref _returnCount);
        if (discarded)
            Interlocked.Increment(ref _discardedOnReturnCount);
    }
}

public ref struct PooledArray<T>
{
    private T[]? _array;
    private readonly bool _clearOnReturn;
    private readonly int _maxRetainedElements;

    internal PooledArray(T[] array, int length, bool clearOnReturn, int maxRetainedElements)
    {
        _array = array;
        Length = length;
        _clearOnReturn = clearOnReturn;
        _maxRetainedElements = Math.Max(0, maxRetainedElements);
    }

    public int Length { get; }

    public bool IsEmpty => Length == 0;

    public Span<T> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array is null ? throw new ObjectDisposedException(nameof(PooledArray<T>)) : _array.AsSpan(0, Length);
    }

    public T[] Array
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array ?? throw new ObjectDisposedException(nameof(PooledArray<T>));
    }

    public void Dispose()
    {
        T[]? array = _array;
        if (array is null)
            return;

        _array = null;
        bool discard = array.Length > _maxRetainedElements;
        if (!discard)
            ArrayPool<T>.Shared.Return(array, _clearOnReturn);
        else if (_clearOnReturn)
            System.Array.Clear(array);

        PooledArray.RecordReturn(discard);
    }
}
