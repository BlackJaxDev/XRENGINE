using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Producer-side planning authority used while sealing one frame. Exact keyed
/// planner publications are refreshed before the immutable frame plan is built;
/// a single-context compatibility path may use the captured fallback publication.
/// </summary>
internal readonly record struct VulkanFramePlanRenderGraphAuthority(
    VulkanRenderGraphPlan FallbackPlan,
    FrameOpResourcePlannerSwitchingState? SwitchingState,
    VulkanFramePlanner? Planner = null,
    VulkanBackendObjectContext? BackendContext = null,
    bool AllowSynchronousResourceUploads = false,
    FrameOpResourcePlannerSwitchingState? SecondarySwitchingState = null)
{
    internal bool TryResolve(
        in VulkanFrameOpPlannerStateKey key,
        int plannerContextCount,
        out VulkanRenderGraphPlan plan,
        out string failureReason)
    {
        ResourcePlannerRuntimeState primaryState = default;
        ResourcePlannerRuntimeState secondaryState = default;
        bool hasPrimary = SwitchingState is not null &&
            SwitchingState.States.TryGetValue(key, out primaryState);
        bool hasSecondary = SecondarySwitchingState is not null &&
            SecondarySwitchingState.States.TryGetValue(key, out secondaryState);
        if (SecondarySwitchingState is not null &&
            (SwitchingState is null || ReferenceEquals(SwitchingState, SecondarySwitchingState)))
        {
            plan = VulkanRenderGraphPlan.Empty;
            failureReason = "Primary and secondary planner publications do not have distinct switching-state owners.";
            return false;
        }
        if (hasPrimary || hasSecondary)
        {
            if (hasPrimary && hasSecondary)
            {
                plan = VulkanRenderGraphPlan.Empty;
                failureReason = "The keyed planner publication exists in both primary and secondary switching states.";
                return false;
            }

            ResourcePlannerRuntimeState state = hasPrimary ? primaryState : secondaryState;
            FrameOpResourcePlannerSwitchingState selectedSwitchingState = hasPrimary
                ? SwitchingState!
                : SecondarySwitchingState!;
            // Desktop publications shallow-copy historical keyed states. Only
            // the paired eye contract requires each state to own its exact map.
            if (SecondarySwitchingState is not null && !ReferenceEquals(
                    state.FrameOpResourcePlannerSwitchingState,
                    selectedSwitchingState))
            {
                plan = VulkanRenderGraphPlan.Empty;
                failureReason = "The keyed paired-eye publication does not own its selected switching-state map.";
                return false;
            }
            ulong currentBufferRevision = BackendContext?.Resources.NativeBufferBindingRevision ?? 0UL;
            if (state.RenderGraphPlan.Barriers.NativeBufferBindingRevision != currentBufferRevision)
            {
                string reason = Planner is null
                    ? "No producer planner is available to refreeze the keyed native barrier publication."
                    : string.Empty;
                bool nativeBindingsSuperseded = false;
                if (Planner is null || !Planner.TryFreezeResourcePlannerRenderGraphPlan(
                        ref state,
                        BackendContext,
                        AllowSynchronousResourceUploads,
                        out reason,
                        out nativeBindingsSuperseded))
                {
                    if (nativeBindingsSuperseded)
                        throw new VulkanNativeBufferBindingSupersededException(reason);
                    plan = VulkanRenderGraphPlan.Empty;
                    failureReason = $"The keyed publication could not refreeze native barriers: {reason}";
                    return false;
                }

                selectedSwitchingState.States[key] = state;
            }

            if (!IsRecordable(state.RenderGraphPlan))
            {
                plan = VulkanRenderGraphPlan.Empty;
                failureReason = DescribeNonRecordablePlan(state);
                return false;
            }

            plan = state.RenderGraphPlan;
            failureReason = string.Empty;
            return true;
        }

        if (SecondarySwitchingState is null && plannerContextCount == 1 && IsRecordable(FallbackPlan))
        {
            plan = FallbackPlan;
            failureReason = string.Empty;
            return true;
        }

        plan = VulkanRenderGraphPlan.Empty;
        failureReason = $"No exact keyed publication exists (primary={SwitchingState?.States.Count ?? 0}, " +
            $"secondary={SecondarySwitchingState?.States.Count ?? 0}); fallbackEligible={SecondarySwitchingState is null && plannerContextCount == 1}, " +
            $"fallbackRecordable={IsRecordable(FallbackPlan)}.";
        return false;
    }

    private static string DescribeNonRecordablePlan(in ResourcePlannerRuntimeState state)
    {
        VulkanRenderGraphPlan? plan = state.RenderGraphPlan;
        return $"The exact keyed publication is not recordable: allocatorRetired={state.ResourceAllocator.IsRetired}, " +
            $"planNull={plan is null}, planEmpty={ReferenceEquals(plan, VulkanRenderGraphPlan.Empty)}, " +
            $"completeNativeBindings={plan?.Barriers.HasCompleteNativeBindings ?? false}, " +
            $"nativeBufferRevision={plan?.Barriers.NativeBufferBindingRevision ?? 0}.";
    }

    private static bool IsRecordable(VulkanRenderGraphPlan? plan)
        => plan is not null &&
           !ReferenceEquals(plan, VulkanRenderGraphPlan.Empty) &&
           plan.Barriers.HasCompleteNativeBindings;
}
