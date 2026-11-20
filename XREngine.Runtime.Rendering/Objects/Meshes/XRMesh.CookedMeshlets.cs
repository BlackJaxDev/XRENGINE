using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using XREngine.Core.Files;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering;

public partial class XRMesh
{
    private const long MaxStandaloneMeshletPayloadBytes = 512L * 1024L * 1024L;

    /// <summary>
    /// Serializes a meshlet payload to a freshly allocated byte buffer using the
    /// same on-disk layout as the cooked-mesh writer. Intended for runtime disk
    /// caches that mirror the cooking format.
    /// </summary>
    public static byte[] SerializeMeshletPayloadToBytes(MeshletPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        payload.ValidatePortablePayload();

        long size = CalculateMeshletPayloadSize(payload);
        if (size <= 0 || size > int.MaxValue || size > MaxStandaloneMeshletPayloadBytes)
            throw new InvalidOperationException($"MeshletPayload size out of range: {size}.");

        byte[] buffer = new byte[size];
        RuntimeCookedBinaryWriter writer = new(buffer);
        WriteMeshletPayload(writer, payload);
        return buffer;
    }

    /// <summary>
    /// Deserializes a meshlet payload previously produced by
    /// <see cref="SerializeMeshletPayloadToBytes"/>. Returns <c>null</c> when
    /// the input represents an absent payload.
    /// </summary>
    public static MeshletPayload? DeserializeMeshletPayloadFromBytes(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (buffer.Length == 0)
            return null;
        if (buffer.LongLength > MaxStandaloneMeshletPayloadBytes)
            throw new InvalidDataException("The serialized meshlet payload exceeds the standalone read limit.");

        RuntimeCookedBinaryReader reader = new(buffer);
        MeshletPayload? payload = ReadMeshletPayload(reader);
        if (reader.Remaining != 0)
            throw new InvalidDataException("The serialized meshlet payload contains trailing bytes.");
        return payload;
    }

    internal static long CalculateMeshletPayloadSize(MeshletPayload? payload)
    {
        long size = sizeof(byte);
        if (payload is null)
            return size;

        size += sizeof(int); // payload version
        size += sizeof(byte) * 2; // generation enabled + derived-data state
        size += CalculateStringSize(payload.MeshOptimizerVersionKey);
        size += CalculateStringSize(payload.CookProvenanceKey);
        size += sizeof(ulong); // runtime compatibility token
        size += CalculateStringSize(payload.SourceMeshIdentity);
        size += sizeof(int) * 2;
        size += sizeof(ulong) * 4;
        size += CalculateMeshletSettingsSize();
        size += CalculateLodSettingsSize();
        size += CalculateCpuMeshletDescriptorArraySize(payload.Meshlets.ToArray());
        size += CalculateUInt32ArraySize(payload.VertexIndices.ToArray());
        size += CalculateByteArraySize(payload.TriangleIndices.ToArray());
        if (payload.PayloadVersion <= MeshletPayload.LastPayloadVersionWithVertexStream)
            size += sizeof(int); // empty legacy vertex stream
        size += sizeof(int) * 4; // stats
        if (payload.PayloadVersion >= MeshletPayload.FirstClusterHierarchyPayloadVersion)
            size += CalculateClusterHierarchySize(payload);
        if (payload.PayloadVersion >= MeshletPayload.ClusterSkinningPayloadVersion)
            size += CalculateClusterSkinningSize(payload);
        if (payload.PayloadVersion >= MeshletPayload.DeformationOrderPayloadVersion)
            size += CalculateDeformationOrderSize(payload);
        return size;
    }

