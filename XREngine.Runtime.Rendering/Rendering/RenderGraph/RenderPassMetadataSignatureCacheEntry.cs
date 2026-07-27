namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Stores a backend-neutral signature for a stable render-pass metadata collection.
/// Keeping this value in the non-collectible contract assembly prevents backend cache
/// handles from retaining a retired renderer module.
/// </summary>
public sealed class RenderPassMetadataSignatureCacheEntry
{
    private int _revisionStamp = int.MinValue;
    private int _signature;

    public int RevisionStamp
    {
        get => Volatile.Read(ref _revisionStamp);
        set => Volatile.Write(ref _revisionStamp, value);
    }

    public int Signature
    {
        get => Volatile.Read(ref _signature);
        set => Volatile.Write(ref _signature, value);
    }
}
