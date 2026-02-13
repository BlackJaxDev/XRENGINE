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

namespace XREngine.Rendering;

public partial class XRMesh : ICookedBinarySerializable
{
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
    }

    private void ReadMeshPayload(CookedBinaryReader reader)
    {
        MeshMetadata metadata = ReadMetadata(reader);

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
    }

    private void ApplyMetadata(MeshMetadata metadata)
    {
        VertexCount = metadata.VertexCount;
        Type = metadata.PrimitiveType;
        Bounds = new AABB(metadata.BoundsMin, metadata.BoundsMax);

        Buffers?.Clear();
        InitMeshBuffers(metadata.HasNormals, metadata.HasTangents, metadata.ColorChannels, metadata.TexCoordChannels, metadata.InterleavedLayout);

        InterleavedStride = metadata.InterleavedStride;
        PositionOffset = metadata.PositionOffset;
        NormalOffset = metadata.NormalOffset;
        TangentOffset = metadata.TangentOffset;
        ColorOffset = metadata.ColorOffset;
        TexCoordOffset = metadata.TexCoordOffset;
    }

    private MeshMetadata ReadMetadata(CookedBinaryReader reader)
    {
        MeshMetadata metadata = new()
        {
            VertexCount = reader.ReadInt32(),
            PrimitiveType = (EPrimitiveType)reader.ReadInt32(),
            BoundsMin = ReadVector3(reader),
            BoundsMax = ReadVector3(reader),
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

    private static void WriteMetadata(CookedBinaryWriter writer, MeshMetadata metadata)
    {
        writer.Write(metadata.VertexCount);
        writer.Write((int)metadata.PrimitiveType);
        WriteVector3(writer, metadata.BoundsMin);
        WriteVector3(writer, metadata.BoundsMax);
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
            BoneWeightOffsets = null;
            BoneWeightCounts = null;
            BoneWeightIndices = null;
            BoneWeightValues = null;
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

        BoneWeightOffsets = ReadBufferData(reader, null, allowMetadata: true);
        BoneWeightCounts = ReadBufferData(reader, null, allowMetadata: true);
        BoneWeightIndices = ReadBufferData(reader, null, allowMetadata: true);
        BoneWeightValues = ReadBufferData(reader, null, allowMetadata: true);
    }

    private void WriteBlendshapeData(CookedBinaryWriter writer, BlendshapePlan plan)
    {
        writer.Write(plan.HasBlendshapes);
        if (!plan.HasBlendshapes)
            return;

        writer.Write(plan.Names.Length);
        foreach (string name in plan.Names)
            writer.Write(name ?? string.Empty);

        WriteBufferPlan(writer, plan.Counts);
        WriteBufferPlan(writer, plan.Indices);
        WriteBufferPlan(writer, plan.Deltas);
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
            return;
        }

        int nameCount = reader.ReadInt32();
        string[] names = new string[nameCount];
        for (int i = 0; i < nameCount; i++)
            names[i] = reader.ReadString();
        BlendshapeNames = names;

        BlendshapeCounts = ReadBufferData(reader, null, allowMetadata: true);
        BlendshapeIndices = ReadBufferData(reader, null, allowMetadata: true);
        BlendshapeDeltas = ReadBufferData(reader, null, allowMetadata: true);
    }

    private SkinningPlan BuildSkinningPlan()
    {
        if (!HasSkinning)
            return SkinningPlan.Empty;

        BoneInfo[] bones = BuildBoneInfos();
        return new SkinningPlan
        {
            HasSkinning = true,
            Bones = bones,
            MaxWeightCount = MaxWeightCount,
            Offsets = CreateBufferPlan(BoneWeightOffsets, BoneWeightOffsets?.AttributeName ?? "BoneWeightOffsets", CaptureBufferMetadata(BoneWeightOffsets)),
            Counts = CreateBufferPlan(BoneWeightCounts, BoneWeightCounts?.AttributeName ?? "BoneWeightCounts", CaptureBufferMetadata(BoneWeightCounts)),
            Indices = CreateBufferPlan(BoneWeightIndices, BoneWeightIndices?.AttributeName ?? "BoneWeightIndices", CaptureBufferMetadata(BoneWeightIndices)),
            Values = CreateBufferPlan(BoneWeightValues, BoneWeightValues?.AttributeName ?? "BoneWeightValues", CaptureBufferMetadata(BoneWeightValues))
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
                BoneId = transform.ID,
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
            Counts = CreateBufferPlan(BlendshapeCounts, BlendshapeCounts?.AttributeName ?? "BlendshapeCounts", CaptureBufferMetadata(BlendshapeCounts)),
            Indices = CreateBufferPlan(BlendshapeIndices, BlendshapeIndices?.AttributeName ?? "BlendshapeIndices", CaptureBufferMetadata(BlendshapeIndices)),
            Deltas = CreateBufferPlan(BlendshapeDeltas, BlendshapeDeltas?.AttributeName ?? "BlendshapeDeltas", CaptureBufferMetadata(BlendshapeDeltas))
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
    {
        if (buffer.ClientSideSource is null || buffer.ClientSideSource.Length < data.Length)
            buffer.ClientSideSource = DataSource.Allocate((uint)data.Length);
        data.CopyTo(new Span<byte>((void*)buffer.ClientSideSource!.Address, data.Length));
    }

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
        public BufferPlan? Counts { get; init; }
        public BufferPlan? Indices { get; init; }
        public BufferPlan? Deltas { get; init; }

        public long GetSerializedLength()
        {
            long size = sizeof(bool);
            if (!HasBlendshapes)
                return size;

            size += sizeof(int); // name count
            foreach (string name in Names)
                size += CalculateStringBytes(name ?? string.Empty);

            size += Counts?.GetSerializedLength() ?? 0;
            size += Indices?.GetSerializedLength() ?? 0;
            size += Deltas?.GetSerializedLength() ?? 0;
            return size;
        }
    }

    private void ApplySkinningPayload(SkinningPayload? payload)
    {
        if (payload is null)
        {
            UtilizedBones = Array.Empty<(TransformBase tfm, Matrix4x4 invBindWorldMtx)>();
            BoneWeightOffsets = null;
            BoneWeightCounts = null;
            BoneWeightIndices = null;
            BoneWeightValues = null;
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

        BoneWeightOffsets = RestoreBuffer(payload.Offsets);
        BoneWeightCounts = RestoreBuffer(payload.Counts);
        BoneWeightIndices = RestoreBuffer(payload.Indices);
        BoneWeightValues = RestoreBuffer(payload.Values);

        RegisterBuffer(BoneWeightOffsets);
        RegisterBuffer(BoneWeightCounts);
        RegisterBuffer(BoneWeightIndices);
        RegisterBuffer(BoneWeightValues);
    }

    private void ApplyBlendshapePayload(BlendshapePayload? payload)
    {
        if (payload is null)
        {
            BlendshapeNames = Array.Empty<string>();
            BlendshapeCounts = null;
            BlendshapeIndices = null;
            BlendshapeDeltas = null;
            return;
        }

        BlendshapeNames = payload.Names ?? Array.Empty<string>();
        BlendshapeCounts = RestoreBuffer(payload.Counts);
        BlendshapeIndices = RestoreBuffer(payload.Indices);
        BlendshapeDeltas = RestoreBuffer(payload.Deltas);

        RegisterBuffer(BlendshapeCounts);
        RegisterBuffer(BlendshapeIndices);
        RegisterBuffer(BlendshapeDeltas);
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
            Data = buffer.ClientSideSource.GetBytes()
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

        buffer.ClientSideSource?.Dispose();
        buffer.ClientSideSource = new DataSource(blob.Data);

        if (blob.ByteLength != buffer.ClientSideSource.Length)
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

    private sealed class MeshCookedPayload
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

    private sealed class SkinningPayload
    {
        public BoneInfo[]? Bones { get; set; }
        public BufferBlob? Offsets { get; set; }
        public BufferBlob? Counts { get; set; }
        public BufferBlob? Indices { get; set; }
        public BufferBlob? Values { get; set; }
        public int MaxWeightCount { get; set; }
    }

    private sealed class BlendshapePayload
    {
        public string[]? Names { get; set; }
        public BufferBlob? Counts { get; set; }
        public BufferBlob? Indices { get; set; }
        public BufferBlob? Deltas { get; set; }
    }

    private sealed class BoneInfo
    {
        public Guid BoneId { get; set; }
        public string? Name { get; set; }
        public int ParentIndex { get; set; }
        public Matrix4x4 BindMatrix { get; set; }
        public Matrix4x4 InverseBindMatrix { get; set; }
    }

    private sealed class BufferBlob
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
