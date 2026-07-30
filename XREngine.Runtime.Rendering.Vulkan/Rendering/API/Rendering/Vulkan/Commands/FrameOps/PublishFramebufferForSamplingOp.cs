namespace XREngine.Rendering.Vulkan;

internal sealed record PublishFramebufferForSamplingOp(
    int PassIndex,
    XRFrameBuffer FrameBuffer,
    FrameOpContext Context) : FrameOp(PassIndex, FrameBuffer, Context)
{
    public XRFrameBuffer FrameBuffer { get; internal set; } = FrameBuffer;
}
