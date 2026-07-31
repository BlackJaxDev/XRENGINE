namespace XREngine.Rendering;

/// <summary>Minimal presentationless output used by one isolated component fixture.</summary>
public sealed record ComponentRenderTarget(string Component, RenderTargetOutputProperties Properties) : IRendererPresentationTarget
{
    public RenderExecutionMode ExecutionMode => RenderExecutionMode.Component;
    public RendererBackendCapabilities RequiredBackendCapabilities => RendererBackendCapabilities.PresentationlessRendering;
    public RenderTargetOutputProperties? OutputProperties => Properties;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Component);
        Properties.Validate();
    }
}
