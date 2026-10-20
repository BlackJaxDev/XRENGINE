namespace XREngine.Rendering;

/// <summary>One texture-array-layer mip rectangle returned as tightly packed native pixels. An omitted format preserves four-byte color/depth callers.</summary>
public readonly record struct BrowserTextureReadbackDescription(int TextureHandle, int MipLevel,
    int X, int Y, int Width, int Height, int ArrayLayer = 0, string Format = "", uint ProducerFrameSequence = 0)
{
    public int BytesPerPixel => Format switch
    {
        "rgba16float" => 8,
        "" or "rgba8unorm" or "rgba8unorm-srgb" or "bgra8unorm" or "bgra8unorm-srgb" or "depth32float" => 4,
        _ => throw new ArgumentException("Texture readback requires RGBA8/BGRA8, RGBA16F or depth32float pixels.", nameof(Format))
    };

    public int ByteLength => checked(Width * Height * BytesPerPixel);

    public void Validate()
    {
        _ = BrowserResourceHandle.FromPacked(TextureHandle);
        int bytesPerPixel = BytesPerPixel;
        if (MipLevel < 0 || MipLevel > 31 || ArrayLayer < 0 || X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
            (long)Width * Height > BrowserBufferReadbackDescription.MaximumByteLength / bytesPerPixel)
            throw new ArgumentOutOfRangeException(nameof(Width), "Readback requires a positive texture rectangle within the 16 MiB byte budget and a nonnegative layer.");
    }
}
