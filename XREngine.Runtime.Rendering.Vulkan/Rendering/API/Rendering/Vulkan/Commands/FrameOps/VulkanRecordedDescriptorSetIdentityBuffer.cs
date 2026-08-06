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
    public readonly bool Equals(VulkanRecordedDescriptorSetIdentityBuffer other) { if (Count != other.Count || IsComplete != other.IsComplete) return false; for (int i = 0; i < Count; i++) if (_items[i] != other._items[i]) return false; return true; }
    public override readonly bool Equals(object? obj) => obj is VulkanRecordedDescriptorSetIdentityBuffer other && Equals(other);
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
    public readonly bool Equals(VulkanRecordedDescriptorResourceIdentityBuffer other) { if (Count != other.Count || IsComplete != other.IsComplete) return false; for (int i = 0; i < Count; i++) if (_items[i] != other._items[i]) return false; return true; }
    public override readonly bool Equals(object? obj) => obj is VulkanRecordedDescriptorResourceIdentityBuffer other && Equals(other);
    public override readonly int GetHashCode() { HashCode hash = new(); hash.Add(Count); hash.Add(IsComplete); for (int i = 0; i < Count; i++) hash.Add(_items[i]); return hash.ToHashCode(); }
}

[InlineArray(VulkanRecordedDescriptorSetIdentityBuffer.Capacity)] internal struct VulkanRecordedDescriptorSetIdentityInlineArray { private VulkanRecordedDescriptorSetIdentity _element0; }
[InlineArray(VulkanRecordedDescriptorResourceIdentityBuffer.Capacity)] internal struct VulkanRecordedDescriptorResourceIdentityInlineArray { private VulkanRecordedDescriptorResourceIdentity _element0; }
