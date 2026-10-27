using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Cache;

// Decodes the sections of one chunk into the runtime data.
public sealed partial class GaussianCacheRuntimeData
{
    /// <summary>The largest stride of a splat substream before its filter: the scale stream.</summary>
    private const int MaxSplatStride = 12;

    /// <summary>
    /// Decodes all sections of a chunk. The sections of a level must arrive in order and continue the data that is
    /// already decoded. When the method throws, the data can hold part of the chunk.
    /// </summary>
    /// <exception cref="GaussianCacheFormatException">A section is not valid or arrives out of order.</exception>
    internal void ApplyChunk(ReadOnlyMemory<byte> chunkData, IMeshOptimizerBackend meshopt)
    {
        ArgumentNullException.ThrowIfNull(meshopt);
        foreach (GaussianCacheSection section in GaussianCacheChunkReader.Read(chunkData))
        {
            switch (section.Kind)
            {
                case GaussianCacheSectionKind.SplatStreams:
                    ApplySplats(section, meshopt);
                    break;
                case GaussianCacheSectionKind.DriverIndices:
                    ApplyDriverIndices(section, meshopt);
                    break;
                case GaussianCacheSectionKind.DriverMotion:
                    MotionBlockDescriptor block = new(
                        section.Level, section.TemporalLevel, section.First, section.Count, section.FrameFirst, section.FrameCount, section.WideResiduals);
                    MotionDecoder.DecodeBlock(block, section.Payload.Span, Frames, meshopt);
                    break;
            }
        }
    }

    private void ApplySplats(in GaussianCacheSection section, IMeshOptimizerBackend meshopt)
    {
        int level = section.Level;
        if (level >= LevelRecords.Length)
            throw Malformed($"A splat section names level {level}, which does not exist.");
        for (int coarser = 0; coarser < level; coarser++)
        {
            if (_loadedNodeCounts[coarser] != LevelRecords[coarser].Length)
                throw Malformed($"A splat section of level {level} arrives before level {coarser} is complete.");
        }

        GaussianCacheSplatRecord[] records = LevelRecords[level];
        int first = section.First;
        int count = section.Count;
        if (count <= 0 || first != _loadedNodeCounts[level] || count > records.Length - first)
            throw Malformed($"A splat section of level {level} does not continue the decoded nodes.");

        bool hasChildren = level < LevelRecords.Length - 1;
        IReadOnlyDictionary<GaussianCacheSplatAttribute, ReadOnlyMemory<byte>> streams = GaussianCacheSplatStreams.Read(section.Payload);
        if (streams.ContainsKey(GaussianCacheSplatAttribute.ChildCount) != hasChildren)
            throw Malformed(hasChildren ? "A splat section has no child counts." : "A splat section of the last level has child counts.");

        Span<GaussianCacheSplatRecord> target = records.AsSpan(first, count);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(count * MaxSplatStride);
        try
        {
            DecodeBinding(target, Substream(streams, GaussianCacheSplatAttribute.Binding), scratch, meshopt);
            DecodeTriangle(target, Substream(streams, GaussianCacheSplatAttribute.Triangle), scratch, meshopt);
            DecodeRotation(target, Substream(streams, GaussianCacheSplatAttribute.Rotation), scratch, meshopt);
            DecodeScale(target, Substream(streams, GaussianCacheSplatAttribute.Scale), scratch, meshopt);
            DecodeColor(target, Substream(streams, GaussianCacheSplatAttribute.Color), scratch, meshopt);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }

        CheckBindings(target, level, first);
        if (hasChildren)
            ApplyChildCounts(target, streams[GaussianCacheSplatAttribute.ChildCount].Span, level, first);

        LevelNodeLoaded[level].AsSpan(first, count).Fill(true);
        _loadedNodeCounts[level] = first + count;
    }

    private static ReadOnlyMemory<byte> Substream(IReadOnlyDictionary<GaussianCacheSplatAttribute, ReadOnlyMemory<byte>> streams, GaussianCacheSplatAttribute attribute)
        => streams.TryGetValue(attribute, out ReadOnlyMemory<byte> bytes)
            ? bytes
            : throw Malformed($"A splat section has no {attribute} substream.");

    private static Span<byte> DecodeVertices(byte[] scratch, int count, int stride, ReadOnlyMemory<byte> encoded, IMeshOptimizerBackend meshopt)
    {
        Span<byte> bytes = scratch.AsSpan(0, count * stride);
        try
        {
            meshopt.DecodeVertexBuffer(bytes, count, stride, encoded.Span);
        }
        catch (InvalidDataException exception)
        {
            throw Malformed("A splat substream is damaged: " + exception.Message);
        }

        return bytes;
    }

    private static void DecodeBinding(Span<GaussianCacheSplatRecord> target, ReadOnlyMemory<byte> encoded, byte[] scratch, IMeshOptimizerBackend meshopt)
    {
        Span<byte> bytes = DecodeVertices(scratch, target.Length, 8, encoded, meshopt);
        for (int i = 0; i < target.Length; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(i * 8, 8);
            target[i].A = BinaryPrimitives.ReadUInt16LittleEndian(record);
            target[i].B = BinaryPrimitives.ReadUInt16LittleEndian(record[2..]);
            target[i].C = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            target[i].Flags = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        }
    }

