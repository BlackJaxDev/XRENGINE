namespace XREngine.Rendering;

public partial interface IRendererDesktopWindowServices
{
    /// <summary>Gets the desktop window host that owns native window lifecycle and input.</summary>
    IRuntimeRenderWindowHost Window { get; }
    IRuntimeWindowGlContext? GlContext { get; }
    IRuntimeWindowVulkanSurface? VulkanSurface { get; }
    nint PlatformWindowHandle { get; }
    nint OperatingSystemWindowHandle { get; }
}
