using System.Collections.Concurrent;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Sequences temporary resource-planner publications against the command
/// thread workspace. This authority is owned by the frame loop so the planner
/// remains independent of command execution state.
/// </summary>
internal sealed partial class VulkanResourcePlannerSessionService(
    VulkanFramePlanner planner,
    VulkanCommandRuntime commands)
{
    private readonly ConcurrentStack<PooledExternalResourcePlannerReadbackScope> _freeReadbackScopes = new();

    internal ExternalResourcePlannerReadbackScope CreateReadbackScope(
        VulkanDeviceContext device,
        VulkanOutputRuntime output,
        in FrameOpContext context)
        => new(this, device, output, context);

    internal PooledExternalResourcePlannerReadbackScope RentReadbackScope(
        ExternalResourcePlannerReadbackScope readbackScope)
        => RentReadbackScope(readbackScope, default, hasRuntimeStateScope: false);

    internal PooledExternalResourcePlannerReadbackScope RentReadbackScope(
        ExternalResourcePlannerReadbackScope readbackScope,
        RuntimeStateScope runtimeStateScope)
        => RentReadbackScope(readbackScope, runtimeStateScope, hasRuntimeStateScope: true);

    private PooledExternalResourcePlannerReadbackScope RentReadbackScope(
        ExternalResourcePlannerReadbackScope readbackScope,
        RuntimeStateScope runtimeStateScope,
        bool hasRuntimeStateScope)
    {
        if (!_freeReadbackScopes.TryPop(out PooledExternalResourcePlannerReadbackScope? scope))
            scope = new PooledExternalResourcePlannerReadbackScope();

        scope.Lease(
            readbackScope,
            runtimeStateScope,
            hasRuntimeStateScope,
            _freeReadbackScopes);
        return scope;
    }

    internal void ReleaseReadbackScopes()
    {
        _freeReadbackScopes.Clear();
        ClearQueuedMeshPlannerGenerations();
    }

    internal RuntimeStateScope EnterRuntimeStateScope(in ResourcePlannerRuntimeState state)
    {
        VulkanCommandThreadContext context = commands.ThreadWorkspace.Current;
        if (context.PreparedCommandChainEncodingActive)
        {
            throw new InvalidOperationException(
                "Prepared Vulkan command-chain encoding cannot enter a resource-planner scope.");
        }

        return new RuntimeStateScope(context, commands, state);
    }

    internal ResourcePlannerRuntimeState CaptureRuntimeState()
    {
        VulkanCommandThreadContext threadContext = commands.ThreadWorkspace.Current;
        FrameOpResourcePlannerSwitchingState switchingState =
            ResolveActiveSwitchingState(threadContext);
        if (ReferenceEquals(threadContext.ResourcePlannerRuntimeStateOwner, commands) &&
            threadContext.ResourcePlannerRuntimeState.HasValue)
        {
            ResourcePlannerRuntimeState threadState = threadContext.ResourcePlannerRuntimeState.Value;
            threadState.FrameOpResourcePlannerSwitchingState = switchingState;
            return threadState;
        }

        ResourcePlannerRuntimeState state = planner.GetPublishedResourcePlannerGeneration().State;
        state.FrameOpResourcePlannerSwitchingState ??= switchingState;
        return state;
    }

    /// <summary>
    /// Returns the context installed by the current command-chain resource
    /// scope. Transient render-state flags must not be used to reclassify
    /// operations while this scope is active.
    /// </summary>
    internal bool TryGetScopedFrameOpContext(out FrameOpContext context)
    {
        VulkanCommandThreadContext threadContext = commands.ThreadWorkspace.Current;
        if (ReferenceEquals(threadContext.ResourcePlannerRuntimeStateOwner, commands) &&
            threadContext.ResourcePlannerRuntimeGeneration?.State.LastActiveFrameOpContext is { } active)
        {
            context = active;
            return true;
        }

        context = default;
        return false;
    }

    internal FrameOpResourcePlannerSwitchingState ResolveActiveSwitchingState()
        => ResolveActiveSwitchingState(commands.ThreadWorkspace.Current);

    internal void RestoreRuntimeState(in ResourcePlannerRuntimeState state)
    {
        VulkanCommandThreadContext threadContext = commands.ThreadWorkspace.Current;
        ResourcePlannerRuntimeState next = state;
        next.FrameOpResourcePlannerSwitchingState = ResolveActiveSwitchingState(threadContext);
        if (ReferenceEquals(threadContext.ResourcePlannerRuntimeStateOwner, commands) &&
            threadContext.ResourcePlannerRuntimeState.HasValue)
        {
            threadContext.ResourcePlannerRuntimeState = next;
            threadContext.ResourcePlannerRuntimeGeneration =
                new ResourcePlannerRuntimeGeneration(next);
            return;
        }

        lock (planner.PlannerReadbackGate)
            planner.PublishResourcePlannerGeneration(new ResourcePlannerRuntimeGeneration(next));
    }

    internal static void MarkStateUsed(
        FrameOpResourcePlannerSwitchingState switchingState,
        in VulkanFrameOpPlannerStateKey key)
        => switchingState.LastUsedSerials[key] = ++switchingState.UsageSerial;

    internal static bool IsAllocatorExclusivelyOwnedByKey(
        FrameOpResourcePlannerSwitchingState switchingState,
        in VulkanFrameOpPlannerStateKey key,
        VulkanResourceAllocator? allocator)
    {
        if (allocator is null || allocator.IsRetired ||
            switchingState.HasPreparationState &&
            ReferenceEquals(switchingState.PreparationState.ResourceAllocator, allocator))
        {
            return false;
        }

        foreach ((VulkanFrameOpPlannerStateKey candidateKey, ResourcePlannerRuntimeState candidateState) in
                 switchingState.States)
        {
            if (!candidateKey.Equals(key) &&
                ReferenceEquals(candidateState.ResourceAllocator, allocator))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryFindBestPhysicalOwnerState(
        in VulkanFrameOpPlannerStateKey requestedKey,
        FrameOpResourcePlannerSwitchingState switchingState,
        out VulkanFrameOpPlannerStateKey key,
        out ResourcePlannerRuntimeState state)
    {
        key = default;
        state = default;
        bool found = false;
        int bestScore = int.MinValue;
        foreach ((VulkanFrameOpPlannerStateKey candidateKey, ResourcePlannerRuntimeState candidateState) in
                 switchingState.States)
        {
            if (!KeysSharePhysicalOwner(candidateKey, requestedKey) ||
                !IsReusableState(candidateState) ||
                !IsAllocatorExclusivelyOwnedByKey(
                    switchingState,
                    candidateKey,
                    candidateState.ResourceAllocator))
            {
                continue;
            }

            int score = Score(candidateState);
            if (found && score <= bestScore)
                continue;

            found = true;
            bestScore = score;
            key = candidateKey;
            state = candidateState;
        }

        return found;
    }

    internal static void RekeyState(
        FrameOpResourcePlannerSwitchingState switchingState,
        in VulkanFrameOpPlannerStateKey previousKey,
        in VulkanFrameOpPlannerStateKey currentKey,
        in ResourcePlannerRuntimeState state)
    {
        if (!previousKey.Equals(currentKey))
        {
            switchingState.States.Remove(previousKey);
            switchingState.LastUsedSerials.Remove(previousKey);
            switchingState.ActiveKeys.Remove(previousKey);
        }

        switchingState.States[currentKey] = state;
    }

    /// <summary>
    /// Removes older planner states for the same logical output from
    /// <paramref name="switchingState"/>. A newer resource generation replaces
    /// an older one. Within one generation, a newer registry instance revision
    /// replaces the prior descriptor state for the same size and pass. A removed
    /// state's allocator remains owned until its key leaves the table. Allocators
    /// of removed states are appended to
    /// <paramref name="supersededAllocators"/> for the caller to retire once no
    /// remaining state owns them.
    /// </summary>
    internal static void RemoveSupersededGenerationStates(
        FrameOpResourcePlannerSwitchingState switchingState,
        in VulkanFrameOpPlannerStateKey currentKey,
        VulkanResourceAllocator currentAllocator,
        List<VulkanResourceAllocator> supersededAllocators)
    {
        List<VulkanFrameOpPlannerStateKey>? supersededKeys = null;
        foreach (VulkanFrameOpPlannerStateKey candidate in switchingState.States.Keys)
        {
            if (IsSupersededGenerationKey(candidate, currentKey))
                (supersededKeys ??= []).Add(candidate);
        }

        if (supersededKeys is null)
            return;

        for (int index = 0; index < supersededKeys.Count; index++)
        {
            VulkanFrameOpPlannerStateKey supersededKey = supersededKeys[index];
            if (!switchingState.States.Remove(supersededKey, out ResourcePlannerRuntimeState removed))
                continue;

            switchingState.LastUsedSerials.Remove(supersededKey);
            switchingState.ActiveKeys.Remove(supersededKey);
            if (removed.ResourceAllocator is not null &&
                !ReferenceEquals(removed.ResourceAllocator, currentAllocator) &&
                !supersededAllocators.Contains(removed.ResourceAllocator))
            {
                supersededAllocators.Add(removed.ResourceAllocator);
            }
        }
    }

    /// <summary>
    /// Removes plans whose output framebuffer has completed destruction. Capture
    /// cycles can replace outputs without changing the viewport generation.
    /// The caller retires removed allocators through GPU completion tracking.
    /// </summary>
    internal static List<VulkanResourceAllocator>? RemoveDestroyedOutputStates(
        FrameOpResourcePlannerSwitchingState switchingState)
    {
        List<VulkanFrameOpPlannerStateKey>? destroyedKeys = null;
        foreach (KeyValuePair<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> entry in switchingState.States)
        {
            if (!switchingState.ActiveKeys.Contains(entry.Key) &&
                !(switchingState.HasActiveKey && switchingState.ActiveKey.Equals(entry.Key)) &&
                entry.Value.LastActiveFrameOpContext?.OutputFrameBuffer is { IsDestroyed: true })
                (destroyedKeys ??= []).Add(entry.Key);
        }

        if (destroyedKeys is null)
            return null;

        List<VulkanResourceAllocator> removedAllocators = [];
        foreach (VulkanFrameOpPlannerStateKey key in destroyedKeys)
        {
            if (!switchingState.States.Remove(key, out ResourcePlannerRuntimeState removed))
                continue;

            switchingState.LastUsedSerials.Remove(key);
            switchingState.ActiveKeys.Remove(key);
            if (removed.ResourceAllocator is not null && !removedAllocators.Contains(removed.ResourceAllocator))
                removedAllocators.Add(removed.ResourceAllocator);
        }
        switchingState.SwitchingActive = switchingState.ActiveKeys.Count > 1;
        return removedAllocators;
    }

    /// <summary>
    /// Finds the state a resource-generation commit prepared for
    /// <paramref name="context"/> when <paramref name="activeSwitchingState"/> is a
    /// thread-scoped table rather than the published one. Commits always publish
    /// into the published table, but OpenXR planners render from their own
    /// tables, so without this lookup every committed stereo generation would be
    /// allocated a second time by its first eye render. The caller shares the
    /// returned state's allocator; the published entry keeps owning it, so
    /// supersession or eviction there retires it and the scoped table then sees a
    /// retired allocator and prepares its own. The published table is mutated only
    /// on the render thread, so other threads never read it here.
    /// </summary>
    internal bool TryFindCommittedGenerationState(
        FrameOpResourcePlannerSwitchingState activeSwitchingState,
        in FrameOpContext context,
        out ResourcePlannerRuntimeState state)
    {
        state = default;
        if (!RuntimeEngine.IsRenderThread)
            return false;

        lock (planner.PlannerReadbackGate)
        {
            FrameOpResourcePlannerSwitchingState published = ResolvePublishedSwitchingState();
            if (ReferenceEquals(published, activeSwitchingState))
                return false;

            bool found = false;
            int bestScore = int.MinValue;
            VulkanFrameOpPlannerStateKey foundKey = default;
            foreach ((VulkanFrameOpPlannerStateKey candidateKey, ResourcePlannerRuntimeState candidate) in
                     published.States)
            {
                if (candidate.PreparedGenerationManifest is null ||
                    !VulkanFrameOpSnapshotSignatures.MatchesPlannerStateKey(
                        context,
                        candidateKey,
                        candidate.LastActiveFrameOpContext?.PassMetadata) ||
                    !IsReusableState(candidate) ||
                    !IsAllocatorExclusivelyOwnedByKey(published, candidateKey, candidate.ResourceAllocator))
                {
                    continue;
                }

                int score = Score(candidate);
                if (found && score <= bestScore)
                    continue;

                found = true;
                bestScore = score;
                foundKey = candidateKey;
                state = candidate;
            }

            if (found)
                MarkStateUsed(published, foundKey);
            return found;
        }
    }

    /// <summary>
    /// Gets whether the published switching table still references
    /// <paramref name="allocator"/>, as a state shared into a thread-scoped table does.
    /// </summary>
    internal bool IsAllocatorOwnedByPublishedTable(VulkanResourceAllocator allocator)
    {
        lock (planner.PlannerReadbackGate)
            return IsAllocatorOwned(ResolvePublishedSwitchingState(), allocator);
    }

    /// <summary>Preserves image groups that remaining planner states still own.</summary>
    internal void CollectOwnedImageGroups(HashSet<VulkanPhysicalImageGroup> groups)
    {
        AddOwnedImageGroups(ResolveActiveSwitchingState(), groups);
        lock (planner.PlannerReadbackGate)
            AddOwnedImageGroups(ResolvePublishedSwitchingState(), groups);
    }

    private static void AddOwnedImageGroups(
        FrameOpResourcePlannerSwitchingState switchingState,
        HashSet<VulkanPhysicalImageGroup> groups)
    {
        foreach (ResourcePlannerRuntimeState state in switchingState.States.Values)
            AddAllocatorImageGroups(state.ResourceAllocator, groups);
        if (switchingState.HasPreparationState)
            AddAllocatorImageGroups(switchingState.PreparationState.ResourceAllocator, groups);
    }

    private static void AddAllocatorImageGroups(
        VulkanResourceAllocator? allocator,
        HashSet<VulkanPhysicalImageGroup> groups)
    {
        if (allocator is null || allocator.IsRetired)
            return;
        foreach (VulkanPhysicalImageGroup group in allocator.EnumeratePhysicalGroups())
            groups.Add(group);
    }

    /// <summary>
    /// Marks the published table's entry that owns <paramref name="allocator"/> as
    /// used. OpenXR eye and mirror planners render from states shared out of the
    /// published table but touch only their scoped table, so the published LRU
    /// (<c>MaxPlannerStates</c>) could otherwise evict a stereo state still in use,
    /// retire its allocator and force the eye planner to allocate the set again.
    /// Render thread only, like every other published-table mutation.
    /// </summary>
    internal void TouchPublishedOwner(VulkanResourceAllocator? allocator)
    {
        if (allocator is null || !RuntimeEngine.IsRenderThread)
            return;

        lock (planner.PlannerReadbackGate)
        {
            FrameOpResourcePlannerSwitchingState published = ResolvePublishedSwitchingState();
            foreach (KeyValuePair<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> entry in published.States)
            {
                if (!ReferenceEquals(entry.Value.ResourceAllocator, allocator))
                    continue;

                MarkStateUsed(published, entry.Key);
                return;
            }
        }
    }

    private FrameOpResourcePlannerSwitchingState ResolvePublishedSwitchingState()
        => planner.GetPublishedResourcePlannerGeneration().State.FrameOpResourcePlannerSwitchingState ??
            planner.MutableState.DefaultSwitchingState;

    /// <summary>
    /// Gets whether any state in <paramref name="switchingState"/>, including its
    /// pending preparation state, still references <paramref name="allocator"/>.
    /// </summary>
    internal static bool IsAllocatorOwned(
        FrameOpResourcePlannerSwitchingState switchingState,
        VulkanResourceAllocator allocator)
    {
        foreach (ResourcePlannerRuntimeState state in switchingState.States.Values)
        {
            if (ReferenceEquals(state.ResourceAllocator, allocator))
                return true;
        }

        return switchingState.HasPreparationState &&
            ReferenceEquals(switchingState.PreparationState.ResourceAllocator, allocator);
    }

    private static bool IsSupersededGenerationKey(
        in VulkanFrameOpPlannerStateKey candidate,
        in VulkanFrameOpPlannerStateKey currentKey)
        => candidate.ContextKind == currentKey.ContextKind &&
           candidate.PipelineIdentity == currentKey.PipelineIdentity &&
           candidate.ViewportIdentity == currentKey.ViewportIdentity &&
           candidate.OutputFrameBufferIdentity == currentKey.OutputFrameBufferIdentity &&
           candidate.OutputTargetIdentity == currentKey.OutputTargetIdentity &&
           candidate.LogicalViewId == currentKey.LogicalViewId &&
           candidate.SubmissionQueueFamily == currentKey.SubmissionQueueFamily &&
           (candidate.ResourceGeneration < currentKey.ResourceGeneration ||
            candidate.ResourceGeneration == currentKey.ResourceGeneration &&
            candidate.ResourceRegistryInstanceRevision < currentKey.ResourceRegistryInstanceRevision &&
            candidate.DisplayWidth == currentKey.DisplayWidth &&
            candidate.DisplayHeight == currentKey.DisplayHeight &&
            candidate.InternalWidth == currentKey.InternalWidth &&
            candidate.InternalHeight == currentKey.InternalHeight &&
            candidate.PassMetadataSignature == currentKey.PassMetadataSignature);

    private FrameOpResourcePlannerSwitchingState ResolveActiveSwitchingState(
        VulkanCommandThreadContext threadContext)
    {
        if (ReferenceEquals(threadContext.FrameOpResourcePlannerSwitchingStateOwner, commands) &&
            threadContext.FrameOpResourcePlannerSwitchingState is not null)
        {
            return threadContext.FrameOpResourcePlannerSwitchingState;
        }

        return ResolvePublishedSwitchingState();
    }

    private static bool KeysSharePhysicalOwner(
        in VulkanFrameOpPlannerStateKey first,
        in VulkanFrameOpPlannerStateKey second)
        => first.ContextKind == second.ContextKind &&
           first.PipelineIdentity == second.PipelineIdentity &&
           first.ViewportIdentity == second.ViewportIdentity &&
           first.DisplayWidth == second.DisplayWidth &&
           first.DisplayHeight == second.DisplayHeight &&
           first.InternalWidth == second.InternalWidth &&
           first.InternalHeight == second.InternalHeight &&
           first.OutputFrameBufferIdentity == second.OutputFrameBufferIdentity &&
           first.OutputTargetIdentity == second.OutputTargetIdentity &&
           first.SubmissionQueueFamily == second.SubmissionQueueFamily;

    internal static bool IsReusableState(in ResourcePlannerRuntimeState state)
        => state.ResourcePlanner is not null &&
           state.ResourceAllocator is not null &&
           !state.ResourceAllocator.IsRetired &&
           state.ResourceAllocator.OwnershipId == state.AllocatorOwnershipId &&
           state.BarrierPlanner is not null &&
           state.CompiledRenderGraph is not null &&
           state.RenderGraphPlan is not null &&
           state.RenderGraphPlan.Revision == state.ResourcePlannerRevision &&
           ReferenceEquals(state.RenderGraphPlan.CompiledGraph, state.CompiledRenderGraph) &&
           state.RenderGraphPlan.Barriers.HasCompleteNativeBindings;

    private static int Score(in ResourcePlannerRuntimeState state)
    {
        int score = 0;
        if (state.ResourcePlannerRevision != 0)
            score += 10_000;
        if (state.ResourcePlannerSignature != ulong.MaxValue)
            score += 1_000;
        if (state.ResourceAllocationSignature != ulong.MaxValue)
            score += 1_000;
        score += Math.Min(state.ResourceAllocator.LogicalTextureAllocations.Count, 4096) * 4;
        score += Math.Min(state.ResourceAllocator.LogicalBufferAllocations.Count, 4096);
        return score;
    }

    internal readonly struct RuntimeStateScope : IDisposable
    {
        private readonly VulkanCommandThreadContext _context;
        private readonly VulkanCommandRuntime _owner;
        private readonly VulkanCommandRuntime? _previousOwner;
        private readonly ResourcePlannerRuntimeState? _previousState;
        private readonly ResourcePlannerRuntimeGeneration? _previousGeneration;

        internal RuntimeStateScope(
            VulkanCommandThreadContext context,
            VulkanCommandRuntime owner,
            in ResourcePlannerRuntimeState state)
        {
            _context = context;
            _owner = owner;
            ResourcePlannerRuntimeState scopedState = state;
            scopedState.FrameOpResourcePlannerSwitchingState ??=
                new FrameOpResourcePlannerSwitchingState();
            _previousOwner = context.ResourcePlannerRuntimeStateOwner;
            _previousState = context.ResourcePlannerRuntimeState;
            _previousGeneration = context.ResourcePlannerRuntimeGeneration;
            context.ResourcePlannerRuntimeStateOwner = owner;
            context.ResourcePlannerRuntimeState = scopedState;
            context.ResourcePlannerRuntimeGeneration =
                new ResourcePlannerRuntimeGeneration(scopedState);
        }

        internal ResourcePlannerRuntimeState CaptureCurrent(
            in ResourcePlannerRuntimeState fallbackState,
            FrameOpResourcePlannerSwitchingState activeSwitchingState)
        {
            if (!ReferenceEquals(_context.ResourcePlannerRuntimeStateOwner, _owner) ||
                !_context.ResourcePlannerRuntimeState.HasValue)
            {
                return fallbackState;
            }

            ResourcePlannerRuntimeState state = _context.ResourcePlannerRuntimeState.Value;
            state.FrameOpResourcePlannerSwitchingState = activeSwitchingState;
            return state;
        }

        public void Dispose()
        {
            _context.ResourcePlannerRuntimeStateOwner = _previousOwner;
            _context.ResourcePlannerRuntimeState = _previousState;
            _context.ResourcePlannerRuntimeGeneration = _previousGeneration;
        }
    }
}
