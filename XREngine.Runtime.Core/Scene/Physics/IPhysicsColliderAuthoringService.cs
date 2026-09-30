using System.Numerics;
using XREngine.Components.Physics;
using XREngine.Data.Tools;

namespace XREngine.Scene.Physics;

/// <summary>
/// Provides optional convex decomposition and its disk cache to an authoring host.
/// Runtime hosts load cooked collider geometry without installing this service.
/// </summary>
public interface IPhysicsColliderAuthoringService
{
    Task<IReadOnlyList<CoACD.ConvexHullMesh>?> GenerateAsync(
        Vector3[] positions,
        int[] triangleIndices,
        CoACD.CoACDParameters parameters,
        CancellationToken cancellationToken);

    bool TryLoadCachedHulls(
        CoACD.CoACDParameters parameters,
        in ConvexHullInput input,
        string? cacheRoot,
        out List<CoACD.ConvexHullMesh> hulls);

    void StoreCachedHulls(
        CoACD.CoACDParameters parameters,
        in ConvexHullInput input,
        string? cacheRoot,
        IReadOnlyList<CoACD.ConvexHullMesh> hulls);
}
