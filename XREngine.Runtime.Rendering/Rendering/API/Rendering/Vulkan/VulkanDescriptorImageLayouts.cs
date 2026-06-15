using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal ImageLayout ResolveDescriptorImageLayout(IVkImageDescriptorSource source, DescriptorType descriptorType)
    {
        if (descriptorType == DescriptorType.StorageImage)
        {
            ImageLayout trackedStorageLayout = source.TrackedImageLayout;
            if (trackedStorageLayout == ImageLayout.General)
                return trackedStorageLayout;

            if (source.TryTransitionDedicatedImageLayout(trackedStorageLayout, ImageLayout.General))
                return ImageLayout.General;

            return ImageLayout.General;
        }

        ImageLayout trackedLayout = source.TrackedImageLayout;
        if (trackedLayout is ImageLayout.ShaderReadOnlyOptimal
            or ImageLayout.DepthStencilReadOnlyOptimal)
        {
            return trackedLayout;
        }

        ImageLayout requestedLayout = GetDefaultSampledDescriptorLayout(source);
        if (source.TryTransitionDedicatedImageLayout(trackedLayout, requestedLayout))
            return requestedLayout;

        return requestedLayout;
    }

    private static ImageLayout GetDefaultSampledDescriptorLayout(IVkImageDescriptorSource source)
    {
        ImageAspectFlags aspect = source.DescriptorAspect;
        bool depthOrStencil = (aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0 ||
            IsCombinedDepthStencilFormat(source.DescriptorFormat);

        return depthOrStencil
            ? ImageLayout.DepthStencilReadOnlyOptimal
            : ImageLayout.ShaderReadOnlyOptimal;
    }
}
