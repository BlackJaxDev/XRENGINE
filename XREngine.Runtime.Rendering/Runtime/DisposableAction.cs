namespace XREngine;

internal sealed class DisposableAction : IDisposable
{
    public static readonly IDisposable Empty = new DisposableAction(null);
    private readonly Action? _dispose;
    private bool _disposed;

    public DisposableAction(Action? dispose)
    {
        _dispose = dispose;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _dispose?.Invoke();
    }
}
