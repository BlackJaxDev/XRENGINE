namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record TextureUploadFrameOp(
        VulkanImportedTexturePendingUpload Upload,
        FrameOpContext Context) : FrameOp(int.MinValue, null, Context);
}
