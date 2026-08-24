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
        => _freeReadbackScopes.Clear();

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

    private FrameOpResourcePlannerSwitchingState ResolveActiveSwitchingState(
        VulkanCommandThreadContext threadContext)
    {
        if (ReferenceEquals(threadContext.FrameOpResourcePlannerSwitchingStateOwner, commands) &&
            threadContext.FrameOpResourcePlannerSwitchingState is not null)
        {
            return threadContext.FrameOpResourcePlannerSwitchingState;
        }

        return planner.GetPublishedResourcePlannerGeneration().State.FrameOpResourcePlannerSwitchingState ??
            planner.MutableState.DefaultSwitchingState;
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

    private static bool IsReusableState(in ResourcePlannerRuntimeState state)
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
