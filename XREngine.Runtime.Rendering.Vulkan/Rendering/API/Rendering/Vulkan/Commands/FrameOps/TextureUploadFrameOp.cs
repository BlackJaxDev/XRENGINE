namespace XREngine.Rendering.Vulkan;

internal sealed record TextureUploadFrameOp(VulkanImportedTexturePendingUpload Upload, FrameOpContext Context) 
    : FrameOp(int.MinValue, null, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.TextureUpload;
    internal override bool RequiresPrimaryRecordingContext => false;

}
