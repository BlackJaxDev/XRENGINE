namespace XREngine.Rendering;

/// <summary>Desktop WSI target whose native-window lifecycle remains owned by the window host.</summary>
public sealed record DesktopWindowRenderTarget(IRuntimeRenderWindowHost Window) :
    IRendererPresentationTarget,
    IRendererDesktopWindowServices
{
    public RenderExecutionMode ExecutionMode => RenderExecutionMode.DesktopWsi;

    public RendererBackendCapabilities RequiredBackendCapabilities => RendererBackendCapabilities.DesktopPresentation;

    public IRuntimeWindowGlContext? GlContext => Window.DesktopGlContext;
    public IRuntimeWindowVulkanSurface? VulkanSurface => Window.DesktopVulkanSurface;
    public nint PlatformWindowHandle => Window.PlatformWindowHandle;
    public nint OperatingSystemWindowHandle => Window.OperatingSystemWindowHandle;

    public RenderTargetOutputProperties? OutputProperties => null;

    public void Validate() => ArgumentNullException.ThrowIfNull(Window);
}
