using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable descriptor-publication column. Capacity is fixed by its owner so a hot publication
/// cannot allocate while assembling native descriptor data.
/// </summary>
internal sealed class VulkanDescriptorScratchBuffer<T>
{
    private readonly T[] _items;

    internal VulkanDescriptorScratchBuffer(int capacity = 1024) => _items = new T[capacity];

    internal int Count { get; private set; }
    internal ref T this[int index] => ref _items[index];
    internal ReadOnlySpan<T> ReadOnlySpan => _items.AsSpan(0, Count);
    internal Span<T> Span => _items.AsSpan(0, Count);

    internal void Clear() => Count = 0;

    internal void Add(in T item)
    {
        if ((uint)Count >= (uint)_items.Length)
            throw new InvalidOperationException($"Descriptor publication scratch capacity ({_items.Length}) was exceeded.");

        _items[Count++] = item;
    }

    public Enumerator GetEnumerator() => new(_items, Count);

    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<T> _items;
        private int _index;

        internal Enumerator(T[] items, int count)
        {
            _items = items.AsSpan(0, count);
            _index = -1;
        }

        public ref readonly T Current => ref _items[_index];
        public bool MoveNext() => ++_index < _items.Length;
    }
}
