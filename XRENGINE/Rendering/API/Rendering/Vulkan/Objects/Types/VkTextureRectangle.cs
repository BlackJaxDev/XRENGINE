using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Vulkan wrapper for a rectangle texture (<see cref="XRTextureRectangle"/>).
    /// Rectangle textures use a single mip level and are addressed by non-normalised
    /// texel coordinates. The image is always one mip-level deep.
    /// </summary>
    internal sealed class VkTextureRectangle(VulkanRenderer api, XRTextureRectangle data) : VkImageBackedTexture<XRTextureRectangle>(api, data)
    {
        protected override TextureLayout DescribeTexture()
        {
            uint width = Math.Max(Data.Width, 1u);
            uint height = Math.Max(Data.Height, 1u);
            return new TextureLayout(new Extent3D(width, height, 1), 1, 1);
        }

        protected override void PushTextureData()
        {
            Generate();
            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            if (TryCreateStagingBuffer(Data.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
            {
                try
                {
                    Extent3D extent = new(Math.Max(Data.Width, 1u), Math.Max(Data.Height, 1u), 1);
                    CopyBufferToImage(stagingBuffer, 0, 0, 1, extent);
                }
                finally
                {
                    DestroyStagingBuffer(stagingBuffer, stagingMemory);
                }
            }

            TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }
    }
}
