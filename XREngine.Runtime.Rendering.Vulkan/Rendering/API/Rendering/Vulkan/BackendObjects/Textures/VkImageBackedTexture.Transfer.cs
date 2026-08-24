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
    #region Buffer-to-Image Transfer

    /// <summary>
    /// Copies pixel data from <paramref name="buffer"/> into a specific mip level and array
    /// layer range of the image. Prefers NV indirect copy when available; otherwise falls
    /// back to <c>vkCmdCopyBufferToImage</c>, using a dedicated transfer queue with
    /// queue-family ownership barriers when the device exposes one.
    /// </summary>
    /// <param name="buffer">Staging buffer containing the pixel data.</param>
    /// <param name="mipLevel">Target mip level.</param>
    /// <param name="baseArrayLayer">First array layer to write.</param>
    /// <param name="layerCount">Number of array layers to write.</param>
    /// <param name="extent">Pixel extent of the target mip level.</param>
    /// <param name="stagingBufferSize">Size in bytes of the staging buffer. When non-zero,
    /// the method validates that the buffer is large enough for the target image format
    /// and logs an error (skipping the copy) if there is a mismatch.</param>
    protected void CopyBufferToImage(
        in VulkanFrameDataSlice stagingSlice,
        in VulkanSynchronousFrameDataArenaLease arenaLease,
        uint mipLevel,
        uint baseArrayLayer,
        uint layerCount,
        Extent3D extent)
    {
        if (!stagingSlice.IsValid)
            throw new ArgumentException("Texture uploads require a valid frame-data staging slice.", nameof(stagingSlice));
        if (!(BackendContext.Resources.SynchronousFrameDataArena?.TryFlushHostWrites(stagingSlice) ?? false))
            throw new InvalidOperationException("Vulkan frame-data arena could not publish texture staging writes before copy.");

        if (!ValidateCopyBufferToImageRegion(mipLevel, baseArrayLayer, layerCount, extent))
            return;

        // Validate staging buffer size against what the GPU will actually read.
        if (stagingSlice.Length > 0)
        {
            uint bpt = VkFormatConversions.GetBytesPerTexel(ResolvedFormat);
            if (bpt > 0)
            {
                ulong requiredBytes = (ulong)extent.Width * extent.Height * extent.Depth * layerCount * bpt;
                if (stagingSlice.Length < requiredBytes)
                {
                    Debug.LogError(
                        $"[Vulkan] Staging buffer size mismatch for '{Data.Name ?? GetDescribingName()}': " +
                        $"buffer={stagingSlice.Length} bytes but image format {ResolvedFormat} requires " +
                        $"{requiredBytes} bytes ({extent.Width}x{extent.Height}x{extent.Depth} * {layerCount} layers * {bpt} bpp). " +
                        $"Skipping CopyBufferToImage to avoid GPU out-of-bounds read. " +
                        $"Check that the texture's SizedInternalFormat matches its pixel data.");
                    return;
                }
            }
        }

        ImageLayout currentLayout = CurrentImageLayout;
        if (currentLayout != ImageLayout.TransferDstOptimal)
            TransitionImageLayout(currentLayout, ImageLayout.TransferDstOptimal);

        BufferImageCopy region = new()
        {
            BufferOffset = stagingSlice.Offset,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = AspectFlags,
                MipLevel = mipLevel,
                BaseArrayLayer = baseArrayLayer,
                LayerCount = layerCount,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = extent,
        };

        // Keep synchronous texture publication ordered with prior and future image
        // uses on the graphics queue.  The resource command authority owns the
        // tracked submission and fence receipt; no renderer facade participates.
        ResourceCommandPort.CopyBufferToImage(
            stagingSlice,
            _image,
            ImageLayout.TransferDstOptimal,
            ref region,
            in arenaLease,
            "VkImageBackedTexture.CopyBufferToImage");
    }

    private bool ValidateCopyBufferToImageRegion(uint mipLevel, uint baseArrayLayer, uint layerCount, Extent3D extent)
    {
        if (_image.Handle == 0)
            return false;

        uint mipCount = Math.Max(ResolvedMipLevels, 1u);
        uint arrayLayerCount = Math.Max(ResolvedArrayLayers, 1u);
        if (mipLevel >= mipCount)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Texture.CopyMipOutOfRange.{Data.GetHashCode()}.{mipLevel}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Skipping CopyBufferToImage for '{0}': mip {1} is outside image mip count {2}.",
                Data.Name ?? GetDescribingName(),
                mipLevel,
                mipCount);
            return false;
        }

        if (layerCount == 0 || baseArrayLayer >= arrayLayerCount || layerCount > arrayLayerCount - baseArrayLayer)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Texture.CopyLayerOutOfRange.{Data.GetHashCode()}.{baseArrayLayer}.{layerCount}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Skipping CopyBufferToImage for '{0}': layers {1}+{2} exceed image layer count {3}.",
                Data.Name ?? GetDescribingName(),
                baseArrayLayer,
                layerCount,
                arrayLayerCount);
            return false;
        }

        if (extent.Width == 0 || extent.Height == 0 || extent.Depth == 0)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Texture.CopyZeroExtent.{Data.GetHashCode()}.{mipLevel}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Skipping CopyBufferToImage for '{0}': requested extent {1}x{2}x{3} is invalid.",
                Data.Name ?? GetDescribingName(),
                extent.Width,
                extent.Height,
                extent.Depth);
            return false;
        }

        Extent3D baseExtent = ResolvedExtent;
        Extent3D mipExtent = ResolveMipExtent(baseExtent, mipLevel);
        if (extent.Width <= mipExtent.Width && extent.Height <= mipExtent.Height && extent.Depth <= mipExtent.Depth)
            return true;

        Debug.VulkanWarningEvery(
            $"Vulkan.Texture.CopyExtentOutOfRange.{Data.GetHashCode()}.{mipLevel}",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Skipping CopyBufferToImage for '{0}': requested extent {1}x{2}x{3} exceeds mip {4} extent {5}x{6}x{7} (base {8}x{9}x{10}, mips={11}).",
            Data.Name ?? GetDescribingName(),
            extent.Width,
            extent.Height,
            extent.Depth,
            mipLevel,
            mipExtent.Width,
            mipExtent.Height,
            mipExtent.Depth,
            baseExtent.Width,
            baseExtent.Height,
            baseExtent.Depth,
            mipCount);
        return false;
    }

    private static Extent3D ResolveMipExtent(Extent3D baseExtent, uint mipLevel)
    {
        uint width = Math.Max(baseExtent.Width, 1u);
        uint height = Math.Max(baseExtent.Height, 1u);
        uint depth = Math.Max(baseExtent.Depth, 1u);

        for (uint i = 0; i < mipLevel; i++)
        {
            if (width > 1)
                width >>= 1;
            if (height > 1)
                height >>= 1;
            if (depth > 1)
                depth >>= 1;
        }

        return new Extent3D(width, height, depth);
    }

    #endregion
}
