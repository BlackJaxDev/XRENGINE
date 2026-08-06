using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Command-runtime-local per-thread state. Renderer scopes explicitly install and
/// restore values; this workspace never owns a renderer or native device.
/// </summary>
internal sealed class VulkanCommandThreadWorkspace<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>
    where TRenderState : class
    where TPlannerState : struct
    where TSwitchingState : class
    where TFrameBuffer : class
    where TReadBuffer : struct
{
    private readonly ThreadLocal<VulkanCommandThreadContext<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>> _current =
        new(static () => new VulkanCommandThreadContext<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>(), trackAllValues: false);

    public VulkanCommandThreadContext<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer> Current
        => _current.Value ?? throw new InvalidOperationException("The Vulkan command thread workspace has been disposed.");

    public void ReleaseCurrentThread()
    {
        if (_current.IsValueCreated)
            _current.Value!.Reset();
    }
}

/// <summary>Mutable storage accessed only through a command-runtime scope token.</summary>
internal sealed class VulkanCommandThreadContext<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>
    where TRenderState : class
    where TPlannerState : struct
    where TSwitchingState : class
    where TFrameBuffer : class
    where TReadBuffer : struct
{
    public object? RenderStateOwner;
    public TRenderState? RenderState;
    public object? ResourcePlannerRuntimeStateOwner;
    public TPlannerState? ResourcePlannerRuntimeState;
    public object? FrameOpResourcePlannerSwitchingStateOwner;
    public TSwitchingState? FrameOpResourcePlannerSwitchingState;
    public object? FramebufferBindingOwner;
    public TFrameBuffer? BoundDrawFrameBuffer;
    public TFrameBuffer? BoundReadFrameBuffer;
    public TReadBuffer ReadBufferMode;
    public bool PreparedCommandChainEncodingActive;
    public object? BindingCaptureWorkspaceOwner;
    public object? BindingCaptureWorkspace;
    public VulkanFrameOpWorkspace? FrameOpWorkspace;

    public void Reset()
    {
        RenderStateOwner = null;
        RenderState = null;
        ResourcePlannerRuntimeStateOwner = null;
        ResourcePlannerRuntimeState = null;
        FrameOpResourcePlannerSwitchingStateOwner = null;
        FrameOpResourcePlannerSwitchingState = null;
        FramebufferBindingOwner = null;
        BoundDrawFrameBuffer = null;
        BoundReadFrameBuffer = null;
        ReadBufferMode = default;
        PreparedCommandChainEncodingActive = false;
        BindingCaptureWorkspaceOwner = null;
        BindingCaptureWorkspace = null;
        FrameOpWorkspace?.Reset();
        FrameOpWorkspace = null;
    }
}
