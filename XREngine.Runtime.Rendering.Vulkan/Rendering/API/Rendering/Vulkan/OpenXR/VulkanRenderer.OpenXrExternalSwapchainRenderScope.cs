using System.Threading;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class OpenXrExternalSwapchainRenderScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly VulkanOpenXrThreadExecutionState _threadState;
        private readonly VulkanOpenXrFrameContext _previousFrameContext;
        private readonly int _previousThreadDepth;
        private readonly BoundingRectangle _previousGlobalRegion;
        private bool _disposed;

        public OpenXrExternalSwapchainRenderScope(
            VulkanRenderer renderer,
            in VulkanOpenXrFrameContext frameContext)
        {
            _renderer = renderer;
            _threadState = renderer._openXrBackend.CurrentThreadExecutionState;
            _previousFrameContext = _threadState.FrameContext;
            _previousThreadDepth = _threadState.ExternalSwapchainDepth;
            _previousGlobalRegion = renderer._openXrBackend.ExternalSwapchainTargetRegion;

            _threadState.FrameContext = frameContext;
            _threadState.ExternalSwapchainDepth = _previousThreadDepth + 1;

            Interlocked.Increment(ref renderer._openXrBackend.ExternalSwapchainRenderDepth);
            renderer._openXrBackend.ExternalSwapchainTargetRegion = frameContext.TargetRegion;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _threadState.FrameContext = _previousFrameContext;
            _threadState.ExternalSwapchainDepth = _previousThreadDepth;

            if (Interlocked.Decrement(ref _renderer._openXrBackend.ExternalSwapchainRenderDepth) <= 0)
            {
                Volatile.Write(ref _renderer._openXrBackend.ExternalSwapchainRenderDepth, 0);
                _renderer._openXrBackend.ExternalSwapchainTargetRegion = default;
            }
            else
            {
                _renderer._openXrBackend.ExternalSwapchainTargetRegion = _previousGlobalRegion;
            }
        }
    }
}
