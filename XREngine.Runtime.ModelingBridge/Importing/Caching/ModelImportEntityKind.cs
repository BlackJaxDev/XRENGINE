namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Identifies the semantic kind of a source entity reported by a model producer.
/// </summary>
public enum ModelImportEntityKind
{
    Node = 0,
    Mesh = 1,
    Material = 2,
    Texture = 3,
    Animation = 4,
    Skeleton = 5,
    MorphTarget = 6,
    Other = 7,
}
