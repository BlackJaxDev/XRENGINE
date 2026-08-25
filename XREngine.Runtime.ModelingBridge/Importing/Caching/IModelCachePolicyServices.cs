namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Composition-owned access to authored model import options and cook overrides. The modeling
/// cache owns policy; prefab/editor owners provide the source-specific snapshot.
/// </summary>
public interface IModelCachePolicyServices
{
    ModelImportOptions GetImportOptions(string sourcePath, Type assetType, object? suppliedOptions);

    bool TryBuildCookOverrideSnapshot(
        string sourcePath,
        ModelCookSettings modelDefaults,
        out ModelCookOverrideSnapshot snapshot);

    void EnsureSourceBackendRegistered(string sourcePath);
}
