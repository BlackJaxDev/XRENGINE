using XREngine.Data.Tools;
using XREngine.Scene.Physics;

namespace XREngine.Components.Physics;

/// <summary>Resolves an authoring cache root and forwards cache operations to the installed authoring module.</summary>
public static class ConvexHullDiskCache
{
    internal static string? CacheRootOverride { get; set; }

    public static bool TryLoad(CoACD.CoACDParameters parameters, in ConvexHullInput input, out List<CoACD.ConvexHullMesh> hulls)
        => PhysicsColliderAuthoringServices.Require().TryLoadCachedHulls(parameters, in input, ResolveCacheRoot(), out hulls);

    public static void TryStore(CoACD.CoACDParameters parameters, in ConvexHullInput input, IReadOnlyList<CoACD.ConvexHullMesh> hulls)
        => PhysicsColliderAuthoringServices.Require().StoreCachedHulls(parameters, in input, ResolveCacheRoot(), hulls);

    public static string? ResolveCacheRoot()
    {
        if (!string.IsNullOrWhiteSpace(CacheRootOverride))
            return CacheRootOverride;

        string? gameCachePath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GameCachePath);
        if (!string.IsNullOrWhiteSpace(gameCachePath))
            return gameCachePath;

        string? gameAssetsPath = AssetSerializationServices.Current.GameAssetsPath;
        if (string.IsNullOrWhiteSpace(gameAssetsPath))
            return null;

        string normalizedAssetsPath = Path.GetFullPath(gameAssetsPath);
        if (string.Equals(Path.GetFileName(normalizedAssetsPath), "Assets", StringComparison.OrdinalIgnoreCase))
        {
            string? projectRoot = Path.GetDirectoryName(normalizedAssetsPath);
            return string.IsNullOrWhiteSpace(projectRoot) ? null : Path.Combine(projectRoot, "Cache");
        }

        if (Directory.Exists(Path.Combine(normalizedAssetsPath, "Assets")))
            return Path.Combine(normalizedAssetsPath, "Cache");

        string? parent = Path.GetDirectoryName(normalizedAssetsPath);
        return string.IsNullOrWhiteSpace(parent) ? null : Path.Combine(parent, "Cache");
    }
}
