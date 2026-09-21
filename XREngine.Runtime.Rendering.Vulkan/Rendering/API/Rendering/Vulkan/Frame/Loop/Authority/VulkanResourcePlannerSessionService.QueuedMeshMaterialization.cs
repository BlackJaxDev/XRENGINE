using System.Collections.Generic;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourcePlannerSessionService
{
    private const int MaxQueuedMeshPlannerGenerations = 12;

    // The switching-state object changes with each published planner epoch. Keeping
    // cached envelopes only for that object prevents a queued draw from observing a
    // resource commit made after its queue cohort captured the root generation.
    private FrameOpResourcePlannerSwitchingState? _queuedMeshGenerationEpochOwner;
    private readonly Dictionary<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeGeneration>
        _queuedMeshPlannerGenerations = new(MaxQueuedMeshPlannerGenerations);
    private readonly List<VulkanFrameOpPlannerStateKey> _queuedMeshPlannerGenerationPruneScratch =
        new(MaxQueuedMeshPlannerGenerations);

    /// <summary>
    /// Installs the exact planner generation captured for a queued mesh request.
    /// Queued materialization runs after its producer scopes have unwound, so it
    /// cannot use whichever planner context happens to be ambient on the command thread.
    /// </summary>
    internal bool TryEnterQueuedMeshPlannerGeneration(
        ResourcePlannerRuntimeGeneration rootGeneration,
        in FrameOpContext requestContext,
        out VulkanPreparedResourcePlannerThreadScope scope,
        out string detail)
    {
        scope = default;
        detail = string.Empty;

        if (!commands.IsDeviceOperational)
        {
            detail = "PlannerGenerationPending: Vulkan device is not operational.";
            return false;
        }

        VulkanCommandThreadContext threadContext = commands.ThreadWorkspace.Current;
        if (!ReferenceEquals(threadContext.Owner, commands))
        {
            detail = "PlannerGenerationMismatch: queued mesh materialization is not executing on its command runtime workspace.";
            return false;
        }

        if (!ContextHasPlannerResources(requestContext))
        {
            detail = "PlannerGenerationPending: queued mesh request has no captured resource registry or pass metadata.";
            return false;
        }

        ResourcePlannerRuntimeState rootState = rootGeneration.State;
        FrameOpResourcePlannerSwitchingState? switching =
            rootState.FrameOpResourcePlannerSwitchingState;
        if (switching is null)
        {
            detail = "PlannerGenerationPending: captured planner generation has no switching-state publication.";
            return false;
        }

        VulkanFrameOpPlannerStateKey key =
            VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(requestContext);
        ResourcePlannerRuntimeState resolvedState;
        if (switching.MergedPlanActive)
        {
            resolvedState = rootState;
        }
        else if (!switching.States.TryGetValue(key, out resolvedState))
        {
            detail = $"PlannerGenerationPending: no exact planner state is published for {Describe(key)}.";
            return false;
        }
        else if (resolvedState.LastActiveFrameOpContext is not { } resolvedContext ||
                 !VulkanFrameOpSnapshotSignatures.MatchesPlannerStateKey(
                     resolvedContext,
                     key,
                     resolvedContext.PassMetadata))
        {
            detail = $"PlannerGenerationMismatch: exact planner state does not match its captured context for {Describe(key)}.";
            return false;
        }

        if (!IsQueuedMeshPlannerStateUsable(resolvedState))
        {
            detail = $"PlannerGenerationPending: planner resources are not ready for {Describe(key)}.";
            return false;
        }

        ResourcePlannerRuntimeGeneration generation = GetOrCreateQueuedMeshPlannerGeneration(
            switching,
            key,
            resolvedState,
            requestContext);
        scope = new VulkanPreparedResourcePlannerThreadScope(threadContext, commands, generation);
        return true;
    }

    internal void ClearQueuedMeshPlannerGenerations()
    {
        _queuedMeshGenerationEpochOwner = null;
        _queuedMeshPlannerGenerations.Clear();
        _queuedMeshPlannerGenerationPruneScratch.Clear();
    }

    private ResourcePlannerRuntimeGeneration GetOrCreateQueuedMeshPlannerGeneration(
        FrameOpResourcePlannerSwitchingState switching,
        in VulkanFrameOpPlannerStateKey key,
        in ResourcePlannerRuntimeState resolvedState,
        in FrameOpContext requestContext)
    {
        if (!ReferenceEquals(_queuedMeshGenerationEpochOwner, switching))
        {
            _queuedMeshGenerationEpochOwner = switching;
            _queuedMeshPlannerGenerations.Clear();
        }

        // Cached envelopes always carry the root generation's switching owner.
        // Normalize cloned state before matching so an older embedded pointer does
        // not turn a stable per-key generation into a per-draw allocation.
        ResourcePlannerRuntimeState normalizedState = resolvedState;
        normalizedState.FrameOpResourcePlannerSwitchingState = switching;
        if (_queuedMeshPlannerGenerations.TryGetValue(key, out ResourcePlannerRuntimeGeneration? cached) &&
            CachedQueuedMeshPlannerGenerationMatches(cached, normalizedState, key))
        {
            return cached;
        }

        if (!_queuedMeshPlannerGenerations.ContainsKey(key) &&
            _queuedMeshPlannerGenerations.Count >= MaxQueuedMeshPlannerGenerations)
        {
            PruneQueuedMeshPlannerGenerations(switching);
            if (_queuedMeshPlannerGenerations.Count >= MaxQueuedMeshPlannerGenerations)
                _queuedMeshPlannerGenerations.Clear();
        }

        ResourcePlannerRuntimeState scopedState = normalizedState;
        scopedState.LastActiveFrameOpContext = requestContext;
        ResourcePlannerRuntimeGeneration generation = new(scopedState);
        _queuedMeshPlannerGenerations[key] = generation;
        return generation;
    }

    private void PruneQueuedMeshPlannerGenerations(
        FrameOpResourcePlannerSwitchingState switching)
    {
        // Merged plans do not populate States for each request key. Their bounded
        // cache intentionally clears on pressure instead of guessing an owner.
        if (switching.MergedPlanActive)
            return;

        _queuedMeshPlannerGenerationPruneScratch.Clear();
        foreach ((VulkanFrameOpPlannerStateKey key, ResourcePlannerRuntimeGeneration _) in
                 _queuedMeshPlannerGenerations)
        {
            if (!switching.States.ContainsKey(key))
                _queuedMeshPlannerGenerationPruneScratch.Add(key);
        }

        foreach (VulkanFrameOpPlannerStateKey key in _queuedMeshPlannerGenerationPruneScratch)
            _queuedMeshPlannerGenerations.Remove(key);
        _queuedMeshPlannerGenerationPruneScratch.Clear();
    }

    private static bool CachedQueuedMeshPlannerGenerationMatches(
        ResourcePlannerRuntimeGeneration cached,
        in ResourcePlannerRuntimeState resolvedState,
        in VulkanFrameOpPlannerStateKey requestedKey)
    {
        ResourcePlannerRuntimeState cachedState = cached.State;
        return ReferenceEquals(cachedState.ResourcePlanner, resolvedState.ResourcePlanner) &&
               ReferenceEquals(cachedState.ResourceAllocator, resolvedState.ResourceAllocator) &&
               cachedState.AllocatorOwnershipId == resolvedState.AllocatorOwnershipId &&
               ReferenceEquals(cachedState.BarrierPlanner, resolvedState.BarrierPlanner) &&
               ReferenceEquals(cachedState.CompiledRenderGraph, resolvedState.CompiledRenderGraph) &&
               ReferenceEquals(cachedState.RenderGraphPlan, resolvedState.RenderGraphPlan) &&
               cachedState.ResourcePlannerRevision == resolvedState.ResourcePlannerRevision &&
               cachedState.ResourcePlannerSignature == resolvedState.ResourcePlannerSignature &&
               cachedState.ResourceAllocationSignature == resolvedState.ResourceAllocationSignature &&
               ReferenceEquals(
                   cachedState.FrameOpResourcePlannerSwitchingState,
                   resolvedState.FrameOpResourcePlannerSwitchingState) &&
               ReferenceEquals(cachedState.PreparedGenerationManifest, resolvedState.PreparedGenerationManifest) &&
               cachedState.LastActiveFrameOpContext is { } cachedContext &&
               VulkanFrameOpSnapshotSignatures.MatchesPlannerStateKey(
                   cachedContext,
                   requestedKey,
                   cachedContext.PassMetadata);
    }

    private static bool IsQueuedMeshPlannerStateUsable(
        in ResourcePlannerRuntimeState state)
        => state.ResourcePlanner is not null &&
           state.ResourceAllocator is { IsRetired: false } allocator &&
           allocator.OwnershipId == state.AllocatorOwnershipId &&
           state.BarrierPlanner is not null &&
           state.CompiledRenderGraph is not null &&
           state.RenderGraphPlan is not null &&
           state.RenderGraphPlan.Revision == state.ResourcePlannerRevision &&
           ReferenceEquals(state.RenderGraphPlan.CompiledGraph, state.CompiledRenderGraph) &&
           state.RenderGraphPlan.Barriers.HasCompleteNativeBindings;

    private static bool ContextHasPlannerResources(in FrameOpContext context)
        => context.ResourceRegistry is not null || context.PassMetadata is { Count: > 0 };

    private static string Describe(in VulkanFrameOpPlannerStateKey key)
        => $"context={key.ContextKind}; pipeline={key.PipelineIdentity}; viewport={key.ViewportIdentity}; target={key.OutputTargetIdentity}; resourceGeneration={key.ResourceGeneration}; descriptorGeneration={key.DescriptorGeneration}";
}
