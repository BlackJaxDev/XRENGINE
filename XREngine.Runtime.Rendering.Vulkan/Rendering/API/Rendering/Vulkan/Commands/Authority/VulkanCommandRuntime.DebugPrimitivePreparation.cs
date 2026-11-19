using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private readonly VulkanDebugTargetCompatibility[] _debugTargetCompatibilities =
        new VulkanDebugTargetCompatibility[8];
    private int _nextDebugTargetCompatibility;

    /// <summary>
    /// Prepares a captured debug draw for the framebuffer and graph generation
    /// that own its active pass. This method does not publish a frame operation.
    /// </summary>
    internal EDebugPrimitivePreparationStatus TryPrewarmDebugPrimitivePipeline(
        in VulkanMeshOperationRequest operation,
        ResourcePlannerRuntimeGeneration generation,
        bool useDynamicRenderingRenderTargets,
        out string reason)
    {
        if (!useDynamicRenderingRenderTargets)
        {
            reason = "Debug pipeline preparation requires dynamic framebuffer rendering.";
            return EDebugPrimitivePreparationStatus.Unsupported;
        }

        XRFrameBuffer? target = operation.ProducerSnapshot.Target;
        if (target is null)
        {
            reason = "Debug pipeline preparation has no framebuffer target.";
            return EDebugPrimitivePreparationStatus.Pending;
        }

        FrameOpContext context = operation.Context;
        VkFrameBuffer? frameBuffer = GenericToAPI<VkFrameBuffer>(target);
        if (frameBuffer is null ||
            !frameBuffer.TryCaptureRecordedRenderTargetSnapshot(
                out VulkanRecordedRenderTargetSnapshot nativeTarget))
        {
            reason = "Debug pipeline target has no complete native attachment snapshot.";
            return EDebugPrimitivePreparationStatus.Pending;
        }

        VulkanFrameOpPlannerStateKey plannerKey =
            VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(context);
        bool useDynamicRendering = false;
        RenderPass renderPass = default;
        DynamicRenderingFormatSignature dynamicFormats = default;
        bool depthStencilReadOnly = false;
        bool cached = false;
        for (int index = 0; index < _debugTargetCompatibilities.Length; index++)
        {
            ref VulkanDebugTargetCompatibility entry = ref _debugTargetCompatibilities[index];
            if (!ReferenceEquals(entry.Target, frameBuffer) ||
                !entry.NativeTarget.Equals(nativeTarget) ||
                entry.PassIndex != operation.PassIndex ||
                !entry.PlannerKey.Equals(plannerKey) ||
                !ReferenceEquals(entry.Graph, generation.State.CompiledRenderGraph) ||
                entry.AllocatorOwnershipId != generation.State.AllocatorOwnershipId ||
                entry.FramebufferLayers != frameBuffer.FramebufferLayers ||
                entry.DynamicRenderingEnabled != useDynamicRenderingRenderTargets)
                continue;

            useDynamicRendering = entry.UseDynamicRendering;
            renderPass = entry.RenderPass;
            dynamicFormats = entry.DynamicFormats;
            depthStencilReadOnly = entry.DepthStencilReadOnly;
            cached = true;
            break;
        }

        if (!cached && !TryResolveGraphicsPipelinePrewarmTarget(
                target,
                operation.PassIndex,
                in context,
                default,
                useDynamicRenderingRenderTargets,
                generation.State.CompiledRenderGraph,
                out useDynamicRendering,
                out renderPass,
                out dynamicFormats,
                out _,
                out depthStencilReadOnly,
                out reason))
            return EDebugPrimitivePreparationStatus.Pending;

        if (!cached)
        {
            int slot = _nextDebugTargetCompatibility;
            _nextDebugTargetCompatibility = slot + 1 == _debugTargetCompatibilities.Length
                ? 0
                : slot + 1;
            ref VulkanDebugTargetCompatibility entry = ref _debugTargetCompatibilities[slot];
            entry.Target = frameBuffer;
            entry.NativeTarget = nativeTarget;
            entry.PassIndex = operation.PassIndex;
            entry.PlannerKey = plannerKey;
            entry.Graph = generation.State.CompiledRenderGraph;
            entry.AllocatorOwnershipId = generation.State.AllocatorOwnershipId;
            entry.FramebufferLayers = frameBuffer.FramebufferLayers;
            entry.DynamicRenderingEnabled = useDynamicRenderingRenderTargets;
            entry.UseDynamicRendering = useDynamicRendering;
            entry.RenderPass = renderPass;
            entry.DynamicFormats = dynamicFormats;
            entry.DepthStencilReadOnly = depthStencilReadOnly;
        }

        bool ready = operation.Renderer.TryPrewarmGraphicsPipelinesForRecording(
            operation.Draw,
            renderPass,
            useDynamicRendering,
            dynamicFormats,
            operation.PassIndex,
            context.PassMetadata,
            depthStencilReadOnly,
            context.PipelineInstance?.DebugName ?? "<no pipeline>",
            foregroundRequired: false,
            out bool retryable,
            out reason);
        if (ready)
            return EDebugPrimitivePreparationStatus.Ready;
        return retryable
            ? EDebugPrimitivePreparationStatus.Pending
            : EDebugPrimitivePreparationStatus.Failed;
    }
}