    /// <summary>
    /// The vertex order, the prefix counts, the level errors and the three level cover tables, each
    /// with a count (4 bytes per entry).
    /// </summary>
    private static long CalculateDeformationOrderSize(MeshletPayload payload)
        => sizeof(int) + (long)payload.DeformationVertexOrder.Length * sizeof(uint)
           + sizeof(int) + (long)payload.DeformationPrefixCounts.Length * sizeof(uint)
           + sizeof(int) + (long)payload.LevelMaxErrors.Length * sizeof(float)
           + sizeof(int) + (long)payload.LevelCoverIndices.Length * sizeof(uint)
           + sizeof(int) + (long)payload.LevelCoverFirstIndex.Length * sizeof(uint)
           + sizeof(int) + (long)payload.LevelCoverIndexCount.Length * sizeof(uint);

    private static void WriteDeformationOrder(RuntimeCookedBinaryWriter writer, MeshletPayload payload)
    {
        writer.Write(payload.DeformationVertexOrder.Length);
        foreach (uint vertex in payload.DeformationVertexOrder)
            writer.Write(vertex);
        writer.Write(payload.DeformationPrefixCounts.Length);
        foreach (uint count in payload.DeformationPrefixCounts)
            writer.Write(count);
        writer.Write(payload.LevelMaxErrors.Length);
        foreach (float error in payload.LevelMaxErrors)
            writer.Write(error);
        WriteUInt32Table(writer, payload.LevelCoverIndices);
        WriteUInt32Table(writer, payload.LevelCoverFirstIndex);
        WriteUInt32Table(writer, payload.LevelCoverIndexCount);
    }

    private static void WriteUInt32Table(RuntimeCookedBinaryWriter writer, ImmutableArray<uint> table)
    {
        writer.Write(table.Length);
        foreach (uint value in table)
            writer.Write(value);
    }

    /// <summary>The deformation order tables and the level cover tables of a version 7 payload.</summary>
    private readonly record struct DeformationTables(
        uint[] Order,
        uint[] PrefixCounts,
        float[] LevelErrors,
        uint[] CoverIndices,
        uint[] CoverFirstIndex,
        uint[] CoverIndexCount);

    private static DeformationTables ReadDeformationOrder(RuntimeCookedBinaryReader reader)
    {
        uint[] order = ReadUInt32Table(reader, "deformation order entry");
        uint[] prefixCounts = ReadUInt32Table(reader, "deformation prefix count");

        int errorCount = reader.ReadInt32();
        ValidateArrayCount(errorCount, sizeof(float), reader.Remaining, "deformation level error");
        float[] levelErrors = new float[errorCount];
        for (int i = 0; i < errorCount; i++)
            levelErrors[i] = reader.ReadSingle();

        uint[] coverIndices = ReadUInt32Table(reader, "level cover index");
        uint[] coverFirstIndex = ReadUInt32Table(reader, "level cover first index");
        uint[] coverIndexCount = ReadUInt32Table(reader, "level cover index count");
        return new DeformationTables(order, prefixCounts, levelErrors, coverIndices, coverFirstIndex, coverIndexCount);
    }

    private static uint[] ReadUInt32Table(RuntimeCookedBinaryReader reader, string entryName)
    {
        int count = reader.ReadInt32();
        ValidateArrayCount(count, sizeof(uint), reader.Remaining, entryName);
        uint[] table = new uint[count];
        for (int i = 0; i < count; i++)
            table[i] = reader.ReadUInt32();
        return table;
    }

    /// <summary>The skin record count and records (8 bytes each), then the bone count and bones (2 bytes each).</summary>
    private static long CalculateClusterSkinningSize(MeshletPayload payload)
        => sizeof(int) + (long)payload.ClusterSkins.Length * ClusterSkinRecordBytes
           + sizeof(int) + (long)payload.ClusterBones.Length * sizeof(ushort);

    private const int ClusterSkinRecordBytes = sizeof(uint) + sizeof(ushort) * 2;

    /// <summary>Level-0 count, then the cluster records (8 bytes each) and group records (32 bytes each), each with a count.</summary>
    private static long CalculateClusterHierarchySize(MeshletPayload payload)
        => sizeof(int)
           + sizeof(int) + (long)payload.ClusterLods.Length * ClusterLodRecordBytes
           + sizeof(int) + (long)payload.LodGroups.Length * LodGroupRecordBytes;

