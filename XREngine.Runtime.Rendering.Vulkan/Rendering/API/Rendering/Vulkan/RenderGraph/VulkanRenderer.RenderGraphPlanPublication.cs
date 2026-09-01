using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan.RenderGraph;

internal sealed partial class VulkanFramePlanner
{
    /// <summary>
    /// Captures one immutable graph/barrier publication from the exact planner
    /// state that owns its physical resources. This runs only when a planner
    /// generation changes; steady-state frame recording reuses the publication.
    /// </summary>
    internal bool TryFreezeResourcePlannerRenderGraphPlan(
        ref ResourcePlannerRuntimeState state,
        VulkanBackendObjectContext? backendContext,
        bool allowSynchronousResourceUploads,
        out string reason)
		=> TryFreezeResourcePlannerRenderGraphPlan(ref state, backendContext,
			allowSynchronousResourceUploads, out reason, out _);

    internal bool TryFreezeResourcePlannerRenderGraphPlan(
        ref ResourcePlannerRuntimeState state,
        VulkanBackendObjectContext? backendContext,
        bool allowSynchronousResourceUploads,
        out string reason,
        out bool nativeBindingsSuperseded)
    {
		nativeBindingsSuperseded = false;
        VulkanRenderGraphPlan currentPlan = state.RenderGraphPlan;
        ulong nativeBufferBindingRevision = backendContext?.Resources.NativeBufferBindingRevision ?? 0UL;
        if (currentPlan.Revision == state.ResourcePlannerRevision &&
            ReferenceEquals(currentPlan.CompiledGraph, state.CompiledRenderGraph) &&
            currentPlan.Barriers.HasCompleteNativeBindings &&
            currentPlan.Barriers.NativeBufferBindingRevision == nativeBufferBindingRevision)
        {
            reason = string.Empty;
            return true;
        }

        VulkanBarrierPlanner barrierPlanner = state.BarrierPlanner;
        IReadOnlyList<VulkanBarrierPlanner.PlannedImageBarrier> imageBarriers =
            barrierPlanner.ImageBarriers;
        VulkanBarrierPlanner.PlannedImageBarrier[] frozenImageBarriers =
            new VulkanBarrierPlanner.PlannedImageBarrier[imageBarriers.Count];
        for (int index = 0; index < imageBarriers.Count; index++)
        {
            VulkanBarrierPlanner.PlannedImageBarrier barrier = imageBarriers[index];
            Image nativeImage = barrier.Group.Image;
            if (nativeImage.Handle == 0)
            {
                reason =
                    $"Resource plan {state.ResourcePlannerRevision} cannot freeze image barrier '{barrier.ResourceName}'.";
                return false;
            }

            frozenImageBarriers[index] = barrier with
            {
                NativeImage = nativeImage,
                NativeFormat = barrier.Group.Format,
            };
        }

        IReadOnlyList<VulkanBarrierPlanner.PlannedBufferBarrier> bufferBarriers =
            barrierPlanner.BufferBarriers;
        VulkanBarrierPlanner.PlannedBufferBarrier[] frozenBufferBarriers =
            new VulkanBarrierPlanner.PlannedBufferBarrier[bufferBarriers.Count];
        int frozenBufferBarrierCount = 0;
        for (int index = 0; index < bufferBarriers.Count; index++)
        {
            VulkanBarrierPlanner.PlannedBufferBarrier barrier = bufferBarriers[index];
            if (!TryResolveFrozenRenderGraphBuffer(
                    barrier.ResourceName,
                    in state,
                    backendContext,
                    allowSynchronousResourceUploads,
                    out Silk.NET.Vulkan.Buffer nativeBuffer,
                    out ulong nativeSize,
                    out ulong nativeGeneration,
                    out bool nativeBindingSuperseded,
                    out string bufferResolutionFailure))
            {
                // Conditional-feature metadata and external imports may describe a
                // resource that is absent in this planner generation (for example,
                // ReSTIR when disabled or light-probe buffers when no probes exist).
                // A declaration without a live native resource has nothing to
                // synchronize. Binding it later advances the pipeline resource
                // generation and publishes a new plan containing the real barrier.
                if (!nativeBindingSuperseded &&
                    CanOmitUnboundRenderGraphBuffer(
                        barrier.ResourceName,
                        in state))
                {
                    continue;
                }

                nativeBindingsSuperseded = nativeBindingSuperseded;
                reason =
                    $"Resource plan {state.ResourcePlannerRevision} cannot freeze buffer barrier " +
                    $"'{barrier.ResourceName}': {bufferResolutionFailure}";
                return false;
            }

            frozenBufferBarriers[frozenBufferBarrierCount++] = barrier with
            {
                NativeBuffer = nativeBuffer,
                NativeOffset = 0,
                NativeSize = nativeSize,
                NativeGeneration = nativeGeneration,
            };
        }
        if (frozenBufferBarrierCount != frozenBufferBarriers.Length)
            Array.Resize(ref frozenBufferBarriers, frozenBufferBarrierCount);

        IReadOnlyList<VulkanBarrierPlanner.PlannedSwapchainBarrier> swapchainBarriers =
            barrierPlanner.SwapchainBarriers;
        VulkanBarrierPlanner.PlannedSwapchainBarrier[] frozenSwapchainBarriers =
            new VulkanBarrierPlanner.PlannedSwapchainBarrier[swapchainBarriers.Count];
        for (int index = 0; index < swapchainBarriers.Count; index++)
            frozenSwapchainBarriers[index] = swapchainBarriers[index];

        VulkanBarrierPlan frozenBarriers = VulkanBarrierPlan.Capture(
            NextBarrierPlanGeneration(),
            nativeBufferBindingRevision,
            frozenImageBarriers,
            frozenBufferBarriers,
            frozenSwapchainBarriers,
            state.CompiledRenderGraph.Plan.ResourceIds);
        if ((backendContext?.Resources.NativeBufferBindingRevision ?? 0UL) != nativeBufferBindingRevision)
        {
			nativeBindingsSuperseded = true;
            reason = $"Native buffer bindings changed while freezing resource plan {state.ResourcePlannerRevision}; retry with the new binding revision.";
            return false;
        }
        state.RenderGraphPlan = new VulkanRenderGraphPlan(
            state.ResourcePlannerRevision,
            state.CompiledRenderGraph,
            frozenBarriers);
        reason = string.Empty;
        return true;
    }

