namespace XREngine.Rendering.Materials;

/// <summary>
/// Describes whether a backend material texture reference is safe to publish to a draw.
/// </summary>
public enum EMaterialTextureReferenceStatus : byte
{
    Ready = 0,
    Pending = 1,
    Unsupported = 2,
    Failed = 3,
}
