using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Completes every graphics-pipeline requirement captured by a sealed
    /// PresentNow frame plan before a desktop image is acquired. The supplied
    /// target is symbolic compatibility state only: it contains no acquired
    /// image ownership and is therefore safe to use during pre-acquire work.
    /// </summary>
    internal bool TryPreparePresentNowPipelinesForSealedFramePlan(
        FramePlan framePlan,
        FrameOperationSequence staticOperations,
        FrameOperationSequence dynamicOverlayOperations,
        in SwapchainRecordingTarget compatibilityTarget,
        in VulkanPreparedResourcePlanStamp resourcePlanStamp,
        in VulkanRenderGraphPlan renderGraphPlan,
        bool useDynamicRendering,
        bool preserveSwapchainForOverlay,
        ref VulkanPresentNowReadinessWatchdog watchdog,
        out bool retryable,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(framePlan);
        retryable = false;
        if (!framePlan.IsSealed)
            throw new VulkanPlanPreconditionException(
                "PresentNow pipeline readiness requires a sealed frame plan.");
        if (!compatibilityTarget.IsValid)
        {
            reason = "PresentNow pipeline readiness has no symbolic swapchain compatibility target";
            return false;
        }

        if (!TryPreparePresentNowPipelineSequence(
            framePlan,
            staticOperations,
            in compatibilityTarget,
            in resourcePlanStamp,
            in renderGraphPlan,
            useDynamicRendering,
            includeSwapchainDepth: true,
            framePlan.StaticOperationSignature,
            ref watchdog,
            out retryable,
            out reason))
        {
            return false;
        }

        return TryPreparePresentNowPipelineSequence(
            framePlan,
            dynamicOverlayOperations,
            in compatibilityTarget,
            in resourcePlanStamp,
            in renderGraphPlan,
            useDynamicRendering,
            includeSwapchainDepth: !preserveSwapchainForOverlay,
            framePlan.DynamicOverlaySignature,
            ref watchdog,
            out retryable,
            out reason);
    }

    private bool TryPreparePresentNowPipelineSequence(
        FramePlan framePlan,
        FrameOperationSequence operations,
        in SwapchainRecordingTarget compatibilityTarget,
        in VulkanPreparedResourcePlanStamp resourcePlanStamp,
        in VulkanRenderGraphPlan renderGraphPlan,
        bool useDynamicRendering,
        bool includeSwapchainDepth,
        ulong recordingStructuralSignature,
        ref VulkanPresentNowReadinessWatchdog watchdog,
        out bool retryable,
        out string reason)
    {
        retryable = false;
        reason = string.Empty;
        if (operations.Length == 0)
            return true;

        CommandBufferRecordingScratch scratch = _commandBufferRecordingScratch.Value
            ?? throw new VulkanPlanPreconditionException(
                "PresentNow pipeline readiness has no command-recording scratch state.");
        scoped PrimaryCommandBufferRecordingState recordingState = default;
        recordingState.Ops = operations;
        recordingState.FramePlan = framePlan;
        recordingState.SwapchainTarget = includeSwapchainDepth
            ? compatibilityTarget
            : compatibilityTarget with { DepthFormat = Format.Undefined };
        recordingState.ResourcePlanStamp = resourcePlanStamp;
        recordingState.RenderGraphPlan = renderGraphPlan;
        recordingState.RecordingScratch = scratch;
        recordingState.Policy = new VulkanCommandRecordingPolicySnapshot(
            useDynamicRendering,
            AllowSynchronousResourceUploads: true,
            FreshSerialRecording: true,
            IsExternalSwapchainTarget: false,
            PreserveSwapchainForOverlay: !includeSwapchainDepth,
            TransitionSwapchainToPresent: true,
            ReadinessPolicy: ERenderOutputReadinessPolicy.BlockForExact,
            WorkClass: ERenderOutputWorkClass.PresentNow,
            SourceFrameId: framePlan.RenderFrameId,
            AllowArtifactReuse: false,
            AllowSecondaryDeferral: false);
        recordingState.PipelineDeferredOperationIndices =
            scratch.PipelineDeferredOperationIndices;
        recordingState.PipelineDeferredOperationIndices.Clear();

        EMeshSubmissionStrategy submissionStrategy =
            RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();
        VulkanPipelineVariantManifest manifest = GetOrBuildPipelineVariantManifest(
            renderGraphPlan.CompiledGraph.Plan,
            operations,
            submissionStrategy,
            useDynamicRendering,
            recordingStructuralSignature);
        for (int index = 0; index < manifest.Requirements.Count; index++)
        {
            VulkanPipelineVariantRequirement requirement = manifest.Requirements[index];
            if (TryPreparePrimaryPipelineRequirement(
                    ref recordingState,
                    in requirement,
                    out bool optionalDeferred,
                    out bool requirementRetryable,
                    out string pendingReason))
            {
                watchdog.RecordProgress();
                continue;
            }

            retryable = requirementRetryable;
            reason = optionalDeferred
                ? $"optional PresentNow pipeline requirement was not ready: {pendingReason}"
                : $"required PresentNow pipeline requirement {index} was not ready: {pendingReason}";
            return false;
        }

        if (!TryAssociatePrimaryMeshTaskPipelines(ref recordingState, manifest))
        {
            reason = recordingState.RecordingDeferredReason;
            return false;
        }

        watchdog.RecordProgress();
        manifest.MarkWarmupCompleted();
        return true;
    }

    private VulkanPipelineVariantManifest GetOrBuildPipelineVariantManifest(
        VulkanCompiledRenderGraphPlan plan,
        FrameOperationSequence operations,
        EMeshSubmissionStrategy submissionStrategy,
        bool dynamicRendering,
        ulong recordingStructuralSignature)
        => ResourceRuntime.PipelineManager.GetOrBuildVariantManifest(
            plan,
            operations,
            submissionStrategy,
            dynamicRendering,
            recordingStructuralSignature);

    private bool TryResolveGraphicsPipelinePrewarmTarget(
        XRFrameBuffer? target,
        int passIndex,
        in FrameOpContext context,
        in SwapchainRecordingTarget swapchainTarget,
        bool useDynamicRenderingRenderTargets,
        VulkanCompiledRenderGraph compiledGraph,
        out bool useDynamicRendering,
        out RenderPass renderPass,
        out DynamicRenderingFormatSignature dynamicRenderingFormats,
        out SampleCountFlags rasterizationSamples,
        out bool depthStencilReadOnly,
        out string reason)
    {
        useDynamicRendering = false;
        renderPass = default;
        dynamicRenderingFormats = default;
        rasterizationSamples = SampleCountFlags.Count1Bit;
        depthStencilReadOnly = false;
        reason = string.Empty;

        if (target is null)
        {
            useDynamicRendering = useDynamicRenderingRenderTargets && swapchainTarget.IsValid;
            if (useDynamicRendering)
            {
                dynamicRenderingFormats = CreateSwapchainDynamicRenderingFormatSignature(
                    swapchainTarget.ImageFormat,
                    swapchainTarget.DepthFormat);
                return true;
            }

            renderPass = ResourceRuntime.SwapchainRenderPass;
            if (renderPass.Handle != 0)
                return true;

            reason = "legacy swapchain render pass is unavailable";
            return false;
        }

        VkFrameBuffer? frameBuffer = GenericToAPI<VkFrameBuffer>(target);
        if (frameBuffer is null)
        {
            reason = $"target '{target.Name ?? "<unnamed>"}' has no prepared Vulkan framebuffer";
            return false;
        }

        if (!frameBuffer.IsActive)
        {
            reason = $"target '{target.Name ?? "<unnamed>"}' has no prepared native framebuffer state";
            return false;
        }
        ImageLayout[]? trackedLayouts = QueryCurrentAttachmentLayouts(target, frameBuffer);
        FrameBufferAttachmentSignature[] attachmentSignature =
            frameBuffer.ResolveAttachmentSignatureForPass(
                passIndex,
                context.PassMetadata,
                trackedLayouts,
                compiledGraph.Synchronization,
                preserveTrackedClearLoads: false);
        rasterizationSamples = ResolveDynamicRenderingSampleCount(
            attachmentSignature);
        depthStencilReadOnly = VkFrameBuffer.UsesReadOnlyDepthStencil(attachmentSignature);

        if (useDynamicRenderingRenderTargets)
        {
            useDynamicRendering = true;
            uint viewMask = frameBuffer.MultiviewViewMask;
            dynamicRenderingFormats = CreateDynamicRenderingFormatSignature(
                attachmentSignature,
                viewMask,
                VulkanDynamicRenderingUtilities.ResolveLayerCount(
                    frameBuffer.FramebufferLayers,
                    viewMask));
            return true;
        }

        renderPass = frameBuffer.ResolveRenderPassForPass(
            passIndex,
            context.PassMetadata,
            trackedLayouts,
            compiledGraph.Synchronization,
            preserveTrackedClearLoads: false);
        if (renderPass.Handle != 0)
            return true;

        reason = $"target '{target.Name ?? "<unnamed>"}' has no compatible legacy render pass";
        return false;
    }
}
