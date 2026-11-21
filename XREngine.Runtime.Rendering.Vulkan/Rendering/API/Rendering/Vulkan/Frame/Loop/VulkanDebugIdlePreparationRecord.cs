namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies one idle debug warmup in an active viewport generation.</summary>
internal struct VulkanDebugIdlePreparationRecord
{
    public Action? Emitter;
    public XRRenderPipelineInstance? Pipeline;
    public XRViewport? Viewport;
    public int ResourceGeneration;
    public XRFrameBuffer? Target;
    public int PassIndex;
}
