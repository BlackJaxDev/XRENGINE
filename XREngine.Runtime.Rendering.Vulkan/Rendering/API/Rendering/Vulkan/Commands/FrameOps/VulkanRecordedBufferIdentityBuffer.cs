using System;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded allocation-free storage for the concrete buffers bound by a packet.
/// Overflow deliberately marks the packet non-reusable instead of silently
/// dropping a dependency.
/// </summary>
internal struct VulkanRecordedBufferIdentityBuffer : IEquatable<VulkanRecordedBufferIdentityBuffer>
{
    public const int Capacity = 16;

    private VulkanRecordedBufferIdentityInlineArray _identities;

    public int Count { get; private set; }
    public bool IsComplete { get; private set; }

    public void Initialize(int count)
    {
        Count = count;
        IsComplete = count is >= 0 and <= Capacity;
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

        _identities[index] = identity;
        IsComplete &= identity.IsComplete;
    }

    public readonly VulkanRecordedBufferIdentity Get(int index)
    {
        if ((uint)index >= (uint)Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _identities[index];
    }

    public readonly bool Equals(VulkanRecordedBufferIdentityBuffer other)
    {
        if (Count != other.Count || IsComplete != other.IsComplete)
            return false;

        for (int i = 0; i < Count; i++)
            if (_identities[i] != other._identities[i])
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
            hash.Add(_identities[i]);
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

[InlineArray(VulkanRecordedBufferIdentityBuffer.Capacity)]
internal struct VulkanRecordedBufferIdentityInlineArray
{
    private VulkanRecordedBufferIdentity _element0;
}
