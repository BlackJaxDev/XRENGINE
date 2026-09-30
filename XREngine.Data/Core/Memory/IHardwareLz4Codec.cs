namespace XREngine.Data;

/// <summary>Optional hardware codec using the engine's existing LZ4 payload representation.</summary>
public interface IHardwareLz4Codec
{
    bool IsAvailable { get; }
    byte[] Compress(ReadOnlySpan<byte> source);
    byte[] Decompress(ReadOnlySpan<byte> compressed);
}
