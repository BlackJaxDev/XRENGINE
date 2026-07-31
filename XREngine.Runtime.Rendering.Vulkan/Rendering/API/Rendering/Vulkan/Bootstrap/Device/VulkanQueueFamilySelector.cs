using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Selects the queue families used by the engine from one immutable physical
/// device snapshot and the active presentation surface.
/// </summary>
internal static class VulkanQueueFamilySelector
{
    public static VulkanRenderer.QueueFamilyIndices Select(
        ReadOnlySpan<QueueFamilyProperties> queueFamilies,
        KhrSurface? surfaceApi,
        PhysicalDevice physicalDevice,
        SurfaceKHR surface)
    {
        VulkanRenderer.QueueFamilyIndices indices = default;

        for (uint i = 0; i < queueFamilies.Length; i++)
        {
            QueueFamilyProperties queueFamily = queueFamilies[(int)i];
            if ((queueFamily.QueueFlags & QueueFlags.GraphicsBit) != 0 &&
                !indices.GraphicsFamilyIndex.HasValue)
            {
                indices.GraphicsFamilyIndex = i;
                indices.GraphicsFamilySupportsCompute =
                    (queueFamily.QueueFlags & QueueFlags.ComputeBit) != 0;
                indices.GraphicsFamilySupportsTransfer =
                    (queueFamily.QueueFlags &
                        (QueueFlags.TransferBit |
                         QueueFlags.GraphicsBit |
                         QueueFlags.ComputeBit)) != 0;
            }

            if ((queueFamily.QueueFlags & QueueFlags.ComputeBit) != 0 &&
                (!indices.ComputeFamilyIndex.HasValue ||
                 (queueFamily.QueueFlags & QueueFlags.GraphicsBit) == 0))
            {
                indices.ComputeFamilyIndex = i;
            }

            if ((queueFamily.QueueFlags & QueueFlags.TransferBit) != 0 &&
                (!indices.TransferFamilyIndex.HasValue ||
                 ((queueFamily.QueueFlags & QueueFlags.GraphicsBit) == 0 &&
                  (queueFamily.QueueFlags & QueueFlags.ComputeBit) == 0)))
            {
                indices.TransferFamilyIndex = i;
            }

            if (surfaceApi is not null)
            {
                surfaceApi.GetPhysicalDeviceSurfaceSupport(
                    physicalDevice,
                    i,
                    surface,
                    out Bool32 presentSupport);
                if (presentSupport && !indices.PresentFamilyIndex.HasValue)
                    indices.PresentFamilyIndex = i;
            }
        }

        indices.ComputeFamilyIndex ??= indices.GraphicsFamilyIndex;
        indices.TransferFamilyIndex ??=
            indices.ComputeFamilyIndex ?? indices.GraphicsFamilyIndex;
        return indices;
    }
}
