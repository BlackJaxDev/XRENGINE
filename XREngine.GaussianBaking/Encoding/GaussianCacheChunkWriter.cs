using System.Buffers.Binary;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Encoding;

/// <summary>Builds chunk data from sections.</summary>
public sealed class GaussianCacheChunkWriter
{
    private readonly MemoryStream _stream = new();

    /// <summary>The number of bytes written so far.</summary>
    public int Length => (int)_stream.Length;

    public void Append(in GaussianCacheSection section)
    {
        Span<byte> header = stackalloc byte[GaussianCacheSection.HeaderSize];
        header[0] = (byte)section.Kind;
        header[1] = section.Level;
        header[2] = section.TemporalLevel;
        header[3] = section.Flags;
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], section.First);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], section.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], section.FrameFirst);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], section.FrameCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], section.Payload.Length);
        _stream.Write(header);
        _stream.Write(section.Payload.Span);
    }

    public byte[] ToArray() => _stream.ToArray();
}
