using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Vulkan wrapper for a one-dimensional texture (<see cref="XRTexture1D"/>).
    /// Uploads each 1-D mipmap level from the engine's <c>Mipmaps</c> array into
    /// a single-layer image with <see cref="ImageType.Type1D"/>.
    /// </summary>
    internal sealed class VkTexture1D(VulkanRenderer api, XRTexture1D data) : VkImageBackedTexture<XRTexture1D>(api, data)
    {
        protected override ImageType TextureImageType => ImageType.Type1D;
        protected override ImageViewType DefaultImageViewType => ImageViewType.Type1D;

        protected override TextureLayout DescribeTexture()
        {
            uint width = Math.Max(Data.Width, 1u);
            uint mipLevels = (uint)Math.Max(Data.Mipmaps?.Length ?? 1, 1);
            return new TextureLayout(new Extent3D(width, 1, 1), 1, mipLevels);
        }

        protected override AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            if (mipLevel <= 0)
                return default;

            uint baseMip = (uint)Math.Max(mipLevel, 0);
            return new AttachmentViewKey(baseMip, 1, 0, 1, ImageViewType.Type1D, AspectFlags);
        }

        protected override void PushTextureData()
        {
            Generate();

            Mipmap1D[] mipmaps = Data.Mipmaps;
            if (mipmaps is null || mipmaps.Length == 0)
            {
                Debug.VulkanWarning($"1D texture '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
            for (uint level = 0; level < levelCount; level++)
            {
                Mipmap1D? mip = mipmaps[level];
                if (mip is null)
                    continue;

                if (!TryCreateStagingBuffer(mip.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                    continue;

                try
                {
                    Extent3D extent = new(Math.Max(mip.Width, 1u), 1, 1);
                    CopyBufferToImage(stagingBuffer, level, 0, 1, extent, (ulong)(mip.Data?.Length ?? 0));
                }
                finally
                {
                    DestroyStagingBuffer(stagingBuffer, stagingMemory);
                }
            }

            if (Data.AutoGenerateMipmaps && ResolvedMipLevels > 1)
                GenerateMipmapsGPU();
            else
                TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }
    }
}
