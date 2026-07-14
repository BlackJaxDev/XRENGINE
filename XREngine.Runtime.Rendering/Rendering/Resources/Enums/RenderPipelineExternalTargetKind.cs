namespace XREngine.Rendering.Resources;

/// <summary>
/// Classifies the externally owned output imported by a pipeline instance. The
/// class, view count, and view index are generation-key data; the concrete
/// swapchain image or caller FBO is bound separately for the current frame.
/// </summary>
public enum RenderPipelineExternalTargetKind
{
    None,
    Window,
    CallerProvidedFrameBuffer,
    ExternalSwapchain,
}
