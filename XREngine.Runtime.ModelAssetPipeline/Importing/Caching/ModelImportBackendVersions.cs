namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Monotonic output versions for built-in ModelAssetPipeline producers.
/// </summary>
public static class ModelImportBackendVersions
{
    public const uint NativeGltf = 1;
    public const uint NativeFbx = 9;
    public const uint Assimp = 3;
    public const uint SerializedPrefab = 8;
}
