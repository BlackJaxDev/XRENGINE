using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Splits chunk data into sections and checks every length against the buffer.</summary>
public static class GaussianCacheChunkReader
{
    public static IReadOnlyList<GaussianCacheSection> Read(ReadOnlyMemory<byte> chunkData)
    {
        List<GaussianCacheSection> sections = [];
        int offset = 0;
        while (offset < chunkData.Length)
        {
            if (chunkData.Length - offset < GaussianCacheSection.HeaderSize)
                throw Malformed("A section header is cut off.");

            ReadOnlySpan<byte> header = chunkData.Span.Slice(offset, GaussianCacheSection.HeaderSize);
            GaussianCacheSectionKind kind = (GaussianCacheSectionKind)header[0];
            if (kind is not (GaussianCacheSectionKind.SplatStreams or GaussianCacheSectionKind.DriverIndices or GaussianCacheSectionKind.DriverMotion))
                throw Malformed($"The section kind {header[0]} is not known.");

            int first = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            int frameFirst = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            int frameCount = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
            if (first < 0 || count < 0 || frameFirst < 0 || frameCount < 0 || payloadLength < 0)
                throw Malformed("A section field is negative.");

            byte level = header[1];
            byte temporalLevel = header[2];
            byte flags = header[3];
            offset += GaussianCacheSection.HeaderSize;
            if (payloadLength > chunkData.Length - offset)
                throw Malformed("A section payload is longer than the chunk data.");

            sections.Add(new GaussianCacheSection(kind, level, temporalLevel, flags, first, count, frameFirst, frameCount, chunkData.Slice(offset, payloadLength)));
            offset += payloadLength;
        }
        return sections;
    }

    private static GaussianCacheFormatException Malformed(string message)
        => new(GaussianCacheFormatException.MalformedCode, message);
}
