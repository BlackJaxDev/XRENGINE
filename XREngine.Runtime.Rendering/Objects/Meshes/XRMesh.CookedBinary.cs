using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;
using CookedBinaryReader = XREngine.Core.Files.RuntimeCookedBinaryReader;
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;
using CookedBinaryWriter = XREngine.Core.Files.RuntimeCookedBinaryWriter;
using ICookedBinarySerializable = XREngine.Core.Files.IRuntimeCookedBinarySerializable;

namespace XREngine.Rendering;

public partial class XRMesh : ICookedBinarySerializable
{
    private const int CurrentSkinningPayloadVersion = 3;
    private const int CurrentBlendshapePayloadVersion = 2;

    private MeshPayloadWritePlan? _meshPayloadPlan;
    private readonly Dictionary<string, MeshBufferEncoding> _bufferEncodingOverrides = new(StringComparer.OrdinalIgnoreCase);

    [YamlIgnore]
    public Func<string, MeshBufferEncoding>? BufferEncodingResolver { get; set; }

    public void SetBufferEncoding(string streamKey, MeshBufferEncoding encoding)
    {
        if (string.IsNullOrWhiteSpace(streamKey))
            throw new ArgumentException("Stream key cannot be null or whitespace.", nameof(streamKey));
        _bufferEncodingOverrides[streamKey] = encoding;
    }

    public bool ClearBufferEncoding(string streamKey)
        => !string.IsNullOrWhiteSpace(streamKey) && _bufferEncodingOverrides.Remove(streamKey);

    public void ClearBufferEncodings()
        => _bufferEncodingOverrides.Clear();

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    void ICookedBinarySerializable.WriteCookedBinary(CookedBinaryWriter writer)
    {
        writer.WriteBaseObject<XRAsset>(this);
        MeshPayloadWritePlan plan = _meshPayloadPlan ?? BuildMeshPayloadPlan();
        WriteMeshPayload(writer, plan);
        _meshPayloadPlan = null;
    }

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    void ICookedBinarySerializable.ReadCookedBinary(CookedBinaryReader reader)
    {
        reader.ReadBaseObject<XRAsset>(this);
        _meshPayloadPlan = null;
        ReadMeshPayload(reader);
    }

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    long ICookedBinarySerializable.CalculateCookedBinarySize()
    {
        MeshPayloadWritePlan plan = BuildMeshPayloadPlan();
        _meshPayloadPlan = plan;
        long size = CookedBinarySerializer.CalculateBaseObjectSize(this, typeof(XRAsset));
        size += plan.TotalSize;
        return size;
    }

    private MeshPayloadWritePlan BuildMeshPayloadPlan()
    {
        MeshMetadata metadata = new()
        {
            VertexCount = VertexCount,
            PrimitiveType = Type,
            BoundsMin = Bounds.Min,
            BoundsMax = Bounds.Max,
            SkinningShaderConvention = SkinningShaderConvention,
            InterleavedLayout = Interleaved,
            InterleavedStride = InterleavedStride,
            PositionOffset = PositionOffset,
            NormalOffset = NormalOffset,
            TangentOffset = TangentOffset,
            ColorOffset = ColorOffset,
            TexCoordOffset = TexCoordOffset,
            HasNormals = HasNormals,
            HasTangents = HasTangents,
            ColorChannels = (int)ColorCount,
            TexCoordChannels = (int)TexCoordCount
        };

        MeshPayloadWritePlan plan = new()
        {
            Metadata = metadata
        };

        if (metadata.InterleavedLayout && InterleavedVertexBuffer is not null)
            plan.Interleaved = CreateBufferPlan(InterleavedVertexBuffer, InterleavedVertexBuffer.AttributeName ?? nameof(InterleavedVertexBuffer));

        if (!metadata.InterleavedLayout)
        {
            plan.Positions = CreateBufferPlan(PositionsBuffer, PositionsBuffer?.AttributeName ?? nameof(PositionsBuffer));
            if (metadata.HasNormals)
                plan.Normals = CreateBufferPlan(NormalsBuffer, NormalsBuffer?.AttributeName ?? nameof(NormalsBuffer));
            if (metadata.HasTangents)
                plan.Tangents = CreateBufferPlan(TangentsBuffer, TangentsBuffer?.AttributeName ?? nameof(TangentsBuffer));

            for (int i = 0; i < metadata.ColorChannels; i++)
            {
                XRDataBuffer? buffer = ColorBuffers is not null && i < ColorBuffers.Length ? ColorBuffers[i] : null;
                plan.ColorStreams.Add(CreateBufferPlan(buffer, buffer?.AttributeName ?? $"Color{i}"));
            }

            for (int i = 0; i < metadata.TexCoordChannels; i++)
            {
                XRDataBuffer? buffer = TexCoordBuffers is not null && i < TexCoordBuffers.Length ? TexCoordBuffers[i] : null;
                plan.TexCoordStreams.Add(CreateBufferPlan(buffer, buffer?.AttributeName ?? $"TexCoord{i}"));
            }
        }

        plan.Skinning = BuildSkinningPlan();
        plan.Blendshapes = BuildBlendshapePlan();

        long size = CalculateMetadataSize(metadata);
        size += CalculateTriangleDataSize(Triangles);
        size += CalculateLineDataSize(Lines);
        size += CalculatePointsSize(Points);

        size += plan.Interleaved?.GetSerializedLength() ?? 0;
        size += plan.Positions?.GetSerializedLength() ?? 0;
        size += plan.Normals?.GetSerializedLength() ?? 0;
        size += plan.Tangents?.GetSerializedLength() ?? 0;
        foreach (BufferPlan color in plan.ColorStreams)
            size += color.GetSerializedLength();
        foreach (BufferPlan tex in plan.TexCoordStreams)
            size += tex.GetSerializedLength();

        size += plan.Skinning.GetSerializedLength();
        size += plan.Blendshapes.GetSerializedLength();
        size += CalculateMeshletPayloadSize(MeshletPayload);

        plan.TotalSize = size;
        return plan;
    }

    private void WriteMeshPayload(CookedBinaryWriter writer, MeshPayloadWritePlan plan)
    {
        WriteMetadata(writer, plan.Metadata);
        WriteTriangles(writer, Triangles);
        WriteLines(writer, Lines);
        WritePoints(writer, Points);

        if (plan.Metadata.InterleavedLayout)
        {
            WriteBufferPlan(writer, plan.Interleaved);
        }
        else
        {
            WriteBufferPlan(writer, plan.Positions);
            if (plan.Metadata.HasNormals)
                WriteBufferPlan(writer, plan.Normals);
            if (plan.Metadata.HasTangents)
                WriteBufferPlan(writer, plan.Tangents);
            foreach (BufferPlan color in plan.ColorStreams)
                WriteBufferPlan(writer, color);
            foreach (BufferPlan tex in plan.TexCoordStreams)
                WriteBufferPlan(writer, tex);
        }

        WriteSkinningData(writer, plan.Skinning);
        WriteBlendshapeData(writer, plan.Blendshapes);
        WriteMeshletPayload(writer, MeshletPayload);
    }

    private void ReadMeshPayload(CookedBinaryReader reader)
    {
        long payloadStart = reader.Position;
        if (!TryReadMetadata(reader, payloadStart, out MeshMetadata metadata))
        {
            reader.Position = payloadStart;
            if (TryReadLegacyMeshPayload(reader, payloadStart))
                return;

            throw new InvalidOperationException("Unable to read cooked mesh metadata.");
        }

        Triangles = ReadTriangles(reader);
        Lines = ReadLines(reader);
        Points = ReadPoints(reader);

        ApplyMetadata(metadata);

        if (metadata.InterleavedLayout)
        {
            ReadBufferData(reader, InterleavedVertexBuffer, allowMetadata: false);
        }
        else
        {
            ReadBufferData(reader, PositionsBuffer, allowMetadata: false);
            if (metadata.HasNormals)
                ReadBufferData(reader, NormalsBuffer, allowMetadata: false);
            if (metadata.HasTangents)
                ReadBufferData(reader, TangentsBuffer, allowMetadata: false);

            for (int i = 0; i < metadata.ColorChannels; i++)
            {
                XRDataBuffer? buffer = ColorBuffers is not null && i < ColorBuffers.Length ? ColorBuffers[i] : null;
                ReadBufferData(reader, buffer, allowMetadata: false);
            }

            for (int i = 0; i < metadata.TexCoordChannels; i++)
            {
                XRDataBuffer? buffer = TexCoordBuffers is not null && i < TexCoordBuffers.Length ? TexCoordBuffers[i] : null;
                ReadBufferData(reader, buffer, allowMetadata: false);
            }
        }

        ReadSkinningData(reader);
        ReadBlendshapeData(reader);
        MeshletPayload = reader.Remaining > 0
            ? ReadMeshletPayload(reader)
            : null;
    }

    private void ApplyMetadata(MeshMetadata metadata)
    {
        VertexCount = metadata.VertexCount;
        Type = metadata.PrimitiveType;
        Bounds = new AABB(metadata.BoundsMin, metadata.BoundsMax);
        SkinningShaderConvention = metadata.SkinningShaderConvention;

        Buffers?.Clear();
        InitMeshBuffers(metadata.HasNormals, metadata.HasTangents, metadata.ColorChannels, metadata.TexCoordChannels, metadata.InterleavedLayout);

        InterleavedStride = metadata.InterleavedStride;
        PositionOffset = metadata.PositionOffset;
        NormalOffset = metadata.NormalOffset;
        TangentOffset = metadata.TangentOffset;
        ColorOffset = metadata.ColorOffset;
        TexCoordOffset = metadata.TexCoordOffset;
    }

