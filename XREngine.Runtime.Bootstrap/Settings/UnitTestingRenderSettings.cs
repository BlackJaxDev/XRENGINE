namespace XREngine.Runtime.Bootstrap;

public class UnitTestingRenderSettings
{
    public ERenderLibrary RenderBackend { get; set; } = ERenderLibrary.OpenGL;
    public RenderBackendFallbackPolicy BackendFallbackPolicy { get; set; } = RenderBackendFallbackPolicy.RequireRequested;
    public UnitTestingOpenGLRenderSettings OpenGL { get; set; } = new();
    public UnitTestingVulkanRenderSettings Vulkan { get; set; } = new();
}
