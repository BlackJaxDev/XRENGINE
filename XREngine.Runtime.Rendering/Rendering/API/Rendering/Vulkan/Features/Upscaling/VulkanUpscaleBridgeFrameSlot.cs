using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe class VulkanUpscaleBridgeFrameSlot(
    int slotIndex,
    VulkanUpscaleBridgeSharedImage sourceColor,
    VulkanUpscaleBridgeSharedImage sourceDepth,
    VulkanUpscaleBridgeSharedImage sourceMotion,
    VulkanUpscaleBridgeSharedImage exposure,
    VulkanUpscaleBridgeSharedImage outputColor,
    VulkanUpscaleBridgeSharedSemaphore readySemaphore,
    VulkanUpscaleBridgeSharedSemaphore completeSemaphore,
    CommandBuffer commandBuffer,
    Fence submitFence) : IDisposable
{
    private bool _disposed;

    public int SlotIndex { get; } = slotIndex;
    public VulkanUpscaleBridgeSharedImage SourceColor { get; } = sourceColor;
    public VulkanUpscaleBridgeSharedImage SourceDepth { get; } = sourceDepth;
    public VulkanUpscaleBridgeSharedImage SourceMotion { get; } = sourceMotion;
    public VulkanUpscaleBridgeSharedImage Exposure { get; } = exposure;
    public VulkanUpscaleBridgeSharedImage OutputColor { get; } = outputColor;
    public VulkanUpscaleBridgeSharedSemaphore ReadySemaphore { get; } = readySemaphore;
    public VulkanUpscaleBridgeSharedSemaphore CompleteSemaphore { get; } = completeSemaphore;
    public CommandBuffer CommandBuffer { get; } = commandBuffer;
    public Fence SubmitFence { get; } = submitFence;

    public XRTexture2D SourceColorTexture => SourceColor.Texture;
    public XRTexture2D SourceDepthTexture => SourceDepth.Texture;
    public XRTexture2D SourceMotionTexture => SourceMotion.Texture;
    public XRTexture2D ExposureTexture => Exposure.Texture;
    public XRTexture2D OutputColorTexture => OutputColor.Texture;

    public XRFrameBuffer SourceColorFrameBuffer => SourceColor.FrameBuffer;
    public XRFrameBuffer SourceDepthFrameBuffer => SourceDepth.FrameBuffer;
    public XRFrameBuffer SourceMotionFrameBuffer => SourceMotion.FrameBuffer;
    public XRFrameBuffer ExposureFrameBuffer => Exposure.FrameBuffer;
    public XRFrameBuffer OutputColorFrameBuffer => OutputColor.FrameBuffer;

    public uint GlReadySemaphore => ReadySemaphore.GlSemaphore;
    public uint GlCompleteSemaphore => CompleteSemaphore.GlSemaphore;

    internal void DestroyVulkanResources(Vk api, Device device)
    {
        if (SubmitFence.Handle != 0)
            api.DestroyFence(device, SubmitFence, null);

        CompleteSemaphore.DestroyVulkanResources(api, device);
        ReadySemaphore.DestroyVulkanResources(api, device);
        OutputColor.DestroyVulkanResources(api, device);
        Exposure.DestroyVulkanResources(api, device);
        SourceMotion.DestroyVulkanResources(api, device);
        SourceDepth.DestroyVulkanResources(api, device);
        SourceColor.DestroyVulkanResources(api, device);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CompleteSemaphore.Dispose();
        ReadySemaphore.Dispose();
        OutputColor.Dispose();
        Exposure.Dispose();
        SourceMotion.Dispose();
        SourceDepth.Dispose();
        SourceColor.Dispose();
    }
}
