namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    // Diagnostic consumers may read only images belonging to accepted queue work.
    // Planner switching caches also contain prepared, never-submitted generations.
    private const int DesktopReadbackReceiptCapacity = 32;
    private readonly VulkanDesktopReadbackReceipt[] _desktopReadbackReceipts =
        new VulkanDesktopReadbackReceipt[DesktopReadbackReceiptCapacity];

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
