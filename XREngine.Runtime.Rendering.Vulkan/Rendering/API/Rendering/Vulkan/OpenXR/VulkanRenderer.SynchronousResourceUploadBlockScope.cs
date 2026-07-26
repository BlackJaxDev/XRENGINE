using System.Threading;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class SynchronousResourceUploadBlockScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly VulkanRenderer? _previousThreadRenderer;
        private readonly int _previousThreadDepth;
        private bool _disposed;

        public SynchronousResourceUploadBlockScope(VulkanRenderer renderer, string reason)
        {
            _renderer = renderer;
            _previousThreadRenderer = _threadSynchronousResourceUploadBlockRenderer;
            _previousThreadDepth = _threadSynchronousResourceUploadBlockDepth;
            _threadSynchronousResourceUploadBlockRenderer = renderer;
            _threadSynchronousResourceUploadBlockDepth =
                ReferenceEquals(_previousThreadRenderer, renderer)
                    ? _previousThreadDepth + 1
                    : 1;

            Interlocked.Increment(ref renderer._synchronousResourceUploadBlockDepth);
            renderer.LogSynchronousResourceUploadBlock(reason);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _threadSynchronousResourceUploadBlockRenderer = _previousThreadRenderer;
            _threadSynchronousResourceUploadBlockDepth = _previousThreadDepth;

            if (Interlocked.Decrement(ref _renderer._synchronousResourceUploadBlockDepth) < 0)
                Volatile.Write(ref _renderer._synchronousResourceUploadBlockDepth, 0);
        }
    }
}
