namespace XREngine.Rendering.Compute;

/// <summary>
/// Maintains stable, reusable slices for the GPU physics-chain palette atlas.
/// Existing compatible consumers keep their offsets when the surrounding
/// renderer set changes; newly allocated or resized slices are reported so
/// both current and previous history can be initialized together.
/// </summary>
internal sealed class PhysicsChainPaletteAtlasAllocator
{
    private readonly Dictionary<PhysicsChainPaletteSliceKey, Slice> _live = [];
    private readonly HashSet<PhysicsChainPaletteSliceKey> _touched = [];
    private readonly List<FreeRange> _free = [];
    private readonly List<PhysicsChainPaletteSliceKey> _stale = [];
    private uint _highWater;

    public int LiveSliceCount => _live.Count;
    public uint HighWater => _highWater;

    public void BeginLayout()
        => _touched.Clear();

    public PhysicsChainPaletteSlice Acquire(PhysicsChainPaletteSliceKey key, uint elementCount)
    {
        elementCount = Math.Max(elementCount, 1u);
        _touched.Add(key);

        if (_live.TryGetValue(key, out Slice existing) && existing.ElementCount == elementCount)
            return new PhysicsChainPaletteSlice(existing.BaseElement, existing.ElementCount, RequiresHistoryReset: false);

        if (existing.ElementCount > 0u)
        {
            _live.Remove(key);
            Release(existing);
        }

        uint baseElement = Allocate(elementCount);
        _live.Add(key, new Slice(baseElement, elementCount));
        return new PhysicsChainPaletteSlice(baseElement, elementCount, RequiresHistoryReset: true);
    }

    public void EndLayout()
    {
        _stale.Clear();
        foreach (PhysicsChainPaletteSliceKey key in _live.Keys)
            if (!_touched.Contains(key))
                _stale.Add(key);

        for (int i = 0; i < _stale.Count; ++i)
        {
            PhysicsChainPaletteSliceKey key = _stale[i];
            Slice slice = _live[key];
            _live.Remove(key);
            Release(slice);
        }
    }

    public void Reset()
    {
        _live.Clear();
        _touched.Clear();
        _free.Clear();
        _stale.Clear();
        _highWater = 0u;
    }

    private uint Allocate(uint elementCount)
    {
        for (int i = 0; i < _free.Count; ++i)
        {
            FreeRange range = _free[i];
            if (range.ElementCount < elementCount)
                continue;

            uint baseElement = range.BaseElement;
            if (range.ElementCount == elementCount)
                _free.RemoveAt(i);
            else
                _free[i] = new FreeRange(range.BaseElement + elementCount, range.ElementCount - elementCount);
            return baseElement;
        }

        uint allocatedBase = _highWater;
        _highWater = checked(_highWater + elementCount);
        return allocatedBase;
    }

    private void Release(Slice slice)
    {
        int insertIndex = 0;
        while (insertIndex < _free.Count && _free[insertIndex].BaseElement < slice.BaseElement)
            ++insertIndex;

        _free.Insert(insertIndex, new FreeRange(slice.BaseElement, slice.ElementCount));
        CoalesceFreeRanges(Math.Max(insertIndex - 1, 0));
    }

    private void CoalesceFreeRanges(int startIndex)
    {
        for (int i = startIndex; i + 1 < _free.Count;)
        {
            FreeRange current = _free[i];
            FreeRange next = _free[i + 1];
            if (current.BaseElement + current.ElementCount != next.BaseElement)
            {
                ++i;
                continue;
            }

            _free[i] = new FreeRange(current.BaseElement, current.ElementCount + next.ElementCount);
            _free.RemoveAt(i + 1);
        }

        if (_free.Count == 0)
            return;

        FreeRange tail = _free[^1];
        if (tail.BaseElement + tail.ElementCount != _highWater)
            return;

        _highWater = tail.BaseElement;
        _free.RemoveAt(_free.Count - 1);
    }

    private readonly record struct Slice(uint BaseElement, uint ElementCount);
    private readonly record struct FreeRange(uint BaseElement, uint ElementCount);
}

internal readonly struct PhysicsChainPaletteSliceKey : IEquatable<PhysicsChainPaletteSliceKey>
{
    public PhysicsChainPaletteSliceKey(object component, object compatibilityOwner)
    {
        Component = component;
        CompatibilityOwner = compatibilityOwner;
    }

    private object Component { get; }
    private object CompatibilityOwner { get; }

    public bool Equals(PhysicsChainPaletteSliceKey other)
        => ReferenceEquals(Component, other.Component)
            && ReferenceEquals(CompatibilityOwner, other.CompatibilityOwner);

    public override bool Equals(object? obj)
        => obj is PhysicsChainPaletteSliceKey other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Component),
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(CompatibilityOwner));
}

internal readonly record struct PhysicsChainPaletteSlice(
    uint BaseElement,
    uint ElementCount,
    bool RequiresHistoryReset);
