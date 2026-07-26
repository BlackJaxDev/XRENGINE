namespace XREngine.Rendering;

internal sealed class RendererBackendRegistrationLease(
    RendererBackendCatalog owner,
    RendererBackendId id,
    RendererBackendCatalogEntry entry) : IDisposable
{
    private RendererBackendCatalog? _owner = owner;

    public void Dispose()
    {
        RendererBackendCatalog? currentOwner = Interlocked.Exchange(ref _owner, null);
        currentOwner?.Unregister(id, entry);
    }
}
