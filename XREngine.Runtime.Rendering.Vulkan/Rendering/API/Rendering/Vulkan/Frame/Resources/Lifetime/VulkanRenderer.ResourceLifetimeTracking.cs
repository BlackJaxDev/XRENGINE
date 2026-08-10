using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Compatibility entry points at the renderer boundary. Command state is owned by
/// <see cref="VulkanCommandRuntime"/> and resource lifetime state is owned by
/// <see cref="VulkanResourceRuntime"/>; this partial performs no lifetime bookkeeping.
/// </summary>
public unsafe partial class VulkanRenderer
{
    private System.Collections.Concurrent.ConcurrentDictionary<ulong, VulkanCommandBufferTrackingBatch>
        _commandBufferTrackingBatches
        => _commandRuntime.CommandBuffers.TrackingBatches;

    internal ulong GetCurrentVulkanResourceGeneration(ObjectType type, ulong handle)
        => ResourceRuntime.GetPublishedGeneration(type, handle);

    internal bool TryGetBufferViewBackingBuffer(
        BufferView bufferView,
        out Silk.NET.Vulkan.Buffer buffer)
        => ResourceRuntime.TryGetBufferViewBackingBuffer(bufferView, out buffer);

    internal void RegisterVulkanResource(
        ObjectType type,
        ulong handle,
        string owner,
        bool externallyOwned = false)
        => ResourceRuntime.RegisterResource(type, handle, owner, externallyOwned);

    internal void RegisterVulkanPipeline(Pipeline pipeline, string owner)
        => ResourceRuntime.RegisterResource(ObjectType.Pipeline, pipeline.Handle, owner);

    private void RegisterVulkanImageViewResource(
        ImageView imageView,
        Image backingImage,
        string owner,
        bool backingImageExternallyOwned)
        => ResourceRuntime.RegisterImageViewResource(
            imageView,
            backingImage,
            owner,
            backingImageExternallyOwned);

    internal Result CreateVulkanImageTracked(
        ref ImageCreateInfo createInfo,
        Image* image,
        string owner)
    {
        ThrowIfVulkanDeviceOperationNotAdmitted("vkCreateImage." + owner);
        ThrowIfPersistentResourceAllocationDuringRecording(owner);
        Result result = ResourceRuntime.CreateImageTracked(
            Api!,
            _deviceContext.Device,
            ref createInfo,
            image,
            owner);
        if (result == Result.Success && image is not null && image->Handle != 0)
            _commandRuntime.RegisterTrackedImageInitialLayouts(*image, in createInfo);
        return result;
    }

    internal Result CreateVulkanImageTracked(
        ref ImageCreateInfo createInfo,
        out Image image,
        string owner)
    {
        image = default;
        fixed (Image* imagePointer = &image)
            return CreateVulkanImageTracked(ref createInfo, imagePointer, owner);
    }

    internal void DestroyVulkanImageImmediateTracked(Image image, string owner)
        => ResourceRuntime.DestroyImageImmediateTracked(
            Api!,
            _deviceContext.Device,
            _commandRuntime,
            image,
            owner);

    internal void RegisterVulkanFramebuffer(
        Framebuffer framebuffer,
        ReadOnlySpan<ImageView> attachments,
        string owner)
        => ResourceRuntime.RegisterFramebuffer(framebuffer, attachments, owner);

    private void EnsureVulkanImageViewAvailableForCommandRecording(
        CommandBuffer commandBuffer,
        ImageView imageView,
        string owner,
        ulong expectedGeneration = 0)
        => _commandRuntime.EnsureImageViewAvailableForCommandRecording(
            commandBuffer,
            imageView,
            owner,
            expectedGeneration);

    internal Result ResetVulkanCommandBufferTracked(CommandBuffer commandBuffer)
        => _commandRuntime.ResetTrackedCommandBuffer(commandBuffer);

    internal void TrackVulkanCommandBufferResource(
        CommandBuffer commandBuffer,
        ObjectType type,
        ulong handle,
        string owner)
        => _commandRuntime.TrackVulkanCommandBufferResource(commandBuffer, type, handle, owner);

    private Result AllocateVulkanCommandBuffersTracked(
        ref CommandBufferAllocateInfo allocateInfo,
        CommandBuffer* commandBuffers,
        string owner = "CommandBuffer.Allocation")
        => _commandRuntime.AllocateCommandBuffersWithLifetime(
            ref allocateInfo,
            commandBuffers,
            owner);

