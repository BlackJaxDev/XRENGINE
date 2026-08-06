using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Selects the queue families used by the engine from one immutable physical
/// device snapshot and the active presentation surface.
/// </summary>
internal static class VulkanQueueFamilySelector
{
    public static QueueFamilyIndices Select(
        ReadOnlySpan<QueueFamilyProperties> queueFamilies,
        PhysicalDevice physicalDevice,
        VulkanPresentationSupportProbe? presentationSupportProbe)
    {
        QueueFamilyIndices indices = default;

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

            if (presentationSupportProbe is not null)
            {
                Result result = presentationSupportProbe(
                    physicalDevice,
                    i,
                    out bool supportsPresentation);
                if (result != Result.Success)
                {
                    throw new InvalidOperationException(
                        $"Vulkan presentation support query failed for queue family {i}. Result={result}.");
                }
                if (supportsPresentation &&
                    !indices.PresentFamilyIndex.HasValue)
                {
                    indices.PresentFamilyIndex = i;
                }
            }
        }

        indices.ComputeFamilyIndex ??= indices.GraphicsFamilyIndex;
        indices.TransferFamilyIndex ??=
            indices.ComputeFamilyIndex ?? indices.GraphicsFamilyIndex;
        return indices;
    }
}
