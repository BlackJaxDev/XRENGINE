using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    /// <summary>
    /// Builds an indexed triangle mesh directly from a packed construction
    /// source: attribute buffers, Core4 + spill skinning buffers (palette =
    /// <see cref="XRMeshPackedSource.Bones"/>) and blendshape buffers (names =
    /// <see cref="XRMeshPackedSource.BlendshapeNames"/>), with no per-vertex
    /// objects at any point. Vertex order is the source's append order.
    /// </summary>
    public XRMesh(XRMeshPackedSource source, IReadOnlyList<int> triangleIndices)
        : this(deferObjectCachePublication: true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(triangleIndices);
        try
        {
            using (RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication())
            {
                JoinDeferredObjectCachePublication();
                XRMeshCpuPreparationTelemetry.EnterPreparation();
                bool preparationSucceeded = false;
                try
                {
                    using var _ = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRMesh Packed Source Constructor");
                    int vertexCount = source.VertexCount;
                    ReadOnlySpan<Vector3> positions = source.Positions;
                    _bounds = CalculatePackedSourceBounds(positions);
                    _triangles = new List<IndexTriangle>(triangleIndices.Count / 3);
                    for (int i = 0; i + 2 < triangleIndices.Count; i += 3)
                        _triangles.Add(new IndexTriangle(triangleIndices[i], triangleIndices[i + 1], triangleIndices[i + 2]));
                    _type = EPrimitiveType.Triangles;
                    VertexCount = vertexCount;

                    InitMeshBuffers(source.HasNormals, source.HasTangents, source.ColorSetCount, source.TexCoordSetCount);
                    WritePackedSourceAttributes(source);

                    if (source.BlendshapeNames is { Length: > 0 } blendshapeNames)
                    {
                        BlendshapeNames = blendshapeNames;
                        if (source.HasBlendshapeDeltas)
                        {
                            PopulateBlendshapeBuffers((vertex, destination) => ReadPackedSourceBlendshapeDeltas(source, vertex, destination));
                            ApplyBlendshapeBufferState(CaptureBlendshapeBufferState());
                        }
                    }

                    if (source.Bones.Count > 0 && source.HasInfluences)
                    {
                        UtilizedBones = [.. source.Bones];
                        SkinningShaderConvention = ESkinningShaderConvention.ExplicitRowMajorRowVector;
                        PopulateSkinningBuffers((vertex, destination) => ReadPackedSourceInfluences(source, vertex, destination));
                        ApplySkinningBufferState(CaptureSkinningBufferState());
                    }

                    RegisterCookedDynamicBuffers();
                    publication.Complete();
                    preparationSucceeded = true;
                }
                finally
                {
                    XRMeshCpuPreparationTelemetry.ExitPreparation(
                        preparationSucceeded,
                        VertexCount,
                        Buffers.Count,
                        GetPreparedBufferByteCount());
                }
            }
        }
        catch
        {
            AbortMeshConstruction();
            throw;
        }
    }

    private static AABB CalculatePackedSourceBounds(ReadOnlySpan<Vector3> positions)
    {
        if (positions.IsEmpty)
            return new AABB(Vector3.Zero, Vector3.Zero);
        Vector3 min = positions[0];
        Vector3 max = positions[0];
        for (int i = 1; i < positions.Length; i++)
        {
            min = Vector3.Min(min, positions[i]);
            max = Vector3.Max(max, positions[i]);
        }
        return new AABB(min, max);
    }

    private void WritePackedSourceAttributes(XRMeshPackedSource source)
    {
        ReadOnlySpan<Vector3> positions = source.Positions;
        ReadOnlySpan<Vector3> normals = source.Normals;
        ReadOnlySpan<Vector4> tangents = source.Tangents;
        for (int i = 0; i < positions.Length; i++)
        {
            uint index = (uint)i;
            SetPosition(index, positions[i]);
            if (!normals.IsEmpty)
                SetNormal(index, normals[i]);
            if (!tangents.IsEmpty)
            {
                Vector4 tangent = tangents[i];
                SetTangent(index, new Vector3(tangent.X, tangent.Y, tangent.Z), tangent.W);
            }
            for (int set = 0; set < source.TexCoordSetCount; set++)
                SetTexCoord(index, source.GetTexCoord(i, set), (uint)set);
            for (int set = 0; set < source.ColorSetCount; set++)
                SetColor(index, source.GetColor(i, set), (uint)set);
        }
    }

    private static int ReadPackedSourceInfluences(
        XRMeshPackedSource source,
        int vertex,
        List<LogicalSkinningInfluence> destination)
    {
        source.GetInfluences(vertex, out ReadOnlySpan<int> bones, out ReadOnlySpan<float> weights);
        for (int i = 0; i < bones.Length; i++)
            if (weights[i] > 0.0f)
                destination.Add(new LogicalSkinningInfluence(bones[i], weights[i]));
        return bones.Length;
    }

    private static void ReadPackedSourceBlendshapeDeltas(
        XRMeshPackedSource source,
        int vertex,
        List<BlendshapeVertexDelta> destination)
    {
        source.GetBlendshapeEntries(
            vertex,
            out ReadOnlySpan<int> shapes,
            out ReadOnlySpan<Vector3> positions,
            out ReadOnlySpan<Vector3> normals,
            out ReadOnlySpan<Vector3> tangents);
        int start = destination.Count;
        for (int i = 0; i < shapes.Length; i++)
            destination.Add(new BlendshapeVertexDelta(shapes[i], positions[i], normals[i], tangents[i]));
        // The packer expects ascending shape order per vertex.
        destination.Sort(start, destination.Count - start, BlendshapeVertexDeltaShapeComparer.Instance);
    }

    private sealed class BlendshapeVertexDeltaShapeComparer : IComparer<BlendshapeVertexDelta>
    {
        internal static readonly BlendshapeVertexDeltaShapeComparer Instance = new();
        public int Compare(BlendshapeVertexDelta x, BlendshapeVertexDelta y) => x.Shape.CompareTo(y.Shape);
    }
}
