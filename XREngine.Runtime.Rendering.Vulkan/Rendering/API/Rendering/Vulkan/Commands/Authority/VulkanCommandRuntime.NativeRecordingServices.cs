using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;
using XREngine.Rendering.RenderGraph;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    private const uint FrameTimingQueryCount = 2;
    private long _recordedPrimaryFrameCounter;
    private long _primaryReuseCohortGeneration;

    private VulkanMappedFrameArena? MappedFrameArena => ResourceRuntime.MappedFrameArena;
    private ulong VulkanFrameCounter => unchecked((ulong)Volatile.Read(ref _recordedPrimaryFrameCounter));
    private ulong SharedGraphicsPipelineGeneration => ResourceRuntime.PipelineManager.SharedGraphicsPipelineGeneration;
    private ulong VulkanPipelineCompileActivityGeneration
        => unchecked((ulong)Volatile.Read(ref ResourceRuntime.PipelineManager._vulkanPipelineCompileActivityGeneration));
    private bool IsVulkanPipelineAsyncCompilationEnabled
        => RuntimeEngine.Rendering.Settings.AsyncProgramCompilation &&
           DeviceContext.IsReady &&
           Volatile.Read(ref ResourceRuntime.PipelineManager._vulkanPipelineCompileShutdownStarted) == 0;

    private static VulkanResourceLifetimeKey ResourceKey(ObjectType type, ulong handle)
        => new(type, handle);

    private ulong GetCurrentVulkanResourceGeneration(ObjectType type, ulong handle)
        => ResourceRuntime.GetPublishedGeneration(type, handle);

    internal ulong GetResourceGeneration(ObjectType type, ulong handle)
        => GetCurrentVulkanResourceGeneration(type, handle);

    private long SnapshotDescriptorSetContentUpdateGeneration()
        => ResourceRuntime.Descriptors.SnapshotDescriptorSetContentUpdateGeneration();

    private bool HaveDescriptorSetContentsUpdatedSince(long generation)
        => ResourceRuntime.Descriptors.HaveDescriptorSetContentsUpdatedSince(generation);

    /// <summary>
    /// Publishes wrapper-side framebuffer binding state without retaining a
    /// renderer facade. Output extent selection remains a producer concern and
    /// is frozen later in the prepared primary input.
    /// </summary>
    internal void SetBoundFrameBufferState(
        EFramebufferTarget target,
        XRFrameBuffer? frameBuffer)
    {
        switch (target)
        {
            case EFramebufferTarget.Framebuffer:
                CommandBuffers.BoundReadFrameBuffer = frameBuffer;
                CommandBuffers.BoundDrawFrameBuffer = frameBuffer;
                break;
            case EFramebufferTarget.ReadFramebuffer:
                CommandBuffers.BoundReadFrameBuffer = frameBuffer;
                break;
            case EFramebufferTarget.DrawFramebuffer:
                CommandBuffers.BoundDrawFrameBuffer = frameBuffer;
                break;
            default:
                return;
        }

        MarkCommandBuffersDirtyForLegacyMeshState(nameof(SetBoundFrameBufferState));
    }

    /// <summary>Removes a retired renderer from the generation-owned frame-data manifest.</summary>
    internal void RemoveMeshFrameDataManifestRenderer(VkMeshRenderer owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        CommandBuffers.FrameWideMeshDataManifest.RemoveRenderer(owner);
    }

    /// <summary>
    /// Associates a command buffer with the mapped-frame-arena generation captured by a sealed
    /// mesh draw. This is a command-lifetime operation and therefore belongs to this authority.
    /// </summary>
    internal bool TryAcquireMappedFrameArenaRecordingLease(
        CommandBuffer commandBuffer,
        VkMeshRenderer owner,
        int drawSlot,
        ulong sealedGeneration,
        out string reason)
    {
        reason = string.Empty;
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        ulong generation = MappedFrameArena?.Generation ?? 0UL;
        if (commandBufferHandle == 0 || generation == 0)
            return true;

        VulkanMeshFrameDataReservationManifest manifest =
            _commandBufferRecordingScratch.Value!.MeshFrameDataManifest;
        bool manifestOwnsDraw = manifest.ContainsSealedDraw(owner, drawSlot, generation);
        bool workerOwnsSealedDraw = sealedGeneration != 0 && sealedGeneration == generation;
        if (!manifestOwnsDraw && !workerOwnsSealedDraw)
        {
            reason = $"late or unsealed frame-data request for generation {generation}, slot {drawSlot}";
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDynamicUniformExhaustion();
            return false;
        }

        VulkanResourceLifetimeTracker lifetimeTracker = ResourceRuntime.Lifetime.Tracker;
        lock (lifetimeTracker.SyncRoot)
        {
            if (!lifetimeTracker.CommandBufferLifetimes.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                lifetimeTracker.CommandBufferLifetimes[commandBufferHandle] = lifetime;
            }

            if (lifetime.QueuedSubmissionCount != 0)
            {
                reason = $"command buffer 0x{commandBufferHandle:X} is already queued";
                return false;
            }

            if (lifetime.FrameDataLease.Generation != 0 &&
                lifetime.FrameDataLease.Generation != generation)
            {
                reason = $"command buffer 0x{commandBufferHandle:X} captured frame-data generation {lifetime.FrameDataLease.Generation} before current generation {generation}";
                return false;
            }

            if (lifetime.FrameDataLease.TryAcquireRecording(
                    generation,
                    commandBufferQueued: lifetime.QueuedSubmissionCount != 0))
            {
                return true;
            }

            reason = $"command buffer 0x{commandBufferHandle:X} could not acquire frame-data generation {generation}";
            return false;
        }
    }

    /// <summary>Checks exact native descriptor-set lifetime dependencies for a command buffer.</summary>
    internal bool CommandBufferReferencesAllDescriptorSets(
        CommandBuffer commandBuffer,
        ReadOnlySpan<DescriptorSet> descriptorSets,
        out ulong missingDescriptorSetHandle)
    {
        missingDescriptorSetHandle = 0;
        if (commandBuffer.Handle == 0)
            return false;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            if (!ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                return false;
            }

            for (int index = 0; index < descriptorSets.Length; index++)
            {
                DescriptorSet descriptorSet = descriptorSets[index];
                if (descriptorSet.Handle == 0)
                    continue;

                VulkanResourceLifetimeKey key = ResourceKey(ObjectType.DescriptorSet, descriptorSet.Handle);
                if (lifetime.Dependencies.ContainsKey(key))
                    continue;

                missingDescriptorSetHandle = descriptorSet.Handle;
                return false;
            }
        }

        return true;
    }

    private static bool IsCommandBufferVariantGpuProfilerStateDirty(
        PrimaryCommandArtifactOwner variant,
        bool profilingActive,
        int frameSlot)
        => variant.GpuProfilerActive != profilingActive ||
           profilingActive && variant.GpuProfilerFrameSlot != frameSlot;

    private bool IsCommandBufferVariantImageLayoutStateDirty(
        PrimaryCommandArtifactOwner variant,
        ulong imageLayoutStartSignature)
    {
        _ = imageLayoutStartSignature;
        return variant.RecordedImageLayoutEndState is null ||
               TryGetRecordedImageEntryStateMismatch(
                   variant.PrimaryCommandBuffer,
                   out _);
    }

    private bool TryGetRecordedImageEntryStateMismatch(
        CommandBuffer commandBuffer,
        out VulkanImageEntryStateMismatch mismatch)
    {
        mismatch = default;
        if (commandBuffer.Handle == 0)
        {
            mismatch = new VulkanImageEntryStateMismatch(
                EVulkanPrimaryEntryStateMismatch.MissingCommandBufferState,
                0,
                0,
                0,
                ImageAspectFlags.None,
                VulkanImageAccessState.Undefined,
                VulkanImageAccessState.Undefined);
            return true;
        }

        lock (Synchronization._vulkanImageLayoutLock)
        {
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)commandBuffer.Handle),
                    out VulkanRecordedImageLayoutState? recorded))
            {
                mismatch = new VulkanImageEntryStateMismatch(
                    EVulkanPrimaryEntryStateMismatch.MissingCommandBufferState,
                    0,
                    0,
                    0,
                    ImageAspectFlags.None,
                    VulkanImageAccessState.Undefined,
                    VulkanImageAccessState.Undefined);
                return true;
            }
            if (recorded.EntryStateIncomplete)
            {
                mismatch = recorded.EntryStateFailure.RequiresRecording
                    ? recorded.EntryStateFailure
                    : new VulkanImageEntryStateMismatch(
                        EVulkanPrimaryEntryStateMismatch.IncompleteSnapshot,
                        0,
                        0,
                        0,
                        ImageAspectFlags.None,
                        VulkanImageAccessState.Undefined,
                        VulkanImageAccessState.Undefined);
                return true;
            }

            foreach ((VulkanTrackedImageSubresource key, VulkanImageAccessState expected) in
                     recorded.EntrySubresources)
            {
                if (!Synchronization._trackedImageSubresourceStates.TryGetValue(
                        key,
                        out VulkanImageSubresourceState? submittedState))
                {
                    mismatch = new VulkanImageEntryStateMismatch(
                        EVulkanPrimaryEntryStateMismatch.MissingSubmittedState,
                        key.ImageHandle,
                        key.MipLevel,
                        key.ArrayLayer,
                        key.Aspect,
                        expected,
                        VulkanImageAccessState.Undefined);
                    return true;
                }
                EVulkanPrimaryEntryStateMismatch kind = VulkanImageEntryStateContract.Compare(
                    submittedState.Submitted,
                    expected);
                if (kind == EVulkanPrimaryEntryStateMismatch.None)
                    continue;

                mismatch = new VulkanImageEntryStateMismatch(
                    kind,
                    key.ImageHandle,
                    key.MipLevel,
                    key.ArrayLayer,
                    key.Aspect,
                    expected,
                    submittedState.Submitted);
                return true;
            }
        }
        return false;
    }

    private void CaptureCommandBufferVariantImageLayoutEndState(
        PrimaryCommandArtifactOwner variant)
    {
        FrameOpSignatureHasher hash = new();
        lock (Synchronization._vulkanImageLayoutLock)
            if (Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)variant.PrimaryCommandBuffer.Handle),
                    out VulkanRecordedImageLayoutState? recorded))
                foreach ((VulkanTrackedImageSubresource key, VulkanImageAccessState state) in
                         recorded.TouchedSubresources)
                {
                    hash.Add(key.ImageHandle);
                    hash.Add(key.MipLevel);
                    hash.Add(key.ArrayLayer);
                    hash.Add((int)key.Aspect);
                    hash.Add((int)state.Layout);
                    hash.Add((ulong)state.StageMask);
                    hash.Add((ulong)state.AccessMask);
                    hash.Add(state.ResourceGeneration);
                }
        ulong signature = hash.ToHash();
        variant.RecordedImageLayoutEndSignature = signature;
        variant.RecordedImageLayoutEndState = new VulkanImageLayoutStateSnapshot(signature);
    }

    private static void RestoreRecordedImageLayoutEndState(
        PrimaryCommandArtifactOwner variant)
        => _ = variant.RecordedImageLayoutEndState;

    private void SetActivePrimaryCommandArtifactOwner(
        uint imageIndex,
        PrimaryCommandArtifactOwner variant)
    {
        CommandBuffer[]? active = CommandBuffers.ActiveBuffers;
        if (active is not null && imageIndex < active.Length)
            active[imageIndex] = variant.PrimaryCommandBuffer;
    }

    private void PrepareVulkanGpuProfilerReusableSubmission(
        int frameSlot,
        PrimaryCommandArtifactOwner variant,
        bool profilingActive)
    {
        if (FrameTelemetry._vulkanGpuProfilerPendingScopes is { } pendingScopes &&
            (uint)frameSlot < (uint)pendingScopes.Length)
            pendingScopes[frameSlot].Clear();
        if (FrameTelemetry._vulkanGpuProfilerPendingQueryCounts is { } pendingCounts &&
            (uint)frameSlot < (uint)pendingCounts.Length)
            pendingCounts[frameSlot] = 0;
        if (FrameTelemetry._vulkanGpuProfilerSubmittedFrameIds is { } submittedFrames &&
            (uint)frameSlot < (uint)submittedFrames.Length)
            submittedFrames[frameSlot] = 0UL;
        if (FrameTelemetry._vulkanGpuProfilerQueryReady is { } ready &&
            (uint)frameSlot < (uint)ready.Length)
            ready[frameSlot] = false;

        if (!VulkanFrameTelemetry.IsGpuProfilerCommandBufferInstrumentationEnabled ||
            !FrameTelemetry._vulkanGpuProfilerEnabled ||
            !profilingActive ||
            !variant.GpuProfilerActive ||
            variant.GpuProfilerFrameSlot != frameSlot ||
            variant.GpuProfilerScopes is not { Length: > 0 } scopes ||
            variant.GpuProfilerQueryCount <= 0 ||
            FrameTelemetry._vulkanGpuProfilerPendingScopes is null ||
            FrameTelemetry._vulkanGpuProfilerPendingQueryCounts is null ||
            (uint)frameSlot >= (uint)FrameTelemetry._vulkanGpuProfilerPendingScopes.Length ||
            (uint)frameSlot >= (uint)FrameTelemetry._vulkanGpuProfilerPendingQueryCounts.Length)
            return;

        FrameTelemetry._vulkanGpuProfilerPendingScopes[frameSlot].AddRange(scopes);
        FrameTelemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot] =
            variant.GpuProfilerQueryCount;
    }

    internal void PrepareSubmissionMarkersForCommandBufferReuse(
        CommandBuffer commandBuffer,
        ReadOnlySpan<FrameOp> frameOps,
        ReadOnlySpan<FrameOp> dynamicUiFrameOps)
    {
        lock (Synchronization._submissionMarkerLock)
        {
            if (Synchronization._submissionMarkersByCommandBuffer.TryGetValue(
                    commandBuffer.Handle,
                    out List<VulkanTimelineGpuFence>? existing))
            {
                for (int markerIndex = 0; markerIndex < existing.Count; markerIndex++)
                    existing[markerIndex].Fail();
                existing.Clear();
            }
            RegisterSubmissionMarkersNoLock(commandBuffer, frameOps);
            RegisterSubmissionMarkersNoLock(commandBuffer, dynamicUiFrameOps);
        }
    }

    private void RegisterSubmissionMarkersNoLock(
        CommandBuffer commandBuffer,
        ReadOnlySpan<FrameOp> frameOps)
    {
        for (int operationIndex = 0; operationIndex < frameOps.Length; operationIndex++)
        {
            if (frameOps[operationIndex] is not SubmissionMarkerOp marker)
                continue;
            if (!Synchronization._submissionMarkersByCommandBuffer.TryGetValue(
                    commandBuffer.Handle,
                    out List<VulkanTimelineGpuFence>? markers))
            {
                markers = [];
                Synchronization._submissionMarkersByCommandBuffer.Add(
                    commandBuffer.Handle,
                    markers);
            }
            markers.Add(marker.Fence);
        }
    }

    private T? GenericToAPI<T>(GenericRenderObject? renderObject)
        where T : VkObjectBase
        => renderObject is null ? null : ResourceRuntime.BackendObjects.Get(renderObject) as T;

    internal static Extent2D ResolveFrameBufferDrawExtent(XRFrameBuffer frameBuffer)
    {
        var targets = frameBuffer.Targets;
        if (targets is null || targets.Length == 0)
            return new Extent2D(Math.Max(frameBuffer.Width, 1u), Math.Max(frameBuffer.Height, 1u));

        uint minWidth = uint.MaxValue;
        uint minHeight = uint.MaxValue;
        bool found = false;
        for (int index = 0; index < targets.Length; index++)
        {
            var (target, _, mipLevelValue, _) = targets[index];
            if (target is null)
                continue;

            uint width = Math.Max(target.Width, 1u);
            uint height = Math.Max(target.Height, 1u);
            int mipLevel = Math.Max(mipLevelValue, 0);
            if (mipLevel > 0)
            {
                width = Math.Max(width >> mipLevel, 1u);
                height = Math.Max(height >> mipLevel, 1u);
            }

            minWidth = Math.Min(minWidth, width);
            minHeight = Math.Min(minHeight, height);
            found = true;
        }

        return found
            ? new Extent2D(minWidth, minHeight)
            : new Extent2D(Math.Max(frameBuffer.Width, 1u), Math.Max(frameBuffer.Height, 1u));
    }

    private bool TryGetDescriptorHeapImageViewCreateInfo(
        ImageView imageView,
        out ImageViewCreateInfo createInfo)
    {
        if (imageView.Handle != 0 &&
            ResourceRuntime.Lifetime.ImageViews.DescriptorHeapCreateInfos.TryGetValue(
                imageView.Handle,
                out createInfo))
        {
            return true;
        }

        createInfo = default;
        return false;
    }

    private bool TryResolveFrameBufferAttachmentImage(
        IFrameBufferAttachement attachment,
        out Image image)
    {
        VkObjectBase? wrapper = attachment is GenericRenderObject renderObject
            ? ResourceRuntime.BackendObjects.Get(renderObject) as VkObjectBase
            : null;

        image = wrapper switch
        {
            IVkImageDescriptorSource source => source.DescriptorImage,
            VkRenderBuffer renderBuffer => renderBuffer.Image,
            _ => default,
        };
        return image.Handle != 0;
    }

    private RenderPass GetOrCreateFrameBufferRenderPass(
        FrameBufferAttachmentSignature[] signature)
        => ResourceRuntime.Framebuffers.GetOrCreateRenderPass(
            Api,
            DeviceContext.Device,
            signature);

    private void ThrowIfVulkanDeviceOperationNotAdmitted(string operation)
    {
        if (!DeviceContext.IsOperational)
            throw new InvalidOperationException(
                $"Cannot execute Vulkan device operation '{operation}' while device state is {DeviceContext.State}.");
    }

    private bool TryAppendDescriptorHeapInheritancePNext(
        ref CommandBufferInheritanceInfo inheritanceInfo,
        CommandBufferInheritanceDescriptorHeapInfoEXTNative* heapInfo,
        BindHeapInfoEXTNative* samplerHeapInfo,
        BindHeapInfoEXTNative* resourceHeapInfo)
        => PrimaryCommandEncoder.TryAppendDescriptorHeapInheritance(
            ref inheritanceInfo,
            heapInfo,
            samplerHeapInfo,
            resourceHeapInfo);

    private static DynamicRenderingFormatSignature CreateSwapchainDynamicRenderingFormatSignature(
        Format colorFormat,
        Format depthFormat)
    {
        Span<Format> colorFormats = stackalloc Format[1];
        colorFormats[0] = colorFormat;
        return new DynamicRenderingFormatSignature(
            colorFormats,
            depthFormat,
            HasStencilComponent(depthFormat) ? depthFormat : Format.Undefined);
    }

    private static DynamicRenderingFormatSignature CreateDynamicRenderingFormatSignature(
        FrameBufferAttachmentSignature[] signatures,
        uint viewMask = 0u,
        uint layerCount = 1u)
    {
        int colorCount = 0;
        Format depthFormat = Format.Undefined;
        Format stencilFormat = Format.Undefined;
        for (int index = 0; index < signatures.Length; index++)
        {
            FrameBufferAttachmentSignature signature = signatures[index];
            if (signature.Role == AttachmentRole.Color)
                colorCount++;
            else
            {
                if ((signature.AspectMask & ImageAspectFlags.DepthBit) != 0)
                    depthFormat = signature.Format;
                if ((signature.AspectMask & ImageAspectFlags.StencilBit) != 0)
                    stencilFormat = signature.Format;
            }
        }

        Span<Format> colorFormats = colorCount == 0 ? [] : stackalloc Format[colorCount];
        int colorIndex = 0;
        for (int index = 0; index < signatures.Length; index++)
            if (signatures[index].Role == AttachmentRole.Color)
                colorFormats[colorIndex++] = signatures[index].Format;
        return new DynamicRenderingFormatSignature(
            colorFormats,
            depthFormat,
            stencilFormat,
            viewMask,
            layerCount);
    }

    private static bool HasStencilComponent(Format format)
        => format is Format.D16UnormS8Uint or
            Format.D24UnormS8Uint or
            Format.D32SfloatS8Uint or
            Format.S8Uint;

    private bool SupportsDynamicRenderingLocalRead
        => DeviceContext.MutableCapabilities._supportsDynamicRenderingLocalRead;

    private void CmdBeginDynamicRendering(
        CommandBuffer commandBuffer,
        RenderingInfo* renderingInfo,
        bool preferKhrDynamicRendering = false)
    {
        if (renderingInfo is not null)
        {
            for (uint index = 0; index < renderingInfo->ColorAttachmentCount; index++)
            {
                PrimaryCommandEncoder.Track(
                    commandBuffer,
                    ObjectType.ImageView,
                    renderingInfo->PColorAttachments[index].ImageView.Handle);
                PrimaryCommandEncoder.Track(
                    commandBuffer,
                    ObjectType.ImageView,
                    renderingInfo->PColorAttachments[index].ResolveImageView.Handle);
            }
            if (renderingInfo->PDepthAttachment is not null)
            {
                PrimaryCommandEncoder.Track(
                    commandBuffer,
                    ObjectType.ImageView,
                    renderingInfo->PDepthAttachment->ImageView.Handle);
                PrimaryCommandEncoder.Track(
                    commandBuffer,
                    ObjectType.ImageView,
                    renderingInfo->PDepthAttachment->ResolveImageView.Handle);
            }
            if (renderingInfo->PStencilAttachment is not null)
            {
                PrimaryCommandEncoder.Track(
                    commandBuffer,
                    ObjectType.ImageView,
                    renderingInfo->PStencilAttachment->ImageView.Handle);
                PrimaryCommandEncoder.Track(
                    commandBuffer,
                    ObjectType.ImageView,
                    renderingInfo->PStencilAttachment->ResolveImageView.Handle);
            }
        }

        if (!preferKhrDynamicRendering && DeviceContext.InstanceApiVersion >= Vk.Version13)
        {
            Api.CmdBeginRendering(commandBuffer, renderingInfo);
            return;
        }
        if (DeviceContext.ExtensionFunctions.KhrDynamicRendering is { } dynamicRendering)
        {
            dynamicRendering.CmdBeginRendering(commandBuffer, renderingInfo);
            return;
        }
        if (DeviceContext.InstanceApiVersion >= Vk.Version13)
        {
            Api.CmdBeginRendering(commandBuffer, renderingInfo);
            return;
        }
        throw new InvalidOperationException("VK_KHR_dynamic_rendering command extension is unavailable.");
    }

    private void CmdEndDynamicRendering(
        CommandBuffer commandBuffer,
        bool preferKhrDynamicRendering = false)
    {
        if (!preferKhrDynamicRendering && DeviceContext.InstanceApiVersion >= Vk.Version13)
        {
            Api.CmdEndRendering(commandBuffer);
            return;
        }
        if (DeviceContext.ExtensionFunctions.KhrDynamicRendering is { } dynamicRendering)
        {
            dynamicRendering.CmdEndRendering(commandBuffer);
            return;
        }
        if (DeviceContext.InstanceApiVersion >= Vk.Version13)
        {
            Api.CmdEndRendering(commandBuffer);
            return;
        }
        throw new InvalidOperationException("VK_KHR_dynamic_rendering command extension is unavailable.");
    }

    internal static Viewport CreateVulkanViewport(Extent2D extent)
        => RuntimeEngine.Rendering.Settings.ClipSpaceYDirection == ERenderClipSpaceYDirection.YDown
            ? new Viewport
            {
                X = 0,
                Y = 0,
                Width = extent.Width,
                Height = extent.Height,
                MinDepth = 0,
                MaxDepth = 1,
            }
            : new Viewport
            {
                X = 0,
                Y = extent.Height,
                Width = extent.Width,
                Height = -(float)extent.Height,
                MinDepth = 0,
                MaxDepth = 1,
            };

    private void ResetSubmissionMarkersForCommandBuffer(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;
        lock (Synchronization._submissionMarkerLock)
        {
            if (!Synchronization._submissionMarkersByCommandBuffer.Remove(
                    commandBuffer.Handle,
                    out List<VulkanTimelineGpuFence>? markers))
            {
                return;
            }
            for (int index = 0; index < markers.Count; index++)
                markers[index].Fail();
        }
    }

    private void BeginFrameTimingQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        if (!FrameTelemetry._frameTimingGpuEnabled ||
            FrameTelemetry._frameTimingQueryPools is not { } pools ||
            (uint)frameSlot >= (uint)pools.Length ||
            pools[frameSlot].Handle == 0)
        {
            return;
        }

        QueryPool queryPool = pools[frameSlot];
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.QueryPool, queryPool.Handle);
        Api.CmdResetQueryPool(commandBuffer, queryPool, 0, FrameTimingQueryCount);
        Api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, queryPool, 0);
    }

    private void EndFrameTimingQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        if (!FrameTelemetry._frameTimingGpuEnabled ||
            FrameTelemetry._frameTimingQueryPools is not { } pools ||
            (uint)frameSlot >= (uint)pools.Length ||
            pools[frameSlot].Handle == 0)
        {
            return;
        }
        Api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, pools[frameSlot], 1);
    }

    private void BeginVulkanGpuProfilerQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        FrameTelemetry._vulkanGpuProfilerRecordingActive = false;
        FrameTelemetry._vulkanGpuProfilerRecordingFrameSlot = -1;
        FrameTelemetry._vulkanGpuProfilerNextQuery = 0;
        FrameTelemetry._vulkanGpuProfilerBudgetWarningIssued = false;
        if (FrameTelemetry._vulkanGpuProfilerPendingScopes is { } scopes && (uint)frameSlot < (uint)scopes.Length)
            scopes[frameSlot].Clear();
        if (FrameTelemetry._vulkanGpuProfilerPendingQueryCounts is { } counts && (uint)frameSlot < (uint)counts.Length)
            counts[frameSlot] = 0;
        if (FrameTelemetry._vulkanGpuProfilerSubmittedFrameIds is { } frameIds && (uint)frameSlot < (uint)frameIds.Length)
            frameIds[frameSlot] = 0;
        if (FrameTelemetry._vulkanGpuProfilerQueryReady is { } ready && (uint)frameSlot < (uint)ready.Length)
            ready[frameSlot] = false;

        if (!VulkanFrameTelemetry.IsGpuProfilerCommandBufferInstrumentationEnabled)
        {
            if (RenderPipelineGpuProfiler.Instance.IsProfilingActive)
                RenderPipelineGpuProfiler.Instance.RecordBackendGpuTimingStatus(
                    RuntimeEngine.Rendering.State.RenderFrameId,
                    VulkanFrameTelemetry.GpuProfilerBackendName,
                    VulkanFrameTelemetry.GpuProfilerCommandTimingStatusMessage);
            return;
        }
        if (!FrameTelemetry._vulkanGpuProfilerEnabled ||
            !RenderPipelineGpuProfiler.Instance.IsProfilingActive ||
            FrameTelemetry._vulkanGpuProfilerQueryPools is not { } queryPools ||
            (uint)frameSlot >= (uint)queryPools.Length ||
            queryPools[frameSlot].Handle == 0)
            return;

        QueryPool queryPool = queryPools[frameSlot];
        TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.QueryPool,
            queryPool.Handle,
            "GpuProfiler.QueryPool");
        Api.CmdResetQueryPool(
            commandBuffer,
            queryPool,
            0,
            VulkanFrameTelemetry.GpuProfilerQueryCount);
        FrameTelemetry._vulkanGpuProfilerRecordingActive = true;
        FrameTelemetry._vulkanGpuProfilerRecordingFrameSlot = frameSlot;
    }

    private VulkanGpuProfilerScope TryBeginVulkanGpuProfilerScope(
        CommandBuffer commandBuffer,
        FrameOp operation,
        int passIndex)
    {
        if (!TryReserveVulkanGpuProfilerQueries(
                commandBuffer,
                out QueryPool queryPool,
                out uint startQuery,
                out uint endQuery))
            return default;

        string[] path = BuildVulkanGpuProfilerPath(operation, passIndex);
        Api.CmdWriteTimestamp(
            commandBuffer,
            PipelineStageFlags.TopOfPipeBit,
            queryPool,
            startQuery);
        RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(
            ERendererProfilerCounter.TimestampQueryCount);
        return new VulkanGpuProfilerScope(
            Api,
            FrameTelemetry,
            commandBuffer,
            queryPool,
            FrameTelemetry._vulkanGpuProfilerRecordingFrameSlot,
            endQuery,
            path);
    }

    private bool TryReserveVulkanGpuProfilerQueries(
        CommandBuffer commandBuffer,
        out QueryPool queryPool,
        out uint startQuery,
        out uint endQuery)
    {
        queryPool = default;
        startQuery = 0;
        endQuery = 0;
        int frameSlot = FrameTelemetry._vulkanGpuProfilerRecordingFrameSlot;
        if (!FrameTelemetry._vulkanGpuProfilerRecordingActive ||
            FrameTelemetry._vulkanGpuProfilerQueryPools is not { } queryPools ||
            (uint)frameSlot >= (uint)queryPools.Length ||
            commandBuffer.Handle == 0)
            return false;

        if (FrameTelemetry._vulkanGpuProfilerNextQuery + 1 >=
            VulkanFrameTelemetry.GpuProfilerQueryCount)
        {
            if (!FrameTelemetry._vulkanGpuProfilerBudgetWarningIssued)
            {
                FrameTelemetry._vulkanGpuProfilerBudgetWarningIssued = true;
                RenderPipelineGpuProfiler.Instance.RecordBackendGpuTimingStatus(
                    RuntimeEngine.Rendering.State.RenderFrameId,
                    VulkanFrameTelemetry.GpuProfilerBackendName,
                    $"Vulkan GPU pipeline timing reached the per-frame timestamp scope budget ({VulkanFrameTelemetry.GpuProfilerMaxScopesPerFrame}); later scopes were skipped.",
                    skippedSamples: 1);
            }
            return false;
        }

        queryPool = queryPools[frameSlot];
        if (queryPool.Handle == 0)
            return false;
        startQuery = FrameTelemetry._vulkanGpuProfilerNextQuery++;
        endQuery = FrameTelemetry._vulkanGpuProfilerNextQuery++;
        return true;
    }

    private void CaptureVulkanGpuProfilerVariantScopes(
        int frameSlot,
        PrimaryCommandArtifactOwner variant)
    {
        if (!VulkanFrameTelemetry.IsGpuProfilerCommandBufferInstrumentationEnabled ||
            !FrameTelemetry._vulkanGpuProfilerEnabled ||
            !RenderPipelineGpuProfiler.Instance.IsProfilingActive ||
            FrameTelemetry._vulkanGpuProfilerPendingScopes is not { } pendingScopes ||
            FrameTelemetry._vulkanGpuProfilerPendingQueryCounts is not { } pendingCounts ||
            (uint)frameSlot >= (uint)pendingScopes.Length ||
            (uint)frameSlot >= (uint)pendingCounts.Length)
        {
            variant.GpuProfilerScopes = null;
            variant.GpuProfilerQueryCount = 0;
            return;
        }

        List<VulkanGpuProfilerPendingScope> scopes = pendingScopes[frameSlot];
        int queryCount = pendingCounts[frameSlot];
        variant.GpuProfilerScopes = scopes.Count == 0
            ? []
            : scopes.ToArray();
        variant.GpuProfilerQueryCount = scopes.Count == 0 ? 0 : queryCount;
    }

    private static string[] BuildVulkanGpuProfilerPath(FrameOp operation, int passIndex)
        => BuildVulkanGpuProfilerPath(
            operation.Context,
            passIndex,
            BuildVulkanGpuProfilerOperationLabel(operation));

    private static string[] BuildVulkanGpuProfilerPath(
        in FrameOpContext context,
        int passIndex,
        string scopeName)
    {
        string pipelineName = context.PipelineInstance?.ProfilerKey ??
            context.PipelineInstance?.DebugName ??
            (context.PipelineIdentity != 0
                ? $"Pipeline#{context.PipelineIdentity}"
                : "Vulkan");
        string passName = ResolveVulkanGpuProfilerPassName(
            passIndex,
            context.PassMetadata);
        return [pipelineName, passName, scopeName];
    }

    private static string ResolveVulkanGpuProfilerPassName(
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passIndex == VulkanBarrierPlanner.SwapchainPassIndex)
            return $"Pass[{VulkanBarrierPlanner.SwapchainPassIndex}:Swapchain]";
        if (passMetadata is not null)
            foreach (RenderPassMetadata metadata in passMetadata)
                if (metadata.PassIndex == passIndex)
                    return $"Pass[{passIndex}:{metadata.Name}]";
        return passIndex == int.MinValue
            ? "Pass[Unknown]"
            : $"Pass[{passIndex}]";
    }

    private static string BuildVulkanGpuProfilerOperationLabel(FrameOp operation)
        => operation switch
        {
            ClearOp clear => $"Clear[target={GetTargetName(clear.Target)}; color={clear.ClearColor}; depth={clear.ClearDepth}; stencil={clear.ClearStencil}]",
            BlitOp blit => $"Blit[src={GetTargetName(blit.InFbo)}; dst={GetTargetName(blit.OutFbo)}; color={blit.ColorBit}; depth={blit.DepthBit}; stencil={blit.StencilBit}]",
            MeshDrawOp draw => BuildVulkanGpuProfilerMeshDrawLabel(draw),
            QueryOp query => $"Query[{query.Operation}; descriptor={query.Descriptor}; fbo={GetTargetName(query.Target)}]",
            IndirectDrawOp indirect => $"IndirectDraw[count={indirect.DrawCount}; stride={indirect.Stride}; useCount={indirect.UseCount}]",
            MeshTaskDispatchIndirectCountOp meshTask => $"MeshTaskDispatchIndirectCount[max={meshTask.MaxDrawCount}; stride={meshTask.Stride}]",
            TransformFeedbackOp transformFeedback => $"TransformFeedback[{transformFeedback.Operation}; target={GetTargetName(transformFeedback.Target)}]",
            ComputeDispatchOp compute => $"ComputeDispatch[program={GetDisplayName(compute.Program.Data.Name, "UnnamedProgram")}; groups={compute.GroupsX}x{compute.GroupsY}x{compute.GroupsZ}]",
            ComputeDispatchIndirectOp computeIndirect => $"ComputeDispatchIndirect[program={GetDisplayName(computeIndirect.Program.Data.Name, "UnnamedProgram")}; offset={computeIndirect.ArgumentOffset}]",
            BufferCopyOp copy => $"BufferCopy[bytes={copy.ByteCount}; srcOffset={copy.SourceOffset}; dstOffset={copy.DestinationOffset}]",
            SubmissionMarkerOp marker => $"SubmissionMarker[label={marker.Label}]",
            DlssFrameGenerationOp frameGeneration => $"DLSS.FrameGenerationInputs[{frameGeneration.Parameters.InputWidth}x{frameGeneration.Parameters.InputHeight}->{frameGeneration.Parameters.OutputWidth}x{frameGeneration.Parameters.OutputHeight}]",
            MemoryBarrierOp barrier => $"MemoryBarrier[mask={barrier.Mask}]",
            PublishFramebufferForSamplingOp publish => $"PublishFramebufferForSampling[fbo={GetTargetName(publish.FrameBuffer)}]",
            _ => operation.GetType().Name,
        };

    private static string BuildVulkanGpuProfilerMeshDrawLabel(MeshDrawOp draw)
    {
        var meshRenderer = draw.Draw.Renderer.MeshRenderer;
        string meshName = GetDisplayName(meshRenderer.Mesh?.Name, "UnnamedMesh");
        string materialName = GetDisplayName(
            (draw.Draw.MaterialOverride ?? meshRenderer.Material)?.Name,
            "UnnamedMaterial");
        return $"MeshDraw[mesh={meshName}; material={materialName}; target={GetTargetName(draw.Target)}; instances={draw.Draw.Instances}]";
    }

    private static string GetTargetName(XRFrameBuffer? target)
        => target is null
            ? "Swapchain"
            : GetDisplayName(target.Name, "UnnamedFbo");

    private static string GetDisplayName(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static ImageSubresourceRange CreateOpenXrRuntimeColorSubresourceRange()
        => new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

    private void RecordOpenXrExternalImageReleasePending(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range)
    {
        if (TryRecordExternalImageOwnershipDelta(
                commandBuffer,
                image,
                range,
                EVulkanExternalImageOwnership.OpenXrRuntimeReleasePending))
        {
            return;
        }

        if (TryGetRecordedImageAccessState(
                commandBuffer,
                image,
                in range,
                out VulkanImageAccessState entryState) &&
            entryState.Layout != ImageLayout.Undefined)
        {
            VulkanImageAccessState releaseState = entryState with
            {
                ExternalOwnership = EVulkanExternalImageOwnership.OpenXrRuntimeReleasePending,
            };
            PrimaryCommandEncoder.RecordImageAccess(
                commandBuffer,
                image,
                in range,
                in releaseState);
            return;
        }

        throw new InvalidOperationException(
            $"OpenXR command buffer 0x{commandBuffer.Handle:X} did not record or inherit a final state for runtime image 0x{image.Handle:X}.");
    }

    private bool HasCurrentSecondaryDescriptorPayloadRequirements(CommandBuffer secondary)
    {
        if (secondary.Handle == 0)
            return false;

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        lock (Synchronization._vulkanImageLayoutLock)
        {
            bool hasRecordedState =
                Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)secondary.Handle),
                    out VulkanRecordedImageLayoutState? recorded);
            bool hasLifetime =
                ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    unchecked((ulong)secondary.Handle),
                    out VulkanCommandBufferLifetimeRecord? lifetime);
            if (!hasRecordedState || recorded is null ||
                !hasLifetime || lifetime is null)
            {
                if (FrameDataReuseDiagnosticsEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.CommandChains.DescriptorPayload.MissingTracking.{secondary.Handle}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan.CommandChains] Secondary descriptor payload requirements are unavailable commandBuffer=0x{0:X} hasImageState={1} hasLifetime={2}.",
                        secondary.Handle,
                        hasRecordedState,
                        hasLifetime);
                }
                return false;
            }

            foreach (KeyValuePair<VulkanResourceLifetimeKey, ulong> dependency in
                     lifetime.TouchedDependencies)
            {
                if (dependency.Key.Type != ObjectType.DescriptorSet)
                    continue;
                bool hasRecordedPayload =
                    recorded.SecondaryDescriptorImagePayloadGenerations.TryGetValue(
                        dependency.Key.Handle,
                        out ulong recordedPayloadGeneration);
                bool hasCurrentPayload =
                    ResourceRuntime.Lifetime.Tracker.PublishedDescriptorSets.TryGetValue(
                        dependency.Key.Handle,
                        out VulkanPublishedDescriptorSetSnapshot? current);
                if (!hasRecordedPayload ||
                    !hasCurrentPayload || current is null ||
                    current.ImagePayloadGeneration != recordedPayloadGeneration)
                {
                    if (FrameDataReuseDiagnosticsEnabled)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.CommandChains.DescriptorPayload.Mismatch.{secondary.Handle}.{dependency.Key.Handle}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan.CommandChains] Secondary descriptor image payload changed commandBuffer=0x{0:X} descriptorSet=0x{1:X} hasRecorded={2} recordedGeneration={3} hasCurrent={4} currentGeneration={5}.",
                            secondary.Handle,
                            dependency.Key.Handle,
                            hasRecordedPayload,
                            recordedPayloadGeneration,
                            hasCurrentPayload,
                            current?.ImagePayloadGeneration ?? 0UL);
                    }
                    return false;
                }
            }
            return true;
        }
    }

    private void TransitionSecondaryDescriptorImagesForExecution(
        CommandBuffer primary,
        CommandBuffer secondary)
        => TransitionSecondaryDescriptorImagesForExecution(
            PrimaryCommandEncoder,
            FrameTelemetry,
            primary,
            secondary);

    private void TransitionSecondaryDescriptorImagesForExecution(
        CommandBuffer primary,
        CommandBuffer[] secondaryBuffers,
        int secondaryCount)
    {
        int count = Math.Min(secondaryCount, secondaryBuffers.Length);
        for (int index = 0; index < count; index++)
            TransitionSecondaryDescriptorImagesForExecution(primary, secondaryBuffers[index]);
    }

    private void CmdExecuteCommandsTracked(
        CommandBuffer primary,
        uint commandBufferCount,
        CommandBuffer* secondaryCommandBuffers)
    {
        if (commandBufferCount == 0 || secondaryCommandBuffers is null)
            return;

        for (uint index = 0; index < commandBufferCount; index++)
        {
            CommandBuffer secondary = secondaryCommandBuffers[index];
            PrimaryCommandEncoder.Track(
                primary,
                ObjectType.CommandBuffer,
                unchecked((ulong)secondary.Handle));
            // A secondary is never submitted directly, so its final image states
            // become globally visible only through the primary that executes it.
            // Without this merge every re-record sees the same missing entry
            // state and the supposedly reusable secondary is rejected forever.
            MergeSecondaryImageStatesForExecution(primary, secondary, FrameTelemetry);
        }
        Api.CmdExecuteCommands(primary, commandBufferCount, secondaryCommandBuffers);
        InvalidatePrimaryBindStateAfterSecondaryExecution(primary);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExecuteSecondaryCommandBuffers(commandBufferCount);
    }

    private void CmdBeginRenderPassTracked(
        CommandBuffer commandBuffer,
        RenderPassBeginInfo* beginInfo,
        SubpassContents contents)
    {
        if (beginInfo is not null)
        {
            PrimaryCommandEncoder.Track(commandBuffer, ObjectType.RenderPass, beginInfo->RenderPass.Handle);
            PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Framebuffer, beginInfo->Framebuffer.Handle);
        }
        Api.CmdBeginRenderPass(commandBuffer, beginInfo, contents);
    }

    private void LogCommandChainSecondaryInheritanceMismatch(
        string chainName,
        XRFrameBuffer? target,
        int passIndex,
        string reason)
    {
        if (!CommandChainsEnabledForCurrentRecording && !CommandChainValidationEnabled)
            return;
        Debug.VulkanWarningEvery(
            $"Vulkan.CommandChains.SecondaryInheritance.{chainName}.{passIndex}.{target?.GetHashCode() ?? 0}.{reason.GetHashCode(StringComparison.Ordinal)}",
            TimeSpan.FromSeconds(2),
            "[Vulkan.CommandChains] Secondary inheritance mismatch chain={0} target='{1}' pass={2}: {3}",
            chainName,
            target?.Name ?? "<swapchain>",
            passIndex,
            reason);
    }

    private static bool IsProducerCompleteIndirectBuffer(VkDataBuffer buffer)
        => buffer.BufferHandle is { Handle: not 0 } &&
           buffer.IsReadyForRendering &&
           !buffer.HasPendingUpload;

    private ulong CaptureProducerCompleteIndirectBufferIdentity(VkDataBuffer? buffer)
    {
        if (buffer?.BufferHandle is not { } nativeBuffer || nativeBuffer.Handle == 0)
            return 0;

        FrameOpSignatureHasher hash = new();
        hash.Add(nativeBuffer.Handle);
        hash.Add(GetCurrentVulkanResourceGeneration(ObjectType.Buffer, nativeBuffer.Handle));
        hash.Add(buffer.AllocatedByteSize);
        hash.Add((ulong)buffer.LastUsageFlags);
        return hash.ToHash();
    }

    internal EVulkanIndirectSecondaryEligibility EvaluateIndirectSecondaryRecordingContract(
        IndirectDrawOp operation)
    {
        VulkanIndirectSecondaryRecordingContract contract = operation.SecondaryRecordingContract;
        if (!contract.IsEligible)
            return contract.Eligibility == EVulkanIndirectSecondaryEligibility.NotEvaluated
                ? EVulkanIndirectSecondaryEligibility.MutableCurrentFrame
                : contract.Eligibility;

        if (!IsProducerCompleteIndirectBuffer(operation.IndirectBuffer) ||
            operation.UseCount &&
            (operation.ParameterBuffer is null || !IsProducerCompleteIndirectBuffer(operation.ParameterBuffer)))
        {
            return EVulkanIndirectSecondaryEligibility.ProducerIncomplete;
        }

        if (CaptureProducerCompleteIndirectBufferIdentity(operation.IndirectBuffer) != contract.IndirectBufferIdentity ||
            CaptureProducerCompleteIndirectBufferIdentity(operation.ParameterBuffer) != contract.ParameterBufferIdentity)
        {
            return EVulkanIndirectSecondaryEligibility.BufferIdentityChanged;
        }

        return IsIndirectSecondaryRangeValid(
            operation.IndirectBuffer,
            operation.ParameterBuffer,
            operation.DrawCount,
            operation.Stride,
            operation.ByteOffset,
            operation.CountByteOffset,
            operation.UseCount)
                ? EVulkanIndirectSecondaryEligibility.EligibleProducerComplete
                : EVulkanIndirectSecondaryEligibility.InvalidRange;
    }

    private static bool IsIndirectSecondaryRangeValid(
        VkDataBuffer indirectBuffer,
        VkDataBuffer? parameterBuffer,
        uint drawCount,
        uint stride,
        nuint byteOffset,
        nuint countByteOffset,
        bool useCount)
    {
        const ulong IndexedIndirectCommandSize = 5UL * sizeof(uint);
        if (drawCount == 0 || stride < IndexedIndirectCommandSize || (stride & 3u) != 0)
            return false;

        ulong commandOffset = byteOffset;
        ulong lastCommandDelta = (ulong)(drawCount - 1u) * stride;
        if (lastCommandDelta > ulong.MaxValue - IndexedIndirectCommandSize ||
            commandOffset > ulong.MaxValue - (lastCommandDelta + IndexedIndirectCommandSize))
        {
            return false;
        }

        ulong indirectEnd = commandOffset + lastCommandDelta + IndexedIndirectCommandSize;
        if (indirectEnd > indirectBuffer.UploadedByteCount || indirectEnd > indirectBuffer.AllocatedByteSize)
            return false;
        if (!useCount)
            return true;
        if (parameterBuffer is null || (countByteOffset & 3u) != 0)
            return false;

        ulong countOffset = countByteOffset;
        if (countOffset > ulong.MaxValue - sizeof(uint))
            return false;
        ulong countEnd = countOffset + sizeof(uint);
        return countEnd <= parameterBuffer.UploadedByteCount &&
               countEnd <= parameterBuffer.AllocatedByteSize;
    }
    private readonly struct ComputeDispatchPushConstants(
        uint groupsX,
        uint groupsY,
        uint groupsZ,
        uint debugFlags)
    {
        public readonly uint GroupsX = groupsX;
        public readonly uint GroupsY = groupsY;
        public readonly uint GroupsZ = groupsZ;
        public readonly uint DebugFlags = debugFlags;
    }

    private static void WriteFrozenClearValues(
        ClearValue* destination,
        uint attachmentCount,
        in VulkanCommandClearStateSnapshot clearState)
    {
        if (attachmentCount == 0)
            return;

        destination[0] = new ClearValue
        {
            Color = new ClearColorValue(
                clearState.ClearColor.R,
                clearState.ClearColor.G,
                clearState.ClearColor.B,
                clearState.ClearColor.A),
        };
        if (attachmentCount <= 1)
            return;

        destination[1] = new ClearValue
        {
            DepthStencil = new ClearDepthStencilValue(
                clearState.ClearDepth,
                clearState.ClearStencil),
        };
    }

    internal bool CanRecordCommandBufferDebugLabels
        => DeviceContext.DebugUtils is not null &&
           FrameTelemetry._diagnosticOptions.EnableCommandBufferLabels;

    internal bool CmdBeginLabel(CommandBuffer commandBuffer, string name)
    {
        if (!CanRecordCommandBufferDebugLabels)
            return false;

        nint namePointer = SilkMarshal.StringToPtr(name);
        try
        {
            DebugUtilsLabelEXT label = new()
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)namePointer,
            };
            DeviceContext.DebugUtils!.CmdBeginDebugUtilsLabel(
                commandBuffer,
                in label);
            return true;
        }
        finally
        {
            SilkMarshal.Free(namePointer);
        }
    }

    internal void CmdEndLabel(CommandBuffer commandBuffer)
    {
        if (CanRecordCommandBufferDebugLabels)
            DeviceContext.DebugUtils!.CmdEndDebugUtilsLabel(commandBuffer);
    }

    internal void CmdPipelineBarrierTracked(
        CommandBuffer commandBuffer,
        PipelineStageFlags sourceStages,
        PipelineStageFlags destinationStages,
        DependencyFlags dependencyFlags,
        uint memoryBarrierCount,
        MemoryBarrier* memoryBarriers,
        uint bufferBarrierCount,
        BufferMemoryBarrier* bufferBarriers,
        uint imageBarrierCount,
        ImageMemoryBarrier* imageBarriers,
        [CallerMemberName] string? caller = null)
    {
        _ = caller;
        PrimaryCommandEncoder.PipelineBarrier(
            commandBuffer,
            sourceStages,
            destinationStages,
            dependencyFlags,
            memoryBarrierCount,
            memoryBarriers,
            bufferBarrierCount,
            bufferBarriers,
            imageBarrierCount,
            imageBarriers);
    }

    internal void CmdCopyBufferTracked(
        CommandBuffer commandBuffer,
        Buffer source,
        Buffer destination,
        uint regionCount,
        BufferCopy* regions)
        => PrimaryCommandEncoder.CopyBuffer(
            commandBuffer,
            source,
            destination,
            regionCount,
            regions);

    internal void TrackVulkanCommandBufferResource(
        CommandBuffer commandBuffer,
        ObjectType type,
        ulong handle,
        string owner)
    {
        _ = owner;
        PrimaryCommandEncoder.Track(commandBuffer, type, handle);
    }

    internal VulkanTrackedCommandEncoder CreateQueryCommandEncoder()
        => PrimaryCommandEncoder;

    internal void WriteTimestamp2(
        CommandBuffer commandBuffer,
        PipelineStageFlags2 stage,
        QueryPool queryPool,
        uint query)
    {
        PrimaryCommandEncoder.Track(
            commandBuffer,
            ObjectType.QueryPool,
            queryPool.Handle);
        if (DeviceContext.InstanceApiVersion >= Vk.Version13)
        {
            Api.CmdWriteTimestamp2(commandBuffer, stage, queryPool, query);
            return;
        }

        if (DeviceContext.ExtensionFunctions.KhrSynchronization2 is not { } synchronization2)
            throw new InvalidOperationException(
                "VK_KHR_synchronization2 command extension is unavailable.");
        synchronization2.CmdWriteTimestamp2(commandBuffer, stage, queryPool, query);
    }

    internal void RegisterSubmissionMarker(
        CommandBuffer commandBuffer,
        VulkanTimelineGpuFence fence)
    {
        lock (Synchronization._submissionMarkerLock)
        {
            if (!Synchronization._submissionMarkersByCommandBuffer.TryGetValue(
                    commandBuffer.Handle,
                    out List<VulkanTimelineGpuFence>? markers))
            {
                markers = [];
                Synchronization._submissionMarkersByCommandBuffer.Add(
                    commandBuffer.Handle,
                    markers);
            }

            markers.Add(fence);
        }
    }

    internal void RecordVulkanCommandDiagnosticMarker(
        CommandBuffer commandBuffer,
        FrameOp operation,
        int passIndex,
        int batchIndex)
    {
        _ = commandBuffer;
        _ = operation;
        _ = passIndex;
        _ = batchIndex;
    }

    internal void RecordComputeDispatchIndirectOp(
        CommandBuffer commandBuffer,
        uint imageIndex,
        ComputeDispatchIndirectOp operation)
    {
        Pipeline pipeline = operation.Program.ComputePipeline;
        if (pipeline.Handle == 0)
            throw new InvalidOperationException(
                $"Compute pipeline '{operation.Program.Data.Name ?? "UnnamedProgram"}' is unavailable.");

        BindPipelineTracked(commandBuffer, PipelineBindPoint.Compute, pipeline);
        EnsureComputeStorageImageLayoutsForDispatch(
            commandBuffer,
            operation.Snapshot);
        PushConstantsTracked(
            commandBuffer,
            operation.Program.PipelineLayout,
            CommonPushConstantStageFlags,
            0,
            new ComputeDispatchPushConstants(0u, 0u, 0u, 0u));

        if (!operation.Program.TryBuildAndBindComputeDescriptorSets(
                CreateProgramRecordingRequest(commandBuffer),
                imageIndex,
                operation.Snapshot,
                0,
                out _,
                out DescriptorSet[] boundDescriptorSets,
                out IReadOnlyList<(Buffer buffer, DeviceMemory memory)> temporaryBuffers))
        {
            foreach ((Buffer buffer, DeviceMemory memory) in temporaryBuffers)
                DestroyBuffer(buffer, memory);
            throw new InvalidOperationException(
                $"Descriptor binding failed for indirect compute program '{operation.Program.Data.Name ?? "UnnamedProgram"}'.");
        }

        _commandBufferRecordingScratch.Value!.PreparedComputePayload =
            new VulkanPreparedComputePayload(boundDescriptorSets);
        RegisterComputeTransientUniformBuffers(imageIndex, temporaryBuffers);
        TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.Buffer,
            operation.ArgumentBuffer.Handle,
            $"{operation.Label}.Arguments");
        Api.CmdDispatchIndirect(
            commandBuffer,
            operation.ArgumentBuffer,
            operation.ArgumentOffset);
    }

    internal void RecordBufferCopyOp(
        CommandBuffer commandBuffer,
        BufferCopyOp operation)
    {
        BufferCopy copy = new()
        {
            SrcOffset = operation.SourceOffset,
            DstOffset = operation.DestinationOffset,
            Size = operation.ByteCount,
        };
        CmdCopyBufferTracked(
            commandBuffer,
            operation.SourceBuffer,
            operation.DestinationBuffer,
            1,
            &copy);
    }
}
