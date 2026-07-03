namespace XREngine.Data.Runtime.Memory;

public readonly record struct HotPathPoolStatistics(
    long CreatedCount,
    long RentMissCount,
    long ReleaseOverflowCount,
    long DestroyedCount);

public sealed class HotPathObjectPool<T>
    where T : class
{
    private readonly Func<T> _factory;
    private readonly Action<T>? _onRent;
    private readonly Action<T>? _onRelease;
    private readonly Action<T>? _onDestroy;
    private readonly int _localCapacity;
    private readonly ThreadLocal<LocalBucket> _local;
    private long _createdCount;
    private long _rentMissCount;
    private long _releaseOverflowCount;
    private long _destroyedCount;

    public HotPathObjectPool(
        Func<T> factory,
        int localCapacity,
        Action<T>? onRent = null,
        Action<T>? onRelease = null,
        Action<T>? onDestroy = null)
    {
        if (localCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(localCapacity));

        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _localCapacity = localCapacity;
        _onRent = onRent;
        _onRelease = onRelease;
        _onDestroy = onDestroy;
        _local = new ThreadLocal<LocalBucket>(() => new LocalBucket(_localCapacity));
    }

    public HotPathPoolStatistics Statistics => new(
        Interlocked.Read(ref _createdCount),
        Interlocked.Read(ref _rentMissCount),
        Interlocked.Read(ref _releaseOverflowCount),
        Interlocked.Read(ref _destroyedCount));

    public void Prewarm(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        LocalBucket bucket = _local.Value!;
        int target = Math.Min(count, _localCapacity);
        while (bucket.Count < target)
            bucket.TryPush(CreateItem());
    }

    public T Rent()
    {
        LocalBucket bucket = _local.Value!;
        T item;
        if (!bucket.TryPop(out item!))
        {
            Interlocked.Increment(ref _rentMissCount);
            item = CreateItem();
        }

        _onRent?.Invoke(item);
        return item;
    }

    public void Release(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _onRelease?.Invoke(item);

        LocalBucket bucket = _local.Value!;
        if (bucket.TryPush(item))
            return;

        Interlocked.Increment(ref _releaseOverflowCount);
        DestroyItem(item);
    }

    private T CreateItem()
    {
        Interlocked.Increment(ref _createdCount);
        return _factory();
    }

    private void DestroyItem(T item)
    {
        Interlocked.Increment(ref _destroyedCount);
        _onDestroy?.Invoke(item);
    }

    private sealed class LocalBucket
    {
        private readonly T[] _items;

        public LocalBucket(int capacity)
        {
            _items = new T[capacity];
        }

        public int Count { get; private set; }

        public bool TryPop(out T? item)
        {
            if (Count == 0)
            {
                item = null;
                return false;
            }

            int index = --Count;
            item = _items[index];
            _items[index] = null!;
            return true;
        }

        public bool TryPush(T item)
        {
            if (Count >= _items.Length)
                return false;

            _items[Count++] = item;
            return true;
        }
    }
}
