using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using XREngine.Core.Files;
using CookedBinaryReader = XREngine.Core.Files.RuntimeCookedBinaryReader;
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;
using CookedBinaryWriter = XREngine.Core.Files.RuntimeCookedBinaryWriter;
using ICookedBinarySerializable = XREngine.Core.Files.IRuntimeCookedBinarySerializable;

namespace XREngine.Rendering.Models.Gaussian.Cache;

// Payload after the base object: i32 header length, header bytes, i32 root data length,
// root data bytes, i32 path count, then each path as a length-prefixed UTF-8 string.
public sealed partial class GaussianCache : ICookedBinarySerializable
{
#if !XRE_PUBLISHED
    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
#endif
    void ICookedBinarySerializable.WriteCookedBinary(CookedBinaryWriter writer)
    {
        long start = writer.Position;
        writer.WriteBaseObject<XRAsset>(this);

        byte[] headerBytes = SerializeHeader();
        writer.Write(headerBytes.Length);
        writer.WriteBytes(headerBytes);
        writer.Write(_rootChunkData.Length);
        writer.WriteBytes(_rootChunkData);
        writer.Write(_chunkAssetPaths.Length);
        foreach (string path in _chunkAssetPaths)
            writer.Write(path);

        long expected = CookedBinarySerializer.CalculateBaseObjectSize(this, typeof(XRAsset)) + PayloadSize();
        if (writer.Position - start != expected)
            throw new InvalidOperationException(
                $"Cooked gaussian cache wrote {writer.Position - start} bytes but calculated {expected}.");
    }

#if !XRE_PUBLISHED
    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
#endif
    void ICookedBinarySerializable.ReadCookedBinary(CookedBinaryReader reader)
    {
        reader.ReadBaseObject<XRAsset>(this);

        byte[] headerBytes = reader.ReadBytes(ReadLength(ref reader, "header"));
        using (MemoryStream stream = new(headerBytes, writable: false))
        using (BinaryReader headerReader = new(stream, Encoding.UTF8, leaveOpen: true))
            Header = GaussianCacheHeader.Read(headerReader);

        RootChunkData = reader.ReadBytes(ReadLength(ref reader, "root chunk"));

        // Each path takes at least one byte for its length.
        int pathCount = ReadLength(ref reader, "path list");
        string[] paths = new string[pathCount];
        for (int i = 0; i < pathCount; i++)
            paths[i] = reader.ReadString();
        ChunkAssetPaths = paths;
    }

#if !XRE_PUBLISHED
    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
#endif
    long ICookedBinarySerializable.CalculateCookedBinarySize()
        => CookedBinarySerializer.CalculateBaseObjectSize(this, typeof(XRAsset)) + PayloadSize();

    private static int ReadLength(ref CookedBinaryReader reader, string what)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > reader.Remaining)
            throw new GaussianCacheFormatException(
                GaussianCacheFormatException.MalformedCode,
                $"Gaussian cache {what} length {length} is larger than the remaining data.");
        return length;
    }

    private byte[] SerializeHeader()
    {
        using MemoryStream stream = new(checked((int)_header.CalculateByteSize()));
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            _header.Write(writer);
        return stream.ToArray();
    }

    private long PayloadSize()
    {
        long size = sizeof(int) + _header.CalculateByteSize();
        size += sizeof(int) + _rootChunkData.Length;
        size += sizeof(int);
        foreach (string path in _chunkAssetPaths)
        {
            int byteCount = Encoding.UTF8.GetByteCount(path);
            size += Get7BitEncodedSize(byteCount) + byteCount;
        }
        return size;
    }

    private static int Get7BitEncodedSize(int value)
    {
        uint v = (uint)value;
        int size = 1;
        while (v >= 0x80)
        {
            size++;
            v >>= 7;
        }
        return size;
    }
}
