namespace XREngine.Rendering.Vulkan;

internal sealed record TextureUploadFrameOp(
    VulkanImportedTexturePendingUpload Upload,
    FrameOpContext Context) : FrameOp(int.MinValue, null, Context);
