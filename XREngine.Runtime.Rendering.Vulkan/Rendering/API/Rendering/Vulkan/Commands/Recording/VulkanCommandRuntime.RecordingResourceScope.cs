namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>Uses the sealed physical owner for preparation as well as native encoding.</summary>
    private VulkanPreparedResourcePlannerThreadScope EnterRecordingResourceScope(
        FramePlan? plan, in FrameOpContext context)
    {
        if (plan is null || context.ResourceRegistry is null && context.PassMetadata is not { Count: > 0 })
            return default;
        if (!plan.TryGetRecordingPlannerGeneration(in context, out ResourcePlannerRuntimeGeneration generation))
            throw new VulkanPlanPreconditionException("Resource preparation has no exact sealed physical-resource generation.");
        return new(ThreadWorkspace.Current, this, generation);
    }
}
