using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Meshlets;

public readonly record struct CpuMeshletDescriptor(
    Vector4 BoundsSphere,
    uint VertexOffset,
    uint TriangleOffset,
    uint VertexCount,
    uint TriangleCount,
    Vector4 Cone,
    Vector4 ConeApex,
    uint PackedCone)
{
    public readonly Meshlet ToGpuMeshlet(uint meshID = 0u, uint materialID = 0u, uint renderPass = 0u)
        => new()
        {
            BoundingSphere = BoundsSphere,
            VertexOffset = VertexOffset,
            TriangleOffset = TriangleOffset,
            VertexCount = VertexCount,
            TriangleCount = TriangleCount,
            MeshID = meshID,
            MaterialID = materialID,
            RenderPass = renderPass,
        };
}

public readonly record struct MeshletGenerationSettingsSnapshot(
    bool Enabled,
    MeshletBuildMode BuildMode,
    uint MaxVertices,
    uint MinTriangles,
    uint MaxTriangles,
    float ConeWeight,
    float SplitFactor,
    float FillWeight,
    bool OptimizeMeshlets,
    int OptimizeLevel,
    bool ComputeBounds,
    bool EncodeMeshlets,
    bool EncodeVertexReferences)
{
    public static MeshletGenerationSettingsSnapshot From(MeshletGenerationSettings? settings)
    {
        settings ??= new MeshletGenerationSettings();
        return new MeshletGenerationSettingsSnapshot(
            settings.Enabled,
            settings.BuildMode,
            settings.MaxVertices,
            settings.MinTriangles,
            settings.MaxTriangles,
            settings.ConeWeight,
            settings.SplitFactor,
            settings.FillWeight,
            settings.OptimizeMeshlets,
            settings.OptimizeLevel,
            settings.ComputeBounds,
            settings.EncodeMeshlets,
            settings.EncodeVertexReferences);
    }
}

public readonly record struct MeshLodGenerationSettingsSnapshot(
    bool Enabled,
    MeshOptimizerLodMode Mode,
    int AdditionalLodCount,
    float FirstLodIndexRatio,
    float LodRatioScale,
    float TargetError,
    float FirstLodDistance,
    float LodDistanceScale,
    bool ReusePreviousLodAsSource,
    MeshOptimizerSimplifyOptions Options,
    bool UseNormals,
    float NormalWeight,
    bool UseTangents,
    float TangentWeight,
    bool UseTexCoords,
    float TexCoordWeight,
    bool UseColors,
    float ColorWeight,
    bool ProtectAttributeSeams,
    bool PrioritizeBorderVertices,
    bool LockWeightedVertices)
{
    public static MeshLodGenerationSettingsSnapshot From(MeshLodGenerationSettings? settings)
    {
        settings ??= new MeshLodGenerationSettings();
        return new MeshLodGenerationSettingsSnapshot(
            settings.Enabled,
            settings.Mode,
            settings.AdditionalLodCount,
            settings.FirstLodIndexRatio,
            settings.LodRatioScale,
            settings.TargetError,
            settings.FirstLodDistance,
            settings.LodDistanceScale,
            settings.ReusePreviousLodAsSource,
            settings.Options,
            settings.UseNormals,
            settings.NormalWeight,
            settings.UseTangents,
            settings.TangentWeight,
            settings.UseTexCoords,
            settings.TexCoordWeight,
            settings.UseColors,
            settings.ColorWeight,
            settings.ProtectAttributeSeams,
            settings.PrioritizeBorderVertices,
            settings.LockWeightedVertices);
    }
}

public sealed class MeshletPayload
{
    public const int CurrentPayloadVersion = 1;

