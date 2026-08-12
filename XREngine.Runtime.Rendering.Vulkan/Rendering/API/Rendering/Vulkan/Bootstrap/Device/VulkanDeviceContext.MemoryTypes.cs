using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

internal sealed partial class VulkanDeviceContext
{
    /// <summary>
    /// Resolves a physical-device memory type for a native allocation without routing
    /// the allocator through the renderer composition root.
    /// </summary>
    internal bool TryFindMemoryType(
        Vk api,
        uint typeFilter,
        MemoryPropertyFlags requiredProperties,
        out uint memoryTypeIndex)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);

        for (uint index = 0; index < memoryProperties.MemoryTypeCount; index++)
        {
            if ((typeFilter & (1u << (int)index)) == 0 ||
                (memoryProperties.MemoryTypes[(int)index].PropertyFlags & requiredProperties) != requiredProperties)
            {
                continue;
            }

            memoryTypeIndex = index;
            return true;
        }

        memoryTypeIndex = 0;
        return false;
    }

    internal uint FindMemoryType(Vk api, uint typeFilter, MemoryPropertyFlags requiredProperties)
        => TryFindMemoryType(api, typeFilter, requiredProperties, out uint memoryTypeIndex)
            ? memoryTypeIndex
            : throw new InvalidOperationException("Failed to find a suitable Vulkan memory type.");

    /// <summary>Returns the properties actually exposed by a selected memory type.</summary>
    internal MemoryPropertyFlags GetMemoryTypeProperties(Vk api, uint memoryTypeIndex)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
        if (memoryTypeIndex >= memoryProperties.MemoryTypeCount)
            throw new ArgumentOutOfRangeException(nameof(memoryTypeIndex));

        return memoryProperties.MemoryTypes[(int)memoryTypeIndex].PropertyFlags;
    }
}
