namespace XREngine.Rendering;

/// <summary>
/// Holds one streaming-record monitor across the exact backend publication
/// transaction so cancellation or supersession cannot pass a stale precheck.
/// </summary>
internal sealed class TextureStreamingPublicationAuthority(object sync) : IDisposable
{
    private object? _sync = sync;

    public void Dispose()
    {
        object? heldSync = Interlocked.Exchange(ref _sync, null);
        if (heldSync is not null)
            Monitor.Exit(heldSync);
    }
}
