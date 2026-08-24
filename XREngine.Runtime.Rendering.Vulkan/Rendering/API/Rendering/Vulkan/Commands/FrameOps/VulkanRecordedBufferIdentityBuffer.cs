using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded storage for the concrete buffers bound by a packet. Copies share
/// the exact-count backing array after publication; builders must finish every
/// <see cref="Set"/> call before the buffer enters a recorded packet key.
/// Overflow deliberately marks the packet non-reusable instead of silently
/// dropping a dependency.
/// </summary>
internal struct VulkanRecordedBufferIdentityBuffer : IEquatable<VulkanRecordedBufferIdentityBuffer>
{
    public const int Capacity = 16;
    private const int InlineCapacity = 2;

    private VulkanRecordedBufferIdentity _firstIdentity;
    private VulkanRecordedBufferIdentity _secondIdentity;
    private VulkanRecordedBufferIdentity[]? _overflowIdentities;

    public int Count { get; private set; }
    public bool IsComplete { get; private set; }

    public void Initialize(int count)
    {
        int previousCount = Count;
        Count = count;
        IsComplete = count is >= 0 and <= Capacity;
        if (!IsComplete || count <= InlineCapacity)
            return;

        if (_overflowIdentities is null)
        {
            _overflowIdentities = new VulkanRecordedBufferIdentity[
                Math.Min(Capacity, Math.Max(count, 4))];
            if (previousCount > 0)
                _overflowIdentities[0] = _firstIdentity;
            if (previousCount > 1)
                _overflowIdentities[1] = _secondIdentity;
        }
        else if (_overflowIdentities.Length < count)
        {
            Array.Resize(
                ref _overflowIdentities,
                Math.Min(Capacity, Math.Max(count, _overflowIdentities.Length * 2)));
        }
    }

    public void Invalidate()
    {
        Count = 0;
        IsComplete = false;
    }

    public void Set(int index, in VulkanRecordedBufferIdentity identity)
    {
        if ((uint)index >= (uint)Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (Count > InlineCapacity)
            _overflowIdentities![index] = identity;
        else if (index == 0)
            _firstIdentity = identity;
        else
            _secondIdentity = identity;
        IsComplete &= identity.IsComplete;
    }

    public readonly VulkanRecordedBufferIdentity Get(int index)
    {
        if ((uint)index >= (uint)Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (Count > InlineCapacity)
            return _overflowIdentities![index];

        return index == 0 ? _firstIdentity : _secondIdentity;
    }

    public readonly bool Equals(VulkanRecordedBufferIdentityBuffer other)
    {
        if (Count != other.Count || IsComplete != other.IsComplete)
            return false;

        for (int i = 0; i < Count; i++)
            if (Get(i) != other.Get(i))
                return false;

        return true;
    }

    public override readonly bool Equals(object? obj)
        => obj is VulkanRecordedBufferIdentityBuffer other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Count);
        hash.Add(IsComplete);
        for (int i = 0; i < Count; i++)
            hash.Add(Get(i));
        return hash.ToHashCode();
    }

    public static bool operator ==(
        in VulkanRecordedBufferIdentityBuffer left,
        in VulkanRecordedBufferIdentityBuffer right)
        => left.Equals(right);

    public static bool operator !=(
        in VulkanRecordedBufferIdentityBuffer left,
        in VulkanRecordedBufferIdentityBuffer right)
        => !left.Equals(right);
}
