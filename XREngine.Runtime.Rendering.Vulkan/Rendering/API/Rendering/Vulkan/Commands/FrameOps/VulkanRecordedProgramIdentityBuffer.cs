using System;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded allocation-free collection of every native program/pipeline use in
/// a packet. Overflow or a missing physical pipeline disables reuse.
/// </summary>
internal struct VulkanRecordedProgramIdentityBuffer : IEquatable<VulkanRecordedProgramIdentityBuffer>
{
    // Shadow packets may intentionally span multiple material programs. Keep
    // enough exact native identities for the bounded shadow packet while still
    // retaining an allocation-free value key.
    public const int Capacity = 32;

    private VulkanRecordedProgramIdentityInlineArray _identities;

    public int Count { get; private set; }
    public bool IsComplete { get; private set; }

    public void Initialize(int count)
    {
        Count = count;
        IsComplete = count is > 0 and <= Capacity;
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

        _identities[index] = identity;
        IsComplete &= identity.IsComplete;
    }

    public readonly VulkanRecordedProgramIdentity Get(int index)
    {
        if ((uint)index >= (uint)Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _identities[index];
    }

    public readonly bool Equals(VulkanRecordedProgramIdentityBuffer other)
    {
        if (Count != other.Count || IsComplete != other.IsComplete)
            return false;

        for (int i = 0; i < Count; i++)
            if (_identities[i] != other._identities[i])
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
            hash.Add(_identities[i]);
        return hash.ToHashCode();
    }
}