    public int PayloadVersion { get; init; } = CurrentPayloadVersion;
    public bool GenerationEnabled { get; init; }
    public string MeshOptimizerVersionKey { get; init; } = string.Empty;
    public string SourceMeshIdentity { get; init; } = string.Empty;
    public int SourceVertexCount { get; init; }
    public int SourceTriangleCount { get; init; }
    public ulong SourceMeshHash { get; init; }
    public ulong MeshletSettingsHash { get; init; }
    public ulong LodSettingsHash { get; init; }
    public ulong FreshnessHash { get; init; }
    public MeshletGenerationSettingsSnapshot MeshletSettings { get; init; }
    public MeshLodGenerationSettingsSnapshot LodSettings { get; init; }
    public CpuMeshletDescriptor[] Meshlets { get; init; } = [];
    public uint[] VertexIndices { get; init; } = [];
    public byte[] TriangleIndices { get; init; } = [];
    public MeshletVertex[] Vertices { get; init; } = [];
    public MeshOptimizerMeshletStats Stats { get; init; }

    public bool HasMeshlets => GenerationEnabled && Meshlets.Length > 0;

    public bool IsFreshFor(XRMesh mesh, MeshletGenerationSettings? meshletSettings, MeshLodGenerationSettings? lodSettings, string? sourceMeshIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        MeshletGenerationSettingsSnapshot meshletSnapshot = MeshletGenerationSettingsSnapshot.From(meshletSettings);
        MeshLodGenerationSettingsSnapshot lodSnapshot = MeshLodGenerationSettingsSnapshot.From(lodSettings);
        string identity = MeshletPayloadUtility.ResolveSourceMeshIdentity(mesh, sourceMeshIdentity);
        ulong sourceHash = MeshletPayloadUtility.ComputeSourceMeshHash(mesh);
        ulong meshletHash = MeshletPayloadUtility.ComputeHash(meshletSnapshot);
        ulong lodHash = MeshletPayloadUtility.ComputeHash(lodSnapshot);
        string versionKey = MeshOptimizerIntegration.MeshOptimizerVersionKey;
        ulong freshness = MeshletPayloadUtility.ComputeFreshnessHash(identity, sourceHash, meshletHash, lodHash, versionKey);

        return PayloadVersion == CurrentPayloadVersion
            && SourceVertexCount == mesh.VertexCount
            && SourceTriangleCount == ((mesh.GetIndices(EPrimitiveType.Triangles)?.Length ?? 0) / 3)
            && SourceMeshHash == sourceHash
            && MeshletSettingsHash == meshletHash
            && LodSettingsHash == lodHash
            && FreshnessHash == freshness
            && string.Equals(SourceMeshIdentity, identity, StringComparison.Ordinal)
            && string.Equals(MeshOptimizerVersionKey, versionKey, StringComparison.Ordinal);
    }

    public bool IsFreshForSourceMesh(XRMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        string identity = MeshletPayloadUtility.ResolveSourceMeshIdentity(mesh, SourceMeshIdentity);
        ulong sourceHash = MeshletPayloadUtility.ComputeSourceMeshHash(mesh);
        string versionKey = MeshOptimizerIntegration.MeshOptimizerVersionKey;
        ulong freshness = MeshletPayloadUtility.ComputeFreshnessHash(identity, sourceHash, MeshletSettingsHash, LodSettingsHash, versionKey);

        return PayloadVersion == CurrentPayloadVersion
            && SourceVertexCount == mesh.VertexCount
            && SourceTriangleCount == ((mesh.GetIndices(EPrimitiveType.Triangles)?.Length ?? 0) / 3)
            && SourceMeshHash == sourceHash
            && FreshnessHash == freshness
            && string.Equals(SourceMeshIdentity, identity, StringComparison.Ordinal)
            && string.Equals(MeshOptimizerVersionKey, versionKey, StringComparison.Ordinal);
    }

    public Meshlet[] CreateGpuMeshlets(uint meshID, uint materialID, int renderPass, uint vertexOffset = 0u, uint vertexIndexOffset = 0u, uint triangleOffset = 0u)
    {
        if (Meshlets.Length == 0)
            return [];

        Meshlet[] result = new Meshlet[Meshlets.Length];
        uint pass = (uint)renderPass;
        for (int i = 0; i < Meshlets.Length; i++)
        {
            CpuMeshletDescriptor descriptor = Meshlets[i];
            Meshlet meshlet = descriptor.ToGpuMeshlet(meshID, materialID, pass);
            meshlet.VertexOffset += vertexIndexOffset;
            meshlet.TriangleOffset += triangleOffset;
            result[i] = meshlet;
        }

        _ = vertexOffset;
        return result;
    }

