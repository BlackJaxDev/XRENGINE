using System.Runtime.CompilerServices;

namespace XREngine.Data.Runtime.Collections;

public sealed class HotDenseList<T>
{
    private T[] _items;

    public HotDenseList(int capacity = 0)
    {
        _items = capacity > 0 ? new T[capacity] : [];
    }

    public int Count { get; private set; }

    public int Capacity => _items.Length;

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            return ref _items[index];
        }
    }

    public Span<T> AsSpan()
        => _items.AsSpan(0, Count);

    public void EnsureCapacity(int capacity)
    {
        if (capacity <= _items.Length)
            return;

        int newCapacity = _items.Length == 0 ? 4 : _items.Length;
        while (newCapacity < capacity)
            newCapacity *= 2;

        Array.Resize(ref _items, newCapacity);
    }

    public void Add(T item)
    {
        if (Count == _items.Length)
            EnsureCapacity(Count + 1);

        _items[Count++] = item;
    }

    public void ClearFast()
        => Count = 0;

    public void ClearAndReleaseReferences()
    {
        Array.Clear(_items, 0, Count);
        Count = 0;
    }
}

public sealed class HotBitSet
{
    private ulong[] _words;

    public HotBitSet(int bitCapacity = 0)
    {
        _words = bitCapacity > 0 ? new ulong[WordCount(bitCapacity)] : [];
    }

    public int WordCapacity => _words.Length;

    public void EnsureBitCapacity(int bitCapacity)
    {
        int wordCount = WordCount(bitCapacity);
        if (wordCount > _words.Length)
            Array.Resize(ref _words, wordCount);
    }

    public bool Get(int bitIndex)
    {
        if (bitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(bitIndex));

        int wordIndex = bitIndex >> 6;
        if (wordIndex >= _words.Length)
            return false;

        ulong mask = 1UL << (bitIndex & 63);
        return (_words[wordIndex] & mask) != 0;
    }

    public void Set(int bitIndex)
    {
        if (bitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(bitIndex));

        EnsureBitCapacity(bitIndex + 1);
        _words[bitIndex >> 6] |= 1UL << (bitIndex & 63);
    }

    public void Clear(int bitIndex)
    {
        if (bitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(bitIndex));

        int wordIndex = bitIndex >> 6;
        if (wordIndex >= _words.Length)
            return;

        _words[wordIndex] &= ~(1UL << (bitIndex & 63));
    }

    public void ClearAll()
        => Array.Clear(_words);

    public ReadOnlySpan<ulong> Words => _words;

    private static int WordCount(int bitCount)
        => bitCount <= 0 ? 0 : ((bitCount + 63) >> 6);
}

public sealed class HotSparseSet
{
    private int[] _dense;
    private int[] _sparse;

    public HotSparseSet(int denseCapacity = 0, int sparseCapacity = 0)
    {
        _dense = denseCapacity > 0 ? new int[denseCapacity] : [];
        _sparse = sparseCapacity > 0 ? new int[sparseCapacity] : [];
    }

    public int Count { get; private set; }

    public ReadOnlySpan<int> Dense => _dense.AsSpan(0, Count);

    public void EnsureDenseCapacity(int capacity)
    {
        if (capacity > _dense.Length)
            Array.Resize(ref _dense, capacity);
    }

    public void EnsureSparseCapacity(int capacity)
    {
        if (capacity > _sparse.Length)
            Array.Resize(ref _sparse, capacity);
    }

    public bool Contains(int value)
    {
        if (value < 0 || value >= _sparse.Length)
            return false;

        int denseIndex = _sparse[value] - 1;
        return (uint)denseIndex < (uint)Count && _dense[denseIndex] == value;
    }

    public bool Add(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));

        if (Contains(value))
            return false;

        EnsureSparseCapacity(value + 1);
        EnsureDenseCapacity(Count + 1);
        _dense[Count] = value;
        _sparse[value] = Count + 1;
        Count++;
        return true;
    }

    public bool Remove(int value)
    {
        if (!Contains(value))
            return false;

        int denseIndex = _sparse[value] - 1;
        int lastIndex = Count - 1;
        int moved = _dense[lastIndex];
        _dense[denseIndex] = moved;
        _sparse[moved] = denseIndex + 1;
        _dense[lastIndex] = 0;
        _sparse[value] = 0;
        Count = lastIndex;
        return true;
    }

    public void ClearFast()
    {
        for (int i = 0; i < Count; i++)
            _sparse[_dense[i]] = 0;

        Count = 0;
    }
}

public sealed class FixedRingBuffer<T>
{
    private readonly T[] _items;
    private int _head;
    private int _count;

    public FixedRingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count => _count;
    public long OverflowCount { get; private set; }

    public bool TryEnqueue(T item, bool dropOldest = false)
    {
        if (_count == _items.Length)
        {
            OverflowCount++;
            if (!dropOldest)
                return false;

            _head++;
            if (_head >= _items.Length)
                _head = 0;
            _count--;
        }

        int tail = (_head + _count) % _items.Length;
        _items[tail] = item;
        _count++;
        return true;
    }

    public bool TryPeek(out T item)
    {
        if (_count == 0)
        {
            item = default!;
            return false;
        }

        item = _items[_head];
        return true;
    }

    public bool TryDequeue(out T item)
    {
        if (!TryPeek(out item))
            return false;

        _items[_head] = default!;
        _head++;
        if (_head >= _items.Length)
            _head = 0;

        _count--;
        return true;
    }

    public void ClearFast()
    {
        _head = 0;
        _count = 0;
    }
}
