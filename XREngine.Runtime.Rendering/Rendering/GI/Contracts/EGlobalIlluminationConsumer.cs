namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Identifies the material or presentation path that consumes a GI contribution.
/// A screen-space resolve must only advertise paths for which it has geometric coverage.
/// </summary>
[Flags]
public enum EGlobalIlluminationConsumer
{
    None = 0,
    DeferredOpaque = 1 << 0,
    ForwardOpaque = 1 << 1,
    Transparent = 1 << 2,
    WorldSpace = 1 << 3,
}
