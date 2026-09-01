namespace XREngine.Runtime.Bootstrap;

public class UnitTestingRenderSettings
{
    public ERenderLibrary RenderBackend { get; set; } = ERenderLibrary.OpenGL;
    public RenderBackendFallbackPolicy BackendFallbackPolicy { get; set; } = RenderBackendFallbackPolicy.RequireRequested;

    /// <summary>
    /// Uses <see cref="AdvancedRenderPipeline"/> when enabled and <see cref="DefaultRenderPipeline"/> when disabled.
    /// </summary>
    public bool UseAdvancedRenderPipeline { get; set; } = true;
    public UnitTestingOpenGLRenderSettings OpenGL { get; set; } = new();
    public UnitTestingVulkanRenderSettings Vulkan { get; set; } = new();
}
