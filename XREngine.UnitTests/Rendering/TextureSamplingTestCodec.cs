using XREngine.Data.Rendering;
using XREngine.Imaging;

namespace XREngine.UnitTests.Rendering;

/// <summary>Returns constant RGBA pixels to isolate cache settings from image codecs.</summary>
internal sealed class TextureSamplingTestCodec : IRuntimeImageCodec
{
    public int DecodeCount { get; private set; }

    public RuntimeImage Decode(ReadOnlyMemory<byte> encodedImage)
    {
        if (!encodedImage.Span.SequenceEqual(new byte[] { 17, 31 }))
            throw new InvalidDataException("Unexpected test image bytes.");
        DecodeCount++;
        return CreateImage(128, 64);
    }

    public static RuntimeImage CreateImage(uint width, uint height)
    {
        byte[] pixels = new byte[checked((int)(width * height * 4))];
        Array.Fill(pixels, (byte)73);
        return new RuntimeImage(width, height, EPixelFormat.Rgba, EPixelType.UnsignedByte, pixels);
    }

    public RuntimeImage Resize(RuntimeImage image, uint width, uint height, RuntimeImageResizeMode mode)
        => CreateImage(width, height);

    public IReadOnlyList<RuntimeImage> DecodeFrames(ReadOnlyMemory<byte> encodedImage) => throw new NotSupportedException();
    public byte[] EncodePng(RuntimeImage image) => throw new NotSupportedException();
    public byte[] Encode(RuntimeImage image, RuntimeImageFileFormat format, int quality, bool srgb) => throw new NotSupportedException();
    public RuntimeImage ReprojectEquirectangularToCubeCross(RuntimeImage image) => throw new NotSupportedException();
}
