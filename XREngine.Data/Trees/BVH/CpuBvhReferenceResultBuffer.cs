using System.Numerics;

namespace XREngine.Data.Trees;

/// <summary>Reusable diagnostic storage for a single-frustum BVH reference collection.</summary>
public sealed class CpuBvhReferenceResultBuffer<T> where T : class
{
    private T?[] _items;

    public CpuBvhReferenceResultBuffer(int capacity)
        => _items = new T?[Math.Max(1, capacity)];

    public int Count { get; private set; }
    public int Capacity => _items.Length;
    public bool Overflowed { get; private set; }

    public void EnsureCapacity(int capacity)
    {
        if (_items.Length >= capacity)
            return;
        _items = new T?[checked((int)BitOperations.RoundUpToPowerOf2((uint)capacity))];
    }

    public void Begin()
    {
        Count = 0;
        Overflowed = false;
    }

    public T Get(int index)
        => (uint)index < (uint)Count
            ? _items[index]!
            : throw new ArgumentOutOfRangeException(nameof(index));

    public bool ContainsReference(T item)
    {
        for (int i = 0; i < Count; i++)
            if (ReferenceEquals(_items[i], item))
                return true;
        return false;
    }

    public Visitor CreateVisitor() => new(this);

    public struct Visitor(CpuBvhReferenceResultBuffer<T> owner) : ICpuBvhVisitor<T>
    {
        public void Visit(T item)
        {
            if (owner.Count >= owner._items.Length)
            {
                owner.Overflowed = true;
                return;
            }
            owner._items[owner.Count++] = item;
        }
    }
}
