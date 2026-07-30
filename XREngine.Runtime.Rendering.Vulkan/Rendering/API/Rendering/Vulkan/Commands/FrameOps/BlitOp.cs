namespace XREngine.Rendering.Vulkan;

internal sealed record BlitOp(
    int PassIndex,
    XRFrameBuffer? InFbo,
    XRFrameBuffer? OutFbo,
    int InX,
    int InY,
    uint InW,
    uint InH,
    int OutX,
    int OutY,
    uint OutW,
    uint OutH,
    EReadBufferMode ReadBufferMode,
    bool ColorBit,
    bool DepthBit,
    bool StencilBit,
    bool LinearFilter,
    FrameOpContext Context) : FrameOp(PassIndex, OutFbo, Context)
{
    public XRFrameBuffer? InFbo { get; internal set; } = InFbo;
    public XRFrameBuffer? OutFbo { get; internal set; } = OutFbo;
}
