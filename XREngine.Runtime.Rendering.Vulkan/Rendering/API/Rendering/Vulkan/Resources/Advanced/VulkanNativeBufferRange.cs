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
}
