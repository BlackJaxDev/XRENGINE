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
    bool AllowSynchronousResourceUploads = false)
{
    internal bool TryResolve(
        in VulkanFrameOpPlannerStateKey key,
        int plannerContextCount,
        out VulkanRenderGraphPlan plan)
    {
        if (SwitchingState is not null &&
            SwitchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState state))
        {
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

                SwitchingState.States[key] = state;
            }

            if (!IsRecordable(state.RenderGraphPlan))
            {
                plan = VulkanRenderGraphPlan.Empty;
                return false;
            }

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
