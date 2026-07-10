using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal ImageLayout ResolveDescriptorImageLayout(IVkImageDescriptorSource source, DescriptorType descriptorType)
    {
        if (descriptorType == DescriptorType.StorageImage)
            return ImageLayout.General;

        ImageLayout requestedLayout = GetDefaultSampledDescriptorLayout(source);
        ImageLayout trackedLayout = ResolveSubmittedDescriptorLayout(source, source.DescriptorView);
        if (CanSampleFromTrackedGeneralLayout(source.DescriptorUsage, trackedLayout))
            return ImageLayout.General;
        return requestedLayout;
    }

    internal ImageLayout ResolveDescriptorImageLayout(
        IVkImageDescriptorSource source,
        in VkImageDescriptorSnapshot snapshot,
        DescriptorType descriptorType)
    {
        if (descriptorType == DescriptorType.StorageImage)
            return ImageLayout.General;

        ImageLayout requestedLayout = GetDefaultSampledDescriptorLayout(in snapshot);
        ImageLayout trackedLayout = ResolveSubmittedDescriptorLayout(source, snapshot.View, snapshot.TrackedLayout);
        if (CanSampleFromTrackedGeneralLayout(snapshot.Usage, trackedLayout))
            return ImageLayout.General;
        return requestedLayout;
    }

    private static ImageLayout GetDefaultSampledDescriptorLayout(IVkImageDescriptorSource source)
    {
        if (UsesGeneralSampledDescriptorLayout(source.DescriptorUsage))
            return ImageLayout.General;

        ImageAspectFlags aspect = source.DescriptorAspect;
        bool depthOrStencil = (aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0 ||
            IsCombinedDepthStencilFormat(source.DescriptorFormat);

        return depthOrStencil
            ? ImageLayout.DepthStencilReadOnlyOptimal
            : ImageLayout.ShaderReadOnlyOptimal;
    }

    private static bool CanSampleFromTrackedGeneralLayout(ImageUsageFlags usage, ImageLayout trackedLayout)
        => trackedLayout == ImageLayout.General &&
           (usage & ImageUsageFlags.SampledBit) != 0;

    private static ImageLayout GetDefaultSampledDescriptorLayout(in VkImageDescriptorSnapshot snapshot)
    {
        if (UsesGeneralSampledDescriptorLayout(snapshot.Usage))
            return ImageLayout.General;

        bool depthOrStencil = (snapshot.Aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0 ||
            IsCombinedDepthStencilFormat(snapshot.Format);

        return depthOrStencil
            ? ImageLayout.DepthStencilReadOnlyOptimal
            : ImageLayout.ShaderReadOnlyOptimal;
    }

    private static bool UsesGeneralSampledDescriptorLayout(ImageUsageFlags usage)
        => (usage & ImageUsageFlags.StorageBit) != 0 &&
           (usage & ImageUsageFlags.SampledBit) != 0;

    private ImageLayout ResolveSubmittedDescriptorLayout(
        IVkImageDescriptorSource source,
        ImageView view,
        ImageLayout fallback = ImageLayout.Undefined)
    {
        if (view.Handle != 0 &&
            TryGetDescriptorHeapImageViewCreateInfo(view, out ImageViewCreateInfo viewInfo) &&
            TryGetTrackedImageLayout(viewInfo.Image, viewInfo.SubresourceRange, out ImageLayout submitted))
            return submitted;

        ImageLayout sourceLayout = source.TrackedImageLayout;
        return sourceLayout != ImageLayout.Undefined ? sourceLayout : fallback;
    }
}
