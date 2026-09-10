namespace XREngine.Rendering.Vulkan;

/// <summary>Bounded exact heap-root identities for every draw in a chain.</summary>
internal struct VulkanDescriptorHeapDrawIdentityBuffer : IEquatable<VulkanDescriptorHeapDrawIdentityBuffer>
{
    internal const int Capacity = 32;
    private VulkanDescriptorHeapDrawIdentity _firstIdentity;
    private VulkanDescriptorHeapDrawIdentity[]? _overflowIdentities;

    internal int Count { get; private set; }
    internal bool IsComplete { get; private set; }

    internal void Initialize(int count)
    {
        int previousCount = Count;
        Count = count;
        IsComplete = count is >= 0 and <= Capacity;
        if (!IsComplete || count <= 1)
            return;
        if (_overflowIdentities is null)
        {
            _overflowIdentities = new VulkanDescriptorHeapDrawIdentity[Math.Min(Capacity, Math.Max(count, 4))];
            if (previousCount == 1)
                _overflowIdentities[0] = _firstIdentity;
        }
        else if (_overflowIdentities.Length < count)
            Array.Resize(ref _overflowIdentities, Math.Min(Capacity, Math.Max(count, _overflowIdentities.Length * 2)));
    }

    internal void Set(int index, in VulkanDescriptorHeapDrawIdentity identity)
    {
        if ((uint)index >= (uint)Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (Count == 1)
            _firstIdentity = identity;
        else
            _overflowIdentities![index] = identity;
        IsComplete &= identity.IsComplete;
    }

    internal readonly VulkanDescriptorHeapDrawIdentity Get(int index)
    {
        if ((uint)index >= (uint)Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));
        return Count == 1 ? _firstIdentity : _overflowIdentities![index];
    }

    public readonly bool Equals(VulkanDescriptorHeapDrawIdentityBuffer other)
    {
        if (Count != other.Count || IsComplete != other.IsComplete)
            return false;
        for (int index = 0; index < Count; index++)
            if (Get(index) != other.Get(index))
                return false;
        return true;
    }

    public override readonly bool Equals(object? obj)
        => obj is VulkanDescriptorHeapDrawIdentityBuffer other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Count); hash.Add(IsComplete);
        for (int index = 0; index < Count; index++)
            hash.Add(Get(index));
        return hash.ToHashCode();
    }
}
