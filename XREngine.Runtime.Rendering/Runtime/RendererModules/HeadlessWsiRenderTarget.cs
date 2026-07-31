namespace XREngine.Rendering;

/// <summary>Headless-surface WSI target. Acquire and present remain part of this contract.</summary>
public sealed record HeadlessWsiRenderTarget(RenderTargetOutputProperties Properties) : IRendererPresentationTarget
{
    public RenderExecutionMode ExecutionMode => RenderExecutionMode.HeadlessWsi;
    public RendererBackendCapabilities RequiredBackendCapabilities => RendererBackendCapabilities.HeadlessWsiPresentation;
    public RenderTargetOutputProperties? OutputProperties => Properties;
    public void Validate() => Properties.Validate();
}
