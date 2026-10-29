using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Splits a splat streams payload into attribute substreams. Each substream is a u8 attribute id, an i32 length, and the bytes.</summary>
public static class GaussianCacheSplatStreams
{
    /// <summary>Size in bytes of the header of one substream.</summary>
    public const int SubstreamHeaderSize = 5;

    /// <exception cref="GaussianCacheFormatException">A length is not valid, an attribute id is not known, or an attribute appears twice.</exception>
    public static IReadOnlyDictionary<GaussianCacheSplatAttribute, ReadOnlyMemory<byte>> Read(ReadOnlyMemory<byte> payload)
    {
        Dictionary<GaussianCacheSplatAttribute, ReadOnlyMemory<byte>> streams = [];
        int offset = 0;
        while (offset < payload.Length)
        {
            if (payload.Length - offset < SubstreamHeaderSize)
                throw Malformed("A splat substream header is cut off.");

            byte id = payload.Span[offset];
            int length = BinaryPrimitives.ReadInt32LittleEndian(payload.Span.Slice(offset + 1, 4));
            if (id < (byte)GaussianCacheSplatAttribute.Binding || id > (byte)GaussianCacheSplatAttribute.ChildCount)
                throw Malformed($"The splat attribute id {id} is not known.");
            if (length < 0)
                throw Malformed("A splat substream length is negative.");

            offset += SubstreamHeaderSize;
            if (length > payload.Length - offset)
                throw Malformed("A splat substream is longer than the payload.");
            if (!streams.TryAdd((GaussianCacheSplatAttribute)id, payload.Slice(offset, length)))
                throw Malformed($"The splat attribute id {id} appears more than once.");

            offset += length;
        }
        return streams;
    }

    private static GaussianCacheFormatException Malformed(string message)
        => new(GaussianCacheFormatException.MalformedCode, message);
}
