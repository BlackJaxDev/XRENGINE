using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials.Textures;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal sealed class VkTextureCube(VulkanBackendObjectContext backendContext, IRenderApiWrapperOwner owner, XRTextureCube data) : VkImageBackedTexture<XRTextureCube>(backendContext, owner, data)
{
    private const ImageCreateFlags CubeCompatibleFlag = (ImageCreateFlags)0x10;

    protected override ImageCreateFlags AdditionalImageFlags => CubeCompatibleFlag;
    protected override ImageViewType DefaultImageViewType => ImageViewType.TypeCube;

    protected override TextureLayout DescribeTexture()
    {
        uint extent = Math.Max(Data.Extent, 1u);
        uint mipLevels = (uint)Math.Max(Data.Mipmaps?.Length ?? 1, 1);
        return new TextureLayout(new Extent3D(extent, extent, 1), 6, mipLevels);
    }

    protected override AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
    {
        if (layerIndex >= 0 && layerIndex < 6)
        {
            uint baseMip = ClampAttachmentMipLevel(mipLevel);
            uint baseLayer = ClampAttachmentLayerIndex(layerIndex);
            return new AttachmentViewKey(baseMip, 1, baseLayer, 1, ImageViewType.Type2D, AspectFlags);
        }

        return default;
    }

    protected override void PushTextureData()
    {
        CubeMipmap[] mipmaps = Data.Mipmaps;
        if (mipmaps is null || mipmaps.Length == 0)
        {
            Debug.VulkanWarning($"Cubemap '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
            return;
        }

        RecreateImageForFullTextureDataUpload("full cubemap texture data upload");
        Generate();
        TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

        uint levelCount = Data.AutoGenerateMipmaps
            ? 1u
            : Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
        for (uint level = 0; level < levelCount; level++)
        {
            CubeMipmap? cubeMip = mipmaps[level];
            if (cubeMip is null)
                continue;

            uint faceCount = Math.Min((uint)cubeMip.Sides.Length, ResolvedArrayLayers);
            for (uint face = 0; face < faceCount; face++)
            {
                Mipmap2D side = cubeMip.Sides[face];
                Extent3D extent = new(Math.Max(side.Width, 1u), Math.Max(side.Height, 1u), 1);
                _ = UploadStagingDataToImage(side.Data, level, face, 1, extent);
            }
        }

        if (Data.AutoGenerateMipmaps && ResolvedMipLevels > 1)
            GenerateMipmapsGPU();
        else
            TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
    }
}
