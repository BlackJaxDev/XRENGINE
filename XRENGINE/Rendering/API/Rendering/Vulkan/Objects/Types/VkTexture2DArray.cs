using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Vulkan wrapper for a two-dimensional texture array (<see cref="XRTexture2DArray"/>).
    /// Each element in <c>Data.Textures</c> maps to an array layer. Provides per-layer
    /// attachment views suitable for framebuffer targets (e.g. shadow cascades).
    /// </summary>
    internal sealed class VkTexture2DArray(VulkanRenderer api, XRTexture2DArray data) : VkImageBackedTexture<XRTexture2DArray>(api, data)
    {
        protected override ImageViewType DefaultImageViewType => ImageViewType.Type2DArray;

        protected override TextureLayout DescribeTexture()
        {
            XRTexture2D[] textures = Data.Textures;
            uint width = textures.Length > 0 ? Math.Max(textures[0].Width, 1u) : 1u;
            uint height = textures.Length > 0 ? Math.Max(textures[0].Height, 1u) : 1u;
            uint layers = (uint)Math.Max(textures.Length, 1);
            return new TextureLayout(new Extent3D(width, height, 1), layers, 1);
        }

        protected override AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            if (layerIndex >= 0)
            {
                uint baseLayer = (uint)Math.Max(layerIndex, 0);
                uint baseMip = (uint)Math.Max(mipLevel, 0);
                return new AttachmentViewKey(baseMip, 1, baseLayer, 1, ImageViewType.Type2D, AspectFlags);
            }

            return default;
        }

        protected override void PushTextureData()
        {
            Generate();

            XRTexture2D[] layers = Data.Textures;
            if (layers is null || layers.Length == 0)
            {
                Debug.VulkanWarning($"Texture array '{Data.Name ?? GetDescribingName()}' has no layers to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint arrayLayers = Math.Min((uint)layers.Length, ResolvedArrayLayers);
            for (uint layer = 0; layer < arrayLayers; layer++)
            {
                XRTexture2D layerTexture = layers[layer];
                var mipmaps = layerTexture.Mipmaps;
                if (mipmaps is null || mipmaps.Length == 0)
                    continue;

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
                        CopyBufferToImage(stagingBuffer, level, layer, 1, extent, (ulong)(mip.Data?.Length ?? 0));
                    }
                    finally
                    {
                        DestroyStagingBuffer(stagingBuffer, stagingMemory);
                    }
                }
            }

            if (Data.AutoGenerateMipmaps && ResolvedMipLevels > 1)
                GenerateMipmapsGPU();
            else
                TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }
    }
}
