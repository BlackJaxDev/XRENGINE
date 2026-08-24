using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded descriptor-set identity storage. Copies intentionally share the
/// exact-count backing array after publication; overflow makes reuse unavailable.
/// </summary>
internal struct VulkanRecordedDescriptorSetIdentityBuffer : IEquatable<VulkanRecordedDescriptorSetIdentityBuffer>
{
    public const int Capacity = 16;
    private VulkanRecordedDescriptorSetIdentity _firstIdentity;
    private VulkanRecordedDescriptorSetIdentity[]? _overflowIdentities;

    public int Count { get; private set; }
    public bool IsComplete { get; private set; }

    public void Initialize(int count)
    {
        int previousCount = Count;
        Count = count;
        IsComplete = count is >= 0 and <= Capacity;
        if (!IsComplete || count <= 1)
            return;

        if (_overflowIdentities is null)
        {
            _overflowIdentities = new VulkanRecordedDescriptorSetIdentity[
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

    public void Set(int index, in VulkanRecordedDescriptorSetIdentity value)
    {
        if ((uint)index >= (uint)Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (Count == 1)
            _firstIdentity = value;
        else
            _overflowIdentities![index] = value;
        IsComplete &= value.IsComplete;
    }

    public readonly VulkanRecordedDescriptorSetIdentity Get(int index)
    {
        if ((uint)index >= (uint)Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));

        return Count == 1
            ? _firstIdentity
            : _overflowIdentities![index];
    }

    internal readonly string DescribeFirstIncompleteIdentity()
    {
        if (Count is < 0 or > Capacity)
            return $"count={Count} exceeds capacity={Capacity}";

        for (int index = 0; index < Count; index++)
        {
            VulkanRecordedDescriptorSetIdentity identity = Get(index);
            if (identity.DescriptorSetHandle == 0UL)
                return $"item={index} set={identity.SetIndex} missing handle";
            if (identity.DescriptorSetLifetimeGeneration == 0UL)
                return $"item={index} set={identity.SetIndex} missing lifetime generation";
            if (identity.PayloadGeneration == 0UL)
                return $"item={index} set={identity.SetIndex} missing payload generation";
            if (identity.PublicationGeneration == 0UL)
                return $"item={index} set={identity.SetIndex} missing publication generation";

            ref readonly VulkanRecordedDescriptorResourceIdentityBuffer resources =
                ref VulkanRecordedDescriptorSetIdentity.GetResourcesReference(
                    in identity);
            if (!resources.IsComplete)
                return $"item={index} set={identity.SetIndex} resources: {resources.DescribeFirstIncompleteIdentity()}";
        }

        return $"count={Count} marked incomplete without an incomplete item";
    }
    public readonly bool Equals(in VulkanRecordedDescriptorSetIdentityBuffer other) { if (Count != other.Count || IsComplete != other.IsComplete) return false; for (int i = 0; i < Count; i++) { VulkanRecordedDescriptorSetIdentity identity = Get(i); VulkanRecordedDescriptorSetIdentity otherIdentity = other.Get(i); if (!identity.Matches(in otherIdentity)) return false; } return true; }
    public readonly bool Equals(VulkanRecordedDescriptorSetIdentityBuffer other) => Equals(in other);
    public override readonly bool Equals(object? obj) => obj is VulkanRecordedDescriptorSetIdentityBuffer other && Equals(in other);
    public override readonly int GetHashCode() { HashCode hash = new(); hash.Add(Count); hash.Add(IsComplete); for (int i = 0; i < Count; i++) hash.Add(Get(i)); return hash.ToHashCode(); }
}
