using System.Numerics;
using System.Text;
using XREngine.Core.Files;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering;

public partial class XRMesh
{
    private static long CalculateMeshletPayloadSize(MeshletPayload? payload)
    {
        long size = sizeof(byte);
        if (payload is null)
            return size;

        size += sizeof(int); // payload version
        size += sizeof(byte); // generation enabled
        size += CalculateStringSize(payload.MeshOptimizerVersionKey);
        size += CalculateStringSize(payload.SourceMeshIdentity);
        size += sizeof(int) * 2;
        size += sizeof(ulong) * 4;
        size += CalculateMeshletSettingsSize();
        size += CalculateLodSettingsSize();
        size += CalculateCpuMeshletDescriptorArraySize(payload.Meshlets);
        size += CalculateUInt32ArraySize(payload.VertexIndices);
        size += CalculateByteArraySize(payload.TriangleIndices);
        size += CalculateMeshletVertexArraySize(payload.Vertices);
        size += sizeof(int) * 4; // stats
        return size;
    }

    private static void WriteMeshletPayload(RuntimeCookedBinaryWriter writer, MeshletPayload? payload)
    {
        if (payload is null)
        {
            writer.Write((byte)0);
            return;
        }

        writer.Write((byte)1);
        writer.Write(payload.PayloadVersion);
        writer.Write(payload.GenerationEnabled ? (byte)1 : (byte)0);
        writer.Write(payload.MeshOptimizerVersionKey);
        writer.Write(payload.SourceMeshIdentity);
        writer.Write(payload.SourceVertexCount);
        writer.Write(payload.SourceTriangleCount);
        writer.Write(payload.SourceMeshHash);
        writer.Write(payload.MeshletSettingsHash);
        writer.Write(payload.LodSettingsHash);
        writer.Write(payload.FreshnessHash);
        WriteMeshletSettings(writer, payload.MeshletSettings);
        WriteLodSettings(writer, payload.LodSettings);
        WriteCpuMeshletDescriptors(writer, payload.Meshlets);
        WriteUInt32Array(writer, payload.VertexIndices);
        WriteByteArray(writer, payload.TriangleIndices);
        WriteMeshletVertices(writer, payload.Vertices);
        writer.Write(payload.Stats.MeshletCount);
        writer.Write(payload.Stats.VertexReferenceCount);
        writer.Write(payload.Stats.TriangleByteCount);
        writer.Write(payload.Stats.EncodedByteCount);
    }

    private static MeshletPayload? ReadMeshletPayload(RuntimeCookedBinaryReader reader)
    {
        bool hasPayload = reader.ReadByte() != 0;
        if (!hasPayload)
            return null;

        int payloadVersion = reader.ReadInt32();
        bool generationEnabled = reader.ReadByte() != 0;
        string meshOptimizerVersionKey = reader.ReadString();
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
        MeshletVertex[] vertices = ReadMeshletVertices(reader);
        MeshOptimizerMeshletStats stats = new(
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32());

        return new MeshletPayload
        {
            PayloadVersion = payloadVersion,
            GenerationEnabled = generationEnabled,
            MeshOptimizerVersionKey = meshOptimizerVersionKey,
            SourceMeshIdentity = sourceMeshIdentity,
            SourceVertexCount = sourceVertexCount,
            SourceTriangleCount = sourceTriangleCount,
            SourceMeshHash = sourceMeshHash,
            MeshletSettingsHash = meshletSettingsHash,
            LodSettingsHash = lodSettingsHash,
            FreshnessHash = freshnessHash,
            MeshletSettings = meshletSettings,
            LodSettings = lodSettings,
            Meshlets = meshlets,
            VertexIndices = vertexIndices,
            TriangleIndices = triangleIndices,
            Vertices = vertices,
            Stats = stats,
        };
    }

    private static bool TrySkipMeshletPayload(RuntimeCookedBinaryReader reader)
    {
        bool hasPayload = reader.ReadByte() != 0;
        if (!hasPayload)
            return true;

        if (!TrySkipBytes(reader, sizeof(int) + sizeof(byte))
            || !TrySkipString(reader)
            || !TrySkipString(reader)
            || !TrySkipBytes(reader, sizeof(int) * 2L)
            || !TrySkipBytes(reader, sizeof(ulong) * 4L)
            || !TrySkipBytes(reader, CalculateMeshletSettingsSize())
            || !TrySkipBytes(reader, CalculateLodSettingsSize())
            || !TrySkipCpuMeshletDescriptors(reader)
            || !TrySkipUInt32Array(reader)
            || !TrySkipByteArray(reader)
            || !TrySkipMeshletVertices(reader))
        {
            return false;
        }

        return TrySkipBytes(reader, sizeof(int) * 4L);
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

    private static long CalculateMeshletVertexArraySize(MeshletVertex[]? values)
        => sizeof(int) + (long)(values?.Length ?? 0) * (sizeof(float) * 16);

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
        return count == 0 ? [] : reader.ReadBytes(count);
    }

    private static void WriteMeshletVertices(RuntimeCookedBinaryWriter writer, MeshletVertex[]? vertices)
    {
        int count = vertices?.Length ?? 0;
        writer.Write(count);
        if (count == 0)
            return;

        for (int i = 0; i < count; i++)
        {
            MeshletVertex vertex = vertices![i];
            WriteVector4(writer, vertex.Position);
            WriteVector4(writer, vertex.Normal);
            WriteVector2(writer, vertex.TexCoord);
            WriteVector2(writer, vertex.Padding);
            WriteVector4(writer, vertex.Tangent);
        }
    }

    private static MeshletVertex[] ReadMeshletVertices(RuntimeCookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == 0)
            return [];

        MeshletVertex[] vertices = new MeshletVertex[count];
        for (int i = 0; i < count; i++)
        {
            vertices[i] = new MeshletVertex
            {
                Position = ReadVector4(reader),
                Normal = ReadVector4(reader),
                TexCoord = ReadVector2(reader),
                Padding = ReadVector2(reader),
                Tangent = ReadVector4(reader),
            };
        }

        return vertices;
    }

    private static void WriteVector2(RuntimeCookedBinaryWriter writer, Vector2 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    private static Vector2 ReadVector2(RuntimeCookedBinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle());

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