    private Result AllocateVulkanCommandBuffersTracked(
        ref CommandBufferAllocateInfo allocateInfo,
        out CommandBuffer commandBuffer,
        string owner = "CommandBuffer.Allocation")
        => _commandRuntime.AllocateCommandBufferWithLifetime(
            ref allocateInfo,
            out commandBuffer,
            owner);

    private void FreeVulkanCommandBuffersTracked(
        CommandPool commandPool,
        uint commandBufferCount,
        CommandBuffer* commandBuffers,
        string owner)
        => _commandRuntime.FreeCommandBuffersWithLifetime(
            CurrentDesktopFrameSlot,
            commandPool,
            commandBufferCount,
            commandBuffers,
            owner);

    private void FreeVulkanCommandBufferTracked(
        CommandPool commandPool,
        ref CommandBuffer commandBuffer,
        string owner)
    {
        fixed (CommandBuffer* commandBufferPointer = &commandBuffer)
        {
            _commandRuntime.FreeCommandBuffersWithLifetime(
                CurrentDesktopFrameSlot,
                commandPool,
                1,
                commandBufferPointer,
                owner);
        }
    }

    private void CmdCopyBufferTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Silk.NET.Vulkan.Buffer destination,
        uint regionCount,
        BufferCopy* regions)
        => _commandRuntime.CmdCopyBufferTracked(
            commandBuffer,
            source,
            destination,
            regionCount,
            regions);

    internal void CmdCopyBufferToImageTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        BufferImageCopy* regions)
        => _commandRuntime.CopyBufferToImageTracked(
            commandBuffer,
            source,
            destination,
            destinationLayout,
            regionCount,
            regions);

    internal void CmdCopyBufferToImageTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ref BufferImageCopy region)
        => _commandRuntime.CopyBufferToImageTracked(
            commandBuffer,
            source,
            destination,
            destinationLayout,
            regionCount,
            ref region);

    private void CmdCopyImageToBufferTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Silk.NET.Vulkan.Buffer destination,
        uint regionCount,
        BufferImageCopy* regions)
        => _commandRuntime.CopyImageToBufferTracked(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            regionCount,
            regions);

    private void CmdResolveImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ImageResolve* regions)
        => _commandRuntime.ResolveImageTracked(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            regions);

    internal void CmdBlitImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ImageBlit* regions,
        Filter filter)
        => _commandRuntime.BlitImageTracked(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            regions,
            filter);

    internal void CmdBlitImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ref ImageBlit region,
        Filter filter)
    {
        fixed (ImageBlit* regionPointer = &region)
        {
            _commandRuntime.BlitImageTracked(
                commandBuffer,
                source,
                sourceLayout,
                destination,
                destinationLayout,
                regionCount,
                regionPointer,
                filter);
        }
    }

    internal void RegisterVulkanDescriptorSet(
        DescriptorPool pool,
        DescriptorSet descriptorSet,
        bool usesUpdateAfterBind,
        string owner,
        uint setIndex = 0,
        IReadOnlyList<DescriptorBindingInfo>? reflectedBindings = null)
        => _commandRuntime.RegisterDescriptorSet(
            pool,
            descriptorSet,
            usesUpdateAfterBind,
            owner,
            setIndex,
            reflectedBindings);

    internal void RegisterVulkanDescriptorSets(
        DescriptorPool pool,
        ReadOnlySpan<DescriptorSet> descriptorSets,
        bool usesUpdateAfterBind,
        string owner,
        IReadOnlyList<DescriptorBindingInfo>? reflectedBindings = null)
        => _commandRuntime.RegisterDescriptorSets(
            pool,
            descriptorSets,
            usesUpdateAfterBind,
            owner,
            reflectedBindings);

    private bool TryRecordQueueOwnershipTransferRequirement(
        CommandBuffer commandBuffer,
        in VulkanQueueOwnershipTransferRequirement requirement)
        => _commandRuntime.TryRecordQueueOwnershipTransfer(commandBuffer, requirement);

    private bool TryRecordImageAccessDelta(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        ImageLayout layout,
        PipelineStageFlags stageMask,
        AccessFlags accessMask,
        uint queueFamilyIndex)
        => _commandRuntime.TryRecordImageAccess(
            commandBuffer,
            image,
            range,
            layout,
            stageMask,
            accessMask,
            queueFamilyIndex);

    private bool TryRecordExternalImageOwnershipDelta(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        EVulkanExternalImageOwnership ownership)
        => _commandRuntime.TryRecordExternalImageOwnership(
            commandBuffer,
            image,
            range,
            ownership);

    private bool TryGetPendingImageAccessState(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        out VulkanImageAccessState state)
        => _commandRuntime.TryGetPendingImageAccess(
            commandBuffer,
            image,
            range,
            out state);

    internal bool CommandBufferReferencesAllDescriptorSets(
        CommandBuffer commandBuffer,
        ReadOnlySpan<DescriptorSet> descriptorSets,
        out ulong missingDescriptorSet)
        => _commandRuntime.CommandBufferReferencesAllDescriptorSets(
            commandBuffer,
            descriptorSets,
            out missingDescriptorSet);

    private bool ValidateVulkanSubmissionResourceLifetimes(
        ref SubmitInfo submitInfo,
        in VulkanSubmissionDiagnosticContext diagnosticContext,
        out string failureReason,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage)
        => _commandRuntime.ValidateSubmissionResourceLifetimes(
            ref submitInfo,
            in diagnosticContext,
            out failureReason,
            out injectedFailureStage);

    private VulkanLifetimeSubmission RecordSuccessfulVulkanSubmissionLifetime(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        in VulkanSubmissionDiagnosticContext diagnosticContext)
        => _commandRuntime.RecordSuccessfulSubmissionLifetime(
            queue,
            ref submitInfo,
            fence,
            in diagnosticContext);

    private void ReleaseVulkanSubmissionResourceLifetimePins(ref SubmitInfo submitInfo)
        => _commandRuntime.ReleaseSubmissionResourceLifetimePins(ref submitInfo);

    internal void NotifyVulkanFenceCompleted(Fence fence)
        => _commandRuntime.CompleteTrackedFence(fence);

    private void NotifyVulkanTimelineCompleted(Silk.NET.Vulkan.Semaphore semaphore, ulong value)
        => _commandRuntime.CompleteTrackedTimeline(semaphore, value);

    private void NotifyVulkanQueueIdle(Queue queue)
        => _commandRuntime.CompleteTrackedQueue(queue);

    private void NotifyVulkanDeviceIdle()
        => _commandRuntime.CompleteTrackedDevice();

    private void NotifyVulkanResourceLifetimeDeviceLost()
        => _commandRuntime.MarkTrackedDeviceLost();

    internal void NotifyVulkanResourceUseCompleted(ObjectType type, ulong handle)
        => ResourceRuntime.NotifyResourceUseCompleted(type, handle);

    internal VulkanRetirementTicket CaptureVulkanRetirementTicket(
        ObjectType type,
        ulong handle,
        string owner)
    {
        VulkanResourceLifetimeKey key = new(type, handle);
        return ResourceRuntime.CaptureRetirementTicket(_commandRuntime, key, owner);
    }

    private VulkanRetirementTicket CaptureVulkanRetirementWatermark()
        => ResourceRuntime.CaptureRetirementWatermark();

    private bool IsVulkanRetirementReady(in VulkanRetirementTicket ticket)
        => ResourceRuntime.IsRetirementReady(ticket);

    private bool TryBeginDestroyVulkanResourceGeneration(
        ObjectType type,
        ulong handle,
        ulong expectedGeneration,
        string owner)
        => ResourceRuntime.TryBeginDestroyResourceGeneration(
            type,
            handle,
            expectedGeneration,
            owner);

    private void CompleteVulkanResourceDestruction(ObjectType type, ulong handle)
        => ResourceRuntime.CompleteResourceDestruction(type, handle);

    private void ReactivateVulkanResourceAfterRetirement(
        ObjectType type,
        ulong handle,
        string owner)
        => ResourceRuntime.ReactivateResourceAfterRetirement(type, handle, owner);

    private void BeginForcedVulkanRetirementDrain()
        => ResourceRuntime.BeginForcedRetirementDrain();

    private void EndForcedVulkanRetirementDrain()
        => ResourceRuntime.EndForcedRetirementDrain();

    internal VulkanResourceLifetimeSnapshot GetVulkanResourceLifetimeSnapshot(
        bool includeExactLiveResourceGenerations = false)
        => ResourceRuntime.CaptureLifetimeSnapshot(includeExactLiveResourceGenerations);

    private void LogVulkanResourceLifetimeDiagnostics(string reason)
        => ResourceRuntime.LogLifetimeDiagnostics(reason);

    private static string DescribeVulkanRetirementTicket(
        in VulkanRetirementTicket ticket)
        => $"gfx:{ticket.GraphicsSequence}/transfer:{ticket.TransferSequence}/other:{ticket.OtherSequence}/generation:{ticket.ResourceGeneration}/external:{ticket.ExternalOwnershipPending}/pins:{ticket.PinSet?.Count ?? 0}";
}
