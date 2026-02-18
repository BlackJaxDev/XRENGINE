using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Vulkan wrapper for a one-dimensional texture array (<see cref="XRTexture1DArray"/>).
    /// Each element in <c>Data.Textures</c> becomes one array layer, and mipmaps are uploaded
    /// per-layer with <see cref="ImageViewType.Type1DArray"/>.
    /// </summary>
    internal sealed class VkTexture1DArray(VulkanRenderer api, XRTexture1DArray data) : VkImageBackedTexture<XRTexture1DArray>(api, data)
    {
        protected override ImageType TextureImageType => ImageType.Type1D;
        protected override ImageViewType DefaultImageViewType => ImageViewType.Type1DArray;

        protected override TextureLayout DescribeTexture()
        {
            XRTexture1D[] textures = Data.Textures;
            uint width = textures.Length > 0 ? Math.Max(textures[0].Width, 1u) : 1u;
            uint layers = (uint)Math.Max(textures.Length, 1);
            uint mipLevels = (uint)Math.Max(
                textures.Length > 0 ? textures.Max(t => t?.Mipmaps?.Length ?? 1) : 1,
                1);
            return new TextureLayout(new Extent3D(width, 1, 1), layers, mipLevels);
        }

        protected override AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            if (layerIndex < 0 && mipLevel <= 0)
                return default;

            uint baseLayer = (uint)Math.Max(layerIndex, 0);
            uint baseMip = (uint)Math.Max(mipLevel, 0);
            return new AttachmentViewKey(baseMip, 1, baseLayer, 1, ImageViewType.Type1D, AspectFlags);
        }

        protected override void PushTextureData()
        {
            Generate();

            XRTexture1D[] layers = Data.Textures;
            if (layers is null || layers.Length == 0)
            {
                Debug.VulkanWarning($"1D texture array '{Data.Name ?? GetDescribingName()}' has no layers to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint arrayLayers = Math.Min((uint)layers.Length, ResolvedArrayLayers);
            for (uint layer = 0; layer < arrayLayers; layer++)
            {
                XRTexture1D layerTexture = layers[layer];
                Mipmap1D[] mipmaps = layerTexture.Mipmaps;
                if (mipmaps is null || mipmaps.Length == 0)
                    continue;

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
                        CopyBufferToImage(stagingBuffer, level, layer, 1, extent);
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
