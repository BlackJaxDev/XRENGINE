namespace XREngine.Rendering.Shadows;

/// <summary>
/// Identifies the consumer-visible domains carried by one shadow request. Depth
/// invalidation requires a tile redraw; receiver sampling invalidation only
/// republishes sampling parameters against the existing depth tile.
/// </summary>
[Flags]
public enum ShadowInvalidationDomain
{
    None = 0,
    Depth = 1 << 0,
    ReceiverSampling = 1 << 1,
    AtlasPlacement = 1 << 2,
}
