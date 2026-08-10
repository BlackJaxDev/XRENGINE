namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Explicit command-thread render-state contract for OpenXR command work. It
/// installs no global current-renderer value and is safe to use on worker threads.
/// </summary>
internal readonly record struct VulkanOpenXrThreadRenderStateData(
    VulkanCommandThreadContext<
        VulkanStateTracker,
        ResourcePlannerRuntimeState,
        FrameOpResourcePlannerSwitchingState,
        XRFrameBuffer,
        EReadBufferMode> ThreadContext,
    object OwnerToken);

/// <summary>Restores command-thread render and framebuffer state after OpenXR recording.</summary>
internal readonly struct VulkanOpenXrThreadRenderStateScope : IDisposable
{
    private readonly VulkanOpenXrThreadRenderStateData _data;
    private readonly object? _previousRenderStateOwner;
    private readonly VulkanStateTracker? _previousRenderState;
    private readonly object? _previousFramebufferOwner;
    private readonly XRFrameBuffer? _previousDrawFrameBuffer;
    private readonly XRFrameBuffer? _previousReadFrameBuffer;
    private readonly EReadBufferMode _previousReadBufferMode;

    internal VulkanOpenXrThreadRenderStateScope(
        in VulkanOpenXrThreadRenderStateData data,
        VulkanStateTracker state)
    {
        _data = data;
        _previousRenderStateOwner = data.ThreadContext.RenderStateOwner;
        _previousRenderState = data.ThreadContext.RenderState;
        _previousFramebufferOwner = data.ThreadContext.FramebufferBindingOwner;
        _previousDrawFrameBuffer = data.ThreadContext.BoundDrawFrameBuffer;
        _previousReadFrameBuffer = data.ThreadContext.BoundReadFrameBuffer;
        _previousReadBufferMode = data.ThreadContext.ReadBufferMode;
        data.ThreadContext.RenderStateOwner = data.OwnerToken;
        data.ThreadContext.RenderState = state;
        data.ThreadContext.FramebufferBindingOwner = data.OwnerToken;
        data.ThreadContext.BoundDrawFrameBuffer = null;
        data.ThreadContext.BoundReadFrameBuffer = null;
        data.ThreadContext.ReadBufferMode = EReadBufferMode.ColorAttachment0;
    }

    public void Dispose()
    {
        _data.ThreadContext.RenderStateOwner = _previousRenderStateOwner;
        _data.ThreadContext.RenderState = _previousRenderState;
        _data.ThreadContext.FramebufferBindingOwner = _previousFramebufferOwner;
        _data.ThreadContext.BoundDrawFrameBuffer = _previousDrawFrameBuffer;
        _data.ThreadContext.BoundReadFrameBuffer = _previousReadFrameBuffer;
        _data.ThreadContext.ReadBufferMode = _previousReadBufferMode;
    }
}