    private bool TryReadMetadata(CookedBinaryReader reader, long startPosition, out MeshMetadata metadata)
        => TryReadMetadata(reader, startPosition, includeSkinningConvention: true, out metadata)
            || TryReadMetadata(reader, startPosition, includeSkinningConvention: false, out metadata);

    private bool TryReadMetadata(CookedBinaryReader reader, long startPosition, bool includeSkinningConvention, out MeshMetadata metadata)
    {
        metadata = default;
        reader.Position = startPosition;

        try
        {
            metadata = ReadMetadataCore(reader, includeSkinningConvention);
            if (!IsPlausibleMetadata(metadata))
                return false;

            long positionAfterMetadata = reader.Position;
            if (!TrySkipRemainingPayload(reader, metadata))
                return false;

            reader.Position = positionAfterMetadata;
            return true;
        }
        catch (Exception ex) when (
            ex is EndOfStreamException or
            InvalidOperationException or
            ArgumentOutOfRangeException or
            FormatException or
            NotSupportedException or
            OverflowException)
        {
            reader.Position = startPosition;
            return false;
        }
    }

    private bool TryReadLegacyMeshPayload(CookedBinaryReader reader, long startPosition)
    {
        reader.Position = startPosition;

        try
        {
            MeshCookedPayload payload = ReadPayload(reader);
            ApplyLegacyPayload(payload);
            return true;
        }
        catch (Exception ex) when (
            ex is EndOfStreamException or
            InvalidCastException or
            InvalidOperationException or
            ArgumentOutOfRangeException or
            FormatException or
            NotSupportedException or
            OverflowException)
        {
            reader.Position = startPosition;
            return false;
        }
    }

    private void ApplyLegacyPayload(MeshCookedPayload payload)
    {
        Vector3[] positions = payload.Positions ?? Array.Empty<Vector3>();
        Vector3[]? normals = payload.Normals;
        Vector3[]? tangents = payload.Tangents;
        Vector4[][]? colors = payload.Colors;
        Vector2[][]? texCoords = payload.TexCoords;

        VertexCount = positions.Length;
        Type = Enum.IsDefined(typeof(EPrimitiveType), payload.PrimitiveType)
            ? payload.PrimitiveType
            : EPrimitiveType.Triangles;
        Bounds = new AABB(payload.BoundsMin, payload.BoundsMax);
        SkinningShaderConvention = ESkinningShaderConvention.LegacyImplicitTranspose;

        Triangles = CreateLegacyTriangles(payload.Triangles);
        Lines = CreateLegacyLines(payload.Lines);
        Points = payload.Points is { Length: > 0 } ? new List<int>(payload.Points) : null;

        int colorChannels = colors?.Length ?? 0;
        int texCoordChannels = texCoords?.Length ?? 0;
        bool hasNormals = normals is { Length: > 0 };
        bool hasTangents = tangents is { Length: > 0 };

        Buffers?.Clear();
        InitMeshBuffers(hasNormals, hasTangents, colorChannels, texCoordChannels);
        PopulateLegacyBuffers(positions, normals, tangents, colors, texCoords);
        Vertices = BuildLegacyVertices(positions, normals, tangents, colors, texCoords);

        ApplySkinningPayload(payload.Skinning);
        ApplyBlendshapePayload(payload.Blendshapes);
        MeshletPayload = null;
    }

    private void PopulateLegacyBuffers(
        Vector3[] positions,
        Vector3[]? normals,
        Vector3[]? tangents,
        Vector4[][]? colors,
        Vector2[][]? texCoords)
    {
        for (int i = 0; i < positions.Length; i++)
            SetPosition((uint)i, positions[i]);

        if (normals is not null)
        {
            int count = Math.Min(normals.Length, positions.Length);
            for (int i = 0; i < count; i++)
                SetNormal((uint)i, normals[i]);
        }

        if (tangents is not null)
        {
            int count = Math.Min(tangents.Length, positions.Length);
            for (int i = 0; i < count; i++)
                SetTangent((uint)i, tangents[i]);
        }

        if (colors is not null)
        {
            for (int channel = 0; channel < colors.Length; channel++)
            {
                Vector4[]? values = colors[channel];
                if (values is null)
                    continue;

                int count = Math.Min(values.Length, positions.Length);
                for (int i = 0; i < count; i++)
                    SetColor((uint)i, values[i], (uint)channel);
            }
        }

        if (texCoords is not null)
        {
            for (int channel = 0; channel < texCoords.Length; channel++)
            {
                Vector2[]? values = texCoords[channel];
                if (values is null)
                    continue;

                int count = Math.Min(values.Length, positions.Length);
                for (int i = 0; i < count; i++)
                    SetTexCoord((uint)i, values[i], (uint)channel);
            }
        }
    }

    private static Vertex[] BuildLegacyVertices(
        Vector3[] positions,
        Vector3[]? normals,
        Vector3[]? tangents,
        Vector4[][]? colors,
        Vector2[][]? texCoords)
    {
        Vertex[] vertices = new Vertex[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            Vertex vertex = new(positions[i]);
            if (normals is not null && i < normals.Length)
                vertex.Normal = normals[i];
            if (tangents is not null && i < tangents.Length)
                vertex.Tangent = tangents[i];

            if (colors is not null && colors.Length > 0)
            {
                List<Vector4> colorSets = new(colors.Length);
                for (int channel = 0; channel < colors.Length; channel++)
                {
                    Vector4[]? values = colors[channel];
                    colorSets.Add(values is not null && i < values.Length ? values[i] : Vector4.Zero);
                }

                vertex.ColorSets = colorSets;
            }

            if (texCoords is not null && texCoords.Length > 0)
            {
                List<Vector2> texCoordSets = new(texCoords.Length);
                for (int channel = 0; channel < texCoords.Length; channel++)
                {
                    Vector2[]? values = texCoords[channel];
                    texCoordSets.Add(values is not null && i < values.Length ? values[i] : Vector2.Zero);
                }

                vertex.TextureCoordinateSets = texCoordSets;
            }

            vertices[i] = vertex;
        }

        return vertices;
    }

    private static List<IndexTriangle>? CreateLegacyTriangles(int[]? indices)
    {
        int count = indices?.Length / 3 ?? 0;
        if (count == 0)
            return null;

        List<IndexTriangle> triangles = new(count);
        for (int i = 0; i < count; i++)
        {
            int index = i * 3;
            triangles.Add(new IndexTriangle(indices![index], indices[index + 1], indices[index + 2]));
        }

        return triangles;
    }

    private static List<IndexLine>? CreateLegacyLines(int[]? indices)
    {
        int count = indices?.Length / 2 ?? 0;
        if (count == 0)
            return null;

        List<IndexLine> lines = new(count);
        for (int i = 0; i < count; i++)
        {
            int index = i * 2;
            lines.Add(new IndexLine(indices![index], indices[index + 1]));
        }

        return lines;
    }

    private static MeshMetadata ReadMetadataCore(CookedBinaryReader reader, bool includeSkinningConvention)
    {
        MeshMetadata metadata = new()
        {
            VertexCount = reader.ReadInt32(),
            PrimitiveType = (EPrimitiveType)reader.ReadInt32(),
            BoundsMin = ReadVector3(reader),
            BoundsMax = ReadVector3(reader),
            SkinningShaderConvention = includeSkinningConvention
                ? (ESkinningShaderConvention)reader.ReadByte()
                : ESkinningShaderConvention.LegacyImplicitTranspose,
            InterleavedLayout = reader.ReadBoolean(),
            InterleavedStride = reader.ReadUInt32(),
            PositionOffset = reader.ReadUInt32()
        };

        bool hasNormalOffset = reader.ReadBoolean();
        metadata.NormalOffset = hasNormalOffset ? reader.ReadUInt32() : null;
        bool hasTangentOffset = reader.ReadBoolean();
        metadata.TangentOffset = hasTangentOffset ? reader.ReadUInt32() : null;
        bool hasColorOffset = reader.ReadBoolean();
        metadata.ColorOffset = hasColorOffset ? reader.ReadUInt32() : null;
        bool hasTexOffset = reader.ReadBoolean();
        metadata.TexCoordOffset = hasTexOffset ? reader.ReadUInt32() : null;

        metadata.HasNormals = reader.ReadBoolean();
        metadata.HasTangents = reader.ReadBoolean();
        metadata.ColorChannels = reader.ReadInt32();
        metadata.TexCoordChannels = reader.ReadInt32();

        return metadata;
    }

    private static bool IsPlausibleMetadata(MeshMetadata metadata)
    {
        if (metadata.VertexCount < 0)
            return false;

        if (!Enum.IsDefined(typeof(EPrimitiveType), metadata.PrimitiveType))
            return false;

        if (!Enum.IsDefined(typeof(ESkinningShaderConvention), metadata.SkinningShaderConvention))
            return false;

        if (metadata.ColorChannels is < 0 or > 8 || metadata.TexCoordChannels is < 0 or > 8)
            return false;

        if (metadata.InterleavedLayout)
        {
            if (metadata.InterleavedStride == 0 || metadata.InterleavedStride > 4096)
                return false;
            if (!IsOffsetWithinStride(metadata.PositionOffset, metadata.InterleavedStride)
                || !IsOffsetWithinStride(metadata.NormalOffset, metadata.InterleavedStride)
                || !IsOffsetWithinStride(metadata.TangentOffset, metadata.InterleavedStride)
                || !IsOffsetWithinStride(metadata.ColorOffset, metadata.InterleavedStride)
                || !IsOffsetWithinStride(metadata.TexCoordOffset, metadata.InterleavedStride))
            {
                return false;
            }
        }

        if (!metadata.HasNormals && metadata.NormalOffset.HasValue)
            return false;
        if (!metadata.HasTangents && metadata.TangentOffset.HasValue)
            return false;
        if (metadata.ColorChannels == 0 && metadata.ColorOffset.HasValue)
            return false;
        if (metadata.TexCoordChannels == 0 && metadata.TexCoordOffset.HasValue)
            return false;

        return true;
    }

