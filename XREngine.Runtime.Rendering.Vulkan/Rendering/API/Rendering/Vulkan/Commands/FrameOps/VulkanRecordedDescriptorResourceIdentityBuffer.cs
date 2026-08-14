using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded descriptor-resource identity storage. Copies intentionally share the
/// exact-count backing array after publication; builders must finish all
/// <see cref="Set"/> calls before the buffer enters a recorded packet key.
/// </summary>
internal struct VulkanRecordedDescriptorResourceIdentityBuffer : IEquatable<VulkanRecordedDescriptorResourceIdentityBuffer>
{
    public const int Capacity = 64;
    private VulkanRecordedDescriptorResourceIdentity[]? _items;
    public int Count { get; private set; }
    public bool IsComplete { get; private set; }
    public void Initialize(int count)
    {
        Count = count;
        IsComplete = count is >= 0 and <= Capacity;
        if (!IsComplete || count == 0)
            return;

        if (_items is null)
        {
            _items = new VulkanRecordedDescriptorResourceIdentity[count];
        }
        else if (_items.Length < count)
        {
            Array.Resize(ref _items, count);
        }
    }
    public void Invalidate() { Count = 0; IsComplete = false; }
    public void Set(int index, in VulkanRecordedDescriptorResourceIdentity value) { if ((uint)index >= (uint)Count || index >= Capacity || _items is null) throw new ArgumentOutOfRangeException(nameof(index)); _items[index] = value; IsComplete &= value.IsComplete; }
    public readonly VulkanRecordedDescriptorResourceIdentity Get(int index) => (uint)index < (uint)Count && index < Capacity && _items is not null ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));
    internal static ref readonly VulkanRecordedDescriptorResourceIdentity GetReference(
        in VulkanRecordedDescriptorResourceIdentityBuffer buffer,
        int index)
    {
        if ((uint)index >= (uint)buffer.Count || index >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref buffer._items![index];
    }
    internal readonly string DescribeFirstIncompleteIdentity()
    {
        if (Count is < 0 or > Capacity)
            return $"count={Count} exceeds capacity={Capacity}";

        for (int index = 0; index < Count; index++)
        {
            VulkanRecordedDescriptorResourceIdentity identity = _items![index];
            if (identity.Handle == 0UL)
                return $"item={index} type={identity.Type} missing handle";
            if (identity.Generation == 0UL)
                return $"item={index} type={identity.Type} handle=0x{identity.Handle:X} missing generation";
        }

        return $"count={Count} marked incomplete without an incomplete item";
    }
    public readonly bool Equals(in VulkanRecordedDescriptorResourceIdentityBuffer other) { if (Count != other.Count || IsComplete != other.IsComplete) return false; for (int i = 0; i < Count; i++) if (_items![i] != other._items![i]) return false; return true; }
    public readonly bool Equals(VulkanRecordedDescriptorResourceIdentityBuffer other) => Equals(in other);
    public override readonly bool Equals(object? obj) => obj is VulkanRecordedDescriptorResourceIdentityBuffer other && Equals(in other);
    public override readonly int GetHashCode() { HashCode hash = new(); hash.Add(Count); hash.Add(IsComplete); for (int i = 0; i < Count; i++) hash.Add(_items![i]); return hash.ToHashCode(); }
}
