using System.Numerics;
using XREngine.Components.Scene.Mesh;

namespace XREngine.Rendering.Models.Gaussian.Baking.Surface;

/// <summary>
/// Holds the data of one mesh that does not change between animation frames: the rest positions, the
/// normalized bone influences, and the blendshape position deltas. It also holds the scratch arrays that one
/// evaluation fills, so an evaluation that reuses a <see cref="DeformedSurface"/> does not allocate.
/// </summary>
internal sealed class DeformedMeshSource
{
    /// <summary>Gets the component that owns the mesh.</summary>
    public required RenderableComponent Component { get; init; }

    /// <summary>Gets the renderer of the first level of detail. It holds the blendshape weights.</summary>
    public required XRMeshRenderer Renderer { get; init; }

    /// <summary>Gets the mesh of the first level of detail.</summary>
    public required XRMesh Mesh { get; init; }

    /// <summary>Gets the geometry revision of <see cref="Mesh"/> when this data was read.</summary>
    public required long GeometryRevision { get; init; }

    /// <summary>Gets the index of the first vertex of this mesh in the surface.</summary>
    public required int VertexStart { get; init; }

    /// <summary>Gets the rest position of each vertex, in mesh space.</summary>
    public required Vector3[] BasePositions { get; init; }

    /// <summary>
    /// Gets the first influence of each vertex. It has one item more than the vertex count. It is empty when the
    /// mesh is not skinned.
    /// </summary>
    public required int[] InfluenceOffsets { get; init; }

    /// <summary>Gets the palette bone index of each influence.</summary>
    public required int[] InfluenceBones { get; init; }

    /// <summary>Gets the weight of each influence. The weights of one vertex add up to 1.</summary>
    public required float[] InfluenceWeights { get; init; }

    /// <summary>Gets the skin palette. Each evaluation writes one matrix for each utilized bone.</summary>
    public required Matrix4x4[] Palette { get; init; }

    /// <summary>
    /// Gets the first blendshape entry of each vertex. It has one item more than the vertex count. It is empty
    /// when the mesh has no blendshapes.
    /// </summary>
    public required int[] BlendshapeOffsets { get; init; }

    /// <summary>Gets the blendshape index of each entry.</summary>
    public required int[] BlendshapeShapes { get; init; }

    /// <summary>Gets the position delta of each entry, in mesh space.</summary>
    public required Vector3[] BlendshapeDeltas { get; init; }

    /// <summary>Gets the weight of each blendshape. Each evaluation reads the weights from <see cref="Renderer"/>.</summary>
    public required float[] BlendshapeWeights { get; init; }

    /// <summary>Gets the number of vertices.</summary>
    public int VertexCount => BasePositions.Length;

    /// <summary>Gets a value that is true when the mesh has bone influences.</summary>
    public bool IsSkinned => InfluenceOffsets.Length != 0;

    /// <summary>Gets a value that is true when the mesh has blendshape deltas.</summary>
    public bool HasBlendshapes => BlendshapeOffsets.Length != 0;

    /// <summary>
    /// Returns true when the renderer still shows the same mesh geometry and bone table that this data was read from.
    /// </summary>
    public bool IsCurrent()
        => ReferenceEquals(Renderer.Mesh, Mesh)
        && Mesh.GeometryRevision == GeometryRevision
        && (!IsSkinned || Mesh.UtilizedBones.Length == Palette.Length);
}
