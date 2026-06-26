namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class OpenXrExternalSwapchainRenderScope(
        VulkanRenderer renderer,
        BoundingRectangle previousRegion) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            renderer._openXrExternalSwapchainRenderDepth = Math.Max(0, renderer._openXrExternalSwapchainRenderDepth - 1);
            renderer._openXrExternalSwapchainTargetRegion = previousRegion;
        }
    }
}