    private const int ClusterLodRecordBytes = sizeof(int) * 2;
    private const int LodGroupRecordBytes = sizeof(float) * 5 + sizeof(int) + sizeof(uint) * 2;

    private static void WriteClusterHierarchy(RuntimeCookedBinaryWriter writer, MeshletPayload payload)
    {
        writer.Write(payload.ClusterLevel0Count);
        writer.Write(payload.ClusterLods.Length);
        foreach (MeshletClusterLodRecord record in payload.ClusterLods)
        {
            writer.Write(record.Group);
            writer.Write(record.RefinedGroup);
        }
        writer.Write(payload.LodGroups.Length);
        foreach (MeshletLodGroupRecord group in payload.LodGroups)
        {
            writer.Write(group.SimplifiedSphere.X);
            writer.Write(group.SimplifiedSphere.Y);
            writer.Write(group.SimplifiedSphere.Z);
            writer.Write(group.SimplifiedSphere.W);
            writer.Write(group.SimplifiedError);
            writer.Write(group.Depth);
            writer.Write(group.Reserved0);
            writer.Write(group.Reserved1);
        }
    }

    private static (int Level0Count, MeshletClusterLodRecord[] Clusters, MeshletLodGroupRecord[] Groups) ReadClusterHierarchy(RuntimeCookedBinaryReader reader)
    {
        int level0Count = reader.ReadInt32();
        int clusterCount = reader.ReadInt32();
        ValidateArrayCount(clusterCount, ClusterLodRecordBytes, reader.Remaining, "cluster hierarchy record");
        MeshletClusterLodRecord[] clusters = new MeshletClusterLodRecord[clusterCount];
        for (int i = 0; i < clusterCount; i++)
            clusters[i] = new MeshletClusterLodRecord(reader.ReadInt32(), reader.ReadInt32());

        int groupCount = reader.ReadInt32();
        ValidateArrayCount(groupCount, LodGroupRecordBytes, reader.Remaining, "cluster hierarchy group");
        MeshletLodGroupRecord[] groups = new MeshletLodGroupRecord[groupCount];
        for (int i = 0; i < groupCount; i++)
        {
            Vector4 sphere = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            groups[i] = new MeshletLodGroupRecord(sphere, reader.ReadSingle(), reader.ReadInt32(), reader.ReadUInt32(), reader.ReadUInt32());
        }

        return (level0Count, clusters, groups);
    }

    internal static void WriteMeshletPayload(RuntimeCookedBinaryWriter writer, MeshletPayload? payload)
    {
        if (payload is null)
        {
            writer.Write((byte)0);
            return;
        }

        writer.Write((byte)1);
        writer.Write(payload.PayloadVersion);
        writer.Write(payload.GenerationEnabled ? (byte)1 : (byte)0);
        writer.Write((byte)payload.State);
        writer.Write(payload.MeshOptimizerVersionKey);
        writer.Write(payload.CookProvenanceKey);
        writer.Write(payload.RuntimeCompatibilityToken);
        writer.Write(payload.SourceMeshIdentity);
        writer.Write(payload.SourceVertexCount);
        writer.Write(payload.SourceTriangleCount);
        writer.Write(payload.SourceMeshHash);
        writer.Write(payload.MeshletSettingsHash);
        writer.Write(payload.LodSettingsHash);
        writer.Write(payload.FreshnessHash);
        WriteMeshletSettings(writer, payload.MeshletSettings);
        WriteLodSettings(writer, payload.LodSettings);
        WriteCpuMeshletDescriptors(writer, payload.Meshlets.ToArray());
        WriteUInt32Array(writer, payload.VertexIndices.ToArray());
        WriteByteArray(writer, payload.TriangleIndices.ToArray());
        if (payload.PayloadVersion <= MeshletPayload.LastPayloadVersionWithVertexStream)
            writer.Write(0); // empty legacy vertex stream
        writer.Write(payload.Stats.MeshletCount);
        writer.Write(payload.Stats.VertexReferenceCount);
        writer.Write(payload.Stats.TriangleByteCount);
        writer.Write(payload.Stats.EncodedByteCount);
        if (payload.PayloadVersion >= MeshletPayload.FirstClusterHierarchyPayloadVersion)
            WriteClusterHierarchy(writer, payload);
        if (payload.PayloadVersion >= MeshletPayload.ClusterSkinningPayloadVersion)
            WriteClusterSkinning(writer, payload);
        if (payload.PayloadVersion >= MeshletPayload.DeformationOrderPayloadVersion)
            WriteDeformationOrder(writer, payload);
    }

