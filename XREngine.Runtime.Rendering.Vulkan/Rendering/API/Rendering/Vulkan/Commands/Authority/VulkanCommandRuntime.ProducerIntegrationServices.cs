using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Narrow command-authority entry points for renderer-owned producers.</summary>
internal sealed unsafe partial class VulkanCommandRuntime
{
    internal ImageLayout[]? QueryCurrentAttachmentLayoutsForProducer(
        XRFrameBuffer target,
        VkFrameBuffer frameBuffer)
        => QueryCurrentAttachmentLayouts(target, frameBuffer);

    internal bool RecordPreparedLegacyContext(ref VulkanCommandRecordingContext context)
    {
        Recorder.EnterRecordingScope();
        try
        {
            return RecordCommandBufferLifecycle(ref context);
        }
        finally
        {
            Recorder.ExitRecordingScope();
        }
    }

    internal void InvalidateDescriptorBindings(CommandBuffer commandBuffer)
        => InvalidateDescriptorSetBindingState(commandBuffer);

    internal void CopyPreparedUploadBufferToImage(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout destinationLayout,
        ref BufferImageCopy region)
    {
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Buffer, source.Handle);
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, destination.Handle);
        Api.CmdCopyBufferToImage(
            commandBuffer,
            source,
            destination,
            destinationLayout,
            1,
            ref region);
    }

    internal void RetireUploadBuffer(
        Silk.NET.Vulkan.Buffer buffer,
        DeviceMemory memory,
        string owner)
        => ResourceRuntime.Buffers.Retire(buffer, memory, owner);

    internal void PipelineBarrier2Tracked(
        CommandBuffer commandBuffer,
        DependencyInfo* dependencyInfo)
    {
        if (dependencyInfo is null)
            throw new ArgumentNullException(nameof(dependencyInfo));

        for (uint index = 0; index < dependencyInfo->BufferMemoryBarrierCount; index++)
        {
            BufferMemoryBarrier2 barrier = dependencyInfo->PBufferMemoryBarriers[index];
            PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Buffer, barrier.Buffer.Handle);
        }
        for (uint index = 0; index < dependencyInfo->ImageMemoryBarrierCount; index++)
        {
            ImageMemoryBarrier2 barrier = dependencyInfo->PImageMemoryBarriers[index];
            PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, barrier.Image.Handle);
        }

        if (DeviceContext.InstanceApiVersion >= Vk.Version13)
        {
            Api.CmdPipelineBarrier2(commandBuffer, dependencyInfo);
            return;
        }

        if (DeviceContext.ExtensionFunctions.KhrSynchronization2 is not { } synchronization2)
            throw new InvalidOperationException("VK_KHR_synchronization2 command extension is unavailable.");
        synchronization2.CmdPipelineBarrier2(commandBuffer, dependencyInfo);
    }

    internal static bool FrameDiagnosticsTraceEnabled =>
        CommandRecordingDiagnosticsEnabled ||
        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||
        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw;
}
