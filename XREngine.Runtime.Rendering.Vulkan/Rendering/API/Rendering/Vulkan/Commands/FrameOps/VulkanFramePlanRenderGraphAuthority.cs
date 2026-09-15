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
        out VulkanRenderGraphPlan plan)
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
            return false;
        }
        if (hasPrimary || hasSecondary)
        {
            if (hasPrimary && hasSecondary)
            {
                plan = VulkanRenderGraphPlan.Empty;
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
                    return false;
                }

                selectedSwitchingState.States[key] = state;
            }

            if (!IsRecordable(state.RenderGraphPlan))
            {
                plan = VulkanRenderGraphPlan.Empty;
                return false;
            }

            plan = state.RenderGraphPlan;
            return true;
        }

        if (SecondarySwitchingState is null && plannerContextCount == 1 && IsRecordable(FallbackPlan))
        {
            plan = FallbackPlan;
            return true;
        }

        plan = VulkanRenderGraphPlan.Empty;
        return false;
    }

    private static bool IsRecordable(VulkanRenderGraphPlan? plan)
        => plan is not null &&
           !ReferenceEquals(plan, VulkanRenderGraphPlan.Empty) &&
           plan.Barriers.HasCompleteNativeBindings;
}
