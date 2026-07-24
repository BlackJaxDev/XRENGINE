namespace XREngine.Scene.Importers;

/// <summary>
/// Unity texture importer shapes represented without flattening array or cube assets.
/// </summary>
public enum UnityTextureShape
{
    Unknown = 0,
    Texture2D = 1,
    Cube = 2,
    Texture2DArray = 4,
    Texture3D = 8,
    CubeArray = 16,
}
