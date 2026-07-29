using System.Threading;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class SynchronousResourceUploadBlockScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly VulkanOpenXrThreadExecutionState _threadState;
        private readonly int _previousThreadDepth;
        private bool _disposed;

        public SynchronousResourceUploadBlockScope(VulkanRenderer renderer, string reason)
        {
            _renderer = renderer;
            _threadState = renderer._openXrBackend.CurrentThreadExecutionState;
            _previousThreadDepth = _threadState.SynchronousUploadBlockDepth;
            _threadState.SynchronousUploadBlockDepth = _previousThreadDepth + 1;

            Interlocked.Increment(ref renderer._openXrBackend.SynchronousResourceUploadBlockDepth);
            renderer.LogSynchronousResourceUploadBlock(reason);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _threadState.SynchronousUploadBlockDepth = _previousThreadDepth;

            if (Interlocked.Decrement(ref _renderer._openXrBackend.SynchronousResourceUploadBlockDepth) < 0)
                Volatile.Write(ref _renderer._openXrBackend.SynchronousResourceUploadBlockDepth, 0);
        }
    }
}
