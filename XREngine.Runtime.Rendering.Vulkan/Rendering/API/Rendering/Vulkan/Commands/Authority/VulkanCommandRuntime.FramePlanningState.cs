using Silk.NET.Vulkan;
using XREngine.Data.Geometry;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns command-thread render state and framebuffer bindings used while a
/// frozen frame-plan generation is being recorded.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    internal VulkanFrameOpWorkspace GetFrameOpWorkspace()
        => ThreadWorkspace.Current.FrameOpWorkspace ??= new VulkanFrameOpWorkspace();

    internal VulkanStateTracker ActiveState
    {
        get
        {
            VulkanCommandThreadContext context = ThreadWorkspace.Current;
            return ReferenceEquals(context.RenderStateOwner, this) && context.RenderState is not null
                ? context.RenderState
                : StateTracker;
        }
    }

    internal XRFrameBuffer? ActiveBoundDrawFrameBuffer
    {
        get
        {
            VulkanCommandThreadContext context = ThreadWorkspace.Current;
            return ReferenceEquals(context.FramebufferBindingOwner, this)
                ? context.BoundDrawFrameBuffer
                : CommandBuffers.BoundDrawFrameBuffer;
        }
        set
        {
            VulkanCommandThreadContext context = ThreadWorkspace.Current;
            if (ReferenceEquals(context.FramebufferBindingOwner, this))
            {
                context.BoundDrawFrameBuffer = value;
                return;
            }

            CommandBuffers.BoundDrawFrameBuffer = value;
        }
    }

    internal XRFrameBuffer? ActiveBoundReadFrameBuffer
    {
        get
        {
            VulkanCommandThreadContext context = ThreadWorkspace.Current;
            return ReferenceEquals(context.FramebufferBindingOwner, this)
                ? context.BoundReadFrameBuffer
                : CommandBuffers.BoundReadFrameBuffer;
        }
        set
        {
            VulkanCommandThreadContext context = ThreadWorkspace.Current;
            if (ReferenceEquals(context.FramebufferBindingOwner, this))
            {
                context.BoundReadFrameBuffer = value;
                return;
            }

            CommandBuffers.BoundReadFrameBuffer = value;
        }
    }

    internal EReadBufferMode ActiveReadBufferMode
    {
        get
        {
            VulkanCommandThreadContext context = ThreadWorkspace.Current;
            return ReferenceEquals(context.FramebufferBindingOwner, this)
                ? context.ReadBufferMode
                : CommandBuffers.ReadBufferMode;
        }
        set
        {
            VulkanCommandThreadContext context = ThreadWorkspace.Current;
            if (ReferenceEquals(context.FramebufferBindingOwner, this))
            {
                context.ReadBufferMode = value;
                return;
            }

            CommandBuffers.ReadBufferMode = value;
        }
    }

    internal XRFrameBuffer? GetCurrentDrawFrameBuffer()
    {
        if (XRFrameBuffer.BoundForWriting is { } directlyBoundTarget)
            return directlyBoundTarget;

        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        XRRenderPipelineInstance.RenderingState.ScopedRenderTargetBinding? binding =
            pipeline?.RenderState.CurrentRenderTargetBinding;
        return binding is { Write: true, FrameBuffer: { } scopedTarget }
            ? scopedTarget
            : ActiveBoundDrawFrameBuffer;
    }

    internal XRFrameBuffer? ResolveCurrentFrameOpDrawTarget()
        => GetCurrentDrawFrameBuffer();

    internal uint ResolveCurrentDrawViewMask()
    {
        XRFrameBuffer? frameBuffer = GetCurrentDrawFrameBuffer();
        return frameBuffer is null
            ? 0u
            : GenericToAPI<VkFrameBuffer>(frameBuffer)?.MultiviewViewMask ?? 0u;
    }

    internal Extent2D ResolveCurrentDrawTargetExtent(Extent2D? externalExtent = null)
    {
        XRFrameBuffer? frameBuffer = GetCurrentDrawFrameBuffer();
        if (frameBuffer is not null)
            return ResolveFrameBufferDrawExtent(frameBuffer);

        return externalExtent ?? ActiveState.GetCurrentTargetExtent();
    }

    internal static Viewport CreateVulkanViewport(
        BoundingRectangle region,
        Extent2D targetExtent)
    {
        if (RuntimeEngine.Rendering.Settings.ClipSpaceYDirection == ERenderClipSpaceYDirection.YDown)
        {
            return new Viewport
            {
                X = region.X,
                Y = targetExtent.Height - (region.Y + region.Height),
                Width = region.Width,
                Height = region.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
        }

        return new Viewport
        {
            X = region.X,
            Y = targetExtent.Height - region.Y,
            Width = region.Width,
            Height = -region.Height,
            MinDepth = 0.0f,
            MaxDepth = 1.0f
        };
    }

    internal void ReleaseCurrentThreadFramePlanningState()
    {
        VulkanCommandThreadContext context = ThreadWorkspace.Current;
        if (ReferenceEquals(context.RenderStateOwner, this))
        {
            context.RenderStateOwner = null;
            context.RenderState = null;
        }

        if (ReferenceEquals(context.ResourcePlannerRuntimeStateOwner, this))
        {
            context.ResourcePlannerRuntimeStateOwner = null;
            context.ResourcePlannerRuntimeState = null;
            context.ResourcePlannerRuntimeGeneration = null;
        }

        if (ReferenceEquals(context.FrameOpResourcePlannerSwitchingStateOwner, this))
        {
            context.FrameOpResourcePlannerSwitchingStateOwner = null;
            context.FrameOpResourcePlannerSwitchingState = null;
        }

        context.FrameOpWorkspace?.Reset();
        context.FrameOpWorkspace = null;

        if (!ReferenceEquals(context.FramebufferBindingOwner, this))
            return;

        context.FramebufferBindingOwner = null;
        context.BoundDrawFrameBuffer = null;
        context.BoundReadFrameBuffer = null;
        context.ReadBufferMode = default;
    }
}
