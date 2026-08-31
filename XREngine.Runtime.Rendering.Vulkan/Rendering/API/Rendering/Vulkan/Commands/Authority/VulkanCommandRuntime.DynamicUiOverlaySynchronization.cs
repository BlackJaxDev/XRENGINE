using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Renderer-free image-state services used by late UI command recording.</summary>
internal sealed partial class VulkanCommandRuntime
{
    internal void SeedRecordedImageLayoutState(
        CommandBuffer commandBuffer,
        CommandBuffer predecessor)
    {
        if (commandBuffer.Handle == 0 || predecessor.Handle == 0)
            return;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        ulong predecessorHandle = unchecked((ulong)predecessor.Handle);
        _ = EnterImageLayoutLockMeasured();
        try
        {
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    predecessorHandle,
                    out VulkanRecordedImageLayoutState? predecessorState))
            {
                return;
            }

            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    commandBufferHandle,
                    out VulkanRecordedImageLayoutState? recorded))
            {
                recorded = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration = CommandBuffers.ResolveRecordingGeneration(commandBuffer),
                };
                Synchronization._recordedImageLayoutsByCommandBuffer[commandBufferHandle] = recorded;
            }

            recorded.EntrySubresources.Clear();
            recorded.SecondaryDescriptorRequirements.Clear();
            recorded.SecondaryDescriptorImagePayloadGenerations.Clear();
            recorded.QueueOwnershipTransfers.Clear();
            recorded.EntryStateIncomplete = predecessorState.EntryStateIncomplete;
            recorded.EntryStateFailure = predecessorState.EntryStateFailure;
            foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in
                     predecessorState.TouchedSubresources)
            {
                recorded.EntrySubresources[pair.Key] = pair.Value;
            }
        }
        finally
        {
            Monitor.Exit(Synchronization._vulkanImageLayoutLock);
        }
    }

    internal void TransitionSecondaryDescriptorImagesForExecution(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        CommandBuffer primary,
        CommandBuffer secondary)
    {
        Span<CommandBuffer> secondaryBuffers = stackalloc CommandBuffer[1];
        secondaryBuffers[0] = secondary;
        TransitionSecondaryDescriptorImagesForExecution(
            encoder,
            telemetry,
            primary,
            secondaryBuffers);
    }

    internal void TransitionSecondaryDescriptorImagesForExecution(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        CommandBuffer primary,
        ReadOnlySpan<CommandBuffer> secondaryBuffers)
    {
        if (primary.Handle == 0 || secondaryBuffers.IsEmpty)
            return;

        lock (Synchronization.SecondaryDescriptorRequirementScratchGate)
        {
            Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> requirements =
                Synchronization._secondaryDescriptorRequirementScratch;
            requirements.Clear();
            try
            {
                _ = EnterImageLayoutLockMeasured();
                try
                {
                    for (int secondaryIndex = 0;
                         secondaryIndex < secondaryBuffers.Length;
                         secondaryIndex++)
                    {
                        CommandBuffer secondary = secondaryBuffers[secondaryIndex];
                        if (secondary.Handle == 0 ||
                            !Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                                unchecked((ulong)secondary.Handle),
                                out VulkanRecordedImageLayoutState? secondaryState))
                        {
                            continue;
                        }

                        foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> requirement in
                                 secondaryState.SecondaryDescriptorRequirements)
                        {
                            if (!requirements.TryGetValue(requirement.Key, out VulkanImageAccessState existing))
                            {
                                requirements.Add(requirement.Key, requirement.Value);
                                continue;
                            }

                            if (existing.Layout != requirement.Value.Layout ||
                                (existing.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                                 requirement.Value.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                                 existing.QueueFamilyIndex != requirement.Value.QueueFamilyIndex) ||
                                (existing.ResourceGeneration != 0 && requirement.Value.ResourceGeneration != 0 &&
                                 existing.ResourceGeneration != requirement.Value.ResourceGeneration) ||
                                existing.ExpectedDescriptorLayout != requirement.Value.ExpectedDescriptorLayout ||
                                existing.ExternalOwnership != requirement.Value.ExternalOwnership)
                            {
                                throw new InvalidOperationException(
                                    $"Secondary command buffer 0x{secondary.Handle:X} publishes an incompatible descriptor image requirement for 0x{requirement.Key.ImageHandle:X}.");
                            }

                            requirements[requirement.Key] = existing with
                            {
                                StageMask = existing.StageMask | requirement.Value.StageMask,
                                AccessMask = existing.AccessMask | requirement.Value.AccessMask,
                                Serial = Math.Max(existing.Serial, requirement.Value.Serial),
                            };
                        }
                    }
                }
                finally
                {
                    Monitor.Exit(
                        Synchronization._vulkanImageLayoutLock);
                }

                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> requirement in requirements)
                {
                    VulkanTrackedImageSubresource key = requirement.Key;
                    VulkanImageAccessState required = requirement.Value;
                    ulong generation = ResourceRuntime.GetPublishedGeneration(ObjectType.Image, key.ImageHandle);
                    if (required.ResourceGeneration != 0 && generation != required.ResourceGeneration)
                    {
                        throw new InvalidOperationException(
                            $"Secondary command-buffer run requires image 0x{key.ImageHandle:X} generation {required.ResourceGeneration}, but generation {generation} is published.");
                    }

                    Image image = new(key.ImageHandle);
                    ImageSubresourceRange range = new()
                    {
                        AspectMask = key.Aspect,
                        BaseMipLevel = key.MipLevel,
                        LevelCount = 1,
                        BaseArrayLayer = key.ArrayLayer,
                        LayerCount = 1,
                    };
                    if (!TryGetRecordedImageAccessState(primary, image, in range, out VulkanImageAccessState prior))
                    {
                        if (generation == 0)
                            continue;
                        prior = VulkanImageAccessState.Undefined with { ResourceGeneration = generation };
                    }

                    if (prior.Layout == required.Layout &&
                        prior.QueueFamilyIndex == required.QueueFamilyIndex &&
                        prior.ResourceGeneration == required.ResourceGeneration)
                    {
                        continue;
                    }
                    if (prior.QueueFamilyIndex != required.QueueFamilyIndex ||
                        prior.ResourceGeneration != required.ResourceGeneration)
                    {
                        throw new InvalidOperationException(
                            $"Secondary command-buffer run cannot establish image 0x{key.ImageHandle:X} descriptor entry state.");
                    }

                    EmitImageTransition(
                        encoder,
                        telemetry,
                        primary,
                        image,
                        in range,
                        prior,
                        required);
                }
            }
            finally
            {
                requirements.Clear();
            }
        }
    }

    /// <summary>
    /// Merges the already-frozen secondary image journal into its executing
    /// primary. The primary's pending barriers are published first so the
    /// secondary entry contract observes their post-transition state.
    /// </summary>
    internal void MergeSecondaryImageStatesForExecution(
        CommandBuffer primary,
        CommandBuffer secondary,
        VulkanFrameTelemetry telemetry)
    {
        if (primary.Handle == 0 || secondary.Handle == 0)
            return;

        if (CommandBuffers.TrackingBatches.TryGetValue(
                unchecked((ulong)primary.Handle),
                out VulkanCommandBufferTrackingBatch? primaryBatch))
        {
            lock (primaryBatch)
            {
                _ = FlushImageAccessBatch(primary, primaryBatch, telemetry);
                MergeSecondaryImageStateCore(primary, secondary, primaryBatch);
            }
            return;
        }

        MergeSecondaryImageStateCore(primary, secondary, null);
    }

    private void MergeSecondaryImageStateCore(
        CommandBuffer primary,
        CommandBuffer secondary,
        VulkanCommandBufferTrackingBatch? primaryBatch)
    {
        _ = EnterImageLayoutLockMeasured();
        try
        {
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)primary.Handle),
                    out VulkanRecordedImageLayoutState? primaryState))
            {
                primaryState = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration = CommandBuffers.ResolveRecordingGeneration(primary),
                };
                Synchronization._recordedImageLayoutsByCommandBuffer[
                    unchecked((ulong)primary.Handle)] = primaryState;
            }
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)secondary.Handle),
                    out VulkanRecordedImageLayoutState? secondaryState))
            {
                return;
            }
            MergeSecondaryImageState(primaryState, secondaryState, primaryBatch);
            primaryState.RefreshTouchedSubresources();
        }
        finally
        {
            Monitor.Exit(Synchronization._vulkanImageLayoutLock);
        }
    }

    /// <summary>
    /// Merges one vkCmdExecuteCommands batch while holding the image-journal
    /// authority once and rebuilds the compact touched list once. Rebuilding it
    /// after every one-draw secondary made primary recording quadratic in the
    /// number of visible command chains during camera movement.
    /// </summary>
    internal void MergeSecondaryImageStatesForExecution(
        CommandBuffer primary,
        ReadOnlySpan<CommandBuffer> secondaries,
        VulkanFrameTelemetry telemetry)
    {
        if (primary.Handle == 0 || secondaries.IsEmpty)
            return;

        if (CommandBuffers.TrackingBatches.TryGetValue(
                unchecked((ulong)primary.Handle),
                out VulkanCommandBufferTrackingBatch? primaryBatch))
        {
            // Match command-finalization's batch -> image-journal lock order.
            // Resource retirement may inspect this recording concurrently, so
            // the secondary merge cannot mutate the batch lookup index unlocked.
            lock (primaryBatch)
            {
                _ = FlushImageAccessBatch(primary, primaryBatch, telemetry);
                MergeSecondaryImageStatesCore(primary, secondaries, primaryBatch);
            }
            return;
        }

        MergeSecondaryImageStatesCore(primary, secondaries, null);
    }

    private void MergeSecondaryImageStatesCore(
        CommandBuffer primary,
        ReadOnlySpan<CommandBuffer> secondaries,
        VulkanCommandBufferTrackingBatch? primaryBatch)
    {
        _ = EnterImageLayoutLockMeasured();
        try
        {
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)primary.Handle),
                    out VulkanRecordedImageLayoutState? primaryState))
            {
                primaryState = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration =
                        CommandBuffers.ResolveRecordingGeneration(primary),
                };
                Synchronization._recordedImageLayoutsByCommandBuffer[
                    unchecked((ulong)primary.Handle)] = primaryState;
            }

            bool merged = false;
            foreach (CommandBuffer secondary in secondaries)
            {
                if (secondary.Handle == 0 ||
                    !Synchronization._recordedImageLayoutsByCommandBuffer
                        .TryGetValue(
                            unchecked((ulong)secondary.Handle),
                            out VulkanRecordedImageLayoutState? secondaryState))
                {
                    continue;
                }

                MergeSecondaryImageState(primaryState, secondaryState, primaryBatch);
                merged = true;
            }

            if (merged)
                primaryState.RefreshTouchedSubresources();
        }
        finally
        {
            Monitor.Exit(Synchronization._vulkanImageLayoutLock);
        }
    }

    private static void MergeSecondaryImageState(
        VulkanRecordedImageLayoutState primaryState,
        VulkanRecordedImageLayoutState secondaryState,
        VulkanCommandBufferTrackingBatch? primaryBatch)
    {
        if (secondaryState.EntryStateIncomplete)
        {
            primaryState.EntryStateIncomplete = true;
            if (!primaryState.EntryStateFailure.RequiresRecording)
                primaryState.EntryStateFailure = secondaryState.EntryStateFailure;
        }
        foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in
                 secondaryState.EntrySubresources)
        {
            if (!primaryState.Subresources.ContainsKey(pair.Key) &&
                !primaryState.EntrySubresources.ContainsKey(pair.Key))
            {
                primaryState.EntrySubresources[pair.Key] = pair.Value;
            }
        }
        foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in
                 secondaryState.TouchedSubresources)
        {
            primaryState.Subresources[pair.Key] = pair.Value;
            if (primaryBatch is not null)
            {
                ImageSubresourceRange range = new()
                {
                    AspectMask = pair.Key.Aspect,
                    BaseMipLevel = pair.Key.MipLevel,
                    LevelCount = 1,
                    BaseArrayLayer = pair.Key.ArrayLayer,
                    LayerCount = 1,
                };
                primaryBatch.RecordExecutedSecondaryImageAccess(
                    new VulkanImageAccessRangeDelta(
                        pair.Key.ImageHandle,
                        range,
                        pair.Value));
            }
        }
        primaryState.QueueOwnershipTransfers.AddRange(
            secondaryState.QueueOwnershipTransfers);
    }

    internal void EmitImageTransition(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        CommandBuffer commandBuffer,
        Image image,
        in ImageSubresourceRange range,
        ImageLayout oldLayout,
        ImageLayout newLayout)
    {
        VulkanImageAccessState prior;
        if (!TryGetRecordedImageAccessState(commandBuffer, image, in range, out prior))
        {
            ulong generation = ResourceRuntime.GetPublishedGeneration(ObjectType.Image, image.Handle);
            prior = ResolveOverlayImageAccessState(oldLayout, range.AspectMask, generation, 0);
        }
        VulkanImageAccessState next = ResolveOverlayImageAccessState(
            newLayout,
            range.AspectMask,
            prior.ResourceGeneration,
            unchecked((ulong)Interlocked.Increment(ref telemetry._vulkanImageLayoutTransitionSerial)));
        EmitImageTransition(encoder, telemetry, commandBuffer, image, in range, prior, next);
    }

    /// <summary>
    /// Establishes the first swapchain-image access in a command buffer that is
    /// submitted directly behind the image-acquire semaphore. The acquire wait
    /// is scoped to color-attachment output, so the layout transition must join
    /// that same stage rather than inherit the generic present/undefined scope.
    /// </summary>
    internal void EmitAcquiredSwapchainImageTransition(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        CommandBuffer commandBuffer,
        Image image,
        in ImageSubresourceRange range,
        ImageLayout oldLayout,
        ImageLayout newLayout)
    {
        ulong generation = ResourceRuntime.GetPublishedGeneration(
            ObjectType.Image,
            image.Handle);
        VulkanImageAccessState prior = new(
            oldLayout,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.None,
            Vk.QueueFamilyIgnored,
            ImageLayout.Undefined,
            0,
            generation);
        VulkanImageAccessState next = ResolveOverlayImageAccessState(
            newLayout,
            range.AspectMask,
            generation,
            unchecked((ulong)Interlocked.Increment(
                ref telemetry._vulkanImageLayoutTransitionSerial)));
        EmitImageTransition(
            encoder,
            telemetry,
            commandBuffer,
            image,
            in range,
            in prior,
            in next);
    }

    private static unsafe void EmitImageTransition(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        CommandBuffer commandBuffer,
        Image image,
        in ImageSubresourceRange range,
        in VulkanImageAccessState prior,
        in VulkanImageAccessState next)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = (AccessFlags)(ulong)prior.AccessMask,
            DstAccessMask = (AccessFlags)(ulong)next.AccessMask,
            OldLayout = prior.Layout,
            NewLayout = next.Layout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        encoder.PipelineBarrier(
            commandBuffer,
            (PipelineStageFlags)(ulong)prior.StageMask,
            (PipelineStageFlags)(ulong)next.StageMask,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
        encoder.RecordImageAccess(commandBuffer, image, in range, in next);
    }

    private bool TryGetRecordedImageAccessState(
        CommandBuffer commandBuffer,
        Image image,
        in ImageSubresourceRange range,
        out VulkanImageAccessState state,
        bool includeEntryState = true,
        bool includeUndefinedState = false)
    {
        state = VulkanImageAccessState.Undefined;
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0 || image.Handle == 0)
            return false;

        if (CommandBuffers.TrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
        {
            lock (batch)
            {
                if (batch.LatestImageAccessStates.TryGet(image.Handle, range, out state))
                    return true;

                // A partial batch can contain newer states for only some cells.
                // Preserve the batch -> layout lock order while overlaying it on
                // the journal/submitted state; a whole-range fallback can hide
                // those writes behind an older homogeneous layout.
                return TryGetMergedRecordedImageAccessState(
                    handle, image, in range, batch.LatestImageAccessStates,
                    out state, includeEntryState, includeUndefinedState);
            }
        }

        return TryGetMergedRecordedImageAccessState(
            handle, image, in range, null,
            out state, includeEntryState, includeUndefinedState);
    }

    private bool TryGetMergedRecordedImageAccessState(
        ulong commandBufferHandle,
        Image image,
        in ImageSubresourceRange range,
        VulkanCommandBufferImageAccessIndex? pending,
        out VulkanImageAccessState state,
        bool includeEntryState,
        bool includeUndefinedState)
    {
        _ = EnterImageLayoutLockMeasured();
        try
        {
            Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(commandBufferHandle, out VulkanRecordedImageLayoutState? recorded);
            return TryGetRecordedImageAccessStateNoLock(
                recorded,
                pending,
                image,
                in range,
                out state,
                includeEntryState,
                includeUndefinedState);
        }
        finally
        {
            Monitor.Exit(Synchronization._vulkanImageLayoutLock);
        }
    }

    private bool TryGetRecordedImageAccessStateNoLock(
        VulkanRecordedImageLayoutState? recorded,
        VulkanCommandBufferImageAccessIndex? pending,
        Image image,
        in ImageSubresourceRange range,
        out VulkanImageAccessState state,
        bool includeEntryState = true,
        bool includeUndefinedState = false)
    {
        state = VulkanImageAccessState.Undefined;
        VulkanImageAccessState? combined = null;
        ImageSubresourceRange requestedRange = range;
        uint levels = Math.Max(requestedRange.LevelCount, 1u);
        uint layers = Math.Max(requestedRange.LayerCount, 1u);
        for (uint levelOffset = 0; levelOffset < levels; levelOffset++)
        for (uint layerOffset = 0; layerOffset < layers; layerOffset++)
        {
            uint mip = requestedRange.BaseMipLevel + levelOffset;
            uint layer = requestedRange.BaseArrayLayer + layerOffset;
            if (!TryMergeRecordedAspect(
                    recorded,
                    pending,
                    image,
                    mip,
                    layer,
                    requestedRange.AspectMask,
                    ImageAspectFlags.ColorBit,
                    includeEntryState,
                    ref combined) ||
                !TryMergeRecordedAspect(
                    recorded,
                    pending,
                    image,
                    mip,
                    layer,
                    requestedRange.AspectMask,
                    ImageAspectFlags.DepthBit,
                    includeEntryState,
                    ref combined) ||
                !TryMergeRecordedAspect(
                    recorded,
                    pending,
                    image,
                    mip,
                    layer,
                    requestedRange.AspectMask,
                    ImageAspectFlags.StencilBit,
                    includeEntryState,
                    ref combined))
            {
                return false;
            }
        }

        if (!combined.HasValue)
            return false;
        state = combined.Value;
        return includeUndefinedState || state.Layout != ImageLayout.Undefined;
    }

    private bool TryMergeRecordedAspect(
        VulkanRecordedImageLayoutState? recorded,
        VulkanCommandBufferImageAccessIndex? pending,
        Image image,
        uint mip,
        uint layer,
        ImageAspectFlags requestedAspects,
        ImageAspectFlags aspect,
        bool includeEntryState,
        ref VulkanImageAccessState? combined)
    {
        if ((requestedAspects & aspect) == 0)
            return true;

        VulkanTrackedImageSubresource key =
            new(image.Handle, mip, layer, aspect);
        VulkanImageAccessState candidate;
        if (pending is not null && pending.TryGetSubresource(in key, out candidate))
        {
            // Pending commands are newer than both the journal and submission.
        }
        else if (recorded is not null &&
            (recorded.Subresources.TryGetValue(key, out candidate) ||
             (includeEntryState &&
              recorded.EntrySubresources.TryGetValue(key, out candidate))))
        {
            // The command buffer has already established this state.
        }
        else if (Synchronization._trackedImageSubresourceStates.TryGetValue(
                     key,
                     out VulkanImageSubresourceState? submitted))
        {
            candidate = submitted.Submitted;
        }
        else
        {
            return false;
        }

        if (!combined.HasValue)
        {
            combined = candidate;
            return true;
        }

        VulkanImageAccessState prior = combined.Value;
        if (prior.Layout != candidate.Layout ||
            prior.QueueFamilyIndex != candidate.QueueFamilyIndex ||
            prior.ResourceGeneration != candidate.ResourceGeneration)
        {
            return false;
        }

        combined = prior with
        {
            StageMask = prior.StageMask | candidate.StageMask,
            AccessMask = prior.AccessMask | candidate.AccessMask,
            ExpectedDescriptorLayout = prior.ExpectedDescriptorLayout == candidate.ExpectedDescriptorLayout
                ? prior.ExpectedDescriptorLayout
                : ImageLayout.Undefined,
            Serial = Math.Max(prior.Serial, candidate.Serial),
        };
        return true;
    }

    private static VulkanImageAccessState ResolveOverlayImageAccessState(
        ImageLayout layout,
        ImageAspectFlags aspect,
        ulong generation,
        ulong serial)
    {
        (PipelineStageFlags2 stages, AccessFlags2 access, ImageLayout descriptorLayout) = layout switch
        {
            ImageLayout.ColorAttachmentOptimal => (
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit,
                ImageLayout.Undefined),
            ImageLayout.PresentSrcKhr => (
                PipelineStageFlags2.BottomOfPipeBit,
                AccessFlags2.MemoryReadBit,
                ImageLayout.Undefined),
            ImageLayout.TransferDstOptimal => (
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                ImageLayout.Undefined),
            ImageLayout.General => (
                PipelineStageFlags2.AllCommandsBit,
                AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
                ImageLayout.General),
            _ => (
                PipelineStageFlags2.TopOfPipeBit,
                AccessFlags2.None,
                ImageLayout.Undefined),
        };
        return new VulkanImageAccessState(
            layout,
            stages,
            access,
            Vk.QueueFamilyIgnored,
            descriptorLayout,
            serial,
            generation);
    }
}
