using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Exact retained native range for an externally owned GPU buffer.</summary>
internal readonly record struct VulkanNativeBufferRange(
    XRDataBuffer Owner,
    VkBufferHandle Buffer,
    ulong Offset,
    ulong Length,
    VulkanResourceSlotHandle LifetimeSlot,
    ulong NativeGeneration,
    BufferUsageFlags Usage)
{
    internal bool IsValid => Owner is not null && Buffer.Handle != 0u &&
        Length != 0u && LifetimeSlot.IsValid && NativeGeneration != 0u;

    internal bool Matches(VulkanNativeBufferRange other)
        => ReferenceEquals(Owner, other.Owner) &&
           Buffer.Handle == other.Buffer.Handle &&
           Offset == other.Offset &&
           Length == other.Length &&
           LifetimeSlot.Index == other.LifetimeSlot.Index &&
           LifetimeSlot.Generation == other.LifetimeSlot.Generation &&
           NativeGeneration == other.NativeGeneration &&
           Usage == other.Usage;

    /// <summary>
    /// Compares all fields. The check compares the native handle by value, so it does not box
    /// the Silk.NET handle struct (bin keys that contain this range are compared in each frame).
    /// </summary>
    public bool Equals(VulkanNativeBufferRange other) => Matches(other);

    /// <summary>Combines the fields without boxing, consistent with <see cref="Equals(VulkanNativeBufferRange)"/>.</summary>
    public override int GetHashCode()
        => HashCode.Combine(Owner, Buffer.Handle, Offset, Length, LifetimeSlot.Index, LifetimeSlot.Generation, NativeGeneration, Usage);
}
