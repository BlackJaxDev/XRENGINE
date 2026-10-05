namespace XREngine.Data;

public interface IHardwareLz4Codec
{
    bool IsAvailable { get; }
    byte[] Compress(ReadOnlySpan<byte> source);
    byte[] Decompress(ReadOnlySpan<byte> compressed);

    /// <summary>
    /// Decompresses into caller-owned storage and returns the bytes written. Backends that can
    /// target a span override this; the default decodes to an array and copies once.
    /// </summary>
    int Decompress(ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        byte[] decoded = Decompress(compressed);
        if (decoded.Length > destination.Length)
            throw new InvalidDataException($"Hardware LZ4 payload of {decoded.Length} bytes exceeds the destination of {destination.Length} bytes.");
        decoded.CopyTo(destination);
        return decoded.Length;
    }
}
