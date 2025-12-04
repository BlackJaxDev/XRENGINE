using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Scene;

namespace XREngine.Rendering.Picking;

/// <summary>
/// Determines what geometric primitive the editor raycast will snap to.
/// </summary>
public enum ERaycastHitMode
{
    Faces,
    Lines,
    Points
}

/// <summary>
/// Stores details about a mesh raycast hit gathered during editor picking.
/// </summary>
public readonly record struct MeshPickResult(
    RenderableComponent Component,
    RenderableMesh Mesh,
    Triangle WorldTriangle,
    Vector3 HitPoint,
    int TriangleIndex,
    IndexTriangle Indices)
{
    public SceneNode? SceneNode => Component.SceneNode;
}

/// <summary>
/// Represents the closest point on a mesh edge relative to the ray.
/// </summary>
public readonly record struct MeshEdgePickResult(
    MeshPickResult FaceHit,
    Vector3 EdgeStart,
    Vector3 EdgeEnd,
    Vector3 ClosestPoint,
    int EdgeIndex)
{
    public RenderableComponent Component => FaceHit.Component;
    public SceneNode? SceneNode => FaceHit.SceneNode;
    public Triangle WorldTriangle => FaceHit.WorldTriangle;
}

/// <summary>
/// Represents a vertex position picked via raycast.
/// </summary>
public readonly record struct MeshVertexPickResult(
    MeshPickResult FaceHit,
    Vector3 Position,
    int VertexIndex)
{
    public RenderableComponent Component => FaceHit.Component;
    public SceneNode? SceneNode => FaceHit.SceneNode;
    public Triangle WorldTriangle => FaceHit.WorldTriangle;
}
