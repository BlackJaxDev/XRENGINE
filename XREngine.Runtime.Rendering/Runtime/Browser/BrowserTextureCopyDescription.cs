namespace XREngine.Rendering;

/// <summary>One non-overlapping, single-mip RGBA8 texture-to-texture copy rectangle.</summary>
public readonly record struct BrowserTextureCopyDescription(
    int SourceHandle, int DestinationHandle,
    int SourceX, int SourceY, int DestinationX, int DestinationY,
    int Width, int Height)
{
    public void Validate()
    {
        _ = BrowserResourceHandle.FromPacked(SourceHandle);
        _ = BrowserResourceHandle.FromPacked(DestinationHandle);
        if (SourceHandle == DestinationHandle || SourceX < 0 || SourceY < 0 || DestinationX < 0 || DestinationY < 0
            || Width <= 0 || Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(BrowserTextureCopyDescription),
                "Copies require distinct textures, nonnegative origins and a positive extent.");
    }
}
