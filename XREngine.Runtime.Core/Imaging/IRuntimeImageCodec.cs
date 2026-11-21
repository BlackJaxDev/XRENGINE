namespace XREngine.Imaging;

/// <summary>Decodes source images and encodes cooked pixels for authoring and capture.</summary>
public interface IRuntimeImageCodec
{
    /// <summary>Decodes an image. Use the source path when the format needs an explicit hint.</summary>
    RuntimeImage Decode(ReadOnlyMemory<byte> encodedImage, string? sourcePath = null);

    /// <summary>Fully decodes and checks an image without exporting pixels. Leaves the input stream open.</summary>
    (uint Width, uint Height) Validate(Stream encodedImage, string? sourcePath = null);

    IReadOnlyList<RuntimeImage> DecodeFrames(ReadOnlyMemory<byte> encodedImage, string? sourcePath = null);

    byte[] EncodePng(RuntimeImage image);

    byte[] Encode(RuntimeImage image, RuntimeImageFileFormat format, int quality, bool srgb);

    /// <summary>Resamples to the exact positive dimensions supplied by the caller.</summary>
    RuntimeImage Resize(RuntimeImage image, uint width, uint height, RuntimeImageResizeMode mode);

    RuntimeImage ReprojectEquirectangularToCubeCross(RuntimeImage image);
}
