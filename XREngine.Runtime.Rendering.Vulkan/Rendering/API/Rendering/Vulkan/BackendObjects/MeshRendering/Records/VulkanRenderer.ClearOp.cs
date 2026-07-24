using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record ClearOp(
        int PassIndex,
        XRFrameBuffer? Target,
        bool ClearColor,
        bool ClearDepth,
        bool ClearStencil,
        ColorF4 Color,
        float Depth,
        uint Stencil,
        Rect2D Rect,
        FrameOpContext Context) : FrameOp(PassIndex, Target, Context);
}
