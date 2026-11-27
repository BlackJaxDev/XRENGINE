namespace XREngine.Components.Lights;

/// <summary>
/// Selects how <see cref="MirrorCaptureComponent"/> renders planar reflections.
/// </summary>
public enum EMirrorCaptureMode
{
    /// <summary>
    /// Renders reflections into private offscreen capture viewports and samples them
    /// through a projective mirror material. Required for blurry, distorted, throttled,
    /// or transparent-background mirrors.
    /// </summary>
    OffscreenCapture = 0,

    /// <summary>
    /// Renders planar and recursive reflections directly into the active
    /// <c>AdvancedRenderPipeline</c> visibility, depth-stencil, and HDR targets by
    /// using stencil masks and shared GPU scene data.
    /// </summary>
    InPlaceStencil = 1,

    /// <summary>
    /// Renders reflections into private offscreen capture viewports on the graphics queue
    /// and publishes the submitted texture for the same frame's eye render without waiting
    /// for a multi-frame CPU fence round-trip. Supports transparent-background and
    /// layer-filtered VR mirrors with zero frame lag.
    /// </summary>
    SameFrameOffscreen = 2,
}
