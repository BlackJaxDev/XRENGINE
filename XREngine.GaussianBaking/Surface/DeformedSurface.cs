using XREngine.Scene;

namespace XREngine.Rendering.Models.Gaussian.Baking.Surface;

/// <summary>
/// Holds the world-space triangle surface of content at one animation time.
/// <see cref="DeformedSurfaceEvaluator"/> fills it. The vertices of each mesh are in one continuous range,
/// in the order that the evaluator finds the meshes.
/// </summary>
/// <remarks>
/// When an evaluation reuses a surface, only <see cref="Positions"/> changes. The index data and the mesh ranges
/// stay the same while the content keeps the same meshes.
/// </remarks>
public sealed class DeformedSurface
{
    /// <summary>Gets the world-space position of each vertex, as x, y, and z. The length is 3 × <see cref="VertexCount"/>.</summary>
    public float[] Positions { get; internal set; } = [];

    /// <summary>Gets the three vertex indices of each triangle. The indices refer to <see cref="Positions"/>.</summary>
    public uint[] Indices { get; internal set; } = [];

    /// <summary>Gets the mesh index of each triangle.</summary>
    public int[] TriangleMesh { get; internal set; } = [];

    /// <summary>
    /// Gets the first vertex of each mesh. The array has <see cref="MeshCount"/> + 1 items. The last item is
    /// <see cref="VertexCount"/>, so mesh <c>m</c> has the vertices from <c>MeshVertexStart[m]</c> to
    /// <c>MeshVertexStart[m + 1] − 1</c>.
    /// </summary>
    public int[] MeshVertexStart { get; internal set; } = [0];

    /// <summary>Gets the number of meshes.</summary>
    public int MeshCount { get; internal set; }

    /// <summary>Gets the number of vertices.</summary>
    public int VertexCount { get; internal set; }

    /// <summary>Gets the number of triangles.</summary>
    public int TriangleCount { get; internal set; }

    /// <summary>Gets the root nodes that the surface was evaluated from, in evaluation order.</summary>
    internal SceneNode[] Roots { get; set; } = [];

    /// <summary>Gets the cached data of each mesh, in mesh order. An evaluation that reuses the surface reads this data.</summary>
    internal DeformedMeshSource[] Sources { get; set; } = [];
}