    public static MeshletPayload CreateDisabled(
        XRMesh mesh,
        MeshletGenerationSettings? meshletSettings,
        MeshLodGenerationSettings? lodSettings,
        string? sourceMeshIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        MeshletGenerationSettingsSnapshot meshletSnapshot = MeshletGenerationSettingsSnapshot.From(meshletSettings);
        MeshLodGenerationSettingsSnapshot lodSnapshot = MeshLodGenerationSettingsSnapshot.From(lodSettings);
        string identity = MeshletPayloadUtility.ResolveSourceMeshIdentity(mesh, sourceMeshIdentity);
        ulong sourceHash = MeshletPayloadUtility.ComputeSourceMeshHash(mesh);
        ulong meshletHash = MeshletPayloadUtility.ComputeHash(meshletSnapshot);
        ulong lodHash = MeshletPayloadUtility.ComputeHash(lodSnapshot);
        string versionKey = MeshOptimizerIntegration.MeshOptimizerVersionKey;

        return new MeshletPayload
        {
            GenerationEnabled = false,
            MeshOptimizerVersionKey = versionKey,
            SourceMeshIdentity = identity,
            SourceVertexCount = mesh.VertexCount,
            SourceTriangleCount = (mesh.GetIndices(EPrimitiveType.Triangles)?.Length ?? 0) / 3,
            SourceMeshHash = sourceHash,
            MeshletSettingsHash = meshletHash,
            LodSettingsHash = lodHash,
            FreshnessHash = MeshletPayloadUtility.ComputeFreshnessHash(identity, sourceHash, meshletHash, lodHash, versionKey),
            MeshletSettings = meshletSnapshot,
            LodSettings = lodSnapshot,
            Stats = new MeshOptimizerMeshletStats(0, 0, 0, 0),
        };
    }
}

public static class MeshletPayloadUtility
{
    private const int SourceMeshHashVersion = 1;
    private const int MeshletSettingsHashVersion = 1;
    private const int LodSettingsHashVersion = 1;
    private const int FreshnessHashVersion = 1;

    public static string ResolveSourceMeshIdentity(XRMesh mesh, string? sourceMeshIdentity = null)
    {
        if (!string.IsNullOrWhiteSpace(sourceMeshIdentity))
            return sourceMeshIdentity;

        if (!string.IsNullOrWhiteSpace(mesh.OriginalPath))
            return mesh.OriginalPath!;

        if (!string.IsNullOrWhiteSpace(mesh.FilePath))
            return mesh.FilePath!;

        if (!string.IsNullOrWhiteSpace(mesh.Name))
            return mesh.Name!;

        return mesh.ID.ToString("N");
    }

    public static ulong ComputeSourceMeshHash(XRMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        XxHash64 hash = new();
        AppendInt32(hash, SourceMeshHashVersion);
        AppendInt32(hash, mesh.VertexCount);
        AppendInt32(hash, (int)mesh.Type);

        int[] indices = mesh.GetIndices(EPrimitiveType.Triangles) ?? [];
        AppendInt32(hash, indices.Length);
        for (int i = 0; i < indices.Length; i++)
            AppendInt32(hash, indices[i]);

        for (int i = 0; i < mesh.VertexCount; i++)
            AppendVector3(hash, mesh.GetPosition((uint)i));

        return hash.GetCurrentHashAsUInt64();
    }

