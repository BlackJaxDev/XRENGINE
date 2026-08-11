namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Explicit command-thread render-state contract for OpenXR command work. It
/// installs no global current-renderer value and is safe to use on worker threads.
/// </summary>
/// <summary>Restores command-thread render and framebuffer state after OpenXR recording.</summary>
internal readonly struct VulkanOpenXrThreadRenderStateScope : IDisposable
{
    private readonly VulkanOpenXrThreadRenderStateData _data;
    private readonly VulkanCommandRuntime? _previousRenderStateOwner;
    private readonly VulkanStateTracker? _previousRenderState;
    private readonly VulkanCommandRuntime? _previousFramebufferOwner;
    private readonly XRFrameBuffer? _previousDrawFrameBuffer;
    private readonly XRFrameBuffer? _previousReadFrameBuffer;
    private readonly EReadBufferMode _previousReadBufferMode;

    internal VulkanOpenXrThreadRenderStateScope(
        in VulkanOpenXrThreadRenderStateData data,
        VulkanStateTracker state)
    {
        if (!ReferenceEquals(data.ThreadContext.Owner, data.Owner))
            throw new InvalidOperationException("The OpenXR render-state scope must use its command runtime's thread workspace.");

        _data = data;
        _previousRenderStateOwner = data.ThreadContext.RenderStateOwner;
        _previousRenderState = data.ThreadContext.RenderState;
        _previousFramebufferOwner = data.ThreadContext.FramebufferBindingOwner;
        _previousDrawFrameBuffer = data.ThreadContext.BoundDrawFrameBuffer;
        _previousReadFrameBuffer = data.ThreadContext.BoundReadFrameBuffer;
        _previousReadBufferMode = data.ThreadContext.ReadBufferMode;
        data.ThreadContext.RenderStateOwner = data.Owner;
        data.ThreadContext.RenderState = state;
        data.ThreadContext.FramebufferBindingOwner = data.Owner;
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
