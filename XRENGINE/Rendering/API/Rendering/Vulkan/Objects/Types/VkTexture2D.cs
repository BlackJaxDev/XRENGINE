using System;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Vulkan wrapper for a two-dimensional texture (<see cref="XRTexture2D"/>).
    /// This is the most common texture type; it uploads width×height mipmaps into
    /// a single-layer 2-D image.
    /// </summary>
    internal sealed class VkTexture2D(VulkanRenderer api, XRTexture2D data) : VkImageBackedTexture<XRTexture2D>(api, data)
    {
        protected override TextureLayout DescribeTexture()
        {
            uint width = Math.Max(Data.Width, 1u);
            uint height = Math.Max(Data.Height, 1u);
            uint mipLevels = (uint)Math.Max(Data.Mipmaps?.Length ?? 1, 1);
            return new TextureLayout(new Extent3D(width, height, 1), 1, mipLevels);
        }

        protected override void PushTextureData()
        {
            Generate();

            var mipmaps = Data.Mipmaps;
            if (mipmaps is null || mipmaps.Length == 0)
            {
                Debug.VulkanWarning($"Texture '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
            for (uint level = 0; level < levelCount; level++)
            {
                Mipmap2D? mip = mipmaps[level];
                if (mip is null)
                    continue;

                if (!TryCreateStagingBuffer(mip.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                    continue;

                try
                {
                    Extent3D extent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1);
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
