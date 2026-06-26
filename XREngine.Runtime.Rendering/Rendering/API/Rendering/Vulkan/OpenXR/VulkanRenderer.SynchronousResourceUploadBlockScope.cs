namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class SynchronousResourceUploadBlockScope(VulkanRenderer renderer) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            renderer._synchronousResourceUploadBlockDepth = Math.Max(0, renderer._synchronousResourceUploadBlockDepth - 1);
            _disposed = true;
        }
    }
}
