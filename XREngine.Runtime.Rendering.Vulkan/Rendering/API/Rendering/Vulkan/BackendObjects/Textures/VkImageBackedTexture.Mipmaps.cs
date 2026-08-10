using Silk.NET.Vulkan;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

internal unsafe abstract partial class VkImageBackedTexture<TTexture> : VkTexture<TTexture>, IVkFrameBufferAttachmentSource where TTexture : XRTexture
{
    #region Mipmap Generation

    /// <summary>
    /// Generates a full mipmap chain for the current image using <c>vkCmdBlitImage</c>.
    /// Each mip level is transitioned from <see cref="ImageLayout.TransferDstOptimal"/> to
    /// <see cref="ImageLayout.TransferSrcOptimal"/>, blitted to the next smaller level,
    /// then transitioned to <see cref="ImageLayout.ShaderReadOnlyOptimal"/>.
    /// The final mip level is transitioned directly.
    /// </summary>
    /// <remarks>
    /// This method verifies that the image format supports linear blitting. If it does not,
    /// a warning is emitted and the image is transitioned to shader-read without mips.
    /// </remarks>
    protected void GenerateMipmapsWithBlit()
    {
        Generate();

        if (ResolvedMipLevels <= 1)
        {
            ImageLayout currentLayout = CurrentImageLayout;
            if (currentLayout != ImageLayout.ShaderReadOnlyOptimal &&
                currentLayout != ImageLayout.DepthStencilReadOnlyOptimal)
                TransitionImageLayout(currentLayout, ImageLayout.ShaderReadOnlyOptimal);

            return;
        }

        Api!.GetPhysicalDeviceFormatProperties(PhysicalDevice, ResolvedFormat, out FormatProperties props);
        if ((props.OptimalTilingFeatures & FormatFeatureFlags.SampledImageFilterLinearBit) == 0)
        {
            Debug.VulkanWarning($"Texture format '{ResolvedFormat}' does not support linear blitting; skipping mipmap generation.");
            TransitionImageLayout(CurrentImageLayout, ImageLayout.ShaderReadOnlyOptimal);
            return;
        }

        ImageLayout sourceLayout = CurrentImageLayout;
        if (sourceLayout != ImageLayout.TransferDstOptimal)
            TransitionImageLayout(sourceLayout, ImageLayout.TransferDstOptimal);

        BackendContext.ResourceCommands.GenerateMipmaps(
            Image,
            ResolvedMipLevels,
            ResolvedArrayLayers,
            AspectFlags,
            ResolvedExtent,
            "VkImageBackedTexture.GenerateMipmaps");

        _currentImageLayout = ImageLayout.ShaderReadOnlyOptimal;
        _physicalGroup?.LastKnownLayout = ImageLayout.ShaderReadOnlyOptimal;
    }

    /// <summary>
    /// Builds an <see cref="ImageBlit"/> descriptor that copies from mip level
    /// <paramref name="targetLevel"/> − 1 to <paramref name="targetLevel"/>, halving
    /// the width and height (clamped to 1).
    /// </summary>
    /// <param name="targetLevel">The destination mip level (source is <c>targetLevel − 1</c>).</param>
    /// <param name="mipWidth">Width of the source mip level.</param>
    /// <param name="mipHeight">Height of the source mip level.</param>
    /// <returns>A configured <see cref="ImageBlit"/> ready for <c>CmdBlitImage</c>.</returns>
    private ImageBlit CreateMipBlit(uint targetLevel, int mipWidth, int mipHeight)
    {
        int dstWidth = Math.Max(mipWidth / 2, 1);
        int dstHeight = Math.Max(mipHeight / 2, 1);

        ImageBlit blit = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = AspectFlags,
                MipLevel = targetLevel - 1,
                BaseArrayLayer = 0,
                LayerCount = ResolvedArrayLayers,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = AspectFlags,
                MipLevel = targetLevel,
                BaseArrayLayer = 0,
                LayerCount = ResolvedArrayLayers,
            }
        };

        blit.SrcOffsets.Element0 = new Offset3D(0, 0, 0);
        blit.SrcOffsets.Element1 = new Offset3D(mipWidth, mipHeight, 1);
        blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
        blit.DstOffsets.Element1 = new Offset3D(dstWidth, dstHeight, 1);

        return blit;
    }

    #endregion
}
