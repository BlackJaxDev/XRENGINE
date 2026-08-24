namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Read-only planning authority used while sealing one frame. Exact keyed
/// planner publications are required for mixed-context frames; a single-context
/// compatibility path may use the explicitly captured fallback publication.
/// </summary>
internal readonly record struct VulkanFramePlanRenderGraphAuthority(
    VulkanRenderGraphPlan FallbackPlan,
    FrameOpResourcePlannerSwitchingState? SwitchingState)
{
    internal bool TryResolve(
        in VulkanFrameOpPlannerStateKey key,
        int plannerContextCount,
        out VulkanRenderGraphPlan plan)
    {
        if (SwitchingState is not null &&
            SwitchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState state) &&
            IsRecordable(state.RenderGraphPlan))
        {
            plan = state.RenderGraphPlan;
            return true;
        }

        if (plannerContextCount == 1 && IsRecordable(FallbackPlan))
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
