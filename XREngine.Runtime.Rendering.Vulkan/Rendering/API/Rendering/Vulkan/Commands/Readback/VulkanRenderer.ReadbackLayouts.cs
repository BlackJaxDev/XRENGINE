using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private static ImageLayout ResolvePostTransferReadLayout(in BlitImageInfo info)
    {
        ImageUsageFlags usage = info.DescriptorSource?.DescriptorUsage ?? ImageUsageFlags.None;
        return ResolveReadbackRestoreLayout(
            info.PreferredLayout,
            usage,
            IsDepthOrStencilAspect(info.AspectMask));
    }

    /// <summary>
    /// Readback is observational: when the pre-transfer layout is known, restore that
    /// exact layout instead of selecting a new steady-state layout from usage flags.
    /// Usage-based selection is only a fallback for genuinely untracked images.
    /// </summary>
    internal static ImageLayout ResolveReadbackRestoreLayout(
        ImageLayout preTransferLayout,
        ImageUsageFlags usage,
        bool depthOrStencil)
    {
        if (preTransferLayout != ImageLayout.Undefined)
            return preTransferLayout;

        if ((usage & ImageUsageFlags.StorageBit) != 0)
            return ImageLayout.General;

        if ((usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0)
        {
            return depthOrStencil
                ? ImageLayout.DepthStencilReadOnlyOptimal
                : ImageLayout.ShaderReadOnlyOptimal;
        }

        return depthOrStencil
            ? ImageLayout.DepthStencilAttachmentOptimal
            : ImageLayout.ColorAttachmentOptimal;
    }

    private static void UpdateReadbackRestoredAttachmentLayout(
        in BlitImageInfo info,
        ImageLayout restoredLayout)
    {
        if (info.DescriptorSource is not IVkFrameBufferAttachmentSource attachmentSource)
            return;

        int mipLevel = checked((int)info.MipLevel);
        uint descriptorBaseArrayLayer = info.BaseArrayLayer;
        if (info.DescriptorSource is VkTextureView textureView)
        {
            descriptorBaseArrayLayer -= Math.Min(
                descriptorBaseArrayLayer,
                textureView.Data.MinLayer);
        }

        uint layerCount = Math.Max(info.LayerCount, 1u);
        for (uint layerOffset = 0; layerOffset < layerCount; layerOffset++)
        {
            attachmentSource.UpdateAttachmentTrackedLayout(
                restoredLayout,
                mipLevel,
                checked((int)(descriptorBaseArrayLayer + layerOffset)));
        }
    }
}
