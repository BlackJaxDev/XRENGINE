using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

/// <summary>
/// Scoped, on-demand materialization of a mesh's vertices for cold consumers:
/// import tools, mesh operations (island split, LOD generation), editor actions
/// and diagnostics. A mesh stores no per-vertex objects; its packed attribute,
/// skinning and blendshape buffers are the only CPU copy. A view decodes a
/// vertex range and a subset of that data into <see cref="Vertex"/> objects and
/// releases them when disposed.
/// </summary>
/// <remarks>
/// Materialization allocates per vertex and must run on a worker or import
/// thread, never on the render thread. Weights decode from the quantized Core4
/// skinning buffers (8-bit weights, bind matrices from
/// <see cref="XRMesh.UtilizedBones"/>); blendshape targets are rebuilt as the
/// base attribute plus the stored full-precision delta.
/// </remarks>
public sealed class XRMeshVertexView : IDisposable
{
    private Vertex[]? _vertices;

    private XRMeshVertexView(XRMesh mesh, int firstVertex, Vertex[] vertices, EXRMeshVertexViewContent content)
    {
        Mesh = mesh;
        FirstVertex = firstVertex;
        Content = content;
        _vertices = vertices;
    }

    public XRMesh Mesh { get; }
    /// <summary>Mesh vertex index of <c>Vertices[0]</c>.</summary>
    public int FirstVertex { get; }
    public EXRMeshVertexViewContent Content { get; }
    public int Count => Vertices.Length;

    /// <summary>The materialized vertices. Throws after the view is disposed.</summary>
    public Vertex[] Vertices
        => _vertices ?? throw new ObjectDisposedException(nameof(XRMeshVertexView));

    public void Dispose() => _vertices = null;

