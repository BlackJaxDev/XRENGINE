namespace XREngine.Data;

/// <summary>Optional application-installed GDeflate codec without native API types.</summary>
public interface IGDeflateCodec
{
    bool TryCompress(ReadOnlySpan<byte> source, out byte[] encoded);
    bool TryDecompress(ReadOnlySpan<byte> encodedSource, int expectedDecodedLength, out byte[] decoded);
}
