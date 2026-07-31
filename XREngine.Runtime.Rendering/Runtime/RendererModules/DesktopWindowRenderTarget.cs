namespace XREngine.Rendering;

/// <summary>Desktop WSI target whose native-window lifecycle remains owned by the window host.</summary>
public sealed record DesktopWindowRenderTarget(IRuntimeRenderWindowHost Window) :
    IRendererPresentationTarget,
    IRendererDesktopWindowServices
{
    public RenderExecutionMode ExecutionMode => RenderExecutionMode.DesktopWsi;

    public RendererBackendCapabilities RequiredBackendCapabilities => RendererBackendCapabilities.DesktopPresentation;

    public RenderTargetOutputProperties? OutputProperties => null;

    public void Validate() => ArgumentNullException.ThrowIfNull(Window);
}
