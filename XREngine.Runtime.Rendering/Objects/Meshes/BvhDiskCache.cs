using System;
using System.Collections.Generic;
using SimpleScene.Util.ssBVH;
using XREngine.Core.Files;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Execution;

namespace XREngine.Rendering;

/// <summary>Forwards optional mesh BVH cache operations to the admitted host file-system capability.</summary>
internal static class BvhDiskCache
{
    /// <summary>
    /// Absolute path to the cache root directory.  Falls back to
    /// <see cref="RuntimeRenderingHostServices.GameCachePath"/> when not
    /// explicitly overridden.
    /// </summary>
    internal static string? CacheRootOverride { get; set; }

    public static bool TryLoad(
        List<Triangle> triangles,
        List<IndexTriangle> indexTriangles,
        out BVH<Triangle>? bvh,
        out Dictionary<Triangle, (IndexTriangle Indices, int FaceIndex)>? triangleLookup)
    {
        bvh = null;
        triangleLookup = null;
        if (OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread)
            return false;

        if (AssetFileSystemServices.Current is not IBvhDiskCacheBackend backend)
            return false;

        string? cacheRoot = CacheRootOverride ?? RuntimeRenderingHostServices.GameCachePath;
        return backend.TryLoad(cacheRoot, triangles, indexTriangles, out bvh, out triangleLookup);
    }

    public static void TryStore(
        List<Triangle> triangles,
        Dictionary<Triangle, (IndexTriangle Indices, int FaceIndex)> triangleLookup,
        BVH<Triangle> bvh)
    {
        if (OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread)
            return;

        if (AssetFileSystemServices.Current is not IBvhDiskCacheBackend backend)
            return;

        string? cacheRoot = CacheRootOverride ?? RuntimeRenderingHostServices.GameCachePath;
        backend.TryStore(cacheRoot, triangles, triangleLookup, bvh);
    }
}
