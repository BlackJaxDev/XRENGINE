using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Vulkan wrapper for a three-dimensional (volume) texture (<see cref="XRTexture3D"/>).
    /// The image is created with <see cref="ImageType.Type3D"/> and depth is derived from
    /// the engine texture's <c>Depth</c> property.
    /// </summary>
    internal sealed class VkTexture3D(VulkanRenderer api, XRTexture3D data) : VkImageBackedTexture<XRTexture3D>(api, data)
    {
        protected override ImageType TextureImageType => ImageType.Type3D;
        protected override ImageViewType DefaultImageViewType => ImageViewType.Type3D;

        protected override TextureLayout DescribeTexture()
        {
            uint width = Math.Max(Data.Width, 1u);
            uint height = Math.Max(Data.Height, 1u);
            uint depth = Math.Max(Data.Depth, 1u);
            uint mipLevels = (uint)Math.Max(Data.Mipmaps?.Length ?? 1, 1);
            return new TextureLayout(new Extent3D(width, height, depth), 1, mipLevels);
        }

        protected override void PushTextureData()
        {
            Generate();

            Mipmap3D[] mipmaps = Data.Mipmaps;
            if (mipmaps is null || mipmaps.Length == 0)
            {
                Debug.VulkanWarning($"3D texture '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
            for (uint level = 0; level < levelCount; level++)
            {
                Mipmap3D? mip = mipmaps[level];
                if (mip is null)
                    continue;

                if (!TryCreateStagingBuffer(mip.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                    continue;

                try
                {
                    Extent3D extent = new(
                        Math.Max(mip.Width, 1u),
                        Math.Max(mip.Height, 1u),
                        Math.Max(mip.Depth, 1u));
                    CopyBufferToImage(stagingBuffer, level, 0, 1, extent);
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
