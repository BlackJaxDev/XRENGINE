using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Scene;

namespace XREngine.Rendering.Picking;

/// <summary>
/// Stores details about a mesh raycast hit gathered during editor picking.
/// </summary>
public readonly record struct MeshPickResult(
    RenderableComponent Component,
    RenderableMesh Mesh,
    Triangle WorldTriangle,
    Vector3 HitPoint)
{
    public SceneNode? SceneNode => Component.SceneNode;
}
