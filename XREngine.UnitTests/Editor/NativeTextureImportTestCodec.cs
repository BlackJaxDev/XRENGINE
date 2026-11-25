using XREngine.Imaging;
using XREngine.Runtime.Imaging.Magick;

namespace XREngine.UnitTests.Editor;

/// <summary>Injects a target change at the real decoder boundary.</summary>
internal sealed class NativeTextureImportTestCodec : IRuntimeImageCodec
{
    private readonly MagickRuntimeImageCodec _inner = new();
    internal Action? BeforeDecode { get; set; }
    internal int DecodeCalls { get; private set; }
    public RuntimeImage Decode(ReadOnlyMemory<byte> bytes, string? sourcePath = null)
    {
        DecodeCalls++;
        BeforeDecode?.Invoke();
        return _inner.Decode(bytes, sourcePath);
    }
    public (uint Width, uint Height) Validate(Stream encodedImage, string? sourcePath = null) => _inner.Validate(encodedImage, sourcePath);
    public IReadOnlyList<RuntimeImage> DecodeFrames(ReadOnlyMemory<byte> bytes, string? sourcePath = null) => _inner.DecodeFrames(bytes, sourcePath);
    public byte[] EncodePng(RuntimeImage image) => _inner.EncodePng(image);
    public byte[] Encode(RuntimeImage image, RuntimeImageFileFormat format, int quality, bool srgb) => _inner.Encode(image, format, quality, srgb);
    public RuntimeImage Resize(RuntimeImage image, uint width, uint height, RuntimeImageResizeMode mode) => _inner.Resize(image, width, height, mode);
    public RuntimeImage ReprojectEquirectangularToCubeCross(RuntimeImage image) => _inner.ReprojectEquirectangularToCubeCross(image);
}
