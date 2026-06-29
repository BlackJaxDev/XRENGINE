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

        ImageLayout requestedLayout = GetDefaultSampledDescriptorLayout(source);
        ImageLayout trackedLayout = source.TrackedImageLayout;
        if (trackedLayout == requestedLayout)
            return trackedLayout;

        if (trackedLayout is ImageLayout.ShaderReadOnlyOptimal
            or ImageLayout.DepthStencilReadOnlyOptimal)
        {
            if (source.TryTransitionDedicatedImageLayout(trackedLayout, requestedLayout))
                return requestedLayout;

            return trackedLayout;
        }

        if (source.TryTransitionDedicatedImageLayout(trackedLayout, requestedLayout))
            return requestedLayout;

        return requestedLayout;
    }

    internal ImageLayout ResolveDescriptorImageLayout(
        IVkImageDescriptorSource source,
        in VkImageDescriptorSnapshot snapshot,
        DescriptorType descriptorType)
    {
        if (descriptorType == DescriptorType.StorageImage)
        {
            ImageLayout trackedStorageLayout = snapshot.TrackedLayout;
            if (trackedStorageLayout == ImageLayout.General)
                return trackedStorageLayout;

            if (source.TryTransitionDedicatedImageLayout(trackedStorageLayout, ImageLayout.General))
                return ImageLayout.General;

            return ImageLayout.General;
        }

        ImageLayout requestedLayout = GetDefaultSampledDescriptorLayout(in snapshot);
        ImageLayout trackedLayout = snapshot.TrackedLayout;
        if (trackedLayout == requestedLayout)
            return trackedLayout;

        if (trackedLayout is ImageLayout.ShaderReadOnlyOptimal
            or ImageLayout.DepthStencilReadOnlyOptimal)
        {
            if (source.TryTransitionDedicatedImageLayout(trackedLayout, requestedLayout))
                return requestedLayout;

            return trackedLayout;
        }

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

    private static ImageLayout GetDefaultSampledDescriptorLayout(in VkImageDescriptorSnapshot snapshot)
    {
        bool depthOrStencil = (snapshot.Aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0 ||
            IsCombinedDepthStencilFormat(snapshot.Format);

        return depthOrStencil
            ? ImageLayout.DepthStencilReadOnlyOptimal
            : ImageLayout.ShaderReadOnlyOptimal;
    }
}
