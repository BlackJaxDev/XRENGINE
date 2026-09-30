namespace XREngine.Runtime.Bootstrap;

public class UnitTestingRenderSettings
{
    public ERenderLibrary RenderBackend { get; set; } = ERenderLibrary.OpenGL;
    public RenderBackendFallbackPolicy BackendFallbackPolicy { get; set; } = RenderBackendFallbackPolicy.RequireRequested;

    /// <summary>
    /// Scene pipeline created by bootstrap cameras. Explicit selections take precedence over automatic pipeline policy.
    /// </summary>
    public UnitTestingRenderPipeline RenderPipeline { get; set; } = UnitTestingRenderPipeline.AdvancedRenderPipeline;

    /// <summary>
    /// Path to a <c>.xrs</c> render-pipeline script when <see cref="RenderPipeline"/> is <see cref="UnitTestingRenderPipeline.CustomRenderPipeline"/>.
    /// Relative paths are resolved from the process working directory.
    /// </summary>
    public string? CustomRenderPipelineScriptPath { get; set; }
    public UnitTestingOpenGLRenderSettings OpenGL { get; set; } = new();
    public UnitTestingVulkanRenderSettings Vulkan { get; set; } = new();
}
