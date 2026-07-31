using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Fixed output properties used by non-window renderer targets.</summary>
public readonly record struct RenderTargetOutputProperties(
    uint Width,
    uint Height,
    uint Layers = 1,
    EPixelInternalFormat ColorFormat = EPixelInternalFormat.Rgba8,
    EPixelInternalFormat DepthFormat = EPixelInternalFormat.Depth24Stencil8,
    string ColorSpace = "Linear",
    uint SampleCount = 1,
    uint FrameSlotCount = 3)
{
    public void Validate()
    {
        if (Width == 0 || Height == 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Render-target dimensions must be non-zero.");
        if (Layers == 0 || FrameSlotCount == 0)
            throw new ArgumentOutOfRangeException(nameof(Layers), "Layer and frame-slot counts must be non-zero.");
        if (SampleCount == 0 || (SampleCount & (SampleCount - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(SampleCount), "Sample count must be a non-zero power of two.");
        ArgumentException.ThrowIfNullOrWhiteSpace(ColorSpace);
    }
}
