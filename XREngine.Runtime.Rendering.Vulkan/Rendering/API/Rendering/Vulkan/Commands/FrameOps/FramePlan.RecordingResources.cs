using System.Runtime.CompilerServices;

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
        ResourcePlannerRuntimeState secondRoot = default;
        if (!TryPrepareRecordingPlannerGenerations(in root, in secondRoot, hasSecondRoot: false))
            throw new VulkanPlanPreconditionException("The sealed frame plan has no exact physical-resource generation.");
    }

    internal void PrepareRecordingPlannerGenerations(
        in ResourcePlannerRuntimeState firstRoot,
        in ResourcePlannerRuntimeState secondRoot)
    {
        if (HasPreparedRecordingPlannerGenerations)
            return;
        if (!TryPrepareRecordingPlannerGenerations(in firstRoot, in secondRoot, hasSecondRoot: true))
            throw new VulkanPlanPreconditionException("The sealed frame plan has no exact physical-resource generation.");
    }

    /// <summary>
    /// Freezes the physical owners used by descriptor preparation and recording. A view-family
    /// ID alone cannot distinguish a prepared image from an older allocation.
    /// Stable frame slots reuse these envelopes without per-dispatch allocation.
    /// </summary>
    internal bool TryPrepareRecordingPlannerGenerations(in ResourcePlannerRuntimeState root)
    {
        ResourcePlannerRuntimeState secondRoot = default;
        return TryPrepareRecordingPlannerGenerations(in root, in secondRoot, hasSecondRoot: false);
    }

    internal bool TryPrepareRecordingPlannerGenerations(
        in ResourcePlannerRuntimeState firstRoot,
        in ResourcePlannerRuntimeState secondRoot)
        => TryPrepareRecordingPlannerGenerations(in firstRoot, in secondRoot, hasSecondRoot: true);

    private bool TryPrepareRecordingPlannerGenerations(
        in ResourcePlannerRuntimeState firstRoot,
        in ResourcePlannerRuntimeState secondRoot,
        bool hasSecondRoot)
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
            if (!TryResolveRecordingPlannerState(
                    in key,
                    plan,
                    in firstRoot,
                    in secondRoot,
                    hasSecondRoot,
                    out ResourcePlannerRuntimeState state))
                return false;

            if (!state.HasLiveAllocatorOwnership)
                return false;
            if (state.FrameOpResourcePlannerSwitchingState is null)
                return false;

            ResourcePlannerRuntimeGeneration? cached = _computePlannerGenerations[index];
            if (cached is not null && _computePlannerKeys[index] == key &&
                ReferenceEquals(cached.State.ResourceAllocator, state.ResourceAllocator) &&
                ReferenceEquals(cached.State.RenderGraphPlan, state.RenderGraphPlan) &&
                ReferenceEquals(
                    cached.State.FrameOpResourcePlannerSwitchingState,
                    state.FrameOpResourcePlannerSwitchingState) &&
                cached.State.ResourcePlannerRevision == state.ResourcePlannerRevision &&
                cached.State.ResourcePlannerSignature == state.ResourcePlannerSignature &&
                cached.State.ResourceAllocationSignature == state.ResourceAllocationSignature &&
                cached.State.AllocatorOwnershipId == state.AllocatorOwnershipId)
                continue;

            state.LastActiveFrameOpContext = _staticPlannerContexts[index];
            _computePlannerKeys[index] = key;
            _computePlannerGenerations[index] = new ResourcePlannerRuntimeGeneration(state);
        }
        _computePlannerGenerationCount = _staticPlannerContextKeyCount;
        _recordingPlannerPlanGeneration = Generation;
        return true;
    }

    private static bool TryResolveRecordingPlannerState(
        in VulkanFrameOpPlannerStateKey key,
        VulkanRenderGraphPlan plan,
        in ResourcePlannerRuntimeState firstRoot,
        in ResourcePlannerRuntimeState secondRoot,
        bool hasSecondRoot,
        out ResourcePlannerRuntimeState state)
    {
        ResourcePlannerRuntimeState firstState = default;
        ResourcePlannerRuntimeState secondState = default;
        bool hasFirstPublication = firstRoot.FrameOpResourcePlannerSwitchingState is { } firstSwitching &&
            firstSwitching.States.TryGetValue(key, out firstState);
        bool hasSecondPublication = hasSecondRoot &&
            secondRoot.FrameOpResourcePlannerSwitchingState is { } secondSwitching &&
            secondSwitching.States.TryGetValue(key, out secondState);
        if (hasSecondRoot &&
            (firstRoot.FrameOpResourcePlannerSwitchingState is null ||
             secondRoot.FrameOpResourcePlannerSwitchingState is null ||
             ReferenceEquals(
                 firstRoot.FrameOpResourcePlannerSwitchingState,
                 secondRoot.FrameOpResourcePlannerSwitchingState)))
        {
            state = default;
            return false;
        }
        if (hasFirstPublication && hasSecondPublication)
        {
            state = default;
            return false;
        }
        if (hasFirstPublication)
        {
            if (ReferenceEquals(firstState.RenderGraphPlan, plan) &&
                (!hasSecondRoot || ReferenceEquals(
                    firstState.FrameOpResourcePlannerSwitchingState,
                    firstRoot.FrameOpResourcePlannerSwitchingState)))
            {
                state = firstState;
                return true;
            }

            state = default;
            return false;
        }
        if (hasSecondPublication)
        {
            if (ReferenceEquals(secondState.RenderGraphPlan, plan) &&
                ReferenceEquals(
                    secondState.FrameOpResourcePlannerSwitchingState,
                    secondRoot.FrameOpResourcePlannerSwitchingState))
            {
                state = secondState;
                return true;
            }

            state = default;
            return false;
        }
        if (hasSecondRoot)
        {
            state = default;
            return false;
        }
        if (ReferenceEquals(firstRoot.RenderGraphPlan, plan))
        {
            state = firstRoot;
            return true;
        }
        state = default;
        return false;
    }

    internal bool TryGetRecordingPlannerGeneration(
        in FrameOpContext context, out ResourcePlannerRuntimeGeneration generation)
    {
        VulkanFrameOpPlannerStateKey key = VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(context);
        return TryGetRecordingPlannerGeneration(in key, out generation);
    }

    internal bool TryGetRecordingPlannerGeneration(
        in VulkanFrameOpPlannerStateKey key, out ResourcePlannerRuntimeGeneration generation)
    {
        generation = null!;
        if (!IsSealed || Generation == 0 || _recordingPlannerPlanGeneration != Generation)
            return false;
        for (int index = 0; index < _computePlannerGenerationCount; index++)
            if (_computePlannerKeys[index] == key && _computePlannerGenerations[index] is { } found)
            {
                generation = found;
                return true;
            }
        return false;
    }

    /// <summary>
    /// Validates a recorder's frozen resource stamp against the exact planner
    /// owner selected by its sealed logical-view key.
    /// </summary>
    internal bool MatchesRecordingPlannerStamp(
        in VulkanFrameOpPlannerStateKey key,
        in VulkanPreparedResourcePlanStamp stamp)
    {
        if (!TryGetRecordingPlannerGeneration(in key, out ResourcePlannerRuntimeGeneration generation))
            return false;

        ref readonly ResourcePlannerRuntimeState state = ref generation.State;
        return ReferenceEquals(state.RenderGraphPlan, stamp.PlanningSnapshot.RenderGraphPlan) &&
            state.ResourcePlannerRevision == stamp.ResourcePlannerRevision &&
            state.ResourcePlannerSignature == stamp.ResourcePlannerSignature &&
            state.ResourceAllocationSignature == stamp.ResourceAllocationSignature;
    }

    /// <summary>
    /// Describes a rejected logical-view resource stamp. This is a cold-path
    /// diagnostic paired with <see cref="MatchesRecordingPlannerStamp"/>.
    /// </summary>
    internal string DescribeRecordingPlannerStampMismatch(
        in VulkanFrameOpPlannerStateKey key,
        in VulkanPreparedResourcePlanStamp stamp)
    {
        if (!TryGetRecordingPlannerGeneration(in key, out ResourcePlannerRuntimeGeneration generation))
            return $"missing normalized planner key {DescribeRecordingPlannerKey(in key)}";

        ref readonly ResourcePlannerRuntimeState state = ref generation.State;
        string mismatch = !ReferenceEquals(state.RenderGraphPlan, stamp.PlanningSnapshot.RenderGraphPlan)
            ? "render-graph reference mismatch"
            : state.ResourcePlannerRevision != stamp.ResourcePlannerRevision
                ? "planner revision mismatch"
                : state.ResourcePlannerSignature != stamp.ResourcePlannerSignature
                    ? "planner signature mismatch"
                    : "resource allocation signature mismatch";
        return $"{mismatch}; key={DescribeRecordingPlannerKey(in key)} " +
            $"graphRef expected=0x{RuntimeHelpers.GetHashCode(state.RenderGraphPlan):X8} " +
            $"captured=0x{RuntimeHelpers.GetHashCode(stamp.PlanningSnapshot.RenderGraphPlan):X8} " +
            $"revision expected={state.ResourcePlannerRevision} captured={stamp.ResourcePlannerRevision} " +
            $"plannerSignature expected=0x{state.ResourcePlannerSignature:X16} captured=0x{stamp.ResourcePlannerSignature:X16} " +
            $"allocationSignature expected=0x{state.ResourceAllocationSignature:X16} captured=0x{stamp.ResourceAllocationSignature:X16}";
    }

    private static string DescribeRecordingPlannerKey(in VulkanFrameOpPlannerStateKey key)
        => $"kind={key.ContextKind} pipeline={key.PipelineIdentity} viewport={key.ViewportIdentity} " +
           $"display={key.DisplayWidth}x{key.DisplayHeight} internal={key.InternalWidth}x{key.InternalHeight} " +
           $"fbo={key.OutputFrameBufferIdentity} target={key.OutputTargetIdentity} " +
           $"logicalView=0x{key.LogicalViewId:X16} registry={key.ResourceRegistrySignature} " +
           $"passes={key.PassMetadataSignature} resourceGeneration={key.ResourceGeneration} " +
           $"descriptorGeneration={key.DescriptorGeneration} queueFamily={key.SubmissionQueueFamily}";
}