    private static void DecodeTriangle(Span<GaussianCacheSplatRecord> target, ReadOnlyMemory<byte> encoded, byte[] scratch, IMeshOptimizerBackend meshopt)
    {
        Span<byte> bytes = DecodeVertices(scratch, target.Length, 4, encoded, meshopt);
        for (int i = 0; i < target.Length; i++)
            target[i].Triangle = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]);
    }

    private static void DecodeRotation(Span<GaussianCacheSplatRecord> target, ReadOnlyMemory<byte> encoded, byte[] scratch, IMeshOptimizerBackend meshopt)
    {
        Span<byte> bytes = DecodeVertices(scratch, target.Length, 8, encoded, meshopt);
        meshopt.DecodeFilterQuat(bytes, target.Length, 8);
        for (int i = 0; i < target.Length; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(i * 8, 8);
            target[i].RotX = BinaryPrimitives.ReadInt16LittleEndian(record);
            target[i].RotY = BinaryPrimitives.ReadInt16LittleEndian(record[2..]);
            target[i].RotZ = BinaryPrimitives.ReadInt16LittleEndian(record[4..]);
            target[i].RotW = BinaryPrimitives.ReadInt16LittleEndian(record[6..]);
        }
    }

    private static void DecodeScale(Span<GaussianCacheSplatRecord> target, ReadOnlyMemory<byte> encoded, byte[] scratch, IMeshOptimizerBackend meshopt)
    {
        Span<byte> bytes = DecodeVertices(scratch, target.Length, 12, encoded, meshopt);
        meshopt.DecodeFilterExp(bytes, target.Length, 12);
        for (int i = 0; i < target.Length; i++)
        {
            ReadOnlySpan<byte> record = bytes.Slice(i * 12, 12);
            target[i].ScaleX = BinaryPrimitives.ReadSingleLittleEndian(record);
            target[i].ScaleY = BinaryPrimitives.ReadSingleLittleEndian(record[4..]);
            target[i].ScaleZ = BinaryPrimitives.ReadSingleLittleEndian(record[8..]);
        }
    }

    private static void DecodeColor(Span<GaussianCacheSplatRecord> target, ReadOnlyMemory<byte> encoded, byte[] scratch, IMeshOptimizerBackend meshopt)
    {
        Span<byte> bytes = DecodeVertices(scratch, target.Length, 4, encoded, meshopt);
        meshopt.DecodeFilterColor(bytes, target.Length, 4);

        // The bytes are red, green, blue, and alpha, so red is the low byte.
        for (int i = 0; i < target.Length; i++)
            target[i].ColorRgba8 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]);
    }

    /// <summary>Checks that the static nodes come first and that each bound node names a triangle of its driver level.</summary>
    private void CheckBindings(ReadOnlySpan<GaussianCacheSplatRecord> records, int level, int first)
    {
        int staticCount = _staticCounts[level];
        uint triangleCount = level < DriverLevelIndices.Length ? (uint)(DriverLevelIndices[level].Length / 3) : 0u;
        for (int i = 0; i < records.Length; i++)
        {
            bool isStatic = (records[i].Flags & GaussianCacheSplatRecord.StaticFlag) != 0;
            bool valid = first + i < staticCount
                ? isStatic && records[i].Triangle == GaussianCacheSplatRecord.StaticTriangle
                : !isStatic && records[i].Triangle < triangleCount;
            if (!valid)
                throw Malformed($"Node {first + i} of level {level} has a binding that does not agree with the static count or the driver triangles.");
        }
    }

    /// <summary>Sets the child range of each node and the parent index of each child.</summary>
    private void ApplyChildCounts(Span<GaussianCacheSplatRecord> target, ReadOnlySpan<byte> childCounts, int level, int first)
    {
        if (childCounts.Length != target.Length)
            throw Malformed($"A splat section of level {level} has {childCounts.Length} child counts for {target.Length} nodes.");

        int[] parents = ParentIndex[level + 1];
        uint prefix = _childPrefix[level];
        for (int i = 0; i < target.Length; i++)
        {
            uint childCount = childCounts[i];
            if (prefix + childCount > (uint)parents.Length)
                throw Malformed($"The child counts of level {level} are more than the size of level {level + 1}.");

            target[i].ChildStart = prefix;
            target[i].ChildCount = childCount;
            parents.AsSpan((int)prefix, (int)childCount).Fill(first + i);
            prefix += childCount;
        }

        if (first + target.Length == LevelRecords[level].Length && prefix != (uint)parents.Length)
            throw Malformed($"The child counts of level {level} do not add up to the size of level {level + 1}.");
        _childPrefix[level] = prefix;
    }

    private void ApplyDriverIndices(in GaussianCacheSection section, IMeshOptimizerBackend meshopt)
    {
        int level = section.Level;
        if (level >= DriverLevelIndices.Length)
            throw Malformed($"A driver triangle section names level {level}, which does not exist.");

        uint[] indices = DriverLevelIndices[level];
        int first = section.First;
        int count = section.Count;
        if (count <= 0 || first != _loadedTriangleCounts[level] || count > (indices.Length / 3) - first)
            throw Malformed($"A driver triangle section of level {level} does not continue the decoded triangles.");

        Span<uint> target = indices.AsSpan(first * 3, count * 3);
        try
        {
            meshopt.DecodeIndexBuffer(target, section.Payload.Span);
        }
        catch (InvalidDataException exception)
        {
            throw Malformed("A driver triangle section is damaged: " + exception.Message);
        }

        uint vertexCount = (uint)_driverVertexCounts[level];
        foreach (uint index in target)
        {
            if (index >= vertexCount)
                throw Malformed($"A triangle of driver level {level} names vertex {index}, but the level has {vertexCount} vertices.");
        }

        _loadedTriangleCounts[level] = first + count;
    }
}