    private static bool IsOffsetWithinStride(uint? offset, uint stride)
        => !offset.HasValue || offset.Value < stride;

    private static bool TrySkipRemainingPayload(CookedBinaryReader reader, MeshMetadata metadata)
    {
        if (!TrySkipIndexedData(reader, 3)
            || !TrySkipIndexedData(reader, 2)
            || !TrySkipPointData(reader)
            || !TrySkipVertexBuffers(reader, metadata)
            || !TrySkipSkinningData(reader)
            || !TrySkipBlendshapeData(reader))
        {
            return false;
        }

        if (reader.Position == reader.Length)
            return true;

        return TrySkipMeshletPayload(reader)
            && reader.Position == reader.Length;
    }

    private static bool TrySkipIndexedData(CookedBinaryReader reader, int indicesPerElement)
    {
        int count = reader.ReadInt32();
        if (count < 0)
            return false;

        long bytesToSkip = (long)count * indicesPerElement * sizeof(int);
        return TrySkipBytes(reader, bytesToSkip);
    }

    private static bool TrySkipPointData(CookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0)
            return false;

        return TrySkipBytes(reader, (long)count * sizeof(int));
    }

    private static bool TrySkipVertexBuffers(CookedBinaryReader reader, MeshMetadata metadata)
    {
        if (metadata.InterleavedLayout)
            return TrySkipBufferData(reader, allowMetadata: false);

        if (!TrySkipBufferData(reader, allowMetadata: false))
            return false;
        if (metadata.HasNormals && !TrySkipBufferData(reader, allowMetadata: false))
            return false;
        if (metadata.HasTangents && !TrySkipBufferData(reader, allowMetadata: false))
            return false;

        for (int i = 0; i < metadata.ColorChannels; i++)
        {
            if (!TrySkipBufferData(reader, allowMetadata: false))
                return false;
        }

        for (int i = 0; i < metadata.TexCoordChannels; i++)
        {
            if (!TrySkipBufferData(reader, allowMetadata: false))
                return false;
        }

        return true;
    }

    private static bool TrySkipBufferData(CookedBinaryReader reader, bool allowMetadata)
    {
        bool hasMetadata = reader.ReadBoolean();
        if (hasMetadata)
        {
            if (!allowMetadata)
                return false;
            if (!TrySkipBufferMetadata(reader))
                return false;
        }

        MeshBufferEncoding encoding = (MeshBufferEncoding)reader.ReadByte();
        if (!Enum.IsDefined(typeof(MeshBufferEncoding), encoding))
            return false;

        uint decodedLength = reader.ReadUInt32();
        if (encoding == MeshBufferEncoding.Raw)
            return TrySkipBytes(reader, decodedLength);

        uint encodedLength = reader.ReadUInt32();
        reader.ReadBoolean();
        return TrySkipBytes(reader, encodedLength);
    }

    private static bool TrySkipBufferMetadata(CookedBinaryReader reader)
    {
        return TrySkipString(reader)
            && TrySkipBytes(reader, sizeof(int) * 2L)
            && TrySkipBytes(reader, sizeof(uint) * 2L)
            && TrySkipBytes(reader, sizeof(bool) * 3L);
    }

    private static bool TrySkipSkinningData(CookedBinaryReader reader)
    {
        bool hasSkinning = reader.ReadBoolean();
        if (!hasSkinning)
            return true;

        int boneCount = reader.ReadInt32();
        if (boneCount < 0)
            return false;

        for (int i = 0; i < boneCount; i++)
        {
            if (!TrySkipBytes(reader, 16)
                || !TrySkipString(reader)
                || !TrySkipBytes(reader, sizeof(int))
                || !TrySkipBytes(reader, sizeof(float) * 16L * 2L))
            {
                return false;
            }
        }

        reader.ReadInt32();
        int version = reader.ReadInt32();
        if (version != CurrentSkinningPayloadVersion)
            return false;
        if (!TrySkipBytes(reader, sizeof(byte) * 2L)
            || !TrySkipBytes(reader, sizeof(bool))
            || !TrySkipBytes(reader, sizeof(int)))
        {
            return false;
        }
        return TrySkipBufferData(reader, allowMetadata: true)
            && TrySkipBufferData(reader, allowMetadata: true)
            && TrySkipBufferData(reader, allowMetadata: true)
            && TrySkipBufferData(reader, allowMetadata: true);
    }

    private static bool TrySkipBlendshapeData(CookedBinaryReader reader)
    {
        bool hasBlendshapes = reader.ReadBoolean();
        if (!hasBlendshapes)
            return true;

        int version = reader.ReadInt32();
        if (version != CurrentBlendshapePayloadVersion)
            return false;
        if (!TrySkipBytes(reader, sizeof(byte) * 3L)
            || !TrySkipBytes(reader, sizeof(int) * 2L))
        {
            return false;
        }

        int nameCount = reader.ReadInt32();
        if (nameCount < 0)
            return false;

        for (int i = 0; i < nameCount; i++)
        {
            if (!TrySkipString(reader))
                return false;
        }

        return TrySkipBufferData(reader, allowMetadata: true)
            && TrySkipBufferData(reader, allowMetadata: true)
            && TrySkipBufferData(reader, allowMetadata: true)
            && TrySkipBufferData(reader, allowMetadata: true)
            && TrySkipBufferData(reader, allowMetadata: true)
            && TrySkipBufferData(reader, allowMetadata: true)
            && TrySkipBufferData(reader, allowMetadata: true);
    }

    private static bool TrySkipString(CookedBinaryReader reader)
    {
        int length = reader.Read7BitEncodedInt();
        if (length < 0)
            return false;

        return TrySkipBytes(reader, length);
    }

    private static bool TrySkipBytes(CookedBinaryReader reader, long byteCount)
    {
        if (byteCount < 0 || reader.Position + byteCount > reader.Length)
            return false;

        while (byteCount > int.MaxValue)
        {
            reader.SkipBytes(int.MaxValue);
            byteCount -= int.MaxValue;
        }

        if (byteCount > 0)
            reader.SkipBytes((int)byteCount);

        return true;
    }

    private static void WriteMetadata(CookedBinaryWriter writer, MeshMetadata metadata)
    {
        writer.Write(metadata.VertexCount);
        writer.Write((int)metadata.PrimitiveType);
        WriteVector3(writer, metadata.BoundsMin);
        WriteVector3(writer, metadata.BoundsMax);
        writer.Write((byte)metadata.SkinningShaderConvention);
        writer.Write(metadata.InterleavedLayout);
        writer.Write(metadata.InterleavedStride);
        writer.Write(metadata.PositionOffset);

        writer.Write(metadata.NormalOffset.HasValue);
        if (metadata.NormalOffset.HasValue)
            writer.Write(metadata.NormalOffset.Value);

        writer.Write(metadata.TangentOffset.HasValue);
        if (metadata.TangentOffset.HasValue)
            writer.Write(metadata.TangentOffset.Value);

        writer.Write(metadata.ColorOffset.HasValue);
        if (metadata.ColorOffset.HasValue)
            writer.Write(metadata.ColorOffset.Value);

        writer.Write(metadata.TexCoordOffset.HasValue);
        if (metadata.TexCoordOffset.HasValue)
            writer.Write(metadata.TexCoordOffset.Value);

        writer.Write(metadata.HasNormals);
        writer.Write(metadata.HasTangents);
        writer.Write(metadata.ColorChannels);
        writer.Write(metadata.TexCoordChannels);
    }

    private static long CalculateMetadataSize(MeshMetadata metadata)
    {
        long size = 0;
        size += sizeof(int); // vertex count
        size += sizeof(int); // primitive type
        size += sizeof(float) * 3 * 2; // bounds
        size += sizeof(byte); // skinning shader convention
        size += sizeof(bool); // interleaved
        size += sizeof(uint); // stride
        size += sizeof(uint); // position offset

        size += sizeof(bool) * 4; // optional offsets flags
        if (metadata.NormalOffset.HasValue)
            size += sizeof(uint);
        if (metadata.TangentOffset.HasValue)
            size += sizeof(uint);
        if (metadata.ColorOffset.HasValue)
            size += sizeof(uint);
        if (metadata.TexCoordOffset.HasValue)
            size += sizeof(uint);

        size += sizeof(bool) * 2; // normals/tangents flags
        size += sizeof(int) * 2; // color/tex counts
        return size;
    }

    private static long CalculateTriangleDataSize(List<IndexTriangle>? source)
    {
        int count = source?.Count ?? 0;
        return sizeof(int) + (long)count * 3 * sizeof(int);
    }

    private static long CalculateLineDataSize(List<IndexLine>? source)
    {
        int count = source?.Count ?? 0;
        return sizeof(int) + (long)count * 2 * sizeof(int);
    }

    private static long CalculatePointsSize(List<int>? points)
    {
        int count = points?.Count ?? 0;
        return sizeof(int) + (long)count * sizeof(int);
    }

    private void WriteTriangles(CookedBinaryWriter writer, List<IndexTriangle>? triangles)
    {
        int count = triangles?.Count ?? 0;
        writer.Write(count);
        if (count == 0)
            return;
        foreach (IndexTriangle tri in triangles!)
        {
            writer.Write(tri.Point0);
            writer.Write(tri.Point1);
            writer.Write(tri.Point2);
        }
    }

    private void WriteLines(CookedBinaryWriter writer, List<IndexLine>? lines)
    {
        int count = lines?.Count ?? 0;
        writer.Write(count);
        if (count == 0)
            return;
        foreach (IndexLine line in lines!)
        {
            writer.Write(line.Point0);
            writer.Write(line.Point1);
        }
    }

    private void WritePoints(CookedBinaryWriter writer, List<int>? points)
    {
        int count = points?.Count ?? 0;
        writer.Write(count);
        if (count == 0)
            return;
        foreach (int point in points!)
            writer.Write(point);
    }

    private static List<IndexTriangle>? ReadTriangles(CookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count <= 0)
            return null;
        List<IndexTriangle> triangles = new(count);
        for (int i = 0; i < count; i++)
        {
            int p0 = reader.ReadInt32();
            int p1 = reader.ReadInt32();
            int p2 = reader.ReadInt32();
            triangles.Add(new IndexTriangle(p0, p1, p2));
        }
        return triangles;
    }

    private static List<IndexLine>? ReadLines(CookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count <= 0)
            return null;
        List<IndexLine> lines = new(count);
        for (int i = 0; i < count; i++)
        {
            int p0 = reader.ReadInt32();
            int p1 = reader.ReadInt32();
            lines.Add(new IndexLine(p0, p1));
        }
        return lines;
    }

    private static List<int>? ReadPoints(CookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count <= 0)
            return null;
        List<int> points = new(count);
        for (int i = 0; i < count; i++)
            points.Add(reader.ReadInt32());
        return points;
    }

    private BufferPlan CreateBufferPlan(XRDataBuffer? buffer, string streamKey, BufferMetadata? metadata = null)
    {
        if (buffer is null)
        {
            return new BufferPlan
            {
                Buffer = null,
                StreamKey = streamKey,
                Encoding = MeshBufferEncoding.Raw,
                DecodedLength = 0,
                EncodedLength = 0,
                EncodedData = null,
                UsesLongLength = false,
                Metadata = metadata
            };
        }

        if (buffer.ClientSideSource is null)
            throw new InvalidOperationException($"Buffer '{streamKey}' does not have CPU-side data available.");

        uint decodedLength = buffer.Length;
        MeshBufferEncoding encoding = ResolveEncoding(buffer, streamKey);
        byte[]? encodedData = null;
        uint encodedLength = decodedLength;
        bool usesLongLength = false;

        switch (encoding)
        {
            case MeshBufferEncoding.Raw:
                break;
            case MeshBufferEncoding.Snorm16:
                encodedData = EncodeSnorm16(buffer, out encodedLength);
                break;
            case MeshBufferEncoding.Lzma:
                usesLongLength = decodedLength > int.MaxValue;
                encodedData = EncodeLzma(buffer, decodedLength, usesLongLength, out encodedLength);
                break;
            case MeshBufferEncoding.GDeflate:
                encodedData = EncodeGDeflate(buffer, decodedLength, out encodedLength);
                break;
            default:
                throw new NotSupportedException($"Unsupported mesh buffer encoding '{encoding}'.");
        }

        return new BufferPlan
        {
            Buffer = buffer,
            StreamKey = streamKey,
            Encoding = encoding,
            DecodedLength = decodedLength,
            EncodedLength = encodedLength,
            EncodedData = encodedData,
            UsesLongLength = usesLongLength,
            Metadata = metadata
        };
    }

    private BufferMetadata? CaptureBufferMetadata(XRDataBuffer? buffer)
        => buffer is null
            ? null
            : new BufferMetadata(buffer.AttributeName ?? string.Empty, buffer.Target, buffer.ComponentType, buffer.ComponentCount, buffer.ElementCount, buffer.Normalize, buffer.Integral, buffer.PadEndingToVec4);

    private MeshBufferEncoding ResolveEncoding(XRDataBuffer? buffer, string streamKey)
    {
        if (!string.IsNullOrWhiteSpace(streamKey) && _bufferEncodingOverrides.TryGetValue(streamKey, out MeshBufferEncoding overrideEncoding))
            return overrideEncoding;

        if (BufferEncodingResolver is not null)
        {
            MeshBufferEncoding resolved = BufferEncodingResolver.Invoke(streamKey);
            if (Enum.IsDefined(typeof(MeshBufferEncoding), resolved))
                return resolved;
        }

        if (buffer is null)
            return MeshBufferEncoding.Raw;

        if (IsUnitVectorStream(streamKey, buffer))
            return MeshBufferEncoding.Snorm16;

        if (ShouldCompressBuffer(buffer))
            return MeshBufferEncoding.Lzma;

        return MeshBufferEncoding.Raw;
    }

    private static bool IsUnitVectorStream(string streamKey, XRDataBuffer buffer)
    {
        if (buffer.ComponentType != EComponentType.Float || buffer.ComponentCount < 3)
            return false;

        return streamKey.Contains("normal", StringComparison.OrdinalIgnoreCase)
            || streamKey.Contains("tangent", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldCompressBuffer(XRDataBuffer buffer)
    {
        if (buffer.ComponentType == EComponentType.Struct)
            return false;

        return buffer.Length >= 256 * 1024;
    }

    private void WriteBufferPlan(CookedBinaryWriter writer, BufferPlan? plan)
    {
        if (plan is null)
        {
            writer.Write(false);
            writer.Write((byte)MeshBufferEncoding.Raw);
            writer.Write(0u);
            return;
        }

        bool hasMetadata = plan.Metadata.HasValue;
        writer.Write(hasMetadata);
        if (hasMetadata)
            WriteBufferMetadata(writer, plan.Metadata!.Value);

        writer.Write((byte)plan.Encoding);
        writer.Write(plan.DecodedLength);

        if (plan.Encoding == MeshBufferEncoding.Raw)
        {
            if (plan.DecodedLength > 0)
            {
                if (plan.Buffer is null)
                    throw new InvalidOperationException($"Raw buffer '{plan.StreamKey}' is missing source data.");
                WriteRawBuffer(writer, plan.Buffer, plan.DecodedLength);
            }
            return;
        }

        writer.Write(plan.EncodedLength);
        writer.Write(plan.UsesLongLength);
        writer.Write(plan.EncodedData ?? Array.Empty<byte>());
    }

    private XRDataBuffer? ReadBufferData(CookedBinaryReader reader, XRDataBuffer? existingBuffer, bool allowMetadata)
    {
        bool hasMetadata = reader.ReadBoolean();
        XRDataBuffer? buffer = existingBuffer;
        bool requiresBufferButMissing = false;
        if (hasMetadata)
        {
            if (!allowMetadata)
                throw new InvalidOperationException("Unexpected buffer metadata for fixed stream.");
            BufferMetadata metadata = ReadBufferMetadata(reader);
            buffer = new XRDataBuffer(metadata.AttributeName, metadata.Target, metadata.ElementCount, metadata.ComponentType, metadata.ComponentCount, metadata.Normalize, metadata.Integral)
            {
                PadEndingToVec4 = metadata.PadEndingToVec4
            };
            RegisterBuffer(buffer);
        }
        else if (allowMetadata && buffer is null)
        {
            requiresBufferButMissing = true;
        }

        MeshBufferEncoding encoding = (MeshBufferEncoding)reader.ReadByte();
        uint decodedLength = reader.ReadUInt32();

        if (requiresBufferButMissing && decodedLength > 0)
            throw new InvalidOperationException("Dynamic buffer missing metadata in stream.");

        if (encoding == MeshBufferEncoding.Raw)
        {
            if (buffer is not null)
                CopyReaderToBuffer(reader, buffer, decodedLength);
            else
                reader.SkipBytes((int)decodedLength);
            return buffer;
        }

        uint encodedLength = reader.ReadUInt32();
        bool useLongLength = reader.ReadBoolean();
        byte[] encoded = reader.ReadBytes((int)encodedLength);

        if (buffer is null)
            return null;

        switch (encoding)
        {
            case MeshBufferEncoding.Snorm16:
                DecodeSnorm16(encoded, buffer);
                break;
            case MeshBufferEncoding.Lzma:
                DecodeLzma(encoded, buffer, decodedLength, useLongLength);
                break;
            case MeshBufferEncoding.GDeflate:
                buffer.SetGpuCompressedPayload(XRDataBuffer.EBufferCompressionCodec.GDeflate, encoded, decodedLength);
                break;
            default:
                throw new NotSupportedException($"Unsupported mesh buffer encoding '{encoding}'.");
        }

        return buffer;
    }

    private void WriteSkinningData(CookedBinaryWriter writer, SkinningPlan plan)
    {
        writer.Write(plan.HasSkinning);
        if (!plan.HasSkinning)
            return;

        writer.Write(plan.Bones.Length);
        foreach (BoneInfo bone in plan.Bones)
        {
            WriteGuid(writer, bone.BoneId);
            writer.Write(bone.Name ?? string.Empty);
            writer.Write(bone.ParentIndex);
            WriteMatrix(writer, bone.BindMatrix);
            WriteMatrix(writer, bone.InverseBindMatrix);
        }

        writer.Write(plan.MaxWeightCount);
        writer.Write(CurrentSkinningPayloadVersion);
        writer.Write((byte)plan.InfluenceEncoding);
        writer.Write((byte)plan.CoreIndexFormat);
        writer.Write(plan.HasSpillInfluences);
        writer.Write(plan.MaxSpillInfluenceCount);
        WriteBufferPlan(writer, plan.Offsets);
        WriteBufferPlan(writer, plan.Counts);
        WriteBufferPlan(writer, plan.Indices);
        WriteBufferPlan(writer, plan.Values);
    }

    private void ReadSkinningData(CookedBinaryReader reader)
    {
        bool hasSkinning = reader.ReadBoolean();
        if (!hasSkinning)
        {
            UtilizedBones = Array.Empty<(TransformBase tfm, Matrix4x4 invBindWorldMtx)>();
            BoneInfluenceCoreIndices = null;
            BoneInfluenceCoreWeights = null;
            BoneInfluenceSpillHeaders = null;
            BoneInfluenceSpillEntries = null;
            SkinningInfluenceEncoding = SkinningInfluenceEncoding.None;
            SkinningCoreIndexFormat = SkinningCoreIndexFormat.None;
            HasSpillInfluences = false;
            MaxSpillInfluenceCount = 0;
            _maxWeightCount = 0;
            return;
        }

        int boneCount = reader.ReadInt32();
        BoneInfo[] infos = new BoneInfo[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            Guid id = ReadGuid(reader);
            string name = reader.ReadString();
            int parentIndex = reader.ReadInt32();
            Matrix4x4 bind = ReadMatrix(reader);
            Matrix4x4 inverse = ReadMatrix(reader);
            infos[i] = new BoneInfo { BoneId = id, Name = name, ParentIndex = parentIndex, BindMatrix = bind, InverseBindMatrix = inverse };
        }

        TransformBase[] bones = BuildBonesFromPayload(infos);
        (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] utilized = new (TransformBase, Matrix4x4)[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            utilized[i] = (bones[i], infos[i].InverseBindMatrix);
        UtilizedBones = utilized;

        _maxWeightCount = reader.ReadInt32();
        int payloadVersion = reader.ReadInt32();
        if (payloadVersion != CurrentSkinningPayloadVersion)
            throw new InvalidOperationException($"Unsupported cooked skinning payload version {payloadVersion}; recook the source mesh for compressed skinning.");

        SkinningInfluenceEncoding = (SkinningInfluenceEncoding)reader.ReadByte();
        SkinningCoreIndexFormat = (SkinningCoreIndexFormat)reader.ReadByte();
        HasSpillInfluences = reader.ReadBoolean();
        MaxSpillInfluenceCount = reader.ReadInt32();

        if (SkinningInfluenceEncoding is not (SkinningInfluenceEncoding.Core4Spill or SkinningInfluenceEncoding.Core4NoSpill))
            throw new InvalidOperationException($"Unsupported cooked skinning influence encoding '{SkinningInfluenceEncoding}'.");
        if (SkinningCoreIndexFormat is not (SkinningCoreIndexFormat.Core4x8 or SkinningCoreIndexFormat.Core4x16))
            throw new InvalidOperationException($"Unsupported cooked skinning core index format '{SkinningCoreIndexFormat}'.");

        BoneInfluenceCoreIndices = ReadBufferData(reader, null, allowMetadata: true);
        BoneInfluenceCoreWeights = ReadBufferData(reader, null, allowMetadata: true);
        BoneInfluenceSpillHeaders = ReadBufferData(reader, null, allowMetadata: true);
        BoneInfluenceSpillEntries = ReadBufferData(reader, null, allowMetadata: true);

        if (SkinningInfluenceEncoding == SkinningInfluenceEncoding.Core4NoSpill)
        {
            HasSpillInfluences = false;
            MaxSpillInfluenceCount = 0;
            BoneInfluenceSpillHeaders = null;
            BoneInfluenceSpillEntries = null;
        }
        else if (HasSpillInfluences && (BoneInfluenceSpillHeaders is null || BoneInfluenceSpillEntries is null))
        {
            throw new InvalidOperationException("Cooked Core4Spill skinning payload is missing spill influence buffers.");
        }

        EnsureComputeSkinningBuffers();
    }

    private void WriteBlendshapeData(CookedBinaryWriter writer, BlendshapePlan plan)
    {
        writer.Write(plan.HasBlendshapes);
        if (!plan.HasBlendshapes)
            return;

        writer.Write(CurrentBlendshapePayloadVersion);
        writer.Write((byte)plan.ShaderVariant);
        writer.Write((byte)plan.StorageMode);
        writer.Write((byte)plan.Encoding);
        writer.Write(plan.AffectedVertexCount);
        writer.Write(plan.SparseRecordCount);

        writer.Write(plan.Names.Length);
        foreach (string name in plan.Names)
            writer.Write(name ?? string.Empty);

        WriteBufferPlan(writer, plan.Counts);
        WriteBufferPlan(writer, plan.Indices);
        WriteBufferPlan(writer, plan.Deltas);
        WriteBufferPlan(writer, plan.SparseShapeRanges);
        WriteBufferPlan(writer, plan.SparseRecords);
        WriteBufferPlan(writer, plan.QuantizedDeltas);
        WriteBufferPlan(writer, plan.QuantizationMetadata);
    }

    private void ReadBlendshapeData(CookedBinaryReader reader)
    {
        bool hasBlendshapes = reader.ReadBoolean();
        if (!hasBlendshapes)
        {
            BlendshapeNames = Array.Empty<string>();
            BlendshapeCounts = null;
            BlendshapeIndices = null;
            BlendshapeDeltas = null;
            BlendshapeSparseShapeRanges = null;
            BlendshapeSparseRecords = null;
            BlendshapeQuantizedDeltas = null;
            BlendshapeQuantizationMetadata = null;
            BlendshapeShaderVariant = BlendshapeShaderVariant.None;
            BlendshapeDeltaStorageMode = BlendshapeDeltaStorageMode.DensePerVertex;
            BlendshapeDeltaEncoding = BlendshapeDeltaEncoding.Float32;
            BlendshapeAffectedVertexCount = 0;
            BlendshapeSparseRecordCount = 0;
            return;
        }

        int payloadVersion = reader.ReadInt32();
        if (payloadVersion != CurrentBlendshapePayloadVersion)
            throw new InvalidOperationException($"Unsupported cooked blendshape payload version {payloadVersion}; expected {CurrentBlendshapePayloadVersion}.");

        BlendshapeShaderVariant = (BlendshapeShaderVariant)reader.ReadByte();
        BlendshapeDeltaStorageMode = (BlendshapeDeltaStorageMode)reader.ReadByte();
        BlendshapeDeltaEncoding = (BlendshapeDeltaEncoding)reader.ReadByte();
        BlendshapeAffectedVertexCount = reader.ReadInt32();
        BlendshapeSparseRecordCount = reader.ReadInt32();

        int nameCount = reader.ReadInt32();
        string[] names = new string[nameCount];
        for (int i = 0; i < nameCount; i++)
            names[i] = reader.ReadString();
        BlendshapeNames = names;

        BlendshapeCounts = ReadBufferData(reader, null, allowMetadata: true);
        BlendshapeIndices = ReadBufferData(reader, null, allowMetadata: true);
        BlendshapeDeltas = ReadBufferData(reader, null, allowMetadata: true);
        BlendshapeSparseShapeRanges = ReadBufferData(reader, null, allowMetadata: true);
        BlendshapeSparseRecords = ReadBufferData(reader, null, allowMetadata: true);
        BlendshapeQuantizedDeltas = ReadBufferData(reader, null, allowMetadata: true);
        BlendshapeQuantizationMetadata = ReadBufferData(reader, null, allowMetadata: true);
    }

    private SkinningPlan BuildSkinningPlan()
    {
        EnsureComputeSkinningBuffers();

        if (!HasSkinning)
            return SkinningPlan.Empty;

        BoneInfo[] bones = BuildBoneInfos();
        return new SkinningPlan
        {
            HasSkinning = true,
            Bones = bones,
            MaxWeightCount = MaxWeightCount,
            InfluenceEncoding = SkinningInfluenceEncoding,
            CoreIndexFormat = SkinningCoreIndexFormat,
            HasSpillInfluences = HasSpillInfluences,
            MaxSpillInfluenceCount = MaxSpillInfluenceCount,
            Offsets = CreateBufferPlan(BoneInfluenceCoreIndices, BoneInfluenceCoreIndices?.AttributeName ?? "BoneInfluenceCoreIndices", CaptureBufferMetadata(BoneInfluenceCoreIndices)),
            Counts = CreateBufferPlan(BoneInfluenceCoreWeights, BoneInfluenceCoreWeights?.AttributeName ?? "BoneInfluenceCoreWeights", CaptureBufferMetadata(BoneInfluenceCoreWeights)),
            Indices = CreateBufferPlan(BoneInfluenceSpillHeaders, BoneInfluenceSpillHeaders?.AttributeName ?? "BoneInfluenceSpillHeaders", CaptureBufferMetadata(BoneInfluenceSpillHeaders)),
            Values = CreateBufferPlan(BoneInfluenceSpillEntries, BoneInfluenceSpillEntries?.AttributeName ?? "BoneInfluenceSpillEntries", CaptureBufferMetadata(BoneInfluenceSpillEntries))
        };
    }

    private BoneInfo[] BuildBoneInfos()
    {
        if (UtilizedBones is not { Length: > 0 } utilized)
            return Array.Empty<BoneInfo>();

        Dictionary<TransformBase, int> indexMap = new(utilized.Length);
        for (int i = 0; i < utilized.Length; i++)
            indexMap[utilized[i].tfm] = i;

        BoneInfo[] bones = new BoneInfo[utilized.Length];
        for (int i = 0; i < utilized.Length; i++)
        {
            TransformBase transform = utilized[i].tfm;
            TransformBase? parent = transform.Parent;
            int parentIndex = parent is not null && indexMap.TryGetValue(parent, out int idx) ? idx : -1;

            bones[i] = new BoneInfo
            {
                BoneId = transform.EffectiveSerializedReferenceId,
                Name = transform.Name,
                ParentIndex = parentIndex,
                BindMatrix = transform.BindMatrix,
                InverseBindMatrix = utilized[i].invBindWorldMtx
            };
        }

        return bones;
    }

    private BlendshapePlan BuildBlendshapePlan()
    {
        if (!HasBlendshapes)
            return BlendshapePlan.Empty;

        return new BlendshapePlan
        {
            HasBlendshapes = true,
            Names = BlendshapeNames ?? Array.Empty<string>(),
            ShaderVariant = BlendshapeShaderVariant,
            StorageMode = BlendshapeDeltaStorageMode,
            Encoding = BlendshapeDeltaEncoding,
            AffectedVertexCount = BlendshapeAffectedVertexCount,
            SparseRecordCount = BlendshapeSparseRecordCount,
            Counts = CreateBufferPlan(BlendshapeCounts, BlendshapeCounts?.AttributeName ?? "BlendshapeCounts", CaptureBufferMetadata(BlendshapeCounts)),
            Indices = CreateBufferPlan(BlendshapeIndices, BlendshapeIndices?.AttributeName ?? "BlendshapeIndices", CaptureBufferMetadata(BlendshapeIndices)),
            Deltas = CreateBufferPlan(BlendshapeDeltas, BlendshapeDeltas?.AttributeName ?? "BlendshapeDeltas", CaptureBufferMetadata(BlendshapeDeltas)),
            SparseShapeRanges = CreateBufferPlan(BlendshapeSparseShapeRanges, BlendshapeSparseShapeRanges?.AttributeName ?? "BlendshapeSparseShapeRanges", CaptureBufferMetadata(BlendshapeSparseShapeRanges)),
            SparseRecords = CreateBufferPlan(BlendshapeSparseRecords, BlendshapeSparseRecords?.AttributeName ?? "BlendshapeSparseRecords", CaptureBufferMetadata(BlendshapeSparseRecords)),
            QuantizedDeltas = CreateBufferPlan(BlendshapeQuantizedDeltas, BlendshapeQuantizedDeltas?.AttributeName ?? "BlendshapeQuantizedDeltas", CaptureBufferMetadata(BlendshapeQuantizedDeltas)),
            QuantizationMetadata = CreateBufferPlan(BlendshapeQuantizationMetadata, BlendshapeQuantizationMetadata?.AttributeName ?? "BlendshapeQuantizationMetadata", CaptureBufferMetadata(BlendshapeQuantizationMetadata))
        };
    }

    private static void WriteVector3(CookedBinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(CookedBinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteMatrix(CookedBinaryWriter writer, Matrix4x4 matrix)
    {
        writer.Write(matrix.M11); writer.Write(matrix.M12); writer.Write(matrix.M13); writer.Write(matrix.M14);
        writer.Write(matrix.M21); writer.Write(matrix.M22); writer.Write(matrix.M23); writer.Write(matrix.M24);
        writer.Write(matrix.M31); writer.Write(matrix.M32); writer.Write(matrix.M33); writer.Write(matrix.M34);
        writer.Write(matrix.M41); writer.Write(matrix.M42); writer.Write(matrix.M43); writer.Write(matrix.M44);
    }

    private static Matrix4x4 ReadMatrix(CookedBinaryReader reader)
        => new(
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteGuid(CookedBinaryWriter writer, Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);
        writer.WriteBytes(bytes);
    }

    private static Guid ReadGuid(CookedBinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(16);
        return new Guid(bytes);
    }

    private static void WriteBufferMetadata(CookedBinaryWriter writer, BufferMetadata metadata)
    {
        writer.Write(metadata.AttributeName ?? string.Empty);
        writer.Write((int)metadata.Target);
        writer.Write((int)metadata.ComponentType);
        writer.Write(metadata.ComponentCount);
        writer.Write(metadata.ElementCount);
        writer.Write(metadata.Normalize);
        writer.Write(metadata.Integral);
        writer.Write(metadata.PadEndingToVec4);
    }

    private static BufferMetadata ReadBufferMetadata(CookedBinaryReader reader)
    {
        string name = reader.ReadString();
        EBufferTarget target = (EBufferTarget)reader.ReadInt32();
        EComponentType componentType = (EComponentType)reader.ReadInt32();
        uint componentCount = reader.ReadUInt32();
        uint elementCount = reader.ReadUInt32();
        bool normalize = reader.ReadBoolean();
        bool integral = reader.ReadBoolean();
        bool padEnding = reader.ReadBoolean();
        return new BufferMetadata(name, target, componentType, componentCount, elementCount, normalize, integral, padEnding);
    }

    private static long CalculateBufferMetadataSize(BufferMetadata metadata)
    {
        long size = CalculateStringBytes(metadata.AttributeName ?? string.Empty);
        size += sizeof(int) * 2; // target + component type
        size += sizeof(uint) * 2; // component count + element count
        size += sizeof(bool) * 3; // normalize, integral, pad
        return size;
    }

    private static int CalculateStringBytes(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        return Calculate7BitEncodedIntSize(byteCount) + byteCount;
    }

    private static int Calculate7BitEncodedIntSize(int value)
    {
        int count = 0;
        uint v = (uint)value;
        do
        {
            v >>= 7;
            count++;
        } while (v != 0);
        return count;
    }

    private unsafe void WriteRawBuffer(CookedBinaryWriter writer, XRDataBuffer buffer, uint byteLength)
    {
        if (buffer.ClientSideSource is not { } source || source.Length < byteLength)
            throw new InvalidOperationException($"Buffer '{buffer.AttributeName}' does not contain {byteLength} bytes of CPU data.");
        writer.WriteBytes(new ReadOnlySpan<byte>(source.Address, (int)byteLength));
    }

    private unsafe void CopyReaderToBuffer(CookedBinaryReader reader, XRDataBuffer buffer, uint byteLength)
    {
        if (buffer.ClientSideSource is null || buffer.ClientSideSource.Length < byteLength)
            buffer.ClientSideSource = DataSource.Allocate(byteLength);
        reader.ReadBytes((void*)buffer.ClientSideSource!.Address, (int)byteLength);
    }

    private unsafe byte[] EncodeSnorm16(XRDataBuffer buffer, out uint encodedLength)
    {
        if (buffer.ComponentType != EComponentType.Float || buffer.ComponentCount < 3)
            throw new InvalidOperationException("Snorm16 encoding requires float3 data.");

        if (buffer.ClientSideSource is not { } source)
            throw new InvalidOperationException($"Buffer '{buffer.AttributeName}' does not have CPU data available.");

        int vertexCount = (int)buffer.ElementCount;
        byte[] encoded = new byte[vertexCount * 3 * sizeof(short)];
        encodedLength = (uint)encoded.Length;

        float* src = (float*)source.Address;
        fixed (byte* dstBytes = encoded)
        {
            short* dst = (short*)dstBytes;
            int stride = (int)buffer.ComponentCount;
            for (int i = 0; i < vertexCount; i++)
            {
                float* comp = src + i * stride;
                for (int c = 0; c < 3; c++)
                {
                    float value = Math.Clamp(comp[c], -1f, 1f);
                    dst[i * 3 + c] = (short)MathF.Round(value * 32767f);
                }
            }
        }

        return encoded;
    }

    private unsafe void DecodeSnorm16(ReadOnlySpan<byte> encoded, XRDataBuffer buffer)
    {
        if (buffer.ClientSideSource is null)
            buffer.ClientSideSource = DataSource.Allocate(buffer.Length);

        float* dst = (float*)buffer.ClientSideSource!.Address;
        fixed (byte* srcBytes = encoded)
        {
            short* src = (short*)srcBytes;
            int vertexCount = (int)buffer.ElementCount;
            int stride = (int)buffer.ComponentCount;
            for (int i = 0; i < vertexCount; i++)
            {
                float* comp = dst + i * stride;
                for (int c = 0; c < 3; c++)
                {
                    short raw = src[i * 3 + c];
                    comp[c] = Math.Clamp(raw / 32767f, -1f, 1f);
                }
            }
        }
    }

    private byte[] EncodeLzma(XRDataBuffer buffer, uint decodedLength, bool useLongLength, out uint encodedLength)
    {
        ReadOnlySpan<byte> source = GetBufferSpan(buffer, decodedLength);
        byte[] raw = source.ToArray();
        byte[] encoded = Compression.Compress(raw, useLongLength);
        encodedLength = (uint)encoded.Length;
        return encoded;
    }

    private byte[] EncodeGDeflate(XRDataBuffer buffer, uint decodedLength, out uint encodedLength)
    {
        ReadOnlySpan<byte> source = GetBufferSpan(buffer, decodedLength);
        if (!Compression.TryCompressGDeflate(source, out byte[] encoded))
            throw new NotSupportedException("GDeflate encoder is not available. Integrate a GDeflate encoder in Compression.TryCompressGDeflate to emit mesh streams with MeshBufferEncoding.GDeflate.");

        encodedLength = (uint)encoded.Length;
        return encoded;
    }

    private void DecodeLzma(ReadOnlySpan<byte> encoded, XRDataBuffer buffer, uint decodedLength, bool useLongLength)
    {
        byte[] encodedArray = encoded.ToArray();
        byte[] decoded = Compression.Decompress(encodedArray, useLongLength);
        if ((uint)decoded.Length != decodedLength)
            throw new InvalidOperationException("Decoded buffer length mismatch.");
        CopyBytesToBuffer(decoded, buffer);
    }

    private unsafe ReadOnlySpan<byte> GetBufferSpan(XRDataBuffer buffer, uint length)
    {
        if (buffer.ClientSideSource is not { } source || source.Length < length)
            throw new InvalidOperationException($"Buffer '{buffer.AttributeName}' does not contain {length} bytes.");
        return new ReadOnlySpan<byte>((void*)source.Address, (int)length);
    }

    private unsafe void CopyBytesToBuffer(ReadOnlySpan<byte> data, XRDataBuffer buffer)
        => buffer.SetRawBytes(data, (uint)data.Length);

    private sealed class MeshPayloadWritePlan
    {
        public MeshMetadata Metadata { get; set; }
        public BufferPlan? Interleaved { get; set; }
        public BufferPlan? Positions { get; set; }
        public BufferPlan? Normals { get; set; }
        public BufferPlan? Tangents { get; set; }
        public List<BufferPlan> ColorStreams { get; } = new();
        public List<BufferPlan> TexCoordStreams { get; } = new();
        public SkinningPlan Skinning { get; set; } = SkinningPlan.Empty;
        public BlendshapePlan Blendshapes { get; set; } = BlendshapePlan.Empty;
        public long TotalSize { get; set; }
    }

    private struct MeshMetadata
    {
        public int VertexCount;
        public EPrimitiveType PrimitiveType;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public ESkinningShaderConvention SkinningShaderConvention;
        public bool InterleavedLayout;
        public uint InterleavedStride;
        public uint PositionOffset;
        public uint? NormalOffset;
        public uint? TangentOffset;
        public uint? ColorOffset;
        public uint? TexCoordOffset;
        public bool HasNormals;
        public bool HasTangents;
        public int ColorChannels;
        public int TexCoordChannels;
    }

    public enum MeshBufferEncoding : byte
    {
        Raw = 0,
        Snorm16 = 1,
        Lzma = 2,
        GDeflate = 3
    }

    private sealed class BufferPlan
    {
        public XRDataBuffer? Buffer { get; init; }
        public string StreamKey { get; init; } = string.Empty;
        public MeshBufferEncoding Encoding { get; init; }
        public uint DecodedLength { get; init; }
        public uint EncodedLength { get; init; }
        public byte[]? EncodedData { get; init; }
        public bool UsesLongLength { get; init; }
        public BufferMetadata? Metadata { get; init; }

        public long GetSerializedLength()
        {
            long size = sizeof(bool); // metadata flag
            if (Metadata.HasValue)
                size += CalculateBufferMetadataSize(Metadata.Value);

            size += sizeof(byte); // encoding
            size += sizeof(uint); // decoded length

            if (Encoding == MeshBufferEncoding.Raw)
                return size + DecodedLength;

            size += sizeof(uint); // encoded length
            size += sizeof(bool); // long-length flag
            size += EncodedLength;
            return size;
        }
    }

    private readonly record struct BufferMetadata(
        string AttributeName,
        EBufferTarget Target,
        EComponentType ComponentType,
        uint ComponentCount,
        uint ElementCount,
        bool Normalize,
        bool Integral,
        bool PadEndingToVec4
    );

    private sealed class SkinningPlan
    {
        public static readonly SkinningPlan Empty = new() { HasSkinning = false };

        public bool HasSkinning { get; init; }
        public BoneInfo[] Bones { get; init; } = Array.Empty<BoneInfo>();
        public int MaxWeightCount { get; init; }
        public SkinningInfluenceEncoding InfluenceEncoding { get; init; }
        public SkinningCoreIndexFormat CoreIndexFormat { get; init; }
        public bool HasSpillInfluences { get; init; }
        public int MaxSpillInfluenceCount { get; init; }
        public BufferPlan? Offsets { get; init; }
        public BufferPlan? Counts { get; init; }
        public BufferPlan? Indices { get; init; }
        public BufferPlan? Values { get; init; }

        public long GetSerializedLength()
        {
            long size = sizeof(bool);
            if (!HasSkinning)
                return size;

            size += sizeof(int); // bone count
            foreach (BoneInfo bone in Bones)
            {
                size += 16; // guid
                size += CalculateStringBytes(bone.Name ?? string.Empty);
                size += sizeof(int); // parent index
                size += sizeof(float) * 16 * 2; // bind + inverse
            }

            size += sizeof(int); // max weight count
            size += sizeof(int); // skinning payload version
            size += sizeof(byte); // influence encoding
            size += sizeof(byte); // core index format
            size += sizeof(bool); // has spill influences
            size += sizeof(int); // max spill count
            size += Offsets?.GetSerializedLength() ?? 0;
            size += Counts?.GetSerializedLength() ?? 0;
            size += Indices?.GetSerializedLength() ?? 0;
            size += Values?.GetSerializedLength() ?? 0;
            return size;
        }
    }

    private sealed class BlendshapePlan
    {
        public static readonly BlendshapePlan Empty = new() { HasBlendshapes = false, Names = Array.Empty<string>() };

        public bool HasBlendshapes { get; init; }
        public string[] Names { get; init; } = Array.Empty<string>();
        public BlendshapeShaderVariant ShaderVariant { get; init; }
        public BlendshapeDeltaStorageMode StorageMode { get; init; }
        public BlendshapeDeltaEncoding Encoding { get; init; }
        public int AffectedVertexCount { get; init; }
        public int SparseRecordCount { get; init; }
        public BufferPlan? Counts { get; init; }
        public BufferPlan? Indices { get; init; }
        public BufferPlan? Deltas { get; init; }
        public BufferPlan? SparseShapeRanges { get; init; }
        public BufferPlan? SparseRecords { get; init; }
        public BufferPlan? QuantizedDeltas { get; init; }
        public BufferPlan? QuantizationMetadata { get; init; }

        public long GetSerializedLength()
        {
            long size = sizeof(bool);
            if (!HasBlendshapes)
                return size;

            size += sizeof(int); // payload version
            size += sizeof(byte) * 3; // shader variant + storage mode + encoding
            size += sizeof(int) * 2; // affected vertices + sparse records
            size += sizeof(int); // name count
            foreach (string name in Names)
                size += CalculateStringBytes(name ?? string.Empty);

            size += Counts?.GetSerializedLength() ?? 0;
            size += Indices?.GetSerializedLength() ?? 0;
            size += Deltas?.GetSerializedLength() ?? 0;
            size += SparseShapeRanges?.GetSerializedLength() ?? 0;
            size += SparseRecords?.GetSerializedLength() ?? 0;
            size += QuantizedDeltas?.GetSerializedLength() ?? 0;
            size += QuantizationMetadata?.GetSerializedLength() ?? 0;
            return size;
        }
    }

    private void ApplySkinningPayload(SkinningPayload? payload)
    {
        if (payload is null)
        {
            UtilizedBones = Array.Empty<(TransformBase tfm, Matrix4x4 invBindWorldMtx)>();
            BoneInfluenceCoreIndices = null;
            BoneInfluenceCoreWeights = null;
            BoneInfluenceSpillHeaders = null;
            BoneInfluenceSpillEntries = null;
            SkinningInfluenceEncoding = SkinningInfluenceEncoding.None;
            SkinningCoreIndexFormat = SkinningCoreIndexFormat.None;
            HasSpillInfluences = false;
            MaxSpillInfluenceCount = 0;
            _maxWeightCount = 0;
            return;
        }

        BoneInfo[] boneInfos = payload.Bones ?? Array.Empty<BoneInfo>();
        TransformBase[] bones = BuildBonesFromPayload(boneInfos);
        (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] utilized = new (TransformBase, Matrix4x4)[boneInfos.Length];
        for (int i = 0; i < boneInfos.Length; i++)
            utilized[i] = (bones[i], boneInfos[i].InverseBindMatrix);

        UtilizedBones = utilized;
        _maxWeightCount = payload.MaxWeightCount;
        SkinningInfluenceEncoding = payload.InfluenceEncoding;
        SkinningCoreIndexFormat = payload.CoreIndexFormat;
        HasSpillInfluences = payload.HasSpillInfluences;
        MaxSpillInfluenceCount = payload.MaxSpillInfluenceCount;

        BoneInfluenceCoreIndices = RestoreBuffer(payload.Offsets);
        BoneInfluenceCoreWeights = RestoreBuffer(payload.Counts);
        BoneInfluenceSpillHeaders = RestoreBuffer(payload.Indices);
        BoneInfluenceSpillEntries = RestoreBuffer(payload.Values);

        RegisterBuffer(BoneInfluenceCoreIndices);
        RegisterBuffer(BoneInfluenceCoreWeights);
        RegisterBuffer(BoneInfluenceSpillHeaders);
        RegisterBuffer(BoneInfluenceSpillEntries);

        EnsureComputeSkinningBuffers();
    }

    private void ApplyBlendshapePayload(BlendshapePayload? payload)
    {
        if (payload is null)
        {
            BlendshapeNames = Array.Empty<string>();
            BlendshapeCounts = null;
            BlendshapeIndices = null;
            BlendshapeDeltas = null;
            BlendshapeSparseShapeRanges = null;
            BlendshapeSparseRecords = null;
            BlendshapeQuantizedDeltas = null;
            BlendshapeQuantizationMetadata = null;
            BlendshapeShaderVariant = BlendshapeShaderVariant.None;
            BlendshapeDeltaStorageMode = BlendshapeDeltaStorageMode.DensePerVertex;
            BlendshapeDeltaEncoding = BlendshapeDeltaEncoding.Float32;
            BlendshapeAffectedVertexCount = 0;
            BlendshapeSparseRecordCount = 0;
            return;
        }

        BlendshapeNames = payload.Names ?? Array.Empty<string>();
        BlendshapeShaderVariant = payload.ShaderVariant;
        BlendshapeDeltaStorageMode = payload.StorageMode;
        BlendshapeDeltaEncoding = payload.Encoding;
        BlendshapeAffectedVertexCount = payload.AffectedVertexCount;
        BlendshapeSparseRecordCount = payload.SparseRecordCount;
        BlendshapeCounts = RestoreBuffer(payload.Counts);
        BlendshapeIndices = RestoreBuffer(payload.Indices);
        BlendshapeDeltas = RestoreBuffer(payload.Deltas);
        BlendshapeSparseShapeRanges = RestoreBuffer(payload.SparseShapeRanges);
        BlendshapeSparseRecords = RestoreBuffer(payload.SparseRecords);
        BlendshapeQuantizedDeltas = RestoreBuffer(payload.QuantizedDeltas);
        BlendshapeQuantizationMetadata = RestoreBuffer(payload.QuantizationMetadata);

        RegisterBuffer(BlendshapeCounts);
        RegisterBuffer(BlendshapeIndices);
        RegisterBuffer(BlendshapeDeltas);
        RegisterBuffer(BlendshapeSparseShapeRanges);
        RegisterBuffer(BlendshapeSparseRecords);
        RegisterBuffer(BlendshapeQuantizedDeltas);
        RegisterBuffer(BlendshapeQuantizationMetadata);
    }

    private TransformBase[] BuildBonesFromPayload(BoneInfo[] infos)
    {
        if (infos is null || infos.Length == 0)
            return Array.Empty<TransformBase>();

        Transform[] bones = new Transform[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            BoneInfo info = infos[i];
            Transform bone = new();
            bone.Name = info.Name ?? $"Bone_{i}";
            bone.SerializedReferenceId = info.BoneId;
            bone.BindMatrix = info.BindMatrix;
            bone.InverseBindMatrix = info.InverseBindMatrix;
            bones[i] = bone;
        }

        for (int i = 0; i < infos.Length; i++)
        {
            BoneInfo info = infos[i];
            int parentIndex = info.ParentIndex;
            if (parentIndex >= 0 && parentIndex < bones.Length)
                bones[i].Parent = bones[parentIndex];

            Matrix4x4 parentInverseBind = parentIndex >= 0 && parentIndex < infos.Length
                ? infos[parentIndex].InverseBindMatrix
                : Matrix4x4.Identity;
            Matrix4x4 localMatrix = parentInverseBind * info.BindMatrix;
            bones[i].DeriveLocalMatrix(localMatrix);
        }

        return bones;
    }

    private static BufferBlob? CaptureBuffer(XRDataBuffer? buffer)
    {
        if (buffer?.ClientSideSource is null)
            return null;

        return new BufferBlob
        {
            AttributeName = buffer.AttributeName,
            Target = buffer.Target,
            ComponentType = buffer.ComponentType,
            ComponentCount = buffer.ComponentType == EComponentType.Struct ? buffer.ElementSize : buffer.ComponentCount,
            ElementCount = buffer.ElementCount,
            Normalize = buffer.Normalize,
            Integral = buffer.Integral,
            PadEndingToVec4 = buffer.PadEndingToVec4,
            ByteLength = buffer.ClientSideSource.Length,
            Data = buffer.GetRawBytes(buffer.ClientSideSource.Length)
        };
    }

    private XRDataBuffer? RestoreBuffer(BufferBlob? blob)
    {
        if (blob?.Data is null)
            return null;

        XRDataBuffer buffer = new(blob.AttributeName ?? string.Empty, blob.Target, blob.ElementCount, blob.ComponentType, blob.ComponentCount, blob.Normalize, blob.Integral)
        {
            PadEndingToVec4 = blob.PadEndingToVec4
        };

        buffer.SetRawBytes(blob.Data, blob.ByteLength);

        if (blob.ByteLength != buffer.ClientSideSource?.Length)
            throw new InvalidOperationException($"Cooked buffer '{blob.AttributeName ?? "<unnamed>"}' length mismatch.");

        return buffer;
    }

    private void RegisterBuffer(XRDataBuffer? buffer)
    {
        if (buffer is null)
            return;

        Buffers ??= new BufferCollection();
        string key = string.IsNullOrWhiteSpace(buffer.AttributeName)
            ? buffer.Target.ToString()
            : buffer.AttributeName;

        buffer.AttributeName = key;
        if (Buffers.ContainsKey(key))
            Buffers.Remove(key);

        Buffers.Add(key, buffer);
    }

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    private static void WritePayload(CookedBinaryWriter writer, MeshCookedPayload payload)
    {
        writer.WriteValue(payload.Positions);
        writer.WriteValue(payload.Normals);
        writer.WriteValue(payload.Tangents);
        writer.WriteValue(payload.Colors);
        writer.WriteValue(payload.TexCoords);
        writer.WriteValue(payload.Triangles);
        writer.WriteValue(payload.Lines);
        writer.WriteValue(payload.Points);
        writer.WriteValue(payload.BoundsMin);
        writer.WriteValue(payload.BoundsMax);
        writer.WriteValue(payload.PrimitiveType);
    }

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    private static MeshCookedPayload ReadPayload(CookedBinaryReader reader)
    {
        Vector3[]? positions = reader.ReadValue<Vector3[]>();
        Vector3[]? normals = reader.ReadValue<Vector3[]>();
        Vector3[]? tangents = reader.ReadValue<Vector3[]>();
        Vector4[][]? colors = reader.ReadValue<Vector4[][]>();
        Vector2[][]? texCoords = reader.ReadValue<Vector2[][]>();
        int[]? triangles = reader.ReadValue<int[]>();
        int[]? lines = reader.ReadValue<int[]>();
        int[]? points = reader.ReadValue<int[]>();
        Vector3? boundsMin = reader.ReadValue<Vector3>();
        Vector3? boundsMax = reader.ReadValue<Vector3>();
        EPrimitiveType? primitiveType = reader.ReadValue<EPrimitiveType>();

        return new MeshCookedPayload
        {
            Positions = positions ?? Array.Empty<Vector3>(),
            Normals = normals,
            Tangents = tangents,
            Colors = colors,
            TexCoords = texCoords,
            Triangles = triangles,
            Lines = lines,
            Points = points,
            BoundsMin = boundsMin ?? Vector3.Zero,
            BoundsMax = boundsMax ?? Vector3.Zero,
            PrimitiveType = primitiveType ?? EPrimitiveType.Triangles
        };
    }

    internal sealed class MeshCookedPayload
    {
        public Vector3[] Positions { get; set; } = Array.Empty<Vector3>();
        public Vector3[]? Normals { get; set; }
        public Vector3[]? Tangents { get; set; }
        public Vector4[][]? Colors { get; set; }
        public Vector2[][]? TexCoords { get; set; }
        public int[]? Triangles { get; set; }
        public int[]? Lines { get; set; }
        public int[]? Points { get; set; }
        public Vector3 BoundsMin { get; set; }
        public Vector3 BoundsMax { get; set; }
        public EPrimitiveType PrimitiveType { get; set; } = EPrimitiveType.Triangles;
        public int VertexCount { get; set; }
        public int ColorChannels { get; set; }
        public int TexCoordChannels { get; set; }
        public bool InterleavedLayout { get; set; }
        public uint InterleavedStride { get; set; }
        public uint PositionOffset { get; set; }
        public uint? NormalOffset { get; set; }
        public uint? TangentOffset { get; set; }
        public uint? ColorOffset { get; set; }
        public uint? TexCoordOffset { get; set; }
        public SkinningPayload? Skinning { get; set; }
        public BlendshapePayload? Blendshapes { get; set; }
    }

    internal sealed class SkinningPayload
    {
        public BoneInfo[]? Bones { get; set; }
        public BufferBlob? Offsets { get; set; }
        public BufferBlob? Counts { get; set; }
        public BufferBlob? Indices { get; set; }
        public BufferBlob? Values { get; set; }
        public int MaxWeightCount { get; set; }
        public SkinningInfluenceEncoding InfluenceEncoding { get; set; }
        public SkinningCoreIndexFormat CoreIndexFormat { get; set; }
        public bool HasSpillInfluences { get; set; }
        public int MaxSpillInfluenceCount { get; set; }
    }

    internal sealed class BlendshapePayload
    {
        public string[]? Names { get; set; }
        public BlendshapeShaderVariant ShaderVariant { get; set; }
        public BlendshapeDeltaStorageMode StorageMode { get; set; }
        public BlendshapeDeltaEncoding Encoding { get; set; }
        public int AffectedVertexCount { get; set; }
        public int SparseRecordCount { get; set; }
        public BufferBlob? Counts { get; set; }
        public BufferBlob? Indices { get; set; }
        public BufferBlob? Deltas { get; set; }
        public BufferBlob? SparseShapeRanges { get; set; }
        public BufferBlob? SparseRecords { get; set; }
        public BufferBlob? QuantizedDeltas { get; set; }
        public BufferBlob? QuantizationMetadata { get; set; }
    }

    internal sealed class BoneInfo
    {
        public Guid BoneId { get; set; }
        public string? Name { get; set; }
        public int ParentIndex { get; set; }
        public Matrix4x4 BindMatrix { get; set; }
        public Matrix4x4 InverseBindMatrix { get; set; }
    }

    internal sealed class BufferBlob
    {
        public string? AttributeName { get; set; }
        public EBufferTarget Target { get; set; }
        public EComponentType ComponentType { get; set; }
        public uint ComponentCount { get; set; }
        public uint ElementCount { get; set; }
        public bool Normalize { get; set; }
        public bool Integral { get; set; }
        public bool PadEndingToVec4 { get; set; }
        public uint ByteLength { get; set; }
        public byte[]? Data { get; set; }
    }
}
