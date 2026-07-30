namespace XREngine.Rendering;

/// <summary>
/// Machine-readable reason a surface could not be encoded into the visibility target.
/// Overflow is always rejected; identifiers are never truncated or wrapped.
/// </summary>
public enum EAdvancedVisibilityPayloadOverflow : uint
{
    None = 0u,
    InvalidDraw = 1u,
    DrawIndex = 2u,
    PrimitiveIndex = 3u,
    ViewIndex = 4u,
    Producer = 5u,
    RasterOrigin = 6u,
    PayloadVersion = 7u,
    InvalidProducerPayload = 8u,
}