    private static void WriteClusterSkinning(RuntimeCookedBinaryWriter writer, MeshletPayload payload)
    {
        writer.Write(payload.ClusterSkins.Length);
        foreach (MeshletClusterSkinRecord skin in payload.ClusterSkins)
        {
            writer.Write(skin.FirstBone);
            writer.Write(skin.BoneCount);
            writer.Write(skin.Flags);
        }
        writer.Write(payload.ClusterBones.Length);
        foreach (ushort bone in payload.ClusterBones)
            writer.Write(bone);
    }

    private static (MeshletClusterSkinRecord[] Skins, ushort[] Bones) ReadClusterSkinning(RuntimeCookedBinaryReader reader)
    {
        int skinCount = reader.ReadInt32();
        ValidateArrayCount(skinCount, ClusterSkinRecordBytes, reader.Remaining, "cluster skinning record");
        MeshletClusterSkinRecord[] skins = new MeshletClusterSkinRecord[skinCount];
        for (int i = 0; i < skinCount; i++)
            skins[i] = new MeshletClusterSkinRecord(reader.ReadUInt32(), reader.ReadUInt16(), reader.ReadUInt16());

        int boneCount = reader.ReadInt32();
        ValidateArrayCount(boneCount, sizeof(ushort), reader.Remaining, "cluster bone");
        ushort[] bones = new ushort[boneCount];
        for (int i = 0; i < boneCount; i++)
            bones[i] = reader.ReadUInt16();
        return (skins, bones);
    }

