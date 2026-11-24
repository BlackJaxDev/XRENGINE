namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Identifies the declared type of a ShaderLab material property.
/// </summary>
public enum ShaderLabPropertyKind
{
    Float,
    Int,
    Range,
    Color,
    Vector,
    Texture2D,
    Texture3D,
    TextureCube,
    Texture2DArray,
    TextureCubeArray,
    TextureAny,
}
