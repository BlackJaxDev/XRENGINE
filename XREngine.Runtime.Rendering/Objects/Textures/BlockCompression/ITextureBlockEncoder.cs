namespace XREngine.Rendering;

/// <summary>
/// Encodes RGBA8 pixels into GPU block-compressed formats at texture cook time.
/// </summary>
/// <remarks>
/// The input is a tightly packed RGBA8 image. The output holds whole 4x4 blocks in
/// row-major block order: 16 bytes per block for BC7 and BC5, 8 bytes for BC4. A
/// partial block at the right or bottom edge repeats the last column or row. The
/// methods are thread safe and can encode several images at the same time.
/// </remarks>
public interface ITextureBlockEncoder
{
    /// <summary>A short description of the encoder and its version, for logs.</summary>
    string Description { get; }

    /// <summary>Encodes the image as BC7.</summary>
    /// <param name="perceptual">True to weight the error by luma (color data); false to weight all channels the same (normal and data maps).</param>
    /// <param name="failure">The reason when the method returns false.</param>
    bool TryEncodeBc7(ReadOnlySpan<byte> rgba, uint width, uint height, bool perceptual, Span<byte> output, out string failure);

    /// <summary>Encodes one channel (0 to 3) of the image as BC4.</summary>
    bool TryEncodeBc4(ReadOnlySpan<byte> rgba, uint width, uint height, uint channel, Span<byte> output, out string failure);

    /// <summary>Encodes two channels (0 to 3) of the image as BC5.</summary>
    bool TryEncodeBc5(ReadOnlySpan<byte> rgba, uint width, uint height, uint channel0, uint channel1, Span<byte> output, out string failure);
}
