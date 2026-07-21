namespace XREngine.Data.Trees;

/// <summary>
/// Reusable frame-slot storage for exact masked BVH results.
/// </summary>
public sealed class CpuBvhMaskedResultBuffer<T> where T : class
{
    private readonly CpuBvhMaskedResult<T>[] _items;
    private int _count;

    public CpuBvhMaskedResultBuffer(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new CpuBvhMaskedResult<T>[capacity];
    }

    public int Count => _count;
    public int Capacity => _items.Length;
    public bool Overflowed { get; private set; }

    public void BeginFrame()
    {
        _count = 0;
        Overflowed = false;
    }

    public ref readonly CpuBvhMaskedResult<T> Get(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return ref _items[index];
    }

    public Visitor CreateVisitor() => new(this);

    public struct Visitor(CpuBvhMaskedResultBuffer<T> owner) : ICpuBvhMaskedVisitor<T>
    {
        public void Visit(T item, ulong survivingViewMask)
        {
            if (owner._count >= owner._items.Length)
            {
                owner.Overflowed = true;
                return;
            }

            owner._items[owner._count++] = new(item, survivingViewMask);
        }
    }
}
