using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal sealed class SynchronousResourceUploadBlockScope : IDisposable
{
    private readonly VulkanOpenXrBackend _backend;
    private readonly VulkanOpenXrThreadExecutionState _threadState;
    private readonly int _previousThreadDepth;
    private bool _disposed;

    public SynchronousResourceUploadBlockScope(VulkanOpenXrBackend backend)
    {
        _backend = backend;
        _threadState = backend.CurrentThreadExecutionState;
        _previousThreadDepth = _threadState.SynchronousUploadBlockDepth;
        _threadState.SynchronousUploadBlockDepth = _previousThreadDepth + 1;

        Interlocked.Increment(ref backend.SynchronousResourceUploadBlockDepth);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _threadState.SynchronousUploadBlockDepth = _previousThreadDepth;

        if (Interlocked.Decrement(ref _backend.SynchronousResourceUploadBlockDepth) < 0)
            Volatile.Write(ref _backend.SynchronousResourceUploadBlockDepth, 0);
    }
}
