using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// Prepares portable meshlets for newly generated static billboard and HLOD geometry.
/// It preserves authored triangle order and performs no optimizer, visibility or LOD decisions.
/// </summary>
public static class GeneratedStaticMeshletPayloadBuilder
{
    public const string ProducerIdentity = "xrengine-generated-static-triangles-v1";
    public const int MaximumVertices = 1 << 20;
    public const int MaximumTriangles = 1 << 20;

    /// <summary>Attaches an ownership-validated payload after the generated source mesh is final.</summary>
    public static MeshletPayload Attach(XRMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Type != EPrimitiveType.Triangles || mesh.HasSkinning || mesh.HasBlendshapes ||
            !string.IsNullOrEmpty(mesh.FilePath) || !string.IsNullOrEmpty(mesh.OriginalPath) ||
            mesh.VertexCount is <= 0 or > MaximumVertices || mesh.Triangles?.Count is not (> 0 and <= MaximumTriangles))
            throw new NotSupportedException("GeneratedMeshlets.SourceProfile: only bounded newly generated static triangle geometry is supported.");
        long revision = mesh.GeometryRevision;
        int[] indices = mesh.GetIndices(EPrimitiveType.Triangles)
            ?? throw new InvalidDataException("GeneratedMeshlets.IndicesMissing: source triangles have no index stream.");
        if (indices.Length != checked(mesh.Triangles.Count * 3))
            throw new InvalidDataException("GeneratedMeshlets.TopologyMismatch: source triangle and index counts differ.");
        MeshletVertex[] vertices = new MeshletVertex[mesh.VertexCount];
        for (uint index = 0; index < vertices.Length; index++)
        {
            Vector3 position = mesh.GetPosition(index);
            if (!Finite(position)) throw new InvalidDataException("GeneratedMeshlets.NonfinitePosition: source positions must be finite.");
            vertices[index] = new()
            {
                Position = new(position, 1), Normal = new(mesh.GetNormal(index), 0),
                Tangent = mesh.GetTangentWithSign(index), TexCoord = mesh.GetTexCoord(index, 0),
            };
        }
        List<CpuMeshletDescriptor> descriptors = [];
        List<uint> references = [];
        List<byte> triangles = [];
        Span<uint> local = stackalloc uint[(int)MeshletPayload.PortableMaxVertices];
        int sourceOffset = 0;
        while (sourceOffset < indices.Length)
        {
            int vertexCount = 0, triangleCount = 0;
            uint vertexOffset = checked((uint)references.Count), triangleOffset = checked((uint)triangles.Count);
            while (sourceOffset < indices.Length && triangleCount < MeshletPayload.PortableMaxTriangles)
            {
                int missing = 0;
                for (int corner = 0; corner < 3; corner++)
                {
                    int source = indices[sourceOffset + corner];
                    if (source < 0 || source >= vertices.Length)
                        throw new InvalidDataException("GeneratedMeshlets.IndexRange: source index lies outside the generated vertex stream.");
                    if (local[..vertexCount].IndexOf((uint)source) < 0) missing++;
                }
                if (vertexCount + missing > local.Length) break;
                for (int corner = 0; corner < 3; corner++)
                {
                    uint source = (uint)indices[sourceOffset++];
                    int mapped = local[..vertexCount].IndexOf(source);
                    if (mapped < 0)
                    {
                        mapped = vertexCount;
                        local[vertexCount++] = source;
                        references.Add(source);
                    }
                    triangles.Add(checked((byte)mapped));
                }
                triangleCount++;
            }
            Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            for (int index = 0; index < vertexCount; index++)
            {
                Vector4 position = vertices[local[index]].Position;
                Vector3 point = new(position.X, position.Y, position.Z);
                min = Vector3.Min(min, point); max = Vector3.Max(max, point);
            }
            Vector3 center = min * 0.5f + max * 0.5f;
            double squaredRadius = 0;
            for (int index = 0; index < vertexCount; index++)
            {
                Vector4 point = vertices[local[index]].Position;
                double x = (double)point.X - center.X, y = (double)point.Y - center.Y, z = (double)point.Z - center.Z;
                squaredRadius = Math.Max(squaredRadius, x * x + y * y + z * z);
            }
            float radius = MathF.BitIncrement((float)Math.Sqrt(squaredRadius));
            if (!Finite(center) || !float.IsFinite(radius))
                throw new NotSupportedException("GeneratedMeshlets.BoundsRange: source positions exceed finite portable bounds.");
            descriptors.Add(new(new(center, radius), vertexOffset, triangleOffset, (uint)vertexCount, (uint)triangleCount,
                Vector4.Zero, Vector4.Zero, 0));
            while ((triangles.Count & 3) != 0) triangles.Add(0);
        }
        MeshletGenerationSettingsSnapshot settings = new(true, MeshletBuildMode.Dense, MeshletPayload.PortableMaxVertices,
            1, MeshletPayload.PortableMaxTriangles, 0, 0, 0, false, 0, true, false, false);
        MeshLodGenerationSettingsSnapshot lod = MeshLodGenerationSettingsSnapshot.From(new() { Enabled = false });
        string identity = MeshletPayloadUtility.ResolveSourceMeshIdentity(mesh);
        ulong sourceHash = MeshletPayloadUtility.ComputeSourceMeshHash(mesh);
        ulong settingsHash = MeshletPayloadUtility.ComputeHash(settings), lodHash = MeshletPayloadUtility.ComputeHash(lod);
        string provenance = $"producer={ProducerIdentity};payload={MeshletPayload.CurrentPayloadVersion};source-order=original;cone=disabled";
        MeshletPayload payload = new()
        {
            State = MeshletPayloadState.Present, GenerationEnabled = true,
            // The legacy property is intentionally empty: no meshoptimizer ran.
            MeshOptimizerVersionKey = string.Empty, CookProvenanceKey = provenance,
            RuntimeCompatibilityToken = MeshletPayloadUtility.ComputeRuntimeCompatibilityToken(settings),
            SourceMeshIdentity = identity, SourceVertexCount = mesh.VertexCount, SourceTriangleCount = mesh.Triangles.Count,
            SourceMeshHash = sourceHash, MeshletSettingsHash = settingsHash, LodSettingsHash = lodHash,
            FreshnessHash = MeshletPayloadUtility.ComputeFreshnessHash(identity, sourceHash, settingsHash, lodHash, provenance),
            MeshletSettings = settings, LodSettings = lod, Meshlets = [.. descriptors], VertexIndices = [.. references],
            TriangleIndices = [.. triangles],
            Stats = new(descriptors.Count, references.Count, triangles.Count, 0),
        };
        payload.ValidateForMesh(mesh, identity);
        if (mesh.GeometryRevision != revision)
            throw new InvalidOperationException("GeneratedMeshlets.SourceChanged: generated geometry changed while its payload was prepared.");
        mesh.MeshletPayload = payload;
        return payload;
    }

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