    public static ulong ComputeHash(MeshletGenerationSettingsSnapshot settings)
    {
        XxHash64 hash = new();
        AppendInt32(hash, MeshletSettingsHashVersion);
        AppendBoolean(hash, settings.Enabled);
        AppendInt32(hash, (int)settings.BuildMode);
        AppendUInt32(hash, settings.MaxVertices);
        AppendUInt32(hash, settings.MinTriangles);
        AppendUInt32(hash, settings.MaxTriangles);
        AppendSingle(hash, settings.ConeWeight);
        AppendSingle(hash, settings.SplitFactor);
        AppendSingle(hash, settings.FillWeight);
        AppendBoolean(hash, settings.OptimizeMeshlets);
        AppendInt32(hash, settings.OptimizeLevel);
        AppendBoolean(hash, settings.ComputeBounds);
        AppendBoolean(hash, settings.EncodeMeshlets);
        AppendBoolean(hash, settings.EncodeVertexReferences);
        return hash.GetCurrentHashAsUInt64();
    }

    public static ulong ComputeHash(MeshLodGenerationSettingsSnapshot settings)
    {
        XxHash64 hash = new();
        AppendInt32(hash, LodSettingsHashVersion);
        AppendBoolean(hash, settings.Enabled);
        AppendInt32(hash, (int)settings.Mode);
        AppendInt32(hash, settings.AdditionalLodCount);
        AppendSingle(hash, settings.FirstLodIndexRatio);
        AppendSingle(hash, settings.LodRatioScale);
        AppendSingle(hash, settings.TargetError);
        AppendSingle(hash, settings.FirstLodDistance);
        AppendSingle(hash, settings.LodDistanceScale);
        AppendBoolean(hash, settings.ReusePreviousLodAsSource);
        AppendUInt32(hash, (uint)settings.Options);
        AppendBoolean(hash, settings.UseNormals);
        AppendSingle(hash, settings.NormalWeight);
        AppendBoolean(hash, settings.UseTangents);
        AppendSingle(hash, settings.TangentWeight);
        AppendBoolean(hash, settings.UseTexCoords);
        AppendSingle(hash, settings.TexCoordWeight);
        AppendBoolean(hash, settings.UseColors);
        AppendSingle(hash, settings.ColorWeight);
        AppendBoolean(hash, settings.ProtectAttributeSeams);
        AppendBoolean(hash, settings.PrioritizeBorderVertices);
        AppendBoolean(hash, settings.LockWeightedVertices);
        return hash.GetCurrentHashAsUInt64();
    }

    public static ulong ComputeFreshnessHash(string sourceMeshIdentity, ulong sourceMeshHash, ulong meshletSettingsHash, ulong lodSettingsHash, string meshOptimizerVersionKey)
    {
        XxHash64 hash = new();
        AppendInt32(hash, FreshnessHashVersion);
        AppendString(hash, sourceMeshIdentity);
        AppendUInt64(hash, sourceMeshHash);
        AppendUInt64(hash, meshletSettingsHash);
        AppendUInt64(hash, lodSettingsHash);
        AppendString(hash, meshOptimizerVersionKey);
        return hash.GetCurrentHashAsUInt64();
    }

    private static void AppendBoolean(XxHash64 hash, bool value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value ? (byte)1 : (byte)0;
        hash.Append(buffer);
    }

    private static void AppendInt32(XxHash64 hash, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.Append(buffer);
    }

    private static void AppendUInt32(XxHash64 hash, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        hash.Append(buffer);
    }

    private static void AppendUInt64(XxHash64 hash, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        hash.Append(buffer);
    }

    private static void AppendSingle(XxHash64 hash, float value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, BitConverter.SingleToUInt32Bits(value));
        hash.Append(buffer);
    }

    private static void AppendVector3(XxHash64 hash, Vector3 value)
    {
        AppendSingle(hash, value.X);
        AppendSingle(hash, value.Y);
        AppendSingle(hash, value.Z);
    }

    private static void AppendString(XxHash64 hash, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            AppendInt32(hash, 0);
            return;
        }

        int byteCount = System.Text.Encoding.UTF8.GetByteCount(value);
        AppendInt32(hash, byteCount);
        Span<byte> stackBuffer = byteCount <= 256 ? stackalloc byte[byteCount] : [];
        if (!stackBuffer.IsEmpty)
        {
            System.Text.Encoding.UTF8.GetBytes(value, stackBuffer);
            hash.Append(stackBuffer);
            return;
        }

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        hash.Append(bytes);
    }
}
