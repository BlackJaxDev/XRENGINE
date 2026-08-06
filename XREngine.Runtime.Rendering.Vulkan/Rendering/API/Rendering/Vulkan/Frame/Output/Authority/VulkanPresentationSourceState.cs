namespace XREngine.Rendering.Vulkan;

/// <summary>Owns the logical desktop presentation-source publication and fallback target.</summary>
internal sealed class VulkanPresentationSourceState
{
    internal XRTexture? ColorTexture;
    internal XRFrameBuffer? FrameBuffer;
    internal XRTexture? FallbackFrameBufferTexture;
    internal XRFrameBuffer? FallbackFrameBuffer;
    internal FrameOpContext? FrameOpContext;
    internal VulkanPresentationSourcePublication Publication { get; } = new();
}
