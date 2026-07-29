namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Identifies a durable imported key that can be rebound by the project layer.
/// </summary>
public enum ModelImportReferenceKind
{
    Material = 0,
    Texture = 1,
    Animation = 2,
    Other = 3,
}
