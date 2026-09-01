using XREngine.Rendering.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    private static readonly Dictionary<int, IComparer<RenderCommand>?> EmptyPipelinePasses = [];
    private readonly object _pipelineTransitionSync = new();
    private RenderPipeline? _requestedPipeline;
    private XRViewport? _requestedPipelineViewport;
    private ulong _pipelineRequestSerial;
    private ulong _appliedPipelineRequestSerial;
    private ulong _startedPipelineRequestSerial;
    private ulong _appliedPipelineRevision;
    private int _pipelineTransitionQueued;
    private int _terminalTeardownRequested;
    private RenderPipeline? _pipelineTransitionPrevious;
    private RenderPipeline? _pipelineTransitionPublishedPartial;
    private RenderPipeline? _applyingPipeline;
    private RenderPipeline? _fullyAppliedPipeline;
    private RenderPipeline? _legacyResourceOwnerPipeline;

    /// <summary>
    /// Gets the instance-local revision of the applied pipeline asset. Each real reference
    /// replacement advances the revision even when both assets share a type and debug name.
    /// </summary>
    public ulong PipelineRevision => _appliedPipelineRevision;

    /// <summary>
    /// Gets the pipeline whose output binding may be updated on the render thread. During a
    /// transition this is the explicit applying target; otherwise it is the fully applied owner.
    /// </summary>
    internal RenderPipeline? PipelineForOutputBinding
    {
        get
        {
            lock (_pipelineTransitionSync)
                return _applyingPipeline ?? _fullyAppliedPipeline;
        }
    }

    /// <summary>
    /// Captures an ownership token only when the supplied asset remains the fully applied owner
    /// and no newer pipeline request is waiting to supersede it.
    /// </summary>
    internal bool TryCaptureAppliedPipelineRevision(
        RenderPipeline ownerPipeline,
        out ulong pipelineRevision)
    {
        ArgumentNullException.ThrowIfNull(ownerPipeline);

        lock (_pipelineTransitionSync)
        {
            if (!IsAppliedPipelineOwnerNoLock(ownerPipeline, _appliedPipelineRevision))
            {
                pipelineRevision = 0;
                return false;
            }

            pipelineRevision = _appliedPipelineRevision;
            return true;
        }
    }

    /// <summary>
    /// Publishes a rebuilt command chain only while both its asset and instance revision still
    /// match. Holding the transition lock through the mutation prevents a request arriving on
    /// another thread from turning the captured asset snapshot into a stale-owner write.
    /// </summary>
    internal bool TryApplyCommandChainUpdate(
        RenderPipeline ownerPipeline,
        ulong expectedPipelineRevision,
        ulong expectedCommandGeneration,
        Dictionary<int, IComparer<RenderCommand>?> passIndicesAndSorters,
        IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        ArgumentNullException.ThrowIfNull(ownerPipeline);
        ArgumentNullException.ThrowIfNull(passIndicesAndSorters);
        ArgumentNullException.ThrowIfNull(passMetadata);

        EnsureOwnedMutationRunsOnRenderThread();
        lock (_pipelineTransitionSync)
        {
            if (!IsAppliedPipelineOwnerNoLock(ownerPipeline, expectedPipelineRevision) ||
                ownerPipeline.CommandGeneration != expectedCommandGeneration)
            {
                return false;
            }

            MeshRenderCommands.SetRenderPasses(passIndicesAndSorters, passMetadata);
            XRViewport? viewport = LastWindowViewport;
            bool presentsDirectlyToWindow =
                viewport is not null &&
                viewport.Window?.Viewports.Contains(viewport) == true;
            bool rendersToExternalSwapchain =
                viewport?.RendersToExternalSwapchainTarget == true;

            if (presentsDirectlyToWindow || rendersToExternalSwapchain)
                InvalidatePhysicalResources();
            else
                DestroyCacheOnRenderThread();

            return true;
        }
    }

    internal bool TryInvalidateOwnedPhysicalResources(
        RenderPipeline ownerPipeline,
        ulong expectedPipelineRevision,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(ownerPipeline);

        EnsureOwnedMutationRunsOnRenderThread();
        lock (_pipelineTransitionSync)
        {
            if (!IsAppliedPipelineOwnerNoLock(ownerPipeline, expectedPipelineRevision))
                return false;

            InvalidatePhysicalResources(reason);
            return true;
        }
    }

    internal bool TryInvalidateOwnedAntiAliasingResources(
        RenderPipeline ownerPipeline,
        ulong expectedPipelineRevision,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(ownerPipeline);

        EnsureOwnedMutationRunsOnRenderThread();
        lock (_pipelineTransitionSync)
        {
            if (!IsAppliedPipelineOwnerNoLock(ownerPipeline, expectedPipelineRevision))
                return false;

            RenderPipelineAntiAliasingResources.InvalidateAntiAliasingResources(this, reason);
            return true;
        }
    }

    private bool IsAppliedPipelineOwnerNoLock(
        RenderPipeline ownerPipeline,
        ulong expectedPipelineRevision)
        => System.Threading.Volatile.Read(ref _terminalTeardownRequested) == 0 &&
           _pipelineRequestSerial == _appliedPipelineRequestSerial &&
           _appliedPipelineRevision == expectedPipelineRevision &&
           ReferenceEquals(_pipeline, ownerPipeline) &&
           ReferenceEquals(_fullyAppliedPipeline, ownerPipeline);

    private static void EnsureOwnedMutationRunsOnRenderThread()
    {
        if (!RuntimeEngine.IsRenderThread &&
            RuntimeRenderingHostServices.FrameTiming.IsRendererActive)
        {
            throw new InvalidOperationException(
                "Pipeline-owned instance mutations must execute on the render thread while the renderer is active.");
        }
    }

    /// <summary>
    /// Requests a render-thread-owned pipeline asset transition. Multiple requests queued before
    /// the render thread runs collapse to the most recent asset and viewport.
    /// </summary>
    internal void RequestPipelineChange(RenderPipeline? pipeline, XRViewport? viewport)
    {
        if (System.Threading.Volatile.Read(ref _terminalTeardownRequested) != 0)
            return;

        bool processInline;
        lock (_pipelineTransitionSync)
        {
            RenderPipeline? effectiveRequest = _pipelineRequestSerial == _appliedPipelineRequestSerial
                ? _pipeline
                : _requestedPipeline;
            if (ReferenceEquals(effectiveRequest, pipeline))
            {
                if (_pipelineRequestSerial != _appliedPipelineRequestSerial && viewport is not null)
                    _requestedPipelineViewport = viewport;
                return;
            }

            _requestedPipeline = pipeline;
            _requestedPipelineViewport = viewport;
            _pipelineRequestSerial++;

            if (_pipelineTransitionQueued != 0)
                return;

            _pipelineTransitionQueued = 1;
            processInline = RuntimeEngine.IsRenderThread ||
                !RuntimeRenderingHostServices.FrameTiming.IsRendererActive;
        }

        if (processInline)
        {
            ProcessRequestedPipelineTransitions();
            return;
        }

        RuntimeEngine.EnqueueRenderThreadTask(
            () => ProcessRequestedPipelineTransitions(),
            $"XRRenderPipelineInstance.ApplyPipelineTransition[{InstanceId}]",
            RenderThreadJobKind.RenderPipelineResource);
    }

    /// <summary>
    /// Applies an outstanding request before command recording if the scheduler has not yet
    /// drained its queued transition job.
    /// </summary>
    private bool ApplyLatestRequestedPipelineIfNeeded()
    {
        if (System.Threading.Volatile.Read(ref _terminalTeardownRequested) != 0)
            return false;

        lock (_pipelineTransitionSync)
            if (_pipelineRequestSerial == _appliedPipelineRequestSerial)
                return ReferenceEquals(_fullyAppliedPipeline, _pipeline);

        return ProcessRequestedPipelineTransitions();
    }

    private bool ProcessRequestedPipelineTransitions()
    {
        if (!RuntimeEngine.IsRenderThread && RuntimeRenderingHostServices.FrameTiming.IsRendererActive)
            throw new InvalidOperationException("Render pipeline transitions must execute on the render thread while the renderer is active.");

        while (true)
        {
            if (System.Threading.Volatile.Read(ref _terminalTeardownRequested) != 0)
            {
                lock (_pipelineTransitionSync)
                    _pipelineTransitionQueued = 0;
                return false;
            }

            RenderPipeline? requestedPipeline;
            XRViewport? requestedViewport;
            ulong requestSerial;
            lock (_pipelineTransitionSync)
            {
                requestSerial = _pipelineRequestSerial;
                if (requestSerial == _appliedPipelineRequestSerial)
                {
                    _pipelineTransitionQueued = 0;
                    return true;
                }

                requestedPipeline = _requestedPipeline;
                requestedViewport = _requestedPipelineViewport;
            }

            try
            {
                ApplyPipelineTransition(requestedPipeline, requestedViewport, requestSerial);
            }
            catch (Exception ex)
            {
                lock (_pipelineTransitionSync)
                    _pipelineTransitionQueued = 0;

                Debug.RenderingWarning(
                    "[RenderPipelineTransition] Apply failed; the command chain remains disabled and the transition will retry. Instance={0} Target={1} Request={2} Error={3}",
                    InstanceId,
                    requestedPipeline?.DebugName ?? "<none>",
                    requestSerial,
                    ex.Message);
                return false;
            }

            lock (_pipelineTransitionSync)
            {
                _appliedPipelineRequestSerial = requestSerial;
                if (_pipelineRequestSerial == requestSerial)
                {
                    _pipelineTransitionQueued = 0;
                    return true;
                }
            }
        }
    }

    private void ApplyPipelineTransition(
        RenderPipeline? pipeline,
        XRViewport? viewport,
        ulong requestSerial)
    {
        viewport ??= LastWindowViewport;
        if (_startedPipelineRequestSerial != requestSerial)
        {
            RenderPipeline? publishedBefore = _pipeline;
            if (!ReferenceEquals(_pipeline, pipeline))
            {
                bool changed = SetField(ref _pipeline, pipeline, nameof(Pipeline));
                if (!changed && !ReferenceEquals(_pipeline, pipeline))
                {
                    throw new InvalidOperationException(
                        $"The {nameof(Pipeline)} change to '{pipeline?.DebugName ?? "<none>"}' was vetoed.");
                }
            }

            // The last fully applied owner remains authoritative even when a failed attempt
            // already published a partial target reference. Track that partial separately so a
            // superseding request can detach its membership without misattributing old resources.
            _pipelineTransitionPrevious = _fullyAppliedPipeline;
            _pipelineTransitionPublishedPartial =
                !ReferenceEquals(publishedBefore, _fullyAppliedPipeline)
                    ? publishedBefore
                    : null;
            _startedPipelineRequestSerial = requestSerial;
            _appliedPipelineRevision++;
            _requiresManagedResourceGeneration = null;
            _classifiedResourceLayoutKey = null;
            _lastSuccessfulLayoutlessResourceKey = null;

            // Command/output packages compare this counter before execution. Advance it once
            // when the transition starts so an old package cannot validate during a retry.
            ResourceGeneration++;
        }

        RenderPipeline? outgoing = _pipelineTransitionPrevious;
        outgoing?.RemoveInstance(this);
        RenderPipeline? publishedPartial = _pipelineTransitionPublishedPartial;
        if (!ReferenceEquals(publishedPartial, outgoing) &&
            !ReferenceEquals(publishedPartial, pipeline))
            publishedPartial?.RemoveInstance(this);

        lock (_pipelineTransitionSync)
            _applyingPipeline = pipeline;

        try
        {
            ApplyPipelineChanged(outgoing, pipeline, viewport);
            lock (_pipelineTransitionSync)
            {
                _fullyAppliedPipeline = pipeline;
                _pipelineTransitionPublishedPartial = null;
            }
        }
        finally
        {
            lock (_pipelineTransitionSync)
                _applyingPipeline = null;
        }
    }

    private void ApplyPipelineChanged(
        RenderPipeline? previous,
        RenderPipeline? pipeline,
        XRViewport? viewport)
    {
        ClearAdvancedOutputBinding();

        // CPU publications and variables can point directly at the outgoing pipeline's
        // resources. Clear them before a replacement command package can be published.
        NotifyCacheClearingHandlers("RenderPipelineAssetChanged");
        Variables.Clear();
        DestroyLegacyResources(
            _legacyResourceOwnerPipeline ?? previous,
            "RenderPipelineAssetChanged");

        if (pipeline is not null)
        {
            MeshRenderCommands.ResetForPipelineTransition(
                pipeline.PassIndicesAndSorters,
                pipeline.PassMetadata);
            InvalidMaterial = pipeline.InvalidMaterial;
            pipeline.AddInstance(this);
        }
        else
        {
            MeshRenderCommands.ResetForPipelineTransition(EmptyPipelinePasses);
            InvalidMaterial = null;
        }

        ResetPipelineScopedRuntimeState();

        if (PendingGeneration is not null &&
            PendingGeneration.PipelineRevision != _appliedPipelineRevision)
        {
            DiscardPendingGeneration("RenderPipelineAssetChanged");
        }

        viewport ??= LastWindowViewport;
        if (viewport is null)
            return;

        RuntimeEngine.Rendering.ReleaseVulkanUpscaleBridge(
            viewport,
            $"render pipeline changed from {previous?.DebugName ?? "<none>"} to {pipeline?.DebugName ?? "<none>"}");
        viewport.HandleAppliedRenderPipelineTransition(pipeline);
    }

    /// <summary>
    /// Requests terminal render-thread cleanup for an instance whose viewport will never render
    /// again. Unlike a revision-guarded cache clear, this request must release every owner.
    /// </summary>
    internal void RequestTerminalTeardown()
    {
        if (System.Threading.Interlocked.Exchange(ref _terminalTeardownRequested, 1) != 0)
            return;

        if (RuntimeEngine.IsRenderThread || !RuntimeRenderingHostServices.FrameTiming.IsRendererActive)
        {
            TerminalTeardownOnRenderThread();
            return;
        }

        RuntimeEngine.EnqueueRenderThreadTask(
            TerminalTeardownOnRenderThread,
            $"XRRenderPipelineInstance.TerminalTeardown[{InstanceId}]",
            RenderThreadJobKind.RenderPipelineResource);
    }

    private void TerminalTeardownOnRenderThread()
    {
        if (!RuntimeEngine.IsRenderThread && RuntimeRenderingHostServices.FrameTiming.IsRendererActive)
            throw new InvalidOperationException("Render pipeline teardown must execute on the render thread while the renderer is active.");

        lock (_pipelineTransitionSync)
        {
            _requestedPipeline = null;
            _requestedPipelineViewport = null;
            _appliedPipelineRequestSerial = _pipelineRequestSerial;
            _pipelineTransitionQueued = 0;
        }

        RenderPipeline? published = _pipeline;
        RenderPipeline? fullyApplied = _fullyAppliedPipeline;
        RenderPipeline? publishedPartial = _pipelineTransitionPublishedPartial;
        Exception? cleanupFailure = null;

        RunResourceCleanupStep(
            "RemovePublishedPipelineMembership",
            () => published?.RemoveInstance(this),
            ref cleanupFailure);
        if (!ReferenceEquals(fullyApplied, published))
        {
            RunResourceCleanupStep(
                "RemoveFullyAppliedPipelineMembership",
                () => fullyApplied?.RemoveInstance(this),
                ref cleanupFailure);
        }
        if (!ReferenceEquals(publishedPartial, published) &&
            !ReferenceEquals(publishedPartial, fullyApplied))
        {
            RunResourceCleanupStep(
                "RemovePartialPipelineMembership",
                () => publishedPartial?.RemoveInstance(this),
                ref cleanupFailure);
        }

        RunResourceCleanupStep(
            "ClearAdvancedOutputBinding",
            ClearAdvancedOutputBinding,
            ref cleanupFailure);
        RunResourceCleanupStep(
            "ResetRenderCommands",
            () => MeshRenderCommands.ResetForPipelineTransition(EmptyPipelinePasses),
            ref cleanupFailure);
        InvalidMaterial = null;
        RunResourceCleanupStep(
            "ClearPipelineVariables",
            Variables.Clear,
            ref cleanupFailure);
        DestroyCacheOnRenderThread(suppressFailures: true);
        RunResourceCleanupStep(
            "ResetPipelineRuntimeState",
            ResetPipelineScopedRuntimeState,
            ref cleanupFailure);
        _requiresManagedResourceGeneration = null;
        _classifiedResourceLayoutKey = null;
        _lastSuccessfulLayoutlessResourceKey = null;
        _legacyResourceOwnerPipeline = null;
        ResourceGeneration++;

        if (_pipeline is not null)
        {
            RunResourceCleanupStep(
                "PublishNullPipeline",
                () => SetFieldUnchecked(ref _pipeline, null, nameof(Pipeline)),
                ref cleanupFailure);
        }

        lock (_pipelineTransitionSync)
        {
            _applyingPipeline = null;
            _fullyAppliedPipeline = null;
            _pipelineTransitionPrevious = null;
            _pipelineTransitionPublishedPartial = null;
        }

        if (cleanupFailure is not null)
        {
            Debug.RenderingWarning(
                "[RenderPipelineTransition] Terminal teardown completed with isolated cleanup failures. Instance={0} FirstError={1}",
                InstanceId,
                cleanupFailure.Message);
        }
    }

    private void ResetPipelineScopedRuntimeState()
    {
        RenderPipelineAntiAliasingResources.InvalidateAntiAliasingResources(
            this,
            "RenderPipelineAssetChanged");

        lock (_resourceSettingsSnapshotLock)
        {
            _lastResourceSettingsSnapshotByOutput.Clear();
            _resourceGenerationDiagnostics.Clear();
            _resourceGenerationDiagnosticOrder.Clear();
        }

        lock (_renderGraphValidationLock)
        {
            _executedRenderGraphPassIndices.Clear();
            _executedBranchRenderGraphPassIndices.Clear();
            _activeRenderGraphBranchDepth = 0;
        }

        _lastFailedGenerationKey = null;
        _failedGenerationRetryAfterTimestamp = 0;
        SetField(ref _lastResourceGenerationFailure, null, nameof(LastResourceGenerationFailure));
        ClearPendingGenerationDebounce();
        _lastDescriptorParityGeneration = -1;
        _appliedInternalResolutionScale = null;
        _screenSpaceUiCommandPipeline = null;
        _screenSpaceUiCommandGeneration = ulong.MaxValue;
        _containsScreenSpaceUiRenderCommand = false;
        _resizeCatchUpSkippedFrameId = ulong.MaxValue;
        _lastCompletedCommandChainFrameId = ulong.MaxValue;
        _lastCompletedCommandChainViewport = null;
        LastSceneCamera = null;
        LastRenderingCamera = null;
        FinalOutput = null;
        EffectiveOutputHDRThisFrame = null;
        EffectiveAntiAliasingModeThisFrame = null;
        EffectiveMsaaSampleCountThisFrame = null;
        EffectiveTsrRenderScaleThisFrame = null;
        ForwardContactPrePassAvailableThisFrame = false;
    }

    private void DisposeGeneration(RenderResourceGeneration generation, string reason)
    {
        try
        {
            NotifyPipelineResourcesDestroyed(
                generation.OwnerPipeline,
                generation.Registry,
                reason);
        }
        finally
        {
            generation.Dispose();
        }
    }

    private void DestroyLegacyResources(
        RenderPipeline? ownerPipeline,
        string reason,
        bool prepareForDestruction = true)
    {
        if (_legacyResources.TextureRecords.Count == 0 &&
            _legacyResources.FrameBufferRecords.Count == 0 &&
            _legacyResources.BufferRecords.Count == 0 &&
            _legacyResources.RenderBufferRecords.Count == 0)
        {
            _legacyResourceOwnerPipeline = null;
            _lastSuccessfulLayoutlessResourceKey = null;
            return;
        }

        try
        {
            NotifyPipelineResourcesDestroyed(ownerPipeline, _legacyResources, reason);
        }
        finally
        {
            try
            {
                if (prepareForDestruction)
                    PrepareForPhysicalResourceDestruction(reason);
            }
            finally
            {
                try
                {
                    _legacyResources.DestroyAllPhysicalResources();
                }
                finally
                {
                    _legacyResourceOwnerPipeline = null;
                    _lastSuccessfulLayoutlessResourceKey = null;
                }
            }
        }
    }

    private void InvalidateLegacyPhysicalResources(
        RenderPipeline ownerPipeline,
        string reason)
    {
        if (_legacyResources.TextureRecords.Count == 0 &&
            _legacyResources.FrameBufferRecords.Count == 0 &&
            _legacyResources.BufferRecords.Count == 0 &&
            _legacyResources.RenderBufferRecords.Count == 0)
        {
            return;
        }

        try
        {
            NotifyPipelineResourcesDestroyed(ownerPipeline, _legacyResources, reason);
        }
        finally
        {
            try
            {
                PrepareForPhysicalResourceDestruction(reason);
            }
            finally
            {
                try
                {
                    _legacyResources.DestroyAllPhysicalResources(retainDescriptors: true);
                }
                finally
                {
                    ResourceGeneration++;
                }
            }
        }
    }

    private void NotifyCacheClearingHandlers(string reason)
    {
        Action? handlers = CacheClearing;
        if (handlers is null)
            return;

        // Cache transitions are rare. Snapshotting the multicast invocation list lets one
        // faulty owner be isolated without preventing the remaining owners from releasing state.
        foreach (Delegate callback in handlers.GetInvocationList())
        {
            try
            {
                ((Action)callback)();
            }
            catch (Exception ex)
            {
                Debug.RenderingWarning(
                    "[RenderResources] Cache-clearing callback failed and was isolated. Pipeline={0} Reason={1} Error={2}",
                    ProfilerKey,
                    reason,
                    ex.Message);
            }
        }
    }

    private void RunResourceCleanupStep(
        string step,
        Action cleanup,
        ref Exception? firstFailure)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            firstFailure ??= ex;
            Debug.RenderingWarning(
                "[RenderResources] Cleanup step failed and later cleanup will continue. Pipeline={0} Step={1} Error={2}",
                ProfilerKey,
                step,
                ex.Message);
        }
    }
}
