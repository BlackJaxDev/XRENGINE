using System.Text;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    // Diagnostic consumers may read only images belonging to accepted queue work.
    // Planner switching caches also contain prepared, never-submitted generations.
    private const int DesktopReadbackReceiptCapacity = 32;
    private const int DesktopReadbackMismatchReceiptsDescribed = 3;
    private readonly VulkanDesktopReadbackReceipt[] _desktopReadbackReceipts =
        new VulkanDesktopReadbackReceipt[DesktopReadbackReceiptCapacity];

    /// <summary>
    /// Describes why no submitted desktop receipt matches a readback context: the newest
    /// receipts recorded for the same pipeline with the planner-key fields that differ, or
    /// the receipt population when the pipeline has none. Cold path for readback failures.
    /// </summary>
    private string DescribeDesktopReadbackMismatch(in FrameOpContext requested)
    {
        VulkanFrameOpPlannerStateKey key = VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(requested);
        StringBuilder message = new(512);
        message.Append(" Requested context: kind=").Append(key.ContextKind)
            .Append(" pipeline=").Append(key.PipelineIdentity)
            .Append(" viewport=").Append(key.ViewportIdentity)
            .Append(" logicalView=").Append(key.LogicalViewId)
            .Append(" resourceGeneration=").Append(key.ResourceGeneration)
            .Append(" outputTarget=").Append(key.OutputTargetIdentity).Append('.');
        int samePipeline = 0;
        int submitted = 0;
        for (int index = 0; index < _desktopReadbackReceipts.Length; index++)
        {
            ref readonly VulkanDesktopReadbackReceipt receipt = ref _desktopReadbackReceipts[index];
            if (receipt.SubmissionSerial == 0)
                continue;
            submitted++;
            if (receipt.Key.PipelineIdentity != key.PipelineIdentity)
                continue;
            if (samePipeline++ >= DesktopReadbackMismatchReceiptsDescribed)
                continue;
            message.Append(" Receipt serial ").Append(receipt.SubmissionSerial).Append(" differs in:");
            VulkanFrameOpPlannerStateKey receiptKey = receipt.Key;
            AppendDesktopReadbackKeyDifferences(message, in key, in receiptKey);
            if (!ReferenceEquals(receipt.Context.ResourceRegistry, requested.ResourceRegistry))
                message.Append(" resourceRegistry");
            if (!ReferenceEquals(receipt.Context.PipelineInstance, requested.PipelineInstance))
                message.Append(" pipelineInstance");
            if (receipt.PlannerState.ResourceAllocator is null)
                message.Append(" allocator=null");
            else if (receipt.PlannerState.ResourceAllocator.IsRetired)
                message.Append(" allocator=retired");
            message.Append('.');
        }
        if (samePipeline == 0)
        {
            message.Append(" No submitted receipt exists for this pipeline; ")
                .Append(submitted).Append(" receipts belong to other pipelines.");
        }
        return message.ToString();
    }

    private static void AppendDesktopReadbackKeyDifferences(
        StringBuilder message,
        in VulkanFrameOpPlannerStateKey requested,
        in VulkanFrameOpPlannerStateKey receipt)
    {
        if (requested.ContextKind != receipt.ContextKind)
            message.Append(" kind=").Append(receipt.ContextKind);
        if (requested.ViewportIdentity != receipt.ViewportIdentity)
            message.Append(" viewport=").Append(receipt.ViewportIdentity);
        if (requested.DisplayWidth != receipt.DisplayWidth || requested.DisplayHeight != receipt.DisplayHeight)
            message.Append(" display=").Append(receipt.DisplayWidth).Append('x').Append(receipt.DisplayHeight);
        if (requested.InternalWidth != receipt.InternalWidth || requested.InternalHeight != receipt.InternalHeight)
            message.Append(" internal=").Append(receipt.InternalWidth).Append('x').Append(receipt.InternalHeight);
        if (requested.OutputFrameBufferIdentity != receipt.OutputFrameBufferIdentity)
            message.Append(" outputFrameBuffer");
        if (requested.OutputTargetIdentity != receipt.OutputTargetIdentity)
            message.Append(" outputTarget=").Append(receipt.OutputTargetIdentity);
        if (requested.LogicalViewId != receipt.LogicalViewId)
            message.Append(" logicalView=").Append(receipt.LogicalViewId);
        if (requested.ResourceRegistrySignature != receipt.ResourceRegistrySignature)
            message.Append(" registrySignature");
        if (requested.ResourceRegistryInstanceRevision != receipt.ResourceRegistryInstanceRevision)
            message.Append(" registryRevision");
        if (requested.PassMetadataSignature != receipt.PassMetadataSignature)
            message.Append(" passMetadata");
        if (requested.ResourceGeneration != receipt.ResourceGeneration)
            message.Append(" resourceGeneration=").Append(receipt.ResourceGeneration);
        if (requested.DescriptorGeneration != receipt.DescriptorGeneration)
            message.Append(" descriptorGeneration=").Append(receipt.DescriptorGeneration);
        if (requested.SubmissionQueueFamily != receipt.SubmissionQueueFamily)
            message.Append(" queueFamily=").Append(receipt.SubmissionQueueFamily);
    }

    private void PublishDesktopReadbackReceipts(in VulkanFrameAttempt attempt)
    {
        VulkanAcceptedFramePlan? accepted = attempt.AcceptedFramePlan;
        if (accepted is null || !accepted.IsSealed || attempt.GraphicsSignalValue == 0)
            return;

        ReadOnlySpan<FrameOpContext> contexts = accepted.LogicalPlan.StaticPlannerContexts;
        ReadOnlySpan<VulkanFrameOpPlannerStateKey> keys = accepted.LogicalPlan.StaticPlannerContextKeys;
        ReadOnlySpan<VulkanRenderGraphPlan> plans = accepted.LogicalPlan.StaticPlannerContextPlans;
        ResourcePlannerRuntimeState root = accepted.PlannerState;
        for (int index = 0; index < contexts.Length; index++)
        {
            FrameOpContext context = contexts[index];
            if (context.PipelineInstance is null || context.ResourceRegistry is null)
                continue;

            ResourcePlannerRuntimeState state = root;
            if (root.FrameOpResourcePlannerSwitchingState is { MergedPlanActive: false } switching)
            {
                if (switching.States.TryGetValue(keys[index], out ResourcePlannerRuntimeState scoped))
                    state = scoped;
                else if (root.LastActiveFrameOpContext is not { } rootContext ||
                    VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(rootContext) != keys[index])
                    continue;
            }
            if (!ReferenceEquals(state.RenderGraphPlan, plans[index]))
                continue;
            if (state.ResourceAllocator is null || state.ResourceAllocator.IsRetired)
                continue;

            int slot = 0;
            ulong oldest = ulong.MaxValue;
            for (int candidate = 0; candidate < _desktopReadbackReceipts.Length; candidate++)
            {
                ref readonly VulkanDesktopReadbackReceipt receipt = ref _desktopReadbackReceipts[candidate];
                if (receipt.Key.PipelineIdentity == keys[index].PipelineIdentity &&
                    receipt.Key.ViewportIdentity == keys[index].ViewportIdentity &&
                    receipt.Key.LogicalViewId == keys[index].LogicalViewId)
                {
                    slot = candidate;
                    break;
                }
                if (receipt.SubmissionSerial < oldest)
                {
                    oldest = receipt.SubmissionSerial;
                    slot = candidate;
                }
            }

            state.LastActiveFrameOpContext = context;
            _desktopReadbackReceipts[slot] = new(
                keys[index], context, state, attempt.GraphicsSignalValue);
        }
    }

    private bool TryEnterDesktopSubmittedReadbackScope(
        in FrameOpContext requested, out IDisposable scope)
    {
        VulkanFrameOpPlannerStateKey key = VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(requested);
        bool offscreen = requested.PipelineInstance?.Pipeline is AdvancedRenderPipeline { OffscreenProfile: not null };
        int matchedIndex = -1;
        for (int index = 0; index < _desktopReadbackReceipts.Length; index++)
        {
            ref readonly VulkanDesktopReadbackReceipt receipt = ref _desktopReadbackReceipts[index];
            // A completed offscreen viewport has left its caller-FBO scope. Its
            // current context cannot reproduce that transient target/face identity.
            // Select the newest submitted face, retaining exact pipeline, viewport,
            // resource generation, layout signature and extent matching.
            VulkanFrameOpPlannerStateKey candidateKey = offscreen
                ? receipt.Key with
                {
                    ContextKind = key.ContextKind,
                    OutputFrameBufferIdentity = key.OutputFrameBufferIdentity,
                    OutputTargetIdentity = key.OutputTargetIdentity,
                    LogicalViewId = key.LogicalViewId,
                }
                : receipt.Key;
            if (receipt.SubmissionSerial == 0 || candidateKey != key ||
                !ReferenceEquals(receipt.Context.ResourceRegistry, requested.ResourceRegistry) ||
                !ReferenceEquals(receipt.Context.PipelineInstance, requested.PipelineInstance) ||
                receipt.PlannerState.ResourceAllocator is null || receipt.PlannerState.ResourceAllocator.IsRetired)
                continue;
            if (matchedIndex < 0 || receipt.SubmissionSerial > _desktopReadbackReceipts[matchedIndex].SubmissionSerial)
                matchedIndex = index;
        }

        if (matchedIndex >= 0)
        {
            ref readonly VulkanDesktopReadbackReceipt receipt = ref _desktopReadbackReceipts[matchedIndex];
            _commandRuntime.ReconcileResourcePlannerImageLayouts(receipt.PlannerState.ResourceAllocator!);
            scope = _resourcePlannerSessions.EnterRuntimeStateScope(receipt.PlannerState);
            return true;
        }

        scope = null!;
        return false;
    }
}
