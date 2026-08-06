using Silk.NET.Vulkan;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe class VulkanUpscaleBridgeFrameSlot(
    ulong allocationGeneration,
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
    private int _vulkanResourcesDestroyed;

    /// <summary>Identifies the sidecar-device allocation transaction that owns this slot.</summary>
    public ulong AllocationGeneration { get; } = allocationGeneration;
    /// <summary>Identifies the sidecar-device publication transaction that made this slot visible.</summary>
    public ulong PublicationGeneration { get; private set; }
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
    public ulong CommandBufferAllocationGeneration => AllocationGeneration;
    public ulong SubmitFenceAllocationGeneration => AllocationGeneration;
    public ulong CommandBufferPublicationGeneration => PublicationGeneration;
    public ulong SubmitFencePublicationGeneration => PublicationGeneration;

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

    internal void Publish(ulong publicationGeneration)
    {
        if (publicationGeneration == 0 || publicationGeneration != AllocationGeneration)
            throw new InvalidOperationException("A bridge frame slot can only be published by its owning sidecar allocation generation.");

        SourceColor.Publish(publicationGeneration);
        SourceDepth.Publish(publicationGeneration);
        SourceMotion.Publish(publicationGeneration);
        Exposure.Publish(publicationGeneration);
        OutputColor.Publish(publicationGeneration);
        ReadySemaphore.Publish(publicationGeneration);
        CompleteSemaphore.Publish(publicationGeneration);
        PublicationGeneration = publicationGeneration;
    }

    internal void DestroyVulkanResources(Vk api, Device device, CommandPool commandPool)
    {
        if (Interlocked.Exchange(ref _vulkanResourcesDestroyed, 1) != 0)
            return;

        if (SubmitFence.Handle != 0)
            api.DestroyFence(device, SubmitFence, null);

        CompleteSemaphore.DestroyVulkanResources(api, device);
        ReadySemaphore.DestroyVulkanResources(api, device);
        OutputColor.DestroyVulkanResources(api, device);
        Exposure.DestroyVulkanResources(api, device);
        SourceMotion.DestroyVulkanResources(api, device);
        SourceDepth.DestroyVulkanResources(api, device);
        SourceColor.DestroyVulkanResources(api, device);

        if (CommandBuffer.Handle != 0 && commandPool.Handle != 0)
        {
            CommandBuffer commandBuffer = CommandBuffer;
            api.FreeCommandBuffers(device, commandPool, 1, &commandBuffer);
        }
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
