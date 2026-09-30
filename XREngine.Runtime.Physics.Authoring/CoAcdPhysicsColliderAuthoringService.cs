using System.Numerics;
using XREngine.Components.Physics;
using XREngine.Data.Tools;
using XREngine.Scene.Physics;

namespace XREngine.Runtime.Physics.Authoring;

/// <summary>Runs CoACD convex decomposition and caches authored hulls for the editor or cooker.</summary>
public sealed class CoAcdPhysicsColliderAuthoringService : IPhysicsColliderAuthoringService
{
    public Task<IReadOnlyList<CoACD.ConvexHullMesh>?> GenerateAsync(
        Vector3[] positions,
        int[] triangleIndices,
        CoACD.CoACDParameters parameters,
        CancellationToken cancellationToken)
        => CoACD.CalculateAsync(positions, triangleIndices, parameters, cancellationToken);

    public bool TryLoadCachedHulls(
        CoACD.CoACDParameters parameters,
        in ConvexHullInput input,
        string? cacheRoot,
        out List<CoACD.ConvexHullMesh> hulls)
        => CoAcdConvexHullDiskCache.TryLoad(parameters, in input, cacheRoot, out hulls);

    public void StoreCachedHulls(
        CoACD.CoACDParameters parameters,
        in ConvexHullInput input,
        string? cacheRoot,
        IReadOnlyList<CoACD.ConvexHullMesh> hulls)
        => CoAcdConvexHullDiskCache.TryStore(parameters, in input, cacheRoot, hulls);
}
