namespace XREngine.Data;

public interface IGDeflateCodec
{
    bool TryCompress(ReadOnlySpan<byte> source, out byte[] encoded);
    bool TryDecompress(ReadOnlySpan<byte> encodedSource, int expectedDecodedLength, out byte[] decoded);

    /// <summary>
    /// Decompresses into caller-owned storage. Backends that can write to a span override this;
    /// the default decodes to an array and copies once so every codec honors the span contract.
    /// </summary>
    bool TryDecompress(ReadOnlySpan<byte> encodedSource, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (!TryDecompress(encodedSource, destination.Length, out byte[] decoded))
            return false;
        if (decoded.Length > destination.Length)
            return false;
        decoded.CopyTo(destination);
        bytesWritten = decoded.Length;
        return true;
    }
}
