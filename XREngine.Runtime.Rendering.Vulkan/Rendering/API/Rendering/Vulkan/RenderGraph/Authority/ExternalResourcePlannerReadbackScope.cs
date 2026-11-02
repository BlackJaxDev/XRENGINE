using System.Threading;
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
    private static long _plannerCacheMissDiagnosticCount;
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

        SupersededAllocators = VulkanResourcePlannerSessionService.RemoveDestroyedOutputStates(switchingState);

        bool canReusePreviousState =
            IsPreparedStateForKey(_previousState, _key) &&
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

        if (sessions.TryFindCommittedGenerationState(
                switchingState,
                context,
                out ResourcePlannerRuntimeState committedState))
        {
            committedState.LastActiveFrameOpContext = context;
            sessions.RestoreRuntimeState(committedState);
            PublishPreparedState(switchingState, committedState);
            return;
        }

        bool publishedSwitchingState = sessions.IsPublishedSwitchingState(switchingState);
        if (publishedSwitchingState)
            CompatibleImageDonor = FindCompatibleImageDonor(context, _key, switchingState);
        if (VulkanBufferRetirementDiagnostics.Enabled)
            RecordPlannerCacheMiss(switchingState, _key, context, publishedSwitchingState, CompatibleImageDonor);
        sessions.RestoreRuntimeState(ResourcePlannerRuntimeState.CreateEmpty());
    }

    private static void RecordPlannerCacheMiss(
        FrameOpResourcePlannerSwitchingState switchingState,
        in VulkanFrameOpPlannerStateKey requestedKey,
        in FrameOpContext context,
        bool publishedSwitchingState,
        VulkanPhysicalImageReuseDonor? donor)
    {
        long miss = Interlocked.Increment(ref _plannerCacheMissDiagnosticCount);
        if (miss > 12 && miss % 64 != 0)
            return;

        VulkanFrameOpPlannerStateKey? nearest = null;
        int nearestScore = int.MinValue;
        foreach (VulkanFrameOpPlannerStateKey candidate in switchingState.States.Keys)
        {
            if (candidate.PipelineIdentity != requestedKey.PipelineIdentity ||
                candidate.ViewportIdentity != requestedKey.ViewportIdentity)
                continue;

            int score = 0;
            if (candidate.ContextKind == requestedKey.ContextKind)
                score++;
            if (candidate.OutputFrameBufferIdentity == requestedKey.OutputFrameBufferIdentity)
                score += 8;
            if (candidate.OutputTargetIdentity == requestedKey.OutputTargetIdentity)
                score += 8;
            if (candidate.ResourceRegistrySignature == requestedKey.ResourceRegistrySignature)
                score += 4;
            if (candidate.ResourceGeneration == requestedKey.ResourceGeneration)
                score += 2;
            if (candidate.DescriptorGeneration == requestedKey.DescriptorGeneration)
                score += 2;
            if (score <= nearestScore)
                continue;

            nearest = candidate;
            nearestScore = score;
        }

        string donorReason = !publishedSwitchingState
            ? "ThreadLocalSwitchingState"
            : donor is not null
                ? "Selected"
                : nearest is VulkanFrameOpPlannerStateKey nearestKey &&
                  switchingState.States.TryGetValue(nearestKey, out ResourcePlannerRuntimeState nearestState)
                    ? DescribeImageDonorRejection(context, requestedKey, nearestKey, nearestState, switchingState)
                    : "NoSamePipelineViewportState";
        Debug.WriteAuxiliaryLog(
            "vulkan-physical-plan-replacements.log",
            $"scopeMiss={miss} cachedStates={switchingState.States.Count} " +
            $"requestedKey={requestedKey} nearestKey={nearest?.ToString() ?? "<none>"} " +
            $"nearestScore={nearestScore} imageDonor={donor?.Key.ToString() ?? "<none>"} " +
            $"imageDonorReason={donorReason}");
    }

    private static string DescribeImageDonorRejection(
        in FrameOpContext context,
        in VulkanFrameOpPlannerStateKey requestedKey,
        in VulkanFrameOpPlannerStateKey candidateKey,
        in ResourcePlannerRuntimeState candidateState,
        FrameOpResourcePlannerSwitchingState switchingState)
    {
        if (context.ContextKind != EVulkanFrameOpContextKind.MainViewport ||
            candidateKey.ContextKind != EVulkanFrameOpContextKind.MainViewport)
            return "NonMainViewport";
        if (candidateKey.OutputFrameBufferIdentity != requestedKey.OutputFrameBufferIdentity ||
            candidateKey.OutputTargetIdentity != requestedKey.OutputTargetIdentity ||
            candidateKey.LogicalViewId != requestedKey.LogicalViewId)
            return "OutputIdentityMismatch";
        if (candidateKey.DisplayWidth != requestedKey.DisplayWidth ||
            candidateKey.DisplayHeight != requestedKey.DisplayHeight ||
            candidateKey.InternalWidth != requestedKey.InternalWidth ||
            candidateKey.InternalHeight != requestedKey.InternalHeight)
            return "DimensionsMismatch";
        if (candidateKey.PassMetadataSignature != requestedKey.PassMetadataSignature)
            return "PassMetadataSignatureMismatch";
        if (candidateKey.SubmissionQueueFamily != requestedKey.SubmissionQueueFamily)
            return "SubmissionQueueMismatch";
        if (!VulkanResourcePlannerSessionService.IsSupersededGenerationKey(candidateKey, requestedKey))
            return "NotSuperseded";
        if (!IsPreparedStateForKey(candidateState, candidateKey))
            return "NotPrepared";
        if (!VulkanResourcePlannerSessionService.IsAllocatorExclusivelyOwnedByKey(
                switchingState, candidateKey, candidateState.ResourceAllocator))
            return "AllocatorShared";
        if (candidateState.LastActiveFrameOpContext is not FrameOpContext priorContext)
            return "MissingPriorContext";
        if (!ReferenceEquals(priorContext.PipelineInstance, context.PipelineInstance) ||
            !ReferenceEquals(priorContext.PassMetadata, context.PassMetadata))
            return "PipelineOrMetadataReferenceMismatch";
        if (!string.Equals(priorContext.OutputFrameBufferName, context.OutputFrameBufferName, StringComparison.Ordinal) ||
            !string.Equals(priorContext.OutputTargetName, context.OutputTargetName, StringComparison.Ordinal))
            return "OutputNameMismatch";
        if (priorContext.StereoEnabled != context.StereoEnabled ||
            priorContext.MultiviewEnabled != context.MultiviewEnabled)
            return "ViewModeMismatch";
        return "NoMatchingCandidate";
    }

    internal bool RequiresPreparation => _active && !_preparationCompleted;

    internal VulkanPhysicalImageReuseDonor? CompatibleImageDonor { get; }

    /// <summary>
    /// Allocators of older resource generations that publishing this scope's
    /// state removed from the active switching table. The caller retires those no
    /// remaining state owns; null when nothing was superseded.
    /// </summary>
    internal List<VulkanResourceAllocator>? SupersededAllocators { get; private set; }

    internal void CompletePreparation()
    {
        if (!RequiresPreparation)
            return;

        ResourcePlannerRuntimeState preparedState =
            _sessions.CaptureRuntimeState();
        if (!IsPreparedStateForKey(preparedState, _key))
        {
            throw new RenderResourcePreparationPendingException(
                "The requested Vulkan resource plan is not ready for publication.");
        }

        PublishPreparedState(_sessions.ResolveActiveSwitchingState(), preparedState);
        preparedState.ResourceAllocator.CommitReusedPhysicalImageMetadata();
    }

    internal void AbortPreparation(VulkanBackendObjectContext backendContext)
    {
        if (!_active || _preparationCompleted)
            return;

        VulkanResourceAllocator candidate = _sessions.CaptureRuntimeState().ResourceAllocator;
        FrameOpResourcePlannerSwitchingState switchingState =
            _sessions.ResolveActiveSwitchingState();
        if (ReferenceEquals(candidate, _previousState.ResourceAllocator) ||
            candidate.IsRetired ||
            VulkanResourcePlannerSessionService.IsAllocatorOwned(switchingState, candidate) ||
            _sessions.IsAllocatorOwnedByPublishedTable(candidate))
        {
            return;
        }

        _ = candidate.TryRetirePhysicalResources(
            backendContext,
            exceptImageGroups: candidate.CapturePendingReusedImageGroups(),
            immediate: true);
    }

    private void PublishPreparedState(
        FrameOpResourcePlannerSwitchingState switchingState,
        in ResourcePlannerRuntimeState preparedState)
    {
        switchingState.States[_key] = preparedState;
        VulkanResourcePlannerSessionService.MarkStateUsed(switchingState, _key);

        // Generation commits publish into the shared table, so tables that only
        // render-time preparation fills (OpenXR eye and mirror planners) would
        // otherwise keep every earlier generation's full image set.
        List<VulkanResourceAllocator> superseded = SupersededAllocators ?? [];
        VulkanResourcePlannerSessionService.RemoveSupersededGenerationStates(
            switchingState,
            _key,
            preparedState.ResourceAllocator,
            superseded);
        if (superseded.Count != 0)
            SupersededAllocators = superseded;
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
            FrameOpResourcePlannerSwitchingState switchingState =
                _sessions.ResolveActiveSwitchingState();
            canPublish = IsPreparedStateForKey(currentState, _key) &&
                VulkanResourcePlannerSessionService.IsAllocatorExclusivelyOwnedByKey(
                    switchingState,
                    _key,
                    currentState.ResourceAllocator);
            if (canPublish)
            {
                switchingState.States[_key] = currentState;
                VulkanResourcePlannerSessionService.MarkStateUsed(switchingState, _key);
            }

            // A state shared from the published table stays recently used there.
            _sessions.TouchPublishedOwner(currentState.ResourceAllocator);
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
                !IsPreparedStateForKey(candidateState, candidateKey) ||
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

    private static VulkanPhysicalImageReuseDonor? FindCompatibleImageDonor(
        in FrameOpContext context,
        in VulkanFrameOpPlannerStateKey requestedKey,
        FrameOpResourcePlannerSwitchingState switchingState)
    {
        if (context.ContextKind != EVulkanFrameOpContextKind.MainViewport)
            return null;

        VulkanPhysicalImageReuseDonor? best = null;
        ulong bestSerial = 0UL;
        foreach ((VulkanFrameOpPlannerStateKey key, ResourcePlannerRuntimeState state) in switchingState.States)
        {
            if (key.ContextKind != EVulkanFrameOpContextKind.MainViewport ||
                key.PipelineIdentity != requestedKey.PipelineIdentity ||
                key.ViewportIdentity != requestedKey.ViewportIdentity ||
                key.LogicalViewId != requestedKey.LogicalViewId ||
                key.OutputFrameBufferIdentity != requestedKey.OutputFrameBufferIdentity ||
                key.OutputTargetIdentity != requestedKey.OutputTargetIdentity ||
                key.DisplayWidth != requestedKey.DisplayWidth ||
                key.DisplayHeight != requestedKey.DisplayHeight ||
                key.InternalWidth != requestedKey.InternalWidth ||
                key.InternalHeight != requestedKey.InternalHeight ||
                key.PassMetadataSignature != requestedKey.PassMetadataSignature ||
                key.SubmissionQueueFamily != requestedKey.SubmissionQueueFamily ||
                !VulkanResourcePlannerSessionService.IsSupersededGenerationKey(key, requestedKey) ||
                !IsPreparedStateForKey(state, key) ||
                !VulkanResourcePlannerSessionService.IsAllocatorExclusivelyOwnedByKey(
                    switchingState, key, state.ResourceAllocator) ||
                state.LastActiveFrameOpContext is not FrameOpContext donorContext ||
                !ReferenceEquals(donorContext.PipelineInstance, context.PipelineInstance) ||
                !ReferenceEquals(donorContext.PassMetadata, context.PassMetadata) ||
                !string.Equals(donorContext.OutputFrameBufferName, context.OutputFrameBufferName, StringComparison.Ordinal) ||
                !string.Equals(donorContext.OutputTargetName, context.OutputTargetName, StringComparison.Ordinal) ||
                donorContext.StereoEnabled != context.StereoEnabled ||
                donorContext.MultiviewEnabled != context.MultiviewEnabled)
            {
                continue;
            }

            ulong serial = switchingState.LastUsedSerials.TryGetValue(key, out ulong lastUsed)
                ? lastUsed
                : 0UL;
            if (best is not null && serial <= bestSerial)
                continue;

            best = new VulkanPhysicalImageReuseDonor(key, state);
            bestSerial = serial;
        }

        return best;
    }

    private static bool ContextHasPlannerResources(in FrameOpContext context)
        => context.ResourceRegistry is not null || context.PassMetadata is { Count: > 0 };

    private static bool IsPreparedStateForKey(
        in ResourcePlannerRuntimeState state,
        in VulkanFrameOpPlannerStateKey key)
        => state.ResourcePlannerRevision != 0 &&
           !ReferenceEquals(state.RenderGraphPlan, VulkanRenderGraphPlan.Empty) &&
           VulkanResourcePlannerSessionService.IsReusableState(state) &&
           state.LastActiveFrameOpContext is FrameOpContext preparedContext &&
           VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(preparedContext) == key;

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
