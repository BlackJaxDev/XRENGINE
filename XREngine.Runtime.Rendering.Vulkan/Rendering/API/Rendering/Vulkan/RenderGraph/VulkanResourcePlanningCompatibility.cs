using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>Pure conversions shared by Vulkan resource-planning code.</summary>
internal static class VulkanResourcePlanningCompatibility
{
    internal static ImageLayout ResolveInitialPhysicalGroupLayout(ImageUsageFlags usage, bool isDepth)
    {
        bool colorAttachment = (usage & ImageUsageFlags.ColorAttachmentBit) != 0;
        bool sampled = (usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0;
        bool storage = (usage & ImageUsageFlags.StorageBit) != 0;
        if (storage)
            return ImageLayout.General;
        if (sampled)
            return isDepth ? ImageLayout.DepthStencilReadOnlyOptimal : ImageLayout.ShaderReadOnlyOptimal;
        if (isDepth)
            return ImageLayout.DepthStencilAttachmentOptimal;
        if (colorAttachment)
            return ImageLayout.ColorAttachmentOptimal;
        if ((usage & ImageUsageFlags.TransferDstBit) != 0)
            return ImageLayout.TransferDstOptimal;
        if ((usage & ImageUsageFlags.TransferSrcBit) != 0)
            return ImageLayout.TransferSrcOptimal;
        return ImageLayout.General;
    }

    internal static bool HasStencilComponent(Format format)
        => format is Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint;

    internal static int SaturatingAddToInt32(uint left, uint right)
    {
        ulong sum = (ulong)left + right;
        return sum > int.MaxValue ? int.MaxValue : (int)sum;
    }

    internal static TextureResourceDescriptor EnrichTextureDescriptorForFrameBufferAttachment(
        TextureResourceDescriptor descriptor,
        XRTexture texture,
        EFrameBufferAttachment attachment,
        int mipLevel,
        int layerIndex)
    {
        RenderPipelineResourceUsage usage = descriptor.Usage | RenderPipelineResourceUsage.SampledTexture;
        usage |= attachment is EFrameBufferAttachment.DepthAttachment
            or EFrameBufferAttachment.DepthStencilAttachment
            or EFrameBufferAttachment.StencilAttachment
                ? RenderPipelineResourceUsage.DepthStencilAttachment
                : RenderPipelineResourceUsage.ColorAttachment;
        uint requiredMipLevels = mipLevel >= 0
            ? Math.Max(descriptor.MipPolicy.MipLevelCount, (uint)mipLevel + 1u)
            : Math.Max(descriptor.MipPolicy.MipLevelCount, 1u);
        uint requiredLayers = layerIndex >= 0
            ? Math.Max(descriptor.ArrayLayers, (uint)layerIndex + 1u)
            : descriptor.ArrayLayers;
        return descriptor with
        {
            Name = texture.Name ?? descriptor.Name,
            Usage = usage,
            MipPolicy = descriptor.MipPolicy with { MipLevelCount = requiredMipLevels },
            MipLevelCount = Math.Max(descriptor.MipLevelCount, requiredMipLevels),
            ArrayLayers = Math.Max(requiredLayers, 1u),
            LayerCount = Math.Max(descriptor.LayerCount, requiredLayers),
        };
    }
}
