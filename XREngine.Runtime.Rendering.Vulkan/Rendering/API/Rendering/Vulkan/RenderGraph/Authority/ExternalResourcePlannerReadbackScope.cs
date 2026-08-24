using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns a temporary resource-planner publication without retaining the renderer
/// facade. A caller performs physical preparation only when <see cref="RequiresPreparation"/>
/// is true, then seals the resulting state with <see cref="CompletePreparation"/>.
/// </summary>
internal sealed class ExternalResourcePlannerReadbackScope : IDisposable
{
    private const int MaxFrameOpResourcePlannerSwitchingStates = 12;
    private readonly VulkanResourcePlannerSessionService _sessions;
    private readonly VulkanDeviceStateMachine _deviceState;
    private readonly Dictionary<RenderResourceRegistry, VulkanOpenXrResourceRegistryWrapperRefreshStamp> _wrapperRefreshStamps;
    private readonly ResourcePlannerRuntimeState _previousState;
    private readonly VulkanFrameOpPlannerStateKey _key;
    private readonly FrameOpContext _context;
    private readonly bool _active;
    private bool _preparationCompleted;
    private bool _disposed;

    internal ExternalResourcePlannerReadbackScope(
        VulkanResourcePlannerSessionService sessions,
        VulkanDeviceContext device,
        VulkanOutputRuntime output,
        in FrameOpContext context)
    {
        _sessions = sessions;
        _deviceState = device.StateMachine;
        _wrapperRefreshStamps = output.OpenXrBackend.ResourceRegistryWrapperRefreshStamps;
        _context = context;
        _previousState = sessions.CaptureRuntimeState();
        FrameOpResourcePlannerSwitchingState switchingState =
            sessions.ResolveActiveSwitchingState();
        _active = device.IsOperational &&
            MaxFrameOpResourcePlannerSwitchingStates > 1 &&
            !switchingState.MergedPlanActive &&
            ContextHasPlannerResources(context);
        _key = _active
            ? VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(context)
            : default;

        if (!_active)
            return;

        bool canReusePreviousState =
            RuntimeStateMatchesKeyIgnoringRegistry(_previousState, _key) &&
            VulkanResourcePlannerSessionService.IsAllocatorExclusivelyOwnedByKey(
                switchingState,
                _key,
                _previousState.ResourceAllocator);
        bool foundCachedState = TryFindBestCompatibleState(
            context,
            switchingState,
            out VulkanFrameOpPlannerStateKey cachedKey,
            out ResourcePlannerRuntimeState cachedState);
        if (foundCachedState &&
            (!canReusePreviousState ||
             Score(cachedState) > Score(_previousState)))
        {
            _key = cachedKey;
            sessions.RestoreRuntimeState(cachedState);
            VulkanResourcePlannerSessionService.MarkStateUsed(switchingState, _key);
            _preparationCompleted = true;
            return;
        }

        if (canReusePreviousState)
        {
            sessions.RestoreRuntimeState(_previousState);
            switchingState.States[_key] = _previousState;
            VulkanResourcePlannerSessionService.MarkStateUsed(switchingState, _key);
            _preparationCompleted = true;
            return;
        }

        sessions.RestoreRuntimeState(ResourcePlannerRuntimeState.CreateEmpty());
    }

    internal bool RequiresPreparation => _active && !_preparationCompleted;

    internal void CompletePreparation()
    {
        if (!RequiresPreparation)
            return;

        ResourcePlannerRuntimeState preparedState =
            _sessions.CaptureRuntimeState();
        preparedState.LastActiveFrameOpContext = _context;
        FrameOpResourcePlannerSwitchingState switchingState =
            _sessions.ResolveActiveSwitchingState();
        switchingState.States[_key] = preparedState;
        VulkanResourcePlannerSessionService.MarkStateUsed(switchingState, _key);
        _preparationCompleted = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        ResourcePlannerRuntimeState currentState = default;
        bool canPublish = _active && _preparationCompleted && _deviceState.IsOperational;
        if (canPublish)
        {
            currentState = _sessions.CaptureRuntimeState();
            currentState.LastActiveFrameOpContext = _context;
            FrameOpResourcePlannerSwitchingState switchingState =
                _sessions.ResolveActiveSwitchingState();
            canPublish = VulkanResourcePlannerSessionService.IsAllocatorExclusivelyOwnedByKey(
                switchingState,
                _key,
                currentState.ResourceAllocator);
            if (canPublish)
            {
                switchingState.States[_key] = currentState;
                VulkanResourcePlannerSessionService.MarkStateUsed(switchingState, _key);
            }
        }

        ResourcePlannerRuntimeState restoreState =
            _active && _previousState.ResourceAllocator is not null &&
            _previousState.ResourceAllocator.IsRetired
                ? canPublish
                    ? currentState
                    : ResourcePlannerRuntimeState.CreateEmpty()
                : _previousState;
        _sessions.RestoreRuntimeState(restoreState);
        if (_active &&
            !ReferenceEquals(currentState.ResourceAllocator, restoreState.ResourceAllocator) &&
            _context.ResourceRegistry is not null)
        {
            _wrapperRefreshStamps.Remove(_context.ResourceRegistry);
        }
    }

    private static bool TryFindBestCompatibleState(
        in FrameOpContext context,
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
            if (!VulkanFrameOpSnapshotSignatures.MatchesPlannerStateKey(
                    context,
                    candidateKey,
                    candidateState.LastActiveFrameOpContext?.PassMetadata) ||
                !VulkanResourcePlannerSessionService.IsAllocatorExclusivelyOwnedByKey(
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

    private static bool RuntimeStateMatchesKeyIgnoringRegistry(
        in ResourcePlannerRuntimeState state,
        in VulkanFrameOpPlannerStateKey key)
    {
        if (state.ResourceAllocator is null || state.ResourceAllocator.IsRetired ||
            state.LastActiveFrameOpContext is not FrameOpContext context)
        {
            return false;
        }

        VulkanFrameOpPlannerStateKey contextKey =
            VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(context);
        return contextKey.ContextKind == key.ContextKind &&
            contextKey.PipelineIdentity == key.PipelineIdentity &&
            contextKey.ViewportIdentity == key.ViewportIdentity &&
            contextKey.DisplayWidth == key.DisplayWidth &&
            contextKey.DisplayHeight == key.DisplayHeight &&
            contextKey.InternalWidth == key.InternalWidth &&
            contextKey.InternalHeight == key.InternalHeight &&
            contextKey.OutputFrameBufferIdentity == key.OutputFrameBufferIdentity &&
            contextKey.OutputTargetIdentity == key.OutputTargetIdentity &&
            contextKey.LogicalViewId == key.LogicalViewId &&
            contextKey.PassMetadataSignature == key.PassMetadataSignature &&
            contextKey.ResourceGeneration == key.ResourceGeneration &&
            contextKey.DescriptorGeneration == key.DescriptorGeneration &&
            contextKey.SubmissionQueueFamily == key.SubmissionQueueFamily;
    }

    private static bool ContextHasPlannerResources(in FrameOpContext context)
        => context.ResourceRegistry is not null || context.PassMetadata is { Count: > 0 };

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
}
