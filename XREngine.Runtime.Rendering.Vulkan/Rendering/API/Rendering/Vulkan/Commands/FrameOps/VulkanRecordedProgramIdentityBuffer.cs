using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded collection of every native program/pipeline use in a packet. Copies
/// share the exact-count backing array after publication; builders must finish
/// every <see cref="Set"/> call before the buffer enters a recorded packet key.
/// Overflow or a missing physical pipeline disables reuse.
/// </summary>
internal struct VulkanRecordedProgramIdentityBuffer : IEquatable<VulkanRecordedProgramIdentityBuffer>
{
    // Shadow packets may intentionally span multiple material programs.
    public const int Capacity = 32;

    private VulkanRecordedProgramIdentity _firstIdentity;
    private VulkanRecordedProgramIdentity[]? _overflowIdentities;

    public int Count { get; private set; }
    public bool IsComplete { get; private set; }

    public void Initialize(int count)
    {
        int previousCount = Count;
        Count = count;
        IsComplete = count is > 0 and <= Capacity;
        if (!IsComplete || count == 1)
            return;

        if (_overflowIdentities is null)
        {
            _overflowIdentities = new VulkanRecordedProgramIdentity[
                Math.Min(Capacity, Math.Max(count, 4))];
            if (previousCount == 1)
                _overflowIdentities[0] = _firstIdentity;
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

    public void Set(int index, in VulkanRecordedProgramIdentity identity)
    {
        if ((uint)index >= (uint)Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (Count == 1)
            _firstIdentity = identity;
        else
            _overflowIdentities![index] = identity;
        IsComplete &= identity.IsComplete;
    }

    public readonly VulkanRecordedProgramIdentity Get(int index)
    {
        if ((uint)index >= (uint)Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));

        return Count == 1
            ? _firstIdentity
            : _overflowIdentities![index];
    }

    public readonly bool Equals(VulkanRecordedProgramIdentityBuffer other)
    {
        if (Count != other.Count || IsComplete != other.IsComplete)
            return false;

        for (int i = 0; i < Count; i++)
            if (Get(i) != other.Get(i))
                return false;

        return true;
    }

    public override readonly bool Equals(object? obj)
        => obj is VulkanRecordedProgramIdentityBuffer other && Equals(other);

    public override readonly int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Count);
        hash.Add(IsComplete);
        for (int i = 0; i < Count; i++)
            hash.Add(Get(i));
        return hash.ToHashCode();
    }
}
