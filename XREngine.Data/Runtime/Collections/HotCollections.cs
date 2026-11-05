using System.Runtime.CompilerServices;

namespace XREngine.Data.Runtime.Collections;

/// <summary>
/// A high-performance dense list implementation that minimizes memory allocations and provides fast access to elements.
/// </summary>
/// <typeparam name="T">The type of elements stored in the list.</typeparam>
/// <param name="capacity">The initial capacity of the list.</param>
public sealed class HotDenseList<T>(int capacity = 0)
{
    private T[] _items = capacity > 0 ? new T[capacity] : [];

    public int Count { get; private set; }

    public int Capacity => _items.Length;

    /// <summary>
    /// Gets a reference to the element at the specified index.
    /// </summary>
    /// <param name="index">The index of the element to retrieve.</param>
    /// <returns>A reference to the element at the specified index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the index is out of range.</exception>
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

    /// <summary>
    /// Returns a span representing the elements in the list.
    /// </summary>
    /// <returns>A span representing the elements in the list.</returns>
    public Span<T> AsSpan()
        => _items.AsSpan(0, Count);

    /// <summary>
    /// Ensures that the list has at least the specified capacity, resizing if necessary.
    /// </summary>
    /// <param name="capacity">The minimum capacity to ensure.</param>
    public void EnsureCapacity(int capacity)
    {
        if (capacity <= _items.Length)
            return;

        int newCapacity = _items.Length == 0 ? 4 : _items.Length;
        while (newCapacity < capacity)
            newCapacity *= 2;

        Array.Resize(ref _items, newCapacity);
    }

    /// <summary>
    /// Adds an element to the end of the list.
    /// </summary>
    /// <param name="item">The element to add to the list.</param>
    public void Add(T item)
    {
        if (Count == _items.Length)
            EnsureCapacity(Count + 1);

        _items[Count++] = item;
    }

    /// <summary>
    /// Clears the list by setting the count to zero without releasing references to the elements.
    /// </summary>
    public void ClearFast()
        => Count = 0;

    /// <summary>
    /// Clears the list and releases references to the elements by setting them to their default values.
    /// </summary>
    /// <remarks>
    /// This method is useful when you want to clear the list and also release references to the elements to allow garbage collection.
    /// </remarks>
    public void ClearAndReleaseReferences()
    {
        Array.Clear(_items, 0, Count);
        Count = 0;
    }
}
