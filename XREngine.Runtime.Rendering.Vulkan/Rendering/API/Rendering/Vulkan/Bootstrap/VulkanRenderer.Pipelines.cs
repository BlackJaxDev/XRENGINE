namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    /// <summary>
    /// Captures device-owned pipeline persistence diagnostics. This is a cold
    /// inspection API for headless evidence, never a render-path query.
    /// </summary>
    public VulkanPipelineCacheDiagnostic CapturePipelineCacheDiagnostic()
        => _resourceRuntime.PipelineManager.CaptureCacheDiagnostic(
            RequestedRenderTargetMode.ToString(),
            EffectiveRenderTargetMode.ToString());
}
