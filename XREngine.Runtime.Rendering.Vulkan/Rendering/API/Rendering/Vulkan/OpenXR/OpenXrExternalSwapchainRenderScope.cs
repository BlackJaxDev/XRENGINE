using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal sealed class OpenXrExternalSwapchainRenderScope : IDisposable
{
    private readonly VulkanOpenXrBackend _backend;
    private readonly VulkanOpenXrThreadExecutionState _threadState;
    private readonly VulkanOpenXrFrameContext _previousFrameContext;
    private readonly OpenXrEyeRenderTargetContext _previousNativeTargetContext;
    private readonly int _previousThreadDepth;
    private readonly BoundingRectangle _previousGlobalRegion;
    private bool _disposed;

    public OpenXrExternalSwapchainRenderScope(
        VulkanOpenXrBackend backend,
        in VulkanOpenXrFrameContext frameContext)
    {
        _backend = backend;
        _threadState = backend.CurrentThreadExecutionState;
        _previousFrameContext = _threadState.FrameContext;
        _previousNativeTargetContext = _threadState.NativeTargetContext;
        _previousThreadDepth = _threadState.ExternalSwapchainDepth;
        _previousGlobalRegion = backend.ExternalSwapchainTargetRegion;

        _threadState.FrameContext = frameContext;
        _threadState.NativeTargetContext = default;
        _threadState.ExternalSwapchainDepth = _previousThreadDepth + 1;

        Interlocked.Increment(ref backend.ExternalSwapchainRenderDepth);
        backend.ExternalSwapchainTargetRegion = frameContext.TargetRegion;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _threadState.FrameContext = _previousFrameContext;
        _threadState.NativeTargetContext = _previousNativeTargetContext;
        _threadState.ExternalSwapchainDepth = _previousThreadDepth;

        if (Interlocked.Decrement(ref _backend.ExternalSwapchainRenderDepth) <= 0)
        {
            Volatile.Write(ref _backend.ExternalSwapchainRenderDepth, 0);
            _backend.ExternalSwapchainTargetRegion = default;
        }
        else
        {
            _backend.ExternalSwapchainTargetRegion = _previousGlobalRegion;
        }
    }
}
