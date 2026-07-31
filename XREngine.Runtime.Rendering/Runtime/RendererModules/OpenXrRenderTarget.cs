namespace XREngine.Rendering;

/// <summary>OpenXR compositor target with runtime-owned swapchains and a fixed view-family output contract.</summary>
public sealed record OpenXrRenderTarget(RenderTargetOutputProperties Properties) : IRendererPresentationTarget
{
    public RenderExecutionMode ExecutionMode => RenderExecutionMode.OpenXr;
    public RendererBackendCapabilities RequiredBackendCapabilities => RendererBackendCapabilities.OpenXrPresentation;
    public RenderTargetOutputProperties? OutputProperties => Properties;
    public void Validate() => Properties.Validate();
}
