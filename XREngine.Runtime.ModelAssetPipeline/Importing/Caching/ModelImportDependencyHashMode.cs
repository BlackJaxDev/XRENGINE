namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Identifies the stable content-hash representation recorded for an import dependency.
/// </summary>
public enum ModelImportDependencyHashMode : uint
{
    None = 0,
    Sha256 = 1,
    XxHash3_64 = 2,
    ProducerDefined = 3,
}
