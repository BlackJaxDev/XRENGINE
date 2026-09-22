namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Rejects a sealed logical packet when an immutable image barrier no longer
    /// resolves to the exact live native generation captured by the planner.
    /// </summary>
    private void ValidateExplicitProductionLogicalPlanNativeImageBindings(
        VulkanAcceptedFramePlan acceptedPlan)
    {
        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            ReadOnlySpan<VulkanRenderGraphPlan> contextPlans =
                acceptedPlan.LogicalPlan.StaticPlannerContextPlans;
            for (int planIndex = 0; planIndex < contextPlans.Length; planIndex++)
            {
                ReadOnlySpan<VulkanFrozenImageBarrier> frozen =
                    contextPlans[planIndex].Barriers.ImageBarriers;
                for (int barrierIndex = 0; barrierIndex < frozen.Length; barrierIndex++)
                {
                    VulkanFrozenImageBarrier barrier = frozen[barrierIndex];
                    if (barrier.NativeImage.Handle == 0 || barrier.NativeGeneration == 0 ||
                        !tracker.TryResolveResourceGenerationNoLock(
                            new VulkanResourceLifetimeKey(
                                Silk.NET.Vulkan.ObjectType.Image,
                                barrier.NativeImage.Handle),
                            barrier.NativeGeneration,
                            out VulkanResourceLifetimeRecord resource) ||
                        (resource.State &
                         (EVulkanResourceLifetimeState.PendingRetirement |
                          EVulkanResourceLifetimeState.Destroyed)) != 0)
                    {
                        throw new VulkanNativeBufferBindingSupersededException(
                            $"Accepted explicit logical packet context={planIndex} references " +
                            $"superseded native image 0x{barrier.NativeImage.Handle:X} " +
                            $"generation {barrier.NativeGeneration} for " +
                            $"resource {barrier.ResourceId.Value}.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Rejects a sealed logical packet when one of its immutable native buffer
    /// barriers no longer resolves to the captured live generation. A global
    /// binding revision is only a fast-path: unrelated growth still permits a
    /// packet whose exact dependencies remain current.
    /// </summary>
    private void ValidateExplicitProductionLogicalPlanNativeBufferBindings(
        VulkanAcceptedFramePlan acceptedPlan)
    {
        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        ulong currentRevision = _resourceRuntime.NativeBufferBindingRevision;
        lock (tracker.SyncRoot)
        {
            ReadOnlySpan<VulkanRenderGraphPlan> contextPlans =
                acceptedPlan.LogicalPlan.StaticPlannerContextPlans;
            for (int planIndex = 0; planIndex < contextPlans.Length; planIndex++)
            {
                VulkanBarrierPlan barriers = contextPlans[planIndex].Barriers;
                if (barriers.NativeBufferBindingRevision == currentRevision)
                    continue;

                ReadOnlySpan<VulkanFrozenBufferBarrier> frozen = barriers.BufferBarriers;
                for (int barrierIndex = 0; barrierIndex < frozen.Length; barrierIndex++)
                {
                    VulkanFrozenBufferBarrier barrier = frozen[barrierIndex];
                    if (barrier.NativeBuffer.Handle == 0 || barrier.NativeGeneration == 0 ||
                        !tracker.TryResolveResourceGenerationNoLock(
                            new VulkanResourceLifetimeKey(
                                Silk.NET.Vulkan.ObjectType.Buffer,
                                barrier.NativeBuffer.Handle),
                            barrier.NativeGeneration,
                            out VulkanResourceLifetimeRecord resource) ||
                        (resource.State &
                         (EVulkanResourceLifetimeState.PendingRetirement |
                          EVulkanResourceLifetimeState.Destroyed)) != 0)
                    {
                        throw new VulkanNativeBufferBindingSupersededException(
                            $"Accepted explicit logical packet context={planIndex} references " +
                            $"superseded native buffer 0x{barrier.NativeBuffer.Handle:X} " +
                            $"generation {barrier.NativeGeneration} for " +
                            $"'{barrier.LogicalResourceName}'.");
                    }
                }
            }
        }
    }
}
