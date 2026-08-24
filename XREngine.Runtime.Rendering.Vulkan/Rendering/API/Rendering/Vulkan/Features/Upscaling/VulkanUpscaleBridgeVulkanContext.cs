using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable Vulkan facts and commands required by a native upscale bridge session.
/// This deliberately excludes the renderer facade and unrelated renderer authorities.
/// </summary>
internal sealed unsafe class VulkanUpscaleBridgeVulkanContext(
    Vk api,
    Instance instance,
    PhysicalDevice physicalDevice,
    Device device,
    uint graphicsQueueFamilyIndex,
    uint streamlineGraphicsQueueIndex,
    uint streamlineComputeQueueIndex,
    uint streamlineOpticalFlowQueueIndex)
{
    public Vk Api { get; } = api;
    public Instance Instance { get; } = instance;
    public PhysicalDevice PhysicalDevice { get; } = physicalDevice;
    public Device Device { get; } = device;
    public uint GraphicsQueueFamilyIndex { get; } = graphicsQueueFamilyIndex;
    public uint StreamlineGraphicsQueueIndex { get; } = streamlineGraphicsQueueIndex;
    public uint StreamlineComputeQueueIndex { get; } = streamlineComputeQueueIndex;
    public uint StreamlineOpticalFlowQueueIndex { get; } = streamlineOpticalFlowQueueIndex;

    /// <summary>Records the single image-layout operation native upscalers require.</summary>
    public void TransitionImageLayout(
        CommandBuffer commandBuffer,
        VulkanUpscaleBridgeSharedImage image,
        ImageLayout newLayout,
        PipelineStageFlags dstStage,
        AccessFlags dstAccessMask)
    {
        ImageLayout oldLayout = image.CurrentLayout;
        if (oldLayout == newLayout)
            return;

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = ResolveAccessMask(oldLayout),
            DstAccessMask = dstAccessMask,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.VulkanImage,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = image.AspectMask,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        ImageMemoryBarrier* barrierPtr = stackalloc ImageMemoryBarrier[1];
        barrierPtr[0] = barrier;
        Api.CmdPipelineBarrier(commandBuffer, ResolvePipelineStage(oldLayout), dstStage, DependencyFlags.None, 0, null, 0, null, 1, barrierPtr);
        image.CurrentLayout = newLayout;
    }

    private static PipelineStageFlags ResolvePipelineStage(ImageLayout layout)
        => layout switch
        {
            ImageLayout.Undefined => PipelineStageFlags.TopOfPipeBit,
            ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
            ImageLayout.ColorAttachmentOptimal => PipelineStageFlags.ColorAttachmentOutputBit,
            ImageLayout.DepthStencilAttachmentOptimal or ImageLayout.DepthAttachmentOptimal => PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags.FragmentShaderBit,
            _ => PipelineStageFlags.AllCommandsBit,
        };

    private static AccessFlags ResolveAccessMask(ImageLayout layout)
        => layout switch
        {
            ImageLayout.Undefined => 0,
            ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
            ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
            ImageLayout.ColorAttachmentOptimal => AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
            ImageLayout.DepthStencilAttachmentOptimal or ImageLayout.DepthAttachmentOptimal => AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
            ImageLayout.DepthStencilReadOnlyOptimal or ImageLayout.DepthReadOnlyOptimal => AccessFlags.DepthStencilAttachmentReadBit,
            ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
            _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
        };
}
