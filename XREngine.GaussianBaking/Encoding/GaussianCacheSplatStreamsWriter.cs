using System.Buffers.Binary;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Encoding;

/// <summary>Builds a splat streams payload from attribute substreams.</summary>
public sealed class GaussianCacheSplatStreamsWriter
{
    private readonly MemoryStream _stream = new();

    public void Append(GaussianCacheSplatAttribute attribute, ReadOnlySpan<byte> bytes)
    {
        Span<byte> header = stackalloc byte[GaussianCacheSplatStreams.SubstreamHeaderSize];
        header[0] = (byte)attribute;
        BinaryPrimitives.WriteInt32LittleEndian(header[1..], bytes.Length);
        _stream.Write(header);
        _stream.Write(bytes);
    }

    public byte[] ToArray() => _stream.ToArray();
}
