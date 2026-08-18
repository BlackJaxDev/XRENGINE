using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Hashing;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Meshlets;

/// <summary>Persisted derived-data state; absence is never inferred from a null payload.</summary>
public enum MeshletPayloadState : byte
{
    Present,
    Disabled,
    Empty,
    MissingRepairable,
    CorruptRepairable,
    RepairFailed,
}

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
    private static long s_nextValidationRevision;
    public const int CurrentPayloadVersion = 3;
    public const uint PortableMaxVertices = 64u;
    public const uint PortableMaxTriangles = 124u;

    public int PayloadVersion { get; init; } = CurrentPayloadVersion;
    public bool GenerationEnabled { get; init; }
    public MeshletPayloadState State { get; init; }
    public string MeshOptimizerVersionKey { get; init; } = string.Empty;
    /// <summary>Cooker-only provenance. Runtime compatibility never depends on this value.</summary>
    public string CookProvenanceKey { get; init; } = string.Empty;
    /// <summary>Stable token used for O(1) runtime payload compatibility checks.</summary>
    public ulong RuntimeCompatibilityToken { get; init; }
    /// <summary>Nonserialized proof that this in-memory payload passed full validation.</summary>
    public long ValidationRevision { get; private set; }
    /// <summary>Nonserialized owner binding issued only after source-mesh validation.</summary>
    public ulong OwnerValidationToken { get; private set; }
    /// <summary>Nonserialized geometry revision of the mesh that issued the owner binding.</summary>
    public long OwnerGeometryRevision { get; private set; }
    public string SourceMeshIdentity { get; init; } = string.Empty;
    public int SourceVertexCount { get; init; }
    public int SourceTriangleCount { get; init; }
    public ulong SourceMeshHash { get; init; }
    public ulong MeshletSettingsHash { get; init; }
    public ulong LodSettingsHash { get; init; }
    public ulong FreshnessHash { get; init; }
    public MeshletGenerationSettingsSnapshot MeshletSettings { get; init; }
    public MeshLodGenerationSettingsSnapshot LodSettings { get; init; }
    public ImmutableArray<CpuMeshletDescriptor> Meshlets { get; init; } = [];
    public ImmutableArray<uint> VertexIndices { get; init; } = [];
    public ImmutableArray<byte> TriangleIndices { get; init; } = [];
    public ImmutableArray<MeshletVertex> Vertices { get; init; } = [];
    public MeshOptimizerMeshletStats Stats { get; init; }

    public bool HasMeshlets => State == MeshletPayloadState.Present && Meshlets.Length > 0;

    public bool IsRuntimeCompatible
        => PayloadVersion == CurrentPayloadVersion
           && RuntimeCompatibilityToken == MeshletPayloadUtility.ComputeRuntimeCompatibilityToken(MeshletSettings)
           && MeshletSettings.MaxVertices <= PortableMaxVertices
           && MeshletSettings.MaxTriangles <= PortableMaxTriangles;

    public bool IsValidatedForRuntime
        => ValidationRevision != 0 && OwnerValidationToken != 0UL && OwnerGeometryRevision != 0 && IsRuntimeCompatible;

    /// <summary>Performs the O(1) owner-revision check required by runtime registration.</summary>
    public bool IsValidatedFor(XRMesh mesh)
        => mesh is not null && IsValidatedForRuntime && OwnerGeometryRevision == mesh.GeometryRevision;

    /// <summary>Validates this derived payload against its owning source mesh on the import/cache thread.</summary>
    public void ValidateForMesh(XRMesh mesh, string? expectedIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ValidatePortablePayload();
        string identity = MeshletPayloadUtility.ResolveSourceMeshIdentity(mesh, expectedIdentity);
        int triangleCount = (mesh.GetIndices(EPrimitiveType.Triangles)?.Length ?? 0) / 3;
        ulong sourceHash = MeshletPayloadUtility.ComputeSourceMeshHash(mesh);
        ulong freshness = MeshletPayloadUtility.ComputeFreshnessHash(identity, sourceHash, MeshletSettingsHash, LodSettingsHash, CookProvenanceKey);
        if (SourceVertexCount != mesh.VertexCount || SourceTriangleCount != triangleCount || SourceMeshHash != sourceHash
            || !string.Equals(SourceMeshIdentity, identity, StringComparison.Ordinal) || FreshnessHash != freshness)
            throw new InvalidDataException("Meshlet payload does not belong to the supplied source mesh.");

        if (OwnerValidationToken == 0UL)
            OwnerValidationToken = MeshletPayloadUtility.ComputeOwnerValidationToken(identity, sourceHash, FreshnessHash);
        OwnerGeometryRevision = mesh.GeometryRevision;
        if (ValidationRevision == 0)
            ValidationRevision = Interlocked.Increment(ref s_nextValidationRevision);
    }

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    /// <summary>Validates all portable CPU payload ranges before persistence or GPU registration.</summary>
    public void ValidatePortablePayload()
    {
        if (!IsRuntimeCompatible)
            throw new InvalidDataException($"Meshlet payload is not compatible with the portable shader profile ({PortableMaxVertices} vertices, {PortableMaxTriangles} triangles).");
        if (!Enum.IsDefined(State))
            throw new InvalidDataException("Meshlet payload has an unknown generated-data state.");
        if (SourceVertexCount < 0 || SourceTriangleCount < 0 || Stats.MeshletCount != Meshlets.Length
            || Stats.VertexReferenceCount != VertexIndices.Length || Stats.TriangleByteCount != TriangleIndices.Length
            || Stats.EncodedByteCount < 0)
            throw new InvalidDataException("Meshlet payload has invalid source or statistics counts.");

        if (State == MeshletPayloadState.Present
            && (!GenerationEnabled || Meshlets.Length == 0 || VertexIndices.Length == 0 || TriangleIndices.Length == 0))
            throw new InvalidDataException("Present meshlet payloads must contain enabled descriptor and index streams.");

        if (State == MeshletPayloadState.Empty && !GenerationEnabled)
            throw new InvalidDataException("Empty meshlet payloads must record enabled generation with no output.");

        if (State == MeshletPayloadState.Disabled
            && (GenerationEnabled || Meshlets.Length != 0 || VertexIndices.Length != 0 || TriangleIndices.Length != 0 || Stats.EncodedByteCount != 0))
            throw new InvalidDataException("Disabled meshlet payloads must not contain generated meshlet data.");

        if ((State is MeshletPayloadState.Empty or MeshletPayloadState.MissingRepairable or MeshletPayloadState.CorruptRepairable or MeshletPayloadState.RepairFailed)
            && (Meshlets.Length != 0 || VertexIndices.Length != 0 || TriangleIndices.Length != 0))
            throw new InvalidDataException($"Meshlet payload state '{State}' must not contain meshlet streams.");

        if (Meshlets.Length == 0 && (VertexIndices.Length != 0 || TriangleIndices.Length != 0))
            throw new InvalidDataException("Empty meshlet payloads must not contain index streams.");

        uint previousVertexEnd = 0;
        uint previousTriangleEnd = 0;
        for (int index = 0; index < Meshlets.Length; index++)
        {
            CpuMeshletDescriptor descriptor = Meshlets[index];
            if (descriptor.VertexCount == 0 || descriptor.TriangleCount == 0
                || descriptor.VertexCount > PortableMaxVertices || descriptor.TriangleCount > PortableMaxTriangles
                || descriptor.VertexCount > MeshletSettings.MaxVertices
                || descriptor.TriangleCount > MeshletSettings.MaxTriangles
                || (ulong)descriptor.VertexOffset + descriptor.VertexCount > (ulong)VertexIndices.Length
                || (ulong)descriptor.TriangleOffset + ((ulong)descriptor.TriangleCount * 3UL) > (ulong)TriangleIndices.Length)
            {
                throw new InvalidDataException($"Meshlet descriptor {index} has an invalid vertex or triangle range.");
            }

            if (descriptor.TriangleOffset % 4u != 0u
                || descriptor.VertexOffset < previousVertexEnd
                || descriptor.TriangleOffset < previousTriangleEnd)
            {
                throw new InvalidDataException($"Meshlet descriptor {index} has non-monotonic or unaligned stream offsets.");
            }

            if (!float.IsFinite(descriptor.BoundsSphere.X) || !float.IsFinite(descriptor.BoundsSphere.Y)
                || !float.IsFinite(descriptor.BoundsSphere.Z) || !float.IsFinite(descriptor.BoundsSphere.W)
                || descriptor.BoundsSphere.W < 0.0f
                || !float.IsFinite(descriptor.Cone.X) || !float.IsFinite(descriptor.Cone.Y)
                || !float.IsFinite(descriptor.Cone.Z) || !float.IsFinite(descriptor.Cone.W)
                || !float.IsFinite(descriptor.ConeApex.X) || !float.IsFinite(descriptor.ConeApex.Y)
                || !float.IsFinite(descriptor.ConeApex.Z) || !float.IsFinite(descriptor.ConeApex.W))
            {
                throw new InvalidDataException($"Meshlet descriptor {index} contains non-finite bounds or cone data.");
            }

            for (uint vertex = 0; vertex < descriptor.VertexCount; vertex++)
                if (VertexIndices[checked((int)(descriptor.VertexOffset + vertex))] >= (uint)SourceVertexCount)
                    throw new InvalidDataException($"Meshlet descriptor {index} references a vertex outside the source mesh.");

            for (uint triangleByte = 0; triangleByte < descriptor.TriangleCount * 3u; triangleByte++)
                if (TriangleIndices[checked((int)(descriptor.TriangleOffset + triangleByte))] >= descriptor.VertexCount)
                    throw new InvalidDataException($"Meshlet descriptor {index} contains an invalid local triangle index.");

            previousVertexEnd = checked(descriptor.VertexOffset + descriptor.VertexCount);
            previousTriangleEnd = checked(descriptor.TriangleOffset + (descriptor.TriangleCount * 3u));
        }

        if (TriangleIndices.Length != 0 && (TriangleIndices.Length & 3) != 0)
            throw new InvalidDataException("The meshlet local-triangle stream must have four-byte-aligned terminal padding.");

        if (Meshlets.Length > 0)
        {
            CpuMeshletDescriptor last = Meshlets[^1];
            ulong requiredByteCount = (last.TriangleOffset + ((ulong)last.TriangleCount * 3UL) + 3UL) & ~3UL;
            if (requiredByteCount != (ulong)TriangleIndices.Length)
                throw new InvalidDataException("The meshlet local-triangle stream does not preserve its required terminal padding.");
        }

        for (int vertexIndex = 0; vertexIndex < Vertices.Length; vertexIndex++)
        {
            MeshletVertex vertex = Vertices[vertexIndex];
            if (!IsFinite(vertex.Position) || !IsFinite(vertex.Normal) || !IsFinite(vertex.Tangent)
                || !float.IsFinite(vertex.TexCoord.X) || !float.IsFinite(vertex.TexCoord.Y))
                throw new InvalidDataException($"Meshlet vertex {vertexIndex} contains non-finite data.");
        }

    }

    public bool IsFreshFor(XRMesh mesh, MeshletGenerationSettings? meshletSettings, MeshLodGenerationSettings? lodSettings, string? sourceMeshIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        MeshletGenerationSettingsSnapshot meshletSnapshot = MeshletGenerationSettingsSnapshot.From(meshletSettings);
        MeshLodGenerationSettingsSnapshot lodSnapshot = MeshLodGenerationSettingsSnapshot.From(lodSettings);
        string identity = MeshletPayloadUtility.ResolveSourceMeshIdentity(mesh, sourceMeshIdentity);
        ulong sourceHash = MeshletPayloadUtility.ComputeSourceMeshHash(mesh);
        ulong meshletHash = MeshletPayloadUtility.ComputeHash(meshletSnapshot);
        ulong lodHash = MeshletPayloadUtility.ComputeHash(lodSnapshot);
        string provenanceKey = MeshletPayloadUtility.CurrentCookProvenanceKey;
        ulong freshness = MeshletPayloadUtility.ComputeFreshnessHash(identity, sourceHash, meshletHash, lodHash, provenanceKey);

        return PayloadVersion == CurrentPayloadVersion
            && OwnerGeometryRevision == mesh.GeometryRevision
            && SourceVertexCount == mesh.VertexCount
            && SourceTriangleCount == ((mesh.GetIndices(EPrimitiveType.Triangles)?.Length ?? 0) / 3)
            && SourceMeshHash == sourceHash
            && MeshletSettingsHash == meshletHash
            && LodSettingsHash == lodHash
            && FreshnessHash == freshness
            && string.Equals(SourceMeshIdentity, identity, StringComparison.Ordinal)
            && string.Equals(CookProvenanceKey, provenanceKey, StringComparison.Ordinal);
    }

    public bool IsFreshForSourceMesh(XRMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        string identity = MeshletPayloadUtility.ResolveSourceMeshIdentity(mesh, SourceMeshIdentity);
        ulong sourceHash = MeshletPayloadUtility.ComputeSourceMeshHash(mesh);
        string versionKey = CookProvenanceKey;
        ulong freshness = MeshletPayloadUtility.ComputeFreshnessHash(identity, sourceHash, MeshletSettingsHash, LodSettingsHash, versionKey);

        return PayloadVersion == CurrentPayloadVersion
            && OwnerGeometryRevision == mesh.GeometryRevision
            && SourceVertexCount == mesh.VertexCount
            && SourceTriangleCount == ((mesh.GetIndices(EPrimitiveType.Triangles)?.Length ?? 0) / 3)
            && SourceMeshHash == sourceHash
            && FreshnessHash == freshness
            && string.Equals(SourceMeshIdentity, identity, StringComparison.Ordinal)
            && IsRuntimeCompatible;
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
        string provenanceKey = MeshletPayloadUtility.CurrentCookProvenanceKey;

        MeshletPayload payload = new()
        {
            GenerationEnabled = false,
            State = MeshletPayloadState.Disabled,
            MeshOptimizerVersionKey = versionKey,
            CookProvenanceKey = provenanceKey,
            RuntimeCompatibilityToken = MeshletPayloadUtility.ComputeRuntimeCompatibilityToken(meshletSnapshot),
            SourceMeshIdentity = identity,
            SourceVertexCount = mesh.VertexCount,
            SourceTriangleCount = (mesh.GetIndices(EPrimitiveType.Triangles)?.Length ?? 0) / 3,
            SourceMeshHash = sourceHash,
            MeshletSettingsHash = meshletHash,
            LodSettingsHash = lodHash,
            FreshnessHash = MeshletPayloadUtility.ComputeFreshnessHash(identity, sourceHash, meshletHash, lodHash, provenanceKey),
            MeshletSettings = meshletSnapshot,
            LodSettings = lodSnapshot,
            Stats = new MeshOptimizerMeshletStats(0, 0, 0, 0),
        };
        payload.ValidatePortablePayload();
        return payload;
    }
}

