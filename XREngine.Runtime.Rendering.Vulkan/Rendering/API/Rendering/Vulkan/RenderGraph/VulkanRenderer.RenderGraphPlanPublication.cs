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
    {
        VulkanRenderGraphPlan currentPlan = state.RenderGraphPlan;
        if (currentPlan.Revision == state.ResourcePlannerRevision &&
            ReferenceEquals(currentPlan.CompiledGraph, state.CompiledRenderGraph) &&
            currentPlan.Barriers.HasCompleteNativeBindings)
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
                    out string bufferResolutionFailure))
            {
                // Conditional-feature metadata and external imports may describe a
                // resource that is absent in this planner generation (for example,
                // ReSTIR when disabled or light-probe buffers when no probes exist).
                // A declaration without a live native resource has nothing to
                // synchronize. Binding it later advances the pipeline resource
                // generation and publishes a new plan containing the real barrier.
                if (CanOmitUnboundRenderGraphBuffer(
                        barrier.ResourceName,
                        in state))
                {
                    continue;
                }

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

        VulkanBarrierPlan frozenBarriers = new(
            NextBarrierPlanGeneration(),
            frozenImageBarriers,
            frozenBufferBarriers,
            frozenSwapchainBarriers);
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
        out string failureReason)
    {
        if (plannerState.ResourceAllocator.TryGetBuffer(
                resourceName,
                out nativeBuffer,
                out nativeSize) &&
            nativeBuffer.Handle != 0)
        {
            nativeSize = Math.Max(nativeSize, 1UL);
            failureReason = string.Empty;
            return true;
        }

        XRDataBuffer? dataBuffer = null;
        string bindingSource = string.Empty;
        if (plannerState.LastActiveFrameOpContext is { } ownerContext)
        {
            _ = ownerContext.ResourceRegistry?.TryGetBuffer(
                resourceName,
                out dataBuffer);
            if (dataBuffer is not null)
                bindingSource = "generation registry";
            if (dataBuffer is null)
            {
                _ = ownerContext.PipelineInstance?.TryGetBuffer(
                    resourceName,
                    out dataBuffer);
                if (dataBuffer is not null)
                    bindingSource = "pipeline instance";
            }
        }

        // Legacy buffers may be registered directly by their backend object rather
        // than through a pipeline registry. Keep that compatibility route after the
        // exact generation-owned lookup; it must not override a context binding.
        if (dataBuffer is null)
        {
            TrackedBuffersByName.TryGetValue(
                resourceName,
                out dataBuffer);
            if (dataBuffer is not null)
                bindingSource = "legacy tracked binding";
        }

        if (dataBuffer is null)
        {
            nativeBuffer = default;
            nativeSize = 0;
            failureReason = plannerState.LastActiveFrameOpContext is null
                ? "the planner generation has no owning context or tracked binding"
                : "the owning context, pipeline instance, and tracked bindings have no live buffer";
            return false;
        }

        if (backendContext is null)
        {
            nativeBuffer = default;
            nativeSize = 0;
            failureReason = $"the {bindingSource} buffer has no Vulkan backend context";
            return false;
        }

        if (backendContext.GetOrCreateAPIRenderObject(
                dataBuffer,
                generateNow: allowSynchronousResourceUploads) is not VkDataBuffer vkBuffer)
        {
            nativeBuffer = default;
            nativeSize = 0;
            failureReason = $"the {bindingSource} buffer has no Vulkan backend object";
            return false;
        }

        if (allowSynchronousResourceUploads)
            _ = vkBuffer.TryEnsureReadyForRendering(allowSynchronousUpload: true);
        if (vkBuffer.BufferHandle is not { } resolvedBuffer || resolvedBuffer.Handle == 0)
        {
            nativeBuffer = default;
            nativeSize = 0;
            failureReason =
                $"the {bindingSource} Vulkan buffer is not ready " +
                $"(syncUploads={allowSynchronousResourceUploads}, " +
                $"ready={vkBuffer.IsReadyForRendering}, pendingUpload={vkBuffer.HasPendingUpload})";
            return false;
        }

        nativeBuffer = resolvedBuffer;
        nativeSize = Math.Max(
            Math.Max(vkBuffer.AllocatedByteSize, dataBuffer.Length),
            1UL);
        failureReason = string.Empty;
        return true;
    }
}
