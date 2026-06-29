using System.Threading;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class OpenXrExternalSwapchainRenderScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly VulkanRenderer? _previousThreadRenderer;
        private readonly int _previousThreadDepth;
        private readonly BoundingRectangle _previousThreadRegion;
        private readonly BoundingRectangle _previousGlobalRegion;
        private bool _disposed;

        public OpenXrExternalSwapchainRenderScope(
            VulkanRenderer renderer,
            BoundingRectangle region)
        {
            _renderer = renderer;
            _previousThreadRenderer = _threadOpenXrExternalSwapchainRenderer;
            _previousThreadDepth = _threadOpenXrExternalSwapchainRenderDepth;
            _previousThreadRegion = _threadOpenXrExternalSwapchainTargetRegion;
            _previousGlobalRegion = renderer._openXrExternalSwapchainTargetRegion;

            _threadOpenXrExternalSwapchainRenderer = renderer;
            _threadOpenXrExternalSwapchainRenderDepth =
                ReferenceEquals(_previousThreadRenderer, renderer)
                    ? _previousThreadDepth + 1
                    : 1;
            _threadOpenXrExternalSwapchainTargetRegion = region;

            Interlocked.Increment(ref renderer._openXrExternalSwapchainRenderDepth);
            renderer._openXrExternalSwapchainTargetRegion = region;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _threadOpenXrExternalSwapchainRenderer = _previousThreadRenderer;
            _threadOpenXrExternalSwapchainRenderDepth = _previousThreadDepth;
            _threadOpenXrExternalSwapchainTargetRegion = _previousThreadRegion;

            if (Interlocked.Decrement(ref _renderer._openXrExternalSwapchainRenderDepth) <= 0)
            {
                Volatile.Write(ref _renderer._openXrExternalSwapchainRenderDepth, 0);
                _renderer._openXrExternalSwapchainTargetRegion = default;
            }
            else
            {
                _renderer._openXrExternalSwapchainTargetRegion = _previousGlobalRegion;
            }
        }
    }
}
