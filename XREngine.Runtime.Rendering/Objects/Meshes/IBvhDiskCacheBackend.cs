using System.Collections.Generic;
using SimpleScene.Util.ssBVH;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Optional host file-system capability for persisting mesh BVH trees outside portable rendering.</summary>
public interface IBvhDiskCacheBackend
{
    /// <summary>Attempts to restore a mesh BVH and triangle lookup from the supplied cache root.</summary>
    bool TryLoad(
        string? cacheRoot,
        List<Triangle> triangles,
        List<IndexTriangle> indexTriangles,
        out BVH<Triangle>? bvh,
        out Dictionary<Triangle, (IndexTriangle Indices, int FaceIndex)>? triangleLookup);

    /// <summary>Attempts to persist a mesh BVH and triangle lookup under the supplied cache root.</summary>
    void TryStore(
        string? cacheRoot,
        List<Triangle> triangles,
        Dictionary<Triangle, (IndexTriangle Indices, int FaceIndex)> triangleLookup,
        BVH<Triangle> bvh);
}
