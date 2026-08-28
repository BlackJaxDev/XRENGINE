namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Describes stable capabilities exposed by a model import backend.
/// </summary>
[Flags]
public enum ModelImportBackendCapabilities
{
    None = 0,
    NativeParser = 1 << 0,
    GeneralPurposeFallback = 1 << 1,
    StableSourceEntityIds = 1 << 2,
    StructuralDependencyDiscovery = 1 << 3,
}
