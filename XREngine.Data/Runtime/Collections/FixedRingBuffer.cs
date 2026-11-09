namespace XREngine.Data.Runtime.Collections;

/// <summary>
/// A fixed-size ring buffer (circular buffer) implementation that supports enqueueing, dequeueing, and peeking elements.
/// </summary>
/// <typeparam name="T">The type of elements stored in the ring buffer.</typeparam>
public sealed class FixedRingBuffer<T>
{
    private readonly T[] _items;
    private int _head;
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixedRingBuffer{T}"/> class with the specified capacity.
    /// </summary>
    /// <param name="capacity">The maximum number of elements the ring buffer can hold.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the specified capacity is less than or equal to zero.</exception>
    public FixedRingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _items = new T[capacity];
    }

    /// <summary>
    /// Gets the maximum number of elements the ring buffer can hold.
    /// </summary>
    public int Capacity => _items.Length;
    /// <summary>
    /// Gets the current number of elements in the ring buffer.
    /// </summary>
    public int Count => _count;
    /// <summary>
    /// Gets the number of times an enqueue operation failed due to the buffer being full.
    /// </summary>
    public long OverflowCount { get; private set; }

    /// <summary>
    /// Attempts to enqueue an element into the ring buffer.
    /// </summary>
    /// <param name="item">The element to enqueue.</param>
    /// <param name="dropOldest">If true, the oldest element will be dropped to make room when the buffer is full; otherwise, the enqueue will fail if the buffer is full.</param>
    /// <returns>True if the element was successfully enqueued; otherwise, false.</returns>
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

    /// <summary>
    /// Attempts to peek at the element at the front of the ring buffer without removing it.
    /// </summary>
    /// <param name="item">When this method returns, contains the element at the front of the ring buffer if the operation was successful; otherwise, the default value of <typeparamref name="T"/>.</param>
    /// <returns>True if an element was successfully peeked; otherwise, false.</returns>
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

    /// <summary>
    /// Attempts to dequeue an element from the ring buffer.
    /// </summary>
    /// <param name="item">When this method returns, contains the dequeued element if the operation was successful; otherwise, the default value of <typeparamref name="T"/>.</param>
    /// <returns>True if an element was successfully dequeued; otherwise, false.</returns>
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

    /// <summary>
    /// Clears the ring buffer quickly by resetting the head and count without modifying the underlying array.
    /// </summary>
    public void ClearFast()
    {
        _head = 0;
        _count = 0;
    }
}
