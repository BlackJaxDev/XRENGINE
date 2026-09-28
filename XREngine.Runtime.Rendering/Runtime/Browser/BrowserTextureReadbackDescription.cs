namespace XREngine.Rendering;

/// <summary>One RGBA8 or BGRA8 color mip rectangle, returned in native channel order with tightly packed rows.</summary>
public readonly record struct BrowserTextureReadbackDescription(int TextureHandle, int MipLevel,
    int X, int Y, int Width, int Height)
{
    public int ByteLength => checked(Width * Height * 4);

    public void Validate()
    {
        _ = BrowserResourceHandle.FromPacked(TextureHandle);
        if (MipLevel < 0 || MipLevel > 31 || X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
            (long)Width * Height > BrowserBufferReadbackDescription.MaximumByteLength / 4)
            throw new ArgumentOutOfRangeException(nameof(Width), "Readback requires a positive bounded color texture rectangle.");
    }
}
