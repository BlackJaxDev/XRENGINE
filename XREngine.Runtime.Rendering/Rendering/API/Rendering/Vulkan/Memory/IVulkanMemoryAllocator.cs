using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Abstraction over Vulkan memory allocation strategies.
/// Implementations include per-resource legacy allocation and block suballocation.
/// </summary>
internal unsafe interface IVulkanMemoryAllocator : IDisposable
{
    /// <summary>
    /// Allocates memory suitable for a Vulkan buffer with the given requirements and properties.
    /// Throws on unrecoverable failure.
    /// </summary>
    VulkanMemoryAllocation AllocateForBuffer(
        Vk api,
        Device device,
        Buffer buffer,
        MemoryPropertyFlags requiredProperties);

    /// <summary>
    /// Allocates memory suitable for a Vulkan image with the given requirements and properties.
    /// Throws on unrecoverable failure.
    /// </summary>
    VulkanMemoryAllocation AllocateForImage(
        Vk api,
        Device device,
        Image image,
        MemoryPropertyFlags requiredProperties);

    /// <summary>
    /// Attempts to allocate memory for a buffer. Returns false on OOM instead of throwing.
    /// </summary>
    bool TryAllocateForBuffer(
        Vk api,
        Device device,
        Buffer buffer,
        MemoryPropertyFlags requiredProperties,
        out VulkanMemoryAllocation allocation);

    /// <summary>
    /// Attempts to allocate memory for an image. Returns false on OOM instead of throwing.
    /// </summary>
    bool TryAllocateForImage(
        Vk api,
        Device device,
        Image image,
        MemoryPropertyFlags requiredProperties,
        out VulkanMemoryAllocation allocation);

    /// <summary>Frees a previously-made allocation.</summary>
    void Free(Vk api, Device device, VulkanMemoryAllocation allocation);

    /// <summary>Reports total number of active Vulkan memory allocations (vkAllocateMemory calls).</summary>
    int ActiveVkAllocationCount { get; }

    /// <summary>Reports total allocated bytes across all live allocations.</summary>
    long TotalAllocatedBytes { get; }
}
