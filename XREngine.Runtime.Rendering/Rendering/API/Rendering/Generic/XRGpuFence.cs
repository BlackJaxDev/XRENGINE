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

    /// <summary>
    /// Reports whether the command stream containing this fence reached backend submission.
    /// Immediate command-stream backends accept a fence when it is created; deferred backends
    /// override this property so producers can distinguish queued work from abandoned work.
    /// </summary>
    public virtual EGpuFenceSubmissionStatus SubmissionStatus
        => EGpuFenceSubmissionStatus.Submitted;

    public EGpuFenceStatus Poll()
    {
        if (_disposed)
            return EGpuFenceStatus.Signaled;

        return PollCore();
    }

    protected abstract EGpuFenceStatus PollCore();

    /// <summary>Reactivates a backend-owned fence returned from a bounded pool.</summary>
    protected void ResetForReuse()
        => _disposed = false;

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
