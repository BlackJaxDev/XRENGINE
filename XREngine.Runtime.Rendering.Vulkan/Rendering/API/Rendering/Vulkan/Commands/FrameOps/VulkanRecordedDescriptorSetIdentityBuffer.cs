using System;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>Bounded, allocation-free descriptor identity storage. Overflow makes reuse unavailable.</summary>
internal struct VulkanRecordedDescriptorSetIdentityBuffer : IEquatable<VulkanRecordedDescriptorSetIdentityBuffer>
{
    public const int Capacity = 16;
    private VulkanRecordedDescriptorSetIdentityInlineArray _items;
    public int Count { get; private set; }
    public bool IsComplete { get; private set; }
    public void Initialize(int count) { Count = count; IsComplete = count is >= 0 and <= Capacity; }
    public void Invalidate() { Count = 0; IsComplete = false; }
    public void Set(int index, in VulkanRecordedDescriptorSetIdentity value) { if ((uint)index >= (uint)Count || index >= Capacity) throw new ArgumentOutOfRangeException(nameof(index)); _items[index] = value; IsComplete &= value.IsComplete; }
    public readonly VulkanRecordedDescriptorSetIdentity Get(int index) => (uint)index < (uint)Count && index < Capacity ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));
    internal static ref readonly VulkanRecordedDescriptorSetIdentity GetReference(
        in VulkanRecordedDescriptorSetIdentityBuffer buffer,
        int index)
    {
        if ((uint)index >= (uint)buffer.Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref buffer._items[index];
    }
    internal readonly string DescribeFirstIncompleteIdentity()
    {
        if (Count is < 0 or > Capacity)
            return $"count={Count} exceeds capacity={Capacity}";

        for (int index = 0; index < Count; index++)
        {
            ref readonly VulkanRecordedDescriptorSetIdentity identity =
                ref _items[index];
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
    public readonly bool Equals(in VulkanRecordedDescriptorSetIdentityBuffer other) { if (Count != other.Count || IsComplete != other.IsComplete) return false; for (int i = 0; i < Count; i++) if (!_items[i].Matches(in other._items[i])) return false; return true; }
    public readonly bool Equals(VulkanRecordedDescriptorSetIdentityBuffer other) => Equals(in other);
    public override readonly bool Equals(object? obj) => obj is VulkanRecordedDescriptorSetIdentityBuffer other && Equals(in other);
    public override readonly int GetHashCode() { HashCode hash = new(); hash.Add(Count); hash.Add(IsComplete); for (int i = 0; i < Count; i++) hash.Add(_items[i]); return hash.ToHashCode(); }
}

internal struct VulkanRecordedDescriptorResourceIdentityBuffer : IEquatable<VulkanRecordedDescriptorResourceIdentityBuffer>
{
    public const int Capacity = 64;
    private VulkanRecordedDescriptorResourceIdentityInlineArray _items;
    public int Count { get; private set; }
    public bool IsComplete { get; private set; }
    public void Initialize(int count) { Count = count; IsComplete = count is >= 0 and <= Capacity; }
    public void Invalidate() { Count = 0; IsComplete = false; }
    public void Set(int index, in VulkanRecordedDescriptorResourceIdentity value) { if ((uint)index >= (uint)Count || index >= Capacity) throw new ArgumentOutOfRangeException(nameof(index)); _items[index] = value; IsComplete &= value.IsComplete; }
    public readonly VulkanRecordedDescriptorResourceIdentity Get(int index) => (uint)index < (uint)Count && index < Capacity ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));
    internal static ref readonly VulkanRecordedDescriptorResourceIdentity GetReference(
        in VulkanRecordedDescriptorResourceIdentityBuffer buffer,
        int index)
    {
        if ((uint)index >= (uint)buffer.Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref buffer._items[index];
    }
    internal readonly string DescribeFirstIncompleteIdentity()
    {
        if (Count is < 0 or > Capacity)
            return $"count={Count} exceeds capacity={Capacity}";

        for (int index = 0; index < Count; index++)
        {
            VulkanRecordedDescriptorResourceIdentity identity = _items[index];
            if (identity.Handle == 0UL)
                return $"item={index} type={identity.Type} missing handle";
            if (identity.Generation == 0UL)
                return $"item={index} type={identity.Type} handle=0x{identity.Handle:X} missing generation";
        }

        return $"count={Count} marked incomplete without an incomplete item";
    }
    public readonly bool Equals(in VulkanRecordedDescriptorResourceIdentityBuffer other) { if (Count != other.Count || IsComplete != other.IsComplete) return false; for (int i = 0; i < Count; i++) if (_items[i] != other._items[i]) return false; return true; }
    public readonly bool Equals(VulkanRecordedDescriptorResourceIdentityBuffer other) => Equals(in other);
    public override readonly bool Equals(object? obj) => obj is VulkanRecordedDescriptorResourceIdentityBuffer other && Equals(in other);
    public override readonly int GetHashCode() { HashCode hash = new(); hash.Add(Count); hash.Add(IsComplete); for (int i = 0; i < Count; i++) hash.Add(_items[i]); return hash.ToHashCode(); }
}

[InlineArray(VulkanRecordedDescriptorSetIdentityBuffer.Capacity)] internal struct VulkanRecordedDescriptorSetIdentityInlineArray { private VulkanRecordedDescriptorSetIdentity _element0; }
[InlineArray(VulkanRecordedDescriptorResourceIdentityBuffer.Capacity)] internal struct VulkanRecordedDescriptorResourceIdentityInlineArray { private VulkanRecordedDescriptorResourceIdentity _element0; }
