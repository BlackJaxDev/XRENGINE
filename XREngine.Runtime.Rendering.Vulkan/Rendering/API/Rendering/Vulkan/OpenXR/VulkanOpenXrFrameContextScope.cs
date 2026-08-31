namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Installs the immutable OpenXR output identity for an engine-owned render
/// target without claiming that target is an externally owned swapchain image.
/// </summary>
internal ref struct VulkanOpenXrFrameContextScope
{
    private VulkanOpenXrThreadExecutionState? _threadState;
    private readonly VulkanOpenXrFrameContext _previousFrameContext;
    private readonly OpenXrEyeRenderTargetContext _previousNativeTargetContext;

    internal VulkanOpenXrFrameContextScope(
        VulkanOpenXrBackend backend,
        in VulkanOpenXrFrameContext frameContext)
    {
        _threadState = backend.CurrentThreadExecutionState;
        _previousFrameContext = _threadState.FrameContext;
        _previousNativeTargetContext = _threadState.NativeTargetContext;
        _threadState.FrameContext = frameContext;
        _threadState.NativeTargetContext = default;
    }

    public void Dispose()
    {
        if (_threadState is not { } threadState)
            return;

        threadState.FrameContext = _previousFrameContext;
        threadState.NativeTargetContext = _previousNativeTargetContext;
        _threadState = null;
    }
}