public static class MeshletPayloadUtility
{
    private const int SourceMeshHashVersion = 3;
    private const int MeshletSettingsHashVersion = 1;
    private const int LodSettingsHashVersion = 1;
    private const int FreshnessHashVersion = 2;
    private const int TopologyPolicyVersion = 1;
    private const int SharedMeshletCodecVersion = 3;

    /// <summary>Import/cache provenance; runtime compatibility is deliberately separate.</summary>
    public static string CurrentCookProvenanceKey
        => $"meshoptimizer={MeshOptimizerIntegration.MeshOptimizerVersionKey};payload={MeshletPayload.CurrentPayloadVersion};sourceHash={SourceMeshHashVersion};meshletSettings={MeshletSettingsHashVersion};lodSettings={LodSettingsHashVersion};freshness={FreshnessHashVersion};topologyPolicy={TopologyPolicyVersion};codec={SharedMeshletCodecVersion}";

    public static ulong ComputeOwnerValidationToken(string identity, ulong sourceHash, ulong freshnessHash)
    {
        XxHash64 hash = new();
        AppendString(hash, identity);
        AppendUInt64(hash, sourceHash);
        AppendUInt64(hash, freshnessHash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash.GetCurrentHash());
    }

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
        AppendBoolean(hash, mesh.HasNormals);
        AppendBoolean(hash, mesh.HasTangents);
        AppendUInt32(hash, mesh.TexCoordCount);
        AppendUInt32(hash, mesh.ColorCount);