    /// <summary>
    /// Materializes <paramref name="count"/> vertices starting at
    /// <paramref name="firstVertex"/> (all remaining vertices when negative).
    /// Requested data the mesh does not have (no normals, not skinned, no
    /// blendshapes) is left unset.
    /// </summary>
    public static XRMeshVertexView Open(
        XRMesh mesh,
        EXRMeshVertexViewContent content = EXRMeshVertexViewContent.All,
        int firstVertex = 0,
        int count = -1)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        int vertexCount = Math.Max(0, mesh.VertexCount);
        ArgumentOutOfRangeException.ThrowIfNegative(firstVertex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstVertex, vertexCount);
        if (count < 0)
            count = vertexCount - firstVertex;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, vertexCount - firstVertex);

        if (RuntimeEngine.IsRenderThread)
            Debug.RenderingWarningEvery(
                "XRMeshVertexView.RenderThread",
                TimeSpan.FromSeconds(5),
                "[XRMeshVertexView] Mesh '{0}' materialized {1} vertices on the render thread; run cold vertex consumers on a worker.",
                mesh.Name ?? "<unnamed>",
                count);

        Vertex[] vertices = new Vertex[count];
        bool attributesReadable = count == 0 || HasReadableAttributes(mesh);
        for (int local = 0; local < count; local++)
            vertices[local] = attributesReadable
                ? CreateVertex(mesh, (uint)(firstVertex + local), content)
                : new Vertex();

        if ((content & EXRMeshVertexViewContent.Weights) != 0)
            MaterializeWeights(mesh, vertices, firstVertex);
        if ((content & EXRMeshVertexViewContent.Blendshapes) != 0 && attributesReadable)
            MaterializeBlendshapes(mesh, vertices, firstVertex);

        return new XRMeshVertexView(mesh, firstVertex, vertices, content);
    }

    private static bool HasReadableAttributes(XRMesh mesh)
        => mesh.Interleaved
            ? mesh.InterleavedVertexBuffer?.ClientSideSource is not null
            : mesh.PositionsBuffer?.ClientSideSource is not null;

    private static Vertex CreateVertex(XRMesh mesh, uint index, EXRMeshVertexViewContent content)
    {
        Vertex vertex = new();
        if ((content & EXRMeshVertexViewContent.Positions) != 0 ||
            (content & EXRMeshVertexViewContent.Blendshapes) != 0)
            vertex.Position = mesh.GetPosition(index);
        if ((content & (EXRMeshVertexViewContent.Normals | EXRMeshVertexViewContent.Blendshapes)) != 0 && mesh.HasNormals)
            vertex.Normal = mesh.GetNormal(index);
        if ((content & (EXRMeshVertexViewContent.Tangents | EXRMeshVertexViewContent.Blendshapes)) != 0 && mesh.HasTangents)
        {
            Vector4 tangent = mesh.GetTangentWithSign(index);
            vertex.Tangent = new Vector3(tangent.X, tangent.Y, tangent.Z);
            vertex.BitangentSign = tangent.W;
        }
        if ((content & EXRMeshVertexViewContent.TexCoords) != 0 && mesh.TexCoordCount > 0u)
        {
            List<Vector2> sets = new((int)mesh.TexCoordCount);
            for (uint set = 0u; set < mesh.TexCoordCount; set++)
                sets.Add(mesh.GetTexCoord(index, set));
            vertex.TextureCoordinateSets = sets;
        }
        if ((content & EXRMeshVertexViewContent.Colors) != 0 && mesh.ColorCount > 0u)
        {
            List<Vector4> sets = new((int)mesh.ColorCount);
            for (uint set = 0u; set < mesh.ColorCount; set++)
                sets.Add(mesh.GetColor(index, set));
            vertex.ColorSets = sets;
        }
        return vertex;
    }

    private static void MaterializeWeights(XRMesh mesh, Vertex[] vertices, int firstVertex)
    {
        if (!XRMeshSkinningInfluenceReader.TryCreate(mesh, out XRMeshSkinningInfluenceReader reader))
            return;

        (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] bones = mesh.UtilizedBones;
        Span<int> boneIndices = stackalloc int[reader.MaxInfluenceCount];
        Span<float> weights = stackalloc float[reader.MaxInfluenceCount];
        for (int local = 0; local < vertices.Length; local++)
        {
            int influenceCount = reader.ReadInfluences(firstVertex + local, boneIndices, weights);
            if (influenceCount == 0)
                continue;

            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> vertexWeights =
                new(influenceCount, ReferenceEqualityComparer.Instance);
            for (int influence = 0; influence < influenceCount; influence++)
            {
                (TransformBase bone, Matrix4x4 inverseBind) = bones[boneIndices[influence]];
                if (bone is null)
                    continue;
                vertexWeights[bone] = vertexWeights.TryGetValue(bone, out var existing)
                    ? (existing.weight + weights[influence], inverseBind)
                    : (weights[influence], inverseBind);
            }
            vertices[local].Weights = vertexWeights.Count > 0 ? vertexWeights : null;
        }
    }

    private static void MaterializeBlendshapes(XRMesh mesh, Vertex[] vertices, int firstVertex)
    {
        if (!XRMeshBlendshapeDeltaTable.TryCreate(mesh, out XRMeshBlendshapeDeltaTable table))
            return;

        string[] names = mesh.BlendshapeNames;
        int lastVertex = firstVertex + vertices.Length;
        ReadOnlySpan<int> recordVertices = table.RecordVertices;
        ReadOnlySpan<byte> flags = table.Flags;
        for (int shape = 0; shape < table.ShapeCount; shape++)
        {
            int end = table.GetShapeRecordOffset(shape) + table.GetShapeRecordCount(shape);
            for (int record = table.GetShapeRecordOffset(shape); record < end; record++)
            {
                int vertexIndex = recordVertices[record];
                if (vertexIndex < firstVertex || vertexIndex >= lastVertex)
                    continue;

                Vertex vertex = vertices[vertexIndex - firstVertex];
                byte recordFlags = flags[record];
                VertexData target = new()
                {
                    Position = vertex.Position + table.PositionDeltas[record],
                    Normal = vertex.Normal.HasValue || (recordFlags & XRMeshBlendshapeDeltaTable.NormalFlag) != 0
                        ? (vertex.Normal ?? Vector3.Zero) + table.NormalDeltas[record]
                        : null,
                    Tangent = vertex.Tangent.HasValue || (recordFlags & XRMeshBlendshapeDeltaTable.TangentFlag) != 0
                        ? (vertex.Tangent ?? Vector3.Zero) + table.TangentDeltas[record]
                        : null,
                    BitangentSign = vertex.BitangentSign,
                };
                (vertex.Blendshapes ??= []).Add((names[shape], target));
            }
        }
    }
}
