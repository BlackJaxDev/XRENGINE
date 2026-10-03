using System.Numerics;

namespace XREngine.Data.Tools;

/// <summary>Native convex decomposition implementation installed by an authoring host.</summary>
public interface ICoAcdBackend
{
    IReadOnlyList<CoACD.ConvexHullMesh>? Calculate(
        Vector3[] positions,
        int[] triangleIndices,
        CoACD.CoACDParameters? parameters = null);

    void SetLogLevel(CoACD.CoACDLogLevel level);
}
