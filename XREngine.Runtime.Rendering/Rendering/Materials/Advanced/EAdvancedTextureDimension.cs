namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral texture dimensionality.
/// </summary>
public enum EAdvancedTextureDimension : uint
{
    Texture1D = 0,
    Texture2D = 1,
    Texture3D = 2,
    Cube = 3,
    Texture1DArray = 4,
    Texture2DArray = 5,
    CubeArray = 6,
}
