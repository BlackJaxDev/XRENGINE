namespace XREngine.Rendering;

/// <summary>
/// Non-blocking status for a GPU fence inserted by the active renderer.
/// </summary>
public enum EGpuFenceStatus
{
    Pending,
    Signaled,
    Failed
}

/// <summary>
/// Backend-owned GPU fence that can be polled from the render thread without
/// waiting for queued GPU work to finish.
/// </summary>
public abstract class XRGpuFence : IDisposable
{
    private bool _disposed;

    public bool IsDisposed => _disposed;

    public EGpuFenceStatus Poll()
    {
        if (_disposed)
            return EGpuFenceStatus.Signaled;

        return PollCore();
    }

    protected abstract EGpuFenceStatus PollCore();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    protected abstract void DisposeCore();
}
