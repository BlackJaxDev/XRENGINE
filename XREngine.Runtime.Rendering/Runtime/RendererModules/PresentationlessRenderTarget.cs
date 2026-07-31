using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Fixed, engine-owned offscreen output contract. No native window, surface, swapchain,
/// acquire, or present operation is implied by this target.
/// </summary>
public sealed record PresentationlessRenderTarget(
    uint Width,
    uint Height,
    uint Layers = 1,
    uint FrameSlotCount = 3,
    uint SampleCount = 1,
    EPixelInternalFormat ColorFormat = EPixelInternalFormat.Rgba8,
    EPixelInternalFormat DepthFormat = EPixelInternalFormat.Depth24Stencil8) : IRendererPresentationTarget
{
    public RenderExecutionMode ExecutionMode => RenderExecutionMode.Presentationless;

    public RendererBackendCapabilities RequiredBackendCapabilities => RendererBackendCapabilities.PresentationlessRendering;

    public RenderTargetOutputProperties? OutputProperties
        => new(Width, Height, Layers, ColorFormat, DepthFormat, "Linear", SampleCount, FrameSlotCount);

    /// <summary>Throws when a target cannot represent a valid deterministic output.</summary>
    public void Validate()
    {
        OutputProperties!.Value.Validate();
    }
}