    private static bool CanOmitUnboundRenderGraphBuffer(
        string resourceName,
        in ResourcePlannerRuntimeState plannerState)
    {
        if (!plannerState.ResourcePlanner.TryGetBufferDescriptor(
                resourceName,
                out BufferResourceDescriptor? descriptor) ||
            descriptor is null)
        {
            // Pass metadata is structural and can mention resources belonging to
            // disabled conditional features. If that resource is absent from this
            // generation's descriptor plan and has no live binding, there is no
            // native object to synchronize.
            return true;
        }

        if (descriptor.Lifetime == RenderResourceLifetime.External)
        {
            return true;
        }

        if (plannerState.LastActiveFrameOpContext is not { } context ||
            context.PipelineInstance is not { } pipeline)
        {
            return false;
        }

        RenderResourceGeneration? generation =
            ReferenceEquals(pipeline.PendingGeneration?.Registry, context.ResourceRegistry)
                ? pipeline.PendingGeneration
                : ReferenceEquals(pipeline.ActiveGeneration?.Registry, context.ResourceRegistry)
                    ? pipeline.ActiveGeneration
                    : null;
        return generation is not null &&
               generation.Layout.ResourcesByName.TryGetValue(
                   resourceName,
                   out RenderPipelineResourceSpec? spec) &&
               spec is ExternalResourceSpec
               {
                   ExternalKind: ExternalRenderResourceKind.Buffer,
               };
    }

    private bool TryResolveFrozenRenderGraphBuffer(
        string resourceName,
        in ResourcePlannerRuntimeState plannerState,
        VulkanBackendObjectContext? backendContext,
        bool allowSynchronousResourceUploads,
        out Silk.NET.Vulkan.Buffer nativeBuffer,
        out ulong nativeSize,
        out ulong nativeGeneration,
        out bool nativeBindingSuperseded,
        out string failureReason)
    {
        nativeBindingSuperseded = false;
        // A sealed context's registry is the authoritative XR resource owner.
        // Never allow a same-named physical allocation or global tracked buffer
        // to replace it: that would freeze a barrier for a different native
        // generation than the command stream's descriptor binding.
        if (plannerState.LastActiveFrameOpContext is { } ownerContext)
        {
            if (ownerContext.ResourceRegistry?.TryGetBuffer(
                    resourceName,
                    out XRDataBuffer? registryBuffer) == true)
            {
                return TryResolveFrozenRenderGraphDataBuffer(
                    registryBuffer,
                    "generation registry",
                    backendContext,
                    allowSynchronousResourceUploads,
                    out nativeBuffer,
                    out nativeSize,
                    out nativeGeneration,
                    out nativeBindingSuperseded,
                    out failureReason);
            }

            if (ownerContext.ResourceRegistry is { } registry &&
                ownerContext.PipelineInstance?.Variables.TryResolveBuffer(
                    registry,
                    resourceName,
                    out XRDataBuffer? pipelineVariableBuffer) == true)
            {
                return TryResolveFrozenRenderGraphDataBuffer(
                    pipelineVariableBuffer,
                    "sealed pipeline variables",
                    backendContext,
                    allowSynchronousResourceUploads,
                    out nativeBuffer,
                    out nativeSize,
                    out nativeGeneration,
                    out nativeBindingSuperseded,
                    out failureReason);
            }

            if (ownerContext.ResourceRegistry is null &&
                ownerContext.PipelineInstance?.TryGetBuffer(
                    resourceName,
                    out XRDataBuffer? legacyPipelineBuffer) == true)
            {
                return TryResolveFrozenRenderGraphDataBuffer(
                    legacyPipelineBuffer,
                    "legacy owner-context pipeline",
                    backendContext,
                    allowSynchronousResourceUploads,
                    out nativeBuffer,
                    out nativeSize,
                    out nativeGeneration,
                    out nativeBindingSuperseded,
                    out failureReason);
            }
        }

        // Contextless legacy publications can still own an XRDataBuffer. A
        // context-bearing plan must never mix this global route with its sealed
        // registry/variable state.
        if (plannerState.LastActiveFrameOpContext is null &&
            TrackedBuffersByName.TryGetValue(resourceName, out XRDataBuffer? trackedBuffer))
        {
            return TryResolveFrozenRenderGraphDataBuffer(
                trackedBuffer,
                "legacy tracked binding",
                backendContext,
                allowSynchronousResourceUploads,
                out nativeBuffer,
                out nativeSize,
                out nativeGeneration,
                out nativeBindingSuperseded,
                out failureReason);
        }

        // No XR-backed or variable binding owns this name, so the planner's
        // physical allocation is the only valid native identity to freeze.
        if (plannerState.ResourceAllocator.TryGetBuffer(
                resourceName,
                out nativeBuffer,
                out nativeSize) &&
            nativeBuffer.Handle != 0)
        {
            nativeSize = Math.Max(nativeSize, 1UL);
            nativeGeneration = backendContext?.Resources.GetPublishedGeneration(ObjectType.Buffer, nativeBuffer.Handle) ?? 0UL;
            if (nativeGeneration != 0UL)
            {
                failureReason = string.Empty;
                return true;
            }
            nativeBindingSuperseded = true;
            failureReason = "native buffer generation is no longer published for the allocator binding";
            nativeBuffer = default;
            nativeSize = 0UL;
            return false;
        }

        nativeBuffer = default;
        nativeSize = 0;
        nativeGeneration = 0;
        failureReason = plannerState.LastActiveFrameOpContext is null
            ? "the planner generation has no contextless XR binding or physical allocator buffer"
            : "the sealed context has no XR buffer and the planner has no physical allocator buffer";
        return false;
    }

