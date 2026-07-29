namespace XREngine.Data.Rendering;

/// <summary>
/// Identifies the texture-binding capability selected for GPU-owned material
/// submission.
/// </summary>
public enum EMaterialTextureBindingRung
{
    Unsupported,
    TextureArray,
    Bindless,
    Sparse,
    CoarseBucket,
}