    internal static MeshletPayload? ReadMeshletPayload(RuntimeCookedBinaryReader reader)
    {
        bool hasPayload = reader.ReadByte() != 0;
        if (!hasPayload)
            return null;

        int payloadVersion = reader.ReadInt32();
        bool generationEnabled = reader.ReadByte() != 0;
        MeshletPayloadState state = payloadVersion >= 3
            ? (MeshletPayloadState)reader.ReadByte()
            : generationEnabled ? MeshletPayloadState.Present : MeshletPayloadState.Disabled;
        string meshOptimizerVersionKey = reader.ReadString();
        string cookProvenanceKey = payloadVersion >= 2 ? reader.ReadString() : meshOptimizerVersionKey;
        ulong runtimeCompatibilityToken = payloadVersion >= 2
            ? reader.ReadUInt64()
            : 0UL;
        string sourceMeshIdentity = reader.ReadString();
        int sourceVertexCount = reader.ReadInt32();
        int sourceTriangleCount = reader.ReadInt32();
        ulong sourceMeshHash = reader.ReadUInt64();
        ulong meshletSettingsHash = reader.ReadUInt64();
        ulong lodSettingsHash = reader.ReadUInt64();
        ulong freshnessHash = reader.ReadUInt64();
        MeshletGenerationSettingsSnapshot meshletSettings = ReadMeshletSettings(reader);
        MeshLodGenerationSettingsSnapshot lodSettings = ReadLodSettings(reader);
        CpuMeshletDescriptor[] meshlets = ReadCpuMeshletDescriptors(reader);
        uint[] vertexIndices = ReadUInt32Array(reader);
        byte[] triangleIndices = ReadByteArray(reader);
        // Older payloads stored a per-vertex copy that runtime never needs.
        if (payloadVersion <= MeshletPayload.LastPayloadVersionWithVertexStream &&
            !TrySkipMeshletVertices(reader))
            throw new InvalidDataException("The meshlet vertex stream has an invalid bounded element count.");
        MeshOptimizerMeshletStats stats = new(
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32());

        // Version 5 adds the cluster hierarchy. The settings bytes do not carry the hierarchy flag;
        // the version implies it.
        int level0Count = 0;
        MeshletClusterLodRecord[] clusterLods = [];
        MeshletLodGroupRecord[] lodGroups = [];
        MeshletClusterSkinRecord[] clusterSkins = [];
        ushort[] clusterBones = [];
        DeformationTables deformation = new([], [], [], [], [], []);
        if (payloadVersion >= MeshletPayload.FirstClusterHierarchyPayloadVersion)
        {
            (level0Count, clusterLods, lodGroups) = ReadClusterHierarchy(reader);
            meshletSettings = meshletSettings with { BuildClusterHierarchy = true };
        }
        if (payloadVersion >= MeshletPayload.ClusterSkinningPayloadVersion)
            (clusterSkins, clusterBones) = ReadClusterSkinning(reader);
        if (payloadVersion >= MeshletPayload.DeformationOrderPayloadVersion)
            deformation = ReadDeformationOrder(reader);

        if (payloadVersion < 2)
            runtimeCompatibilityToken = MeshletPayloadUtility.ComputeRuntimeCompatibilityToken(meshletSettings);

        if (payloadVersion < 3 && state == MeshletPayloadState.Present && meshlets.Length == 0)
            state = MeshletPayloadState.Empty;

        // Version 3 differs from the current layout only by the skipped vertex
        // stream, so its payloads stay runtime compatible without a recook.
        if (payloadVersion == MeshletPayload.LastPayloadVersionWithVertexStream)
        {
            if (!MeshletPayloadUtility.IsLegacyVertexStreamRuntimeCompatible(runtimeCompatibilityToken))
                throw new InvalidDataException("Legacy meshlet payload does not match the portable shader profile.");
            payloadVersion = MeshletPayload.CurrentPayloadVersion;
            runtimeCompatibilityToken = MeshletPayloadUtility.ComputeRuntimeCompatibilityToken(meshletSettings);
        }

        MeshletPayload payload = new()
        {
            PayloadVersion = payloadVersion,
            GenerationEnabled = generationEnabled,
            State = state,
            MeshOptimizerVersionKey = meshOptimizerVersionKey,
            CookProvenanceKey = cookProvenanceKey,
            RuntimeCompatibilityToken = runtimeCompatibilityToken,
            SourceMeshIdentity = sourceMeshIdentity,
            SourceVertexCount = sourceVertexCount,
            SourceTriangleCount = sourceTriangleCount,
            SourceMeshHash = sourceMeshHash,
            MeshletSettingsHash = meshletSettingsHash,
            LodSettingsHash = lodSettingsHash,
            FreshnessHash = freshnessHash,
            MeshletSettings = meshletSettings,
            LodSettings = lodSettings,
            Meshlets = meshlets.ToImmutableArray(),
            VertexIndices = vertexIndices.ToImmutableArray(),
            TriangleIndices = triangleIndices.ToImmutableArray(),
            Stats = stats,
            ClusterLevel0Count = level0Count,
            ClusterLods = clusterLods.ToImmutableArray(),
            LodGroups = lodGroups.ToImmutableArray(),
            ClusterSkins = clusterSkins.ToImmutableArray(),
            ClusterBones = clusterBones.ToImmutableArray(),
            DeformationVertexOrder = deformation.Order.ToImmutableArray(),
            DeformationPrefixCounts = deformation.PrefixCounts.ToImmutableArray(),
            LevelMaxErrors = deformation.LevelErrors.ToImmutableArray(),
            LevelCoverIndices = deformation.CoverIndices.ToImmutableArray(),
            LevelCoverFirstIndex = deformation.CoverFirstIndex.ToImmutableArray(),
            LevelCoverIndexCount = deformation.CoverIndexCount.ToImmutableArray(),
        };
        payload.ValidatePortablePayload();
        return payload;
    }

