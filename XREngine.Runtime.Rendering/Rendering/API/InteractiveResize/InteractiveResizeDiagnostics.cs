using System.Threading;

namespace XREngine.Rendering;

public sealed class InteractiveResizeDiagnostics
{
    private long _callbackCount;
    private long _interactiveRenderCount;
    private long _suppressedRenderCount;
    private long _resizeQueueCount;
    private string _lastResizeReason = string.Empty;

    public long CallbackCount => Interlocked.Read(ref _callbackCount);
    public long InteractiveRenderCount => Interlocked.Read(ref _interactiveRenderCount);
    public long SuppressedRenderCount => Interlocked.Read(ref _suppressedRenderCount);
    public long ResizeQueueCount => Interlocked.Read(ref _resizeQueueCount);
    public string LastResizeReason => Volatile.Read(ref _lastResizeReason);

    public void RecordCallback(string reason)
    {
        Interlocked.Increment(ref _callbackCount);
        Volatile.Write(ref _lastResizeReason, reason);
    }

    public void RecordInteractiveRender(string reason)
    {
        Interlocked.Increment(ref _interactiveRenderCount);
        Volatile.Write(ref _lastResizeReason, reason);
    }

    public void RecordSuppressedRender(string reason)
    {
        Interlocked.Increment(ref _suppressedRenderCount);
        Volatile.Write(ref _lastResizeReason, reason);
    }

    public void RecordResizeQueued(string reason)
    {
        Interlocked.Increment(ref _resizeQueueCount);
        Volatile.Write(ref _lastResizeReason, reason);
    }
}
