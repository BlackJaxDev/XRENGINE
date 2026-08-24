using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

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
    FrameOpContext Context) 
    : FrameOp(PassIndex, Target, Context)
{
    public bool ClearColor { get; private set; } = ClearColor;
    public bool ClearDepth { get; private set; } = ClearDepth;
    public bool ClearStencil { get; private set; } = ClearStencil;
    public ColorF4 Color { get; private set; } = Color;
    public float Depth { get; private set; } = Depth;
    public uint Stencil { get; private set; } = Stencil;
    public Rect2D Rect { get; private set; } = Rect;
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.Clear;

    internal static ClearOp Rent(
        int passIndex,
        XRFrameBuffer? target,
        bool clearColor,
        bool clearDepth,
        bool clearStencil,
        ColorF4 color,
        float depth,
        uint stencil,
        Rect2D rect,
        in FrameOpContext context)
    {
        bool frameOwned = TryRentForCurrentFrame(context, out ClearOp? reusable);
        if (reusable is null)
        {
            ClearOp created = new(
                passIndex,
                target,
                clearColor,
                clearDepth,
                clearStencil,
                color,
                depth,
                stencil,
                rect,
                context);
            return frameOwned ? RetainForCurrentFrame(created, context) : created;
        }

        reusable.Reset(
            passIndex,
            target,
            clearColor,
            clearDepth,
            clearStencil,
            color,
            depth,
            stencil,
            rect,
            context);
        return reusable;
    }

    private void Reset(
        int passIndex,
        XRFrameBuffer? target,
        bool clearColor,
        bool clearDepth,
        bool clearStencil,
        ColorF4 color,
        float depth,
        uint stencil,
        Rect2D rect,
        in FrameOpContext context)
    {
        PassIndex = passIndex;
        Target = target;
        ClearColor = clearColor;
        ClearDepth = clearDepth;
        ClearStencil = clearStencil;
        Color = color;
        Depth = depth;
        Stencil = stencil;
        Rect = rect;
        Context = context;
    }
}