    private static bool TrySkipMeshletPayload(RuntimeCookedBinaryReader reader)
    {
        bool hasPayload = reader.ReadByte() != 0;
        if (!hasPayload)
            return true;

        int payloadVersion = reader.ReadInt32();
        if (!TrySkipBytes(reader, sizeof(byte))
            || (payloadVersion >= 3 && !TrySkipBytes(reader, sizeof(byte)))
            || !TrySkipString(reader)
            || (payloadVersion >= 2 && !TrySkipString(reader))
            || (payloadVersion >= 2 && !TrySkipBytes(reader, sizeof(ulong)))
            || !TrySkipString(reader)
            || !TrySkipBytes(reader, sizeof(int) * 2L)
            || !TrySkipBytes(reader, sizeof(ulong) * 4L)
            || !TrySkipBytes(reader, CalculateMeshletSettingsSize())
            || !TrySkipBytes(reader, CalculateLodSettingsSize())
            || !TrySkipCpuMeshletDescriptors(reader)
            || !TrySkipUInt32Array(reader)
            || !TrySkipByteArray(reader)
            || (payloadVersion <= MeshletPayload.LastPayloadVersionWithVertexStream && !TrySkipMeshletVertices(reader)))
        {
            return false;
        }

        if (!TrySkipBytes(reader, sizeof(int) * 4L))
            return false;
        if (payloadVersion < MeshletPayload.FirstClusterHierarchyPayloadVersion)
            return true;

        if (!TrySkipBytes(reader, sizeof(int)))
            return false;
        int clusterCount = reader.ReadInt32();
        if (clusterCount < 0 || !TrySkipBytes(reader, (long)clusterCount * ClusterLodRecordBytes))
            return false;
        int groupCount = reader.ReadInt32();
        if (groupCount < 0 || !TrySkipBytes(reader, (long)groupCount * LodGroupRecordBytes))
            return false;
        if (payloadVersion < MeshletPayload.ClusterSkinningPayloadVersion)
            return true;

        int skinCount = reader.ReadInt32();
        if (skinCount < 0 || !TrySkipBytes(reader, (long)skinCount * ClusterSkinRecordBytes))
            return false;
        int boneCount = reader.ReadInt32();
        if (boneCount < 0 || !TrySkipBytes(reader, (long)boneCount * sizeof(ushort)))
            return false;
        if (payloadVersion < MeshletPayload.DeformationOrderPayloadVersion)
            return true;

        // The vertex order, the prefix counts, the level errors and the three level cover tables:
        // 4 bytes per entry each.
        for (int table = 0; table < 6; table++)
        {
            int count = reader.ReadInt32();
            if (count < 0 || !TrySkipBytes(reader, (long)count * sizeof(uint)))
                return false;
        }
        return true;
    }

    private static bool TrySkipCpuMeshletDescriptors(RuntimeCookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        return count >= 0 && TrySkipBytes(reader, (long)count * ((sizeof(float) * 12) + (sizeof(uint) * 5)));
    }

    private static bool TrySkipUInt32Array(RuntimeCookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        return count >= 0 && TrySkipBytes(reader, (long)count * sizeof(uint));
    }

    private static bool TrySkipByteArray(RuntimeCookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        return count >= 0 && TrySkipBytes(reader, count);
    }

    private static bool TrySkipMeshletVertices(RuntimeCookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        return count >= 0 && TrySkipBytes(reader, (long)count * (sizeof(float) * 16));
    }

    private static long CalculateStringSize(string? value)
    {
        int byteCount = string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
        return Calculate7BitEncodedIntSize(byteCount) + byteCount;
    }

    private static long CalculateMeshletSettingsSize()
        => sizeof(byte)
           + sizeof(int)
           + sizeof(uint) * 3
           + sizeof(float) * 3
           + sizeof(byte)
           + sizeof(int)
           + sizeof(byte) * 3;

