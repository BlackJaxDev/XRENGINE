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
    FrameOpContext Context) 
    : FrameOp(PassIndex, OutFbo, Context)
{
    private XRFrameBuffer? _inFbo = InFbo;
    private XRFrameBuffer? _outFbo = OutFbo;
    public XRFrameBuffer? InFbo
    {
        get => _inFbo;
        internal set
            => _inFbo = value;
    }
    public XRFrameBuffer? OutFbo
    {
        get => _outFbo;
        internal set
            => _outFbo = value;
    }
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.Blit;

}
