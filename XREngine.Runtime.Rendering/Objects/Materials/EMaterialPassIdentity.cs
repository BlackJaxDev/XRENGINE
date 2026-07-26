namespace XREngine.Rendering;

/// <summary>
/// Identifies a material pass by its semantic purpose rather than by a
/// pipeline-specific numeric render-pass bucket.
/// </summary>
public enum EMaterialPassIdentity
{
    Base,
    EarlyDepth,
    DepthNormal,
    Shadow,
    Velocity,
    TransformId,
    Picking,
    Reflection,
    Outline,
}
