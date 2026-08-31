namespace XREngine.Rendering.Vulkan;

internal sealed partial class FramePlan
{
    private ResourcePlannerRuntimeGeneration?[] _computePlannerGenerations = [];
    private VulkanFrameOpPlannerStateKey[] _computePlannerKeys = [];
    private int _computePlannerGenerationCount;
    private ulong _recordingPlannerPlanGeneration;

    internal bool HasPreparedRecordingPlannerGenerations
        => IsSealed && Generation != 0 && _recordingPlannerPlanGeneration == Generation;

    /// <summary>Publishes physical owners once, before this sealed plan is shared with recorders.</summary>
    internal void PrepareRecordingPlannerGenerations(in ResourcePlannerRuntimeState root)
    {
        if (HasPreparedRecordingPlannerGenerations)
            return;
        if (!TryPrepareRecordingPlannerGenerations(in root))
            throw new VulkanPlanPreconditionException("The sealed frame plan has no exact physical-resource generation.");
    }

    /// <summary>
    /// Freezes the physical owners used by descriptor preparation and recording. A view-family
    /// ID alone cannot distinguish a prepared image from an older allocation.
    /// Stable frame slots reuse these envelopes without per-dispatch allocation.
    /// </summary>
    internal bool TryPrepareRecordingPlannerGenerations(in ResourcePlannerRuntimeState root)
    {
        _recordingPlannerPlanGeneration = 0;
        if (!IsSealed)
            return false;
        if (_computePlannerGenerations.Length < _staticPlannerContextKeyCount)
        {
            Array.Resize(ref _computePlannerGenerations, _staticPlannerContextKeyCount);
            Array.Resize(ref _computePlannerKeys, _staticPlannerContextKeyCount);
        }

        // A frame-slot plan may shrink after a larger publication. Retain the
        // active prefix for warm reuse, but release only entries which no longer
        // belong to this sealed plan so retired allocators are not kept alive by
        // an inactive cache tail.
        if (_computePlannerGenerationCount > _staticPlannerContextKeyCount)
        {
            Array.Clear(
                _computePlannerGenerations,
                _staticPlannerContextKeyCount,
                _computePlannerGenerationCount - _staticPlannerContextKeyCount);
            Array.Clear(
                _computePlannerKeys,
                _staticPlannerContextKeyCount,
                _computePlannerGenerationCount - _staticPlannerContextKeyCount);
        }

        for (int index = 0; index < _staticPlannerContextKeyCount; index++)
        {
            VulkanFrameOpPlannerStateKey key = _staticPlannerContextKeys[index];
            VulkanRenderGraphPlan plan = _staticPlannerContextPlans[index];
            ResourcePlannerRuntimeState state = root;
            if (root.FrameOpResourcePlannerSwitchingState is { } switching &&
                switching.States.TryGetValue(key, out ResourcePlannerRuntimeState scoped) &&
                ReferenceEquals(scoped.RenderGraphPlan, plan))
            {
                state = scoped;
            }
            else if (!ReferenceEquals(root.RenderGraphPlan, plan))
                return false;

            if (state.ResourceAllocator is null || state.ResourceAllocator.IsRetired)
                return false;
            if (state.FrameOpResourcePlannerSwitchingState is null)
                return false;

            ResourcePlannerRuntimeGeneration? cached = _computePlannerGenerations[index];
            if (cached is not null && _computePlannerKeys[index] == key &&
                ReferenceEquals(cached.State.ResourceAllocator, state.ResourceAllocator) &&
                ReferenceEquals(cached.State.RenderGraphPlan, state.RenderGraphPlan) &&
                cached.State.ResourcePlannerRevision == state.ResourcePlannerRevision)
                continue;

            state.LastActiveFrameOpContext = _staticPlannerContexts[index];
            _computePlannerKeys[index] = key;
            _computePlannerGenerations[index] = new ResourcePlannerRuntimeGeneration(state);
        }
        _computePlannerGenerationCount = _staticPlannerContextKeyCount;
        _recordingPlannerPlanGeneration = Generation;
        return true;
    }

    internal bool TryGetRecordingPlannerGeneration(
        in FrameOpContext context, out ResourcePlannerRuntimeGeneration generation)
    {
        generation = null!;
        if (!IsSealed || Generation == 0 || _recordingPlannerPlanGeneration != Generation)
            return false;
        VulkanFrameOpPlannerStateKey key = VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(context);
        for (int index = 0; index < _computePlannerGenerationCount; index++)
            if (_computePlannerKeys[index] == key && _computePlannerGenerations[index] is { } found)
            {
                generation = found;
                return true;
            }
        return false;
    }
}
