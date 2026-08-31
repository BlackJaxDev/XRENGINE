namespace XREngine.Rendering.Vulkan;

public sealed unsafe partial class VulkanExplicitTargetRendererHost
{
    /// <summary>Captures real native/cache provenance for a presentationless pipeline cohort.</summary>
    public VulkanPipelineCacheDiagnostic CapturePipelineCacheDiagnostic()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.CapturePipelineCacheDiagnostic();
    }
}
