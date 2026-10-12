namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Monotonic output versions for built-in ModelAssetPipeline producers.
/// </summary>
public static class ModelImportBackendVersions
{
    public const uint NativeGltf = 1;
    public const uint NativeFbx = 6;
    public const uint Assimp = 2;
    public const uint SerializedPrefab = 1;
}