        int[] indices = mesh.GetIndices(EPrimitiveType.Triangles) ?? [];
        AppendInt32(hash, indices.Length);
        for (int i = 0; i < indices.Length; i++)
            AppendInt32(hash, indices[i]);

        for (int i = 0; i < mesh.VertexCount; i++)
        {
            AppendVector3(hash, mesh.GetPosition((uint)i));
            AppendVector3(hash, mesh.GetNormal((uint)i));
            AppendVector4(hash, mesh.GetTangentWithSign((uint)i));
            AppendBoolean(hash, mesh.Vertices.Length > i && mesh.Vertices[i].Weights is { Count: > 0 });
            for (uint texCoord = 0; texCoord < mesh.TexCoordCount; texCoord++)
                AppendVector2(hash, mesh.GetTexCoord((uint)i, texCoord));
            for (uint color = 0; color < mesh.ColorCount; color++)
                AppendVector4(hash, mesh.GetColor((uint)i, color));
        }

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

    public static ulong ComputeRuntimeCompatibilityToken(MeshletGenerationSettingsSnapshot settings)
    {
        XxHash64 hash = new();
        AppendInt32(hash, MeshletPayload.CurrentPayloadVersion);
        AppendInt32(hash, 1); // portable descriptor layout
        AppendInt32(hash, 1); // local-triangle byte packing
        AppendInt32(hash, 1); // uint32 vertex-reference stream encoding
        AppendUInt32(hash, MeshletPayload.PortableMaxVertices);
        AppendUInt32(hash, MeshletPayload.PortableMaxTriangles);
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

    private static void AppendVector2(XxHash64 hash, Vector2 value)
    {
        AppendSingle(hash, value.X);
        AppendSingle(hash, value.Y);
    }

    private static void AppendVector4(XxHash64 hash, Vector4 value)
    {
        AppendSingle(hash, value.X);
        AppendSingle(hash, value.Y);
        AppendSingle(hash, value.Z);
        AppendSingle(hash, value.W);
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