    private static long CalculateLodSettingsSize()
        => sizeof(byte)
           + sizeof(int)
           + sizeof(int)
           + sizeof(float) * 5
           + sizeof(byte)
           + sizeof(uint)
           + sizeof(byte)
           + sizeof(float)
           + sizeof(byte)
           + sizeof(float)
           + sizeof(byte)
           + sizeof(float)
           + sizeof(byte)
           + sizeof(float)
           + sizeof(byte) * 3;

    private static long CalculateCpuMeshletDescriptorArraySize(CpuMeshletDescriptor[]? descriptors)
        => sizeof(int) + (long)(descriptors?.Length ?? 0) * ((sizeof(float) * 12) + (sizeof(uint) * 5));

    private static long CalculateUInt32ArraySize(uint[]? values)
        => sizeof(int) + (long)(values?.Length ?? 0) * sizeof(uint);

    private static long CalculateByteArraySize(byte[]? values)
        => sizeof(int) + (values?.Length ?? 0);

    private static void WriteMeshletSettings(RuntimeCookedBinaryWriter writer, MeshletGenerationSettingsSnapshot settings)
    {
        writer.Write(settings.Enabled ? (byte)1 : (byte)0);
        writer.Write((int)settings.BuildMode);
        writer.Write(settings.MaxVertices);
        writer.Write(settings.MinTriangles);
        writer.Write(settings.MaxTriangles);
        writer.Write(settings.ConeWeight);
        writer.Write(settings.SplitFactor);
        writer.Write(settings.FillWeight);
        writer.Write(settings.OptimizeMeshlets ? (byte)1 : (byte)0);
        writer.Write(settings.OptimizeLevel);
        writer.Write(settings.ComputeBounds ? (byte)1 : (byte)0);
        writer.Write(settings.EncodeMeshlets ? (byte)1 : (byte)0);
        writer.Write(settings.EncodeVertexReferences ? (byte)1 : (byte)0);
    }

    private static MeshletGenerationSettingsSnapshot ReadMeshletSettings(RuntimeCookedBinaryReader reader)
        => new(
            reader.ReadByte() != 0,
            (MeshletBuildMode)reader.ReadInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadByte() != 0,
            reader.ReadInt32(),
            reader.ReadByte() != 0,
            reader.ReadByte() != 0,
            reader.ReadByte() != 0);

    private static void WriteLodSettings(RuntimeCookedBinaryWriter writer, MeshLodGenerationSettingsSnapshot settings)
    {
        writer.Write(settings.Enabled ? (byte)1 : (byte)0);
        writer.Write((int)settings.Mode);
        writer.Write(settings.AdditionalLodCount);
        writer.Write(settings.FirstLodIndexRatio);
        writer.Write(settings.LodRatioScale);
        writer.Write(settings.TargetError);
        writer.Write(settings.FirstLodDistance);
        writer.Write(settings.LodDistanceScale);
        writer.Write(settings.ReusePreviousLodAsSource ? (byte)1 : (byte)0);
        writer.Write((uint)settings.Options);
        writer.Write(settings.UseNormals ? (byte)1 : (byte)0);
        writer.Write(settings.NormalWeight);
        writer.Write(settings.UseTangents ? (byte)1 : (byte)0);
        writer.Write(settings.TangentWeight);
        writer.Write(settings.UseTexCoords ? (byte)1 : (byte)0);
        writer.Write(settings.TexCoordWeight);
        writer.Write(settings.UseColors ? (byte)1 : (byte)0);
        writer.Write(settings.ColorWeight);
        writer.Write(settings.ProtectAttributeSeams ? (byte)1 : (byte)0);
        writer.Write(settings.PrioritizeBorderVertices ? (byte)1 : (byte)0);
        writer.Write(settings.LockWeightedVertices ? (byte)1 : (byte)0);
    }

