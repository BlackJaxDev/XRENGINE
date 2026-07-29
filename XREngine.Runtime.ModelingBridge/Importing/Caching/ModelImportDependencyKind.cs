namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Describes how a file consulted by a model producer participates in cache freshness
/// or in a referenced-output handoff.
/// </summary>
public enum ModelImportDependencyKind
{
    EntrySource = 0,
    Structural = 1,
    ReferencedTexture = 2,
    ReferencedAnimation = 3,
    ReferencedAsset = 4,
}
