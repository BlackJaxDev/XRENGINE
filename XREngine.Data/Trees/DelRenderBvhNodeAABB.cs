using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Data.Trees;

/// <summary>Receives a BVH node bound together with its query and leaf classifications.</summary>
public delegate void DelRenderBvhNodeAABB(
    Vector3 extents,
    Vector3 center,
    EContainment queryContainment,
    bool isLeaf);
