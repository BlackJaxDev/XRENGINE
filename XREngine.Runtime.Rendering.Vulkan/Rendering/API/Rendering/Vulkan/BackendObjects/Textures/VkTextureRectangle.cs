using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Vulkan wrapper for a rectangle texture (<see cref="XRTextureRectangle"/>).
/// Rectangle textures use a single mip level and are addressed by non-normalised
/// texel coordinates. The image is always one mip-level deep.
/// </summary>
internal sealed class VkTextureRectangle(VulkanBackendObjectContext backendContext, IRenderApiWrapperOwner owner, XRTextureRectangle data) : VkImageBackedTexture<XRTextureRectangle>(backendContext, owner, data)
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

        Extent3D extent = new(Math.Max(Data.Width, 1u), Math.Max(Data.Height, 1u), 1);
        _ = UploadStagingDataToImage(Data.Data, 0, 0, 1, extent);

        TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
    }
}
