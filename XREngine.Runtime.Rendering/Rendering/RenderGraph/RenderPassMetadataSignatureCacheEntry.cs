namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Stores a backend-neutral signature for a stable render-pass metadata collection.
/// Keeping this value in the non-collectible contract assembly prevents backend cache
/// handles from retaining a retired renderer module.
/// </summary>
public sealed class RenderPassMetadataSignatureCacheEntry
{
    public int RevisionStamp { get; set; } = int.MinValue;

    public int Signature { get; set; }
}
