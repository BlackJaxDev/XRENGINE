namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanPipelineForegroundWaitObserver
{
    private long _count;
    private long _ticks;

    internal long Count => Volatile.Read(ref _count);
    internal long Ticks => Volatile.Read(ref _ticks);

    internal void Record(long ticks)
    {
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _ticks, ticks);
    }

    internal void Reset()
    {
        Volatile.Write(ref _count, 0);
        Volatile.Write(ref _ticks, 0);
    }
}

internal ref struct VulkanPipelineForegroundWaitObservationScope
{
    [ThreadStatic]
    private static VulkanPipelineForegroundWaitObserver? _current;

    private readonly VulkanPipelineForegroundWaitObserver? _previous;
    private readonly int _threadId;
    private bool _disposed;

    internal VulkanPipelineForegroundWaitObservationScope(VulkanPipelineForegroundWaitObserver observer)
    {
        _previous = _current;
        _threadId = Environment.CurrentManagedThreadId;
        _disposed = false;
        _current = observer;
    }

    internal static void RecordCurrent(long ticks)
        => _current?.Record(ticks);

    public void Dispose()
    {
        if (_disposed)
            return;
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Foreground pipeline wait observation scopes must end on their owning thread.");
        _current = _previous;
        _disposed = true;
    }
}