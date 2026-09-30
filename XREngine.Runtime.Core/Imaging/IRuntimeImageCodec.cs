namespace XREngine.Imaging;

/// <summary>Decodes source images and encodes cooked pixels for authoring and capture.</summary>
public interface IRuntimeImageCodec
{
    RuntimeImage Decode(ReadOnlyMemory<byte> encodedImage);

    IReadOnlyList<RuntimeImage> DecodeFrames(ReadOnlyMemory<byte> encodedImage);

    byte[] EncodePng(RuntimeImage image);

    byte[] Encode(RuntimeImage image, RuntimeImageFileFormat format, int quality, bool srgb);

    RuntimeImage Resize(RuntimeImage image, uint width, uint height, RuntimeImageResizeMode mode);

    RuntimeImage ReprojectEquirectangularToCubeCross(RuntimeImage image);
}
