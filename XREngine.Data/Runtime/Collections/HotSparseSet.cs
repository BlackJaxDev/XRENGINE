namespace XREngine.Data.Runtime.Collections;

/// <summary>
/// A high-performance sparse set implementation that uses separate dense and sparse arrays to efficiently store and query integer values.
/// </summary>
/// <param name="denseCapacity">The initial capacity of the dense array.</param>
/// <param name="sparseCapacity">The initial capacity of the sparse array.</param>
public sealed class HotSparseSet(int denseCapacity = 0, int sparseCapacity = 0)
{
    private int[] _dense = denseCapacity > 0 ? new int[denseCapacity] : [];
    private int[] _sparse = sparseCapacity > 0 ? new int[sparseCapacity] : [];

    /// <summary>
    /// Gets the current number of elements in the sparse set.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Gets a read-only span of the dense array containing the elements of the sparse set.
    /// </summary>
    public ReadOnlySpan<int> Dense => _dense.AsSpan(0, Count);

    /// <summary>
    /// Ensures that the dense array has at least the specified capacity.
    /// </summary>
    /// <param name="capacity">The required capacity of the dense array.</param>
    public void EnsureDenseCapacity(int capacity)
    {
        if (capacity > _dense.Length)
            Array.Resize(ref _dense, capacity);
    }

    /// <summary>
    /// Ensures that the sparse array has at least the specified capacity.
    /// </summary>
    /// <param name="capacity">The required capacity of the sparse array.</param>
    public void EnsureSparseCapacity(int capacity)
    {
        if (capacity > _sparse.Length)
            Array.Resize(ref _sparse, capacity);
    }

    /// <summary>
    /// Determines whether the sparse set contains the specified value.
    /// </summary>
    /// <param name="value">The value to check for existence in the sparse set.</param>
    /// <returns>True if the value exists in the sparse set; otherwise, false.</returns>
    public bool Contains(int value)
    {
        if (value < 0 || value >= _sparse.Length)
            return false;

        int denseIndex = _sparse[value] - 1;
        return (uint)denseIndex < (uint)Count && _dense[denseIndex] == value;
    }

    /// <summary>
    /// Adds the specified value to the sparse set if it does not already exist.
    /// </summary>
    /// <param name="value">The value to add to the sparse set.</param>
    /// <returns>True if the value was successfully added; otherwise, false.</returns>
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

    /// <summary>
    /// Removes the specified value from the sparse set if it exists.
    /// </summary>
    /// <param name="value">The value to remove from the sparse set.</param>
    /// <returns>True if the value was successfully removed; otherwise, false.</returns>
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

    /// <summary>
    /// Clears the sparse set quickly by resetting the count and sparse array entries without modifying the dense array.
    /// </summary>
    public void ClearFast()
    {
        for (int i = 0; i < Count; i++)
            _sparse[_dense[i]] = 0;

        Count = 0;
    }
}