    private static MeshLodGenerationSettingsSnapshot ReadLodSettings(RuntimeCookedBinaryReader reader)
        => new(
            reader.ReadByte() != 0,
            (MeshOptimizerLodMode)reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadByte() != 0,
            (MeshOptimizerSimplifyOptions)reader.ReadUInt32(),
            reader.ReadByte() != 0,
            reader.ReadSingle(),
            reader.ReadByte() != 0,
            reader.ReadSingle(),
            reader.ReadByte() != 0,
            reader.ReadSingle(),
            reader.ReadByte() != 0,
            reader.ReadSingle(),
            reader.ReadByte() != 0,
            reader.ReadByte() != 0,
            reader.ReadByte() != 0);

    private static void WriteCpuMeshletDescriptors(RuntimeCookedBinaryWriter writer, CpuMeshletDescriptor[]? descriptors)
    {
        int count = descriptors?.Length ?? 0;
        writer.Write(count);
        if (count == 0)
            return;

        for (int i = 0; i < count; i++)
        {
            CpuMeshletDescriptor descriptor = descriptors![i];
            WriteVector4(writer, descriptor.BoundsSphere);
            writer.Write(descriptor.VertexOffset);
            writer.Write(descriptor.TriangleOffset);
            writer.Write(descriptor.VertexCount);
            writer.Write(descriptor.TriangleCount);
            WriteVector4(writer, descriptor.Cone);
            WriteVector4(writer, descriptor.ConeApex);
            writer.Write(descriptor.PackedCone);
        }
    }

    private static CpuMeshletDescriptor[] ReadCpuMeshletDescriptors(RuntimeCookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == 0)
            return [];

        ValidateArrayCount(count, (sizeof(float) * 12) + (sizeof(uint) * 5), reader.Remaining, "meshlet descriptor");

        CpuMeshletDescriptor[] descriptors = new CpuMeshletDescriptor[count];
        for (int i = 0; i < count; i++)
        {
            Vector4 bounds = ReadVector4(reader);
            uint vertexOffset = reader.ReadUInt32();
            uint triangleOffset = reader.ReadUInt32();
            uint vertexCount = reader.ReadUInt32();
            uint triangleCount = reader.ReadUInt32();
            Vector4 cone = ReadVector4(reader);
            Vector4 coneApex = ReadVector4(reader);
            uint packedCone = reader.ReadUInt32();
            descriptors[i] = new CpuMeshletDescriptor(bounds, vertexOffset, triangleOffset, vertexCount, triangleCount, cone, coneApex, packedCone);
        }

        return descriptors;
    }

    private static void WriteUInt32Array(RuntimeCookedBinaryWriter writer, uint[]? values)
    {
        int count = values?.Length ?? 0;
        writer.Write(count);
        if (count == 0)
            return;

        for (int i = 0; i < count; i++)
            writer.Write(values![i]);
    }

    private static uint[] ReadUInt32Array(RuntimeCookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == 0)
            return [];

        ValidateArrayCount(count, sizeof(uint), reader.Remaining, "meshlet vertex-reference");

        uint[] values = new uint[count];
        for (int i = 0; i < count; i++)
            values[i] = reader.ReadUInt32();
        return values;
    }

    private static void WriteByteArray(RuntimeCookedBinaryWriter writer, byte[]? values)
    {
        int count = values?.Length ?? 0;
        writer.Write(count);
        if (count > 0)
            writer.WriteBytes(values!);
    }

    private static byte[] ReadByteArray(RuntimeCookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == 0)
            return [];
        ValidateArrayCount(count, sizeof(byte), reader.Remaining, "meshlet triangle-index");
        return reader.ReadBytes(count);
    }

    private static void ValidateArrayCount(int count, int elementSize, long remainingBytes, string streamName)
    {
        long byteCount = count < 0 ? -1L : (long)count * elementSize;
        if (count < 0 || byteCount > remainingBytes || byteCount > MaxStandaloneMeshletPayloadBytes)
            throw new InvalidDataException($"The {streamName} stream has an invalid bounded element count.");
    }

    private static void WriteVector4(RuntimeCookedBinaryWriter writer, Vector4 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }

    private static Vector4 ReadVector4(RuntimeCookedBinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
