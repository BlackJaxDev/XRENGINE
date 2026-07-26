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
            // If SmallestAllowedMipmapLevel was explicitly set (below its default of 1000),
            // the texture needs a specific mip chain (e.g. bloom stereo). Use SmallestMipmapLevel + 1.
            // Otherwise, default to 1 mip level (framebuffer targets don't need mip chains).
            bool hasExplicitMipRange = Data.SmallestAllowedMipmapLevel < 1000;
            uint mipLevels = Data.AutoGenerateMipmaps || hasExplicitMipRange
                ? (uint)Math.Max(1, Data.SmallestMipmapLevel + 1)
                : 1;
            return new TextureLayout(new Extent3D(width, height, 1), layers, mipLevels);
        }

        protected override AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            uint baseMip = ClampAttachmentMipLevel(mipLevel);

            if (layerIndex >= 0)
            {
                uint baseLayer = ClampAttachmentLayerIndex(layerIndex);
                return new AttachmentViewKey(baseMip, 1, baseLayer, 1, ImageViewType.Type2D, AspectFlags);
            }

            if (baseMip != 0 || ResolvedMipLevels > 1)
            {
                uint layerCount = Math.Max(ResolvedArrayLayers, 1u);
                return new AttachmentViewKey(baseMip, 1, 0, layerCount, ImageViewType.Type2DArray, AspectFlags);
            }

            return default;
        }

        protected override void PushTextureData()
        {
            XRTexture2D[] layers = Data.Textures;
            if (layers is null || layers.Length == 0)
            {
                Debug.VulkanWarning($"Texture array '{Data.Name ?? GetDescribingName()}' has no layers to upload.");
                return;
            }

            RecreateImageForFullTextureDataUpload("full 2D texture array data upload");
            Generate();

            bool uploadedAny = false;
            uint arrayLayers = Math.Min((uint)layers.Length, ResolvedArrayLayers);
            for (uint layer = 0; layer < arrayLayers; layer++)
            {
                XRTexture2D layerTexture = layers[layer];
                var mipmaps = layerTexture.Mipmaps;
                if (mipmaps is null || mipmaps.Length == 0)
                    continue;

                uint levelCount = Data.AutoGenerateMipmaps
                    ? 1u
                    : Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
                for (uint level = 0; level < levelCount; level++)
                {
                    Mipmap2D? mip = mipmaps[level];
                    if (mip is null)
                        continue;

                    if (!TryCreateStagingBuffer(mip.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                        continue;

                    try
                    {
                        if (!uploadedAny)
                        {
                            ImageLayout oldLayout = CurrentImageLayout;
                            if (oldLayout != ImageLayout.TransferDstOptimal)
                                TransitionImageLayout(oldLayout, ImageLayout.TransferDstOptimal);
                            uploadedAny = true;
                        }

                        Extent3D extent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1);
                        CopyBufferToImage(stagingBuffer, level, layer, 1, extent, (ulong)(mip.Data?.Length ?? 0));
                    }
                    finally
                    {
                        DestroyStagingBuffer(stagingBuffer, stagingMemory);
                    }
                }
            }

            if (!uploadedAny)
                return;

            if (Data.AutoGenerateMipmaps && ResolvedMipLevels > 1)
                GenerateMipmapsGPU();
            else
                TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }
    }
}
