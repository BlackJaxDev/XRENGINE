namespace XREngine.Rendering;

internal sealed class CompositeRendererBackendRegistrationLease(
    IDisposable first,
    IDisposable second) : IDisposable
{
    private IDisposable? _first = first;
    private IDisposable? _second = second;

    public void Dispose()
    {
        Interlocked.Exchange(ref _second, null)?.Dispose();
        Interlocked.Exchange(ref _first, null)?.Dispose();
    }
}
