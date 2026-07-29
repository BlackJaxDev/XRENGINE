namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Separates project, engine-owned, and external model-cache namespaces.
/// </summary>
public enum ModelCacheSourceOrigin
{
    Project = 0,
    Engine = 1,
    External = 2,
}
