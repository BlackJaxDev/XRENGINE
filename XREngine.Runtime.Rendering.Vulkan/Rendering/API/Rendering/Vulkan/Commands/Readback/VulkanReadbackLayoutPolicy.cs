using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Renderer-free layout policy for observational image readback.</summary>
internal static class VulkanReadbackLayoutPolicy
{
    internal static ImageLayout ResolvePostTransfer(in BlitImageInfo info)
    {
        ImageUsageFlags usage =
            info.Usage;
        bool depthOrStencil =
            (info.AspectMask &
             (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0;
        return ResolveRestore(info.PreferredLayout, usage, depthOrStencil);
    }

    internal static ImageLayout ResolveRestore(
        ImageLayout preTransferLayout,
        ImageUsageFlags usage,
        bool depthOrStencil)
    {
        if (preTransferLayout != ImageLayout.Undefined)
            return preTransferLayout;
        if ((usage & ImageUsageFlags.StorageBit) != 0)
            return ImageLayout.General;
        if ((usage &
             (ImageUsageFlags.SampledBit |
              ImageUsageFlags.InputAttachmentBit)) != 0)
        {
            return depthOrStencil
                ? ImageLayout.DepthStencilReadOnlyOptimal
                : ImageLayout.ShaderReadOnlyOptimal;
        }

        if (depthOrStencil && (usage & ImageUsageFlags.DepthStencilAttachmentBit) != 0)
            return ImageLayout.DepthStencilAttachmentOptimal;
        if (!depthOrStencil && (usage & ImageUsageFlags.ColorAttachmentBit) != 0)
            return ImageLayout.ColorAttachmentOptimal;

        // GENERAL is legal for transfer-only images as well. Do not invent an
        // attachment layout when a physical image has no attachment usage.
        return ImageLayout.General;
    }

    internal static void PublishRestoredAttachmentLayout(
        in BlitImageInfo info,
        ImageLayout restoredLayout)
    {
        if (info.DescriptorSource is not
            IVkFrameBufferAttachmentSource attachmentSource)
        {
            return;
        }

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