    private static bool TryResolveFrozenRenderGraphDataBuffer(
        XRDataBuffer? dataBuffer,
        string bindingSource,
        VulkanBackendObjectContext? backendContext,
        bool allowSynchronousResourceUploads,
        out Silk.NET.Vulkan.Buffer nativeBuffer,
        out ulong nativeSize,
        out ulong nativeGeneration,
        out bool nativeBindingSuperseded,
        out string failureReason)
    {
        nativeBindingSuperseded = false;
        if (dataBuffer is null)
        {
            nativeBuffer = default;
            nativeSize = 0;
            nativeGeneration = 0;
            failureReason = $"the {bindingSource} buffer is absent";
            return false;
        }
        if (backendContext is null)
        {
            nativeBuffer = default;
            nativeSize = 0;
            nativeGeneration = 0;
            failureReason = $"the {bindingSource} buffer has no Vulkan backend context";
            return false;
        }

        if (backendContext.GetOrCreateAPIRenderObject(
                dataBuffer,
                generateNow: allowSynchronousResourceUploads) is not VkDataBuffer vkBuffer)
        {
            nativeBuffer = default;
            nativeSize = 0;
            nativeGeneration = 0;
            failureReason = $"the {bindingSource} buffer has no Vulkan backend object";
            return false;
        }

        if (allowSynchronousResourceUploads)
            _ = vkBuffer.TryEnsureReadyForRendering(allowSynchronousUpload: true);
        // A logical buffer may have grown since the retained native allocation
        // was published. Lookup-only preparation must not inflate a barrier to
        // that new CPU size or consume storage whose upload is still pending.
        if ((!allowSynchronousResourceUploads &&
             (!vkBuffer.IsReadyForRendering || vkBuffer.AllocatedByteSize < dataBuffer.Length)) ||
            vkBuffer.BufferHandle is not { } resolvedBuffer || resolvedBuffer.Handle == 0)
        {
            nativeBuffer = default;
            nativeSize = 0;
            nativeGeneration = 0;
            failureReason =
                $"the {bindingSource} Vulkan buffer is not ready " +
                $"(syncUploads={allowSynchronousResourceUploads}, " +
                $"ready={vkBuffer.IsReadyForRendering}, pendingUpload={vkBuffer.HasPendingUpload})";
            return false;
        }

        nativeBuffer = resolvedBuffer;
        nativeSize = allowSynchronousResourceUploads
            ? Math.Max(Math.Max(vkBuffer.AllocatedByteSize, dataBuffer.Length), 1UL)
            : Math.Max(vkBuffer.AllocatedByteSize, 1UL);
        nativeGeneration = backendContext.Resources.GetPublishedGeneration(ObjectType.Buffer, nativeBuffer.Handle);
        if (nativeGeneration == 0UL)
        {
            nativeBindingSuperseded = true;
            nativeBuffer = default;
            nativeSize = 0UL;
            failureReason = $"native buffer generation was retired before freeze for the {bindingSource} binding";
            return false;
        }
        failureReason = string.Empty;
        return true;
    }
}
