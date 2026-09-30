using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Rendering.API.Rendering.OpenXR;
using SwapchainImageVulkan2KHR = Silk.NET.OpenXR.SwapchainImageVulkan2KHR;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Vulkan implementation of the OpenXR graphics binding contract.
/// </summary>
internal sealed partial class VulkanXrGraphicsBinding : IXrGraphicsBinding
{
    private IOpenXrGraphicsHost? _host;

    private IOpenXrGraphicsHost Host
        => _host ?? throw new InvalidOperationException("The Vulkan OpenXR binding is not attached to an API host.");

    private void Attach(IOpenXrGraphicsHost host)
        => _host = host;

    public RendererBackendId BackendId => RendererBackendId.Vulkan;
    public string BackendName => "Vulkan";
    public XRTexture2D? PreviewLeftEyeTexture => _previewLeftEyeTexture;
    public XRTexture2D? PreviewRightEyeTexture => _previewRightEyeTexture;
    public ulong PreviewLeftEyeFrameId => Volatile.Read(ref _previewLeftEyeFrameId);
    public ulong PreviewRightEyeFrameId => Volatile.Read(ref _previewRightEyeFrameId);
    public XRTexture2D? DesktopMirrorTexture => _viewportMirrorColor;

    private ulong _previewLeftEyeFrameId;
    private ulong _previewRightEyeFrameId;

    private void ClearPreviewEyeFrameId(uint viewIndex)
    {
        if (viewIndex == 0)
            Volatile.Write(ref _previewLeftEyeFrameId, 0);
        else if (viewIndex == 1)
            Volatile.Write(ref _previewRightEyeFrameId, 0);
    }

    private void RecordPreviewEyeCopyIssued(uint viewIndex)
    {
        ulong renderFrameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        if (viewIndex == 0)
            Volatile.Write(ref _previewLeftEyeFrameId, renderFrameId);
        else if (viewIndex == 1)
            Volatile.Write(ref _previewRightEyeFrameId, renderFrameId);
    }

    public bool IsCompatible(AbstractRenderer renderer) => renderer is VulkanRenderer;

    public bool DestroysRuntimeInstanceOnRendererTeardown => true;
    public bool RequiresRenderThreadForTeardown => true;
    public bool RequiresDeferredSwapchainRetirement => true;
    public bool HasPendingDeferredSwapchainRetirement
    {
        get
        {
            lock (_retiredSwapchainsGate)
                return _retiredSwapchainGenerations.Count != 0;
        }
    }

    public bool RequiresRuntimeStateRenderThread(
        RuntimeOpenXrState runtimeState,
        bool runtimeLossPending)
        => runtimeState != RuntimeOpenXrState.SessionRunning || runtimeLossPending ||
           HasPendingDeferredSwapchainRetirement;

    public bool ShouldDeferSessionStart(AbstractRenderer renderer, out string reason)
        => ((VulkanRenderer)renderer).OpenXrFrameLoop.ShouldDeferOpenXrRuntimeSessionStart(out reason);

    public void ExecuteRuntimeGraphicsTransition(
        AbstractRenderer renderer,
        string operation,
        System.Action action)
        => ((VulkanRenderer)renderer).OpenXrFrameLoop.ExecuteOpenXrRuntimeGraphicsTransition(operation, action);

    public bool TryGetRendererOwnedInstance(
        AbstractRenderer renderer,
        out IOpenXrVulkanBootstrapLease? lease)
        => ((VulkanRenderer)renderer).DeviceContext.TryGetOpenXrBootstrapInstance(out lease);

    public bool InvalidateRendererOwnedInstance(AbstractRenderer renderer, string reason)
        => ((VulkanRenderer)renderer).DeviceContext.InvalidateOpenXrBootstrapInstance(reason);

    public int TryDestroyRendererOwnedInstanceAfterDeviceLoss(AbstractRenderer renderer, string reason)
        => ((VulkanRenderer)renderer).DeviceContext.TryDestroyRendererOwnedInstanceAfterDeviceLoss(reason);

    public bool UsesOpenXrVulkanEnable2Creation(AbstractRenderer renderer)
        => ((VulkanRenderer)renderer).DeviceContext.InstanceCreatedThroughOpenXr &&
           ((VulkanRenderer)renderer).DeviceContext.CreatedThroughOpenXr;

    public void ResetRenderingResourcesForRuntimeRecreate(AbstractRenderer renderer, string reason)
        => ((VulkanRenderer)renderer).OpenXrFrameLoop.ResetOpenXrRenderingResourcesForRuntimeRecreate(reason);

    public bool SupportsVulkanFragmentShadingRate(AbstractRenderer renderer)
        => ((VulkanRenderer)renderer).SupportsVulkanFragmentShadingRate;

    public bool SupportsVulkanFragmentDensityMap(AbstractRenderer renderer)
        => ((VulkanRenderer)renderer).SupportsVulkanFragmentDensityMap;

    bool IXrGraphicsBinding.CanUseTrueSinglePassStereo
        => CanUseTrueSinglePassStereo;

    public bool TryResolveViewRenderMode(
        IOpenXrGraphicsHost host,
        out VrViewRenderModeResolution resolution)
    {
        Attach(host);
        return TryResolveOpenXrViewRenderModeForCurrentBackend(out resolution);
    }

    public bool TryCreateSession(IOpenXrGraphicsHost host, AbstractRenderer renderer)
    {
        Attach(host);
        CreateVulkanSession();
        return true;
    }

    public void CreateSwapchains(IOpenXrGraphicsHost host, AbstractRenderer renderer)
    {
        Attach(host);
        InitializeVulkanSwapchains((VulkanRenderer)renderer);
    }

    private const int RetiredSwapchainGenerationCapacity = 4;
    private readonly List<RetiredOpenXrSwapchainGeneration> _retiredSwapchainGenerations = new(RetiredSwapchainGenerationCapacity);
    private readonly object _retiredSwapchainsGate = new();
    private long _nextRetiredSwapchainGenerationId;
    private long _queuedSwapchainGenerationCount;
    private long _drainedSwapchainGenerationCount;
    private long _swapchainRetirementDeferralCount;
    private int _retiredSwapchainGenerationHighWater;
    private EOpenXrSwapchainRetirementBlockers _lastRetirementAdmissionBlockers;
    // OpenXR requires every acquired image to be released before its
    // swapchain is destroyed. Keep this runtime state separate from GPU
    // completion: a completed Vulkan submission cannot prove xrRelease.
    private readonly HashSet<ulong> _runtimeAcquiredSwapchainHandles = [];

    public unsafe OpenXrSwapchainRetirementOutcome RetireSwapchainsForDeferredDestruction(IOpenXrGraphicsHost host, AbstractRenderer renderer)
    {
        lock (_retiredSwapchainsGate)
            _lastRetirementAdmissionBlockers = EOpenXrSwapchainRetirementBlockers.None;
        if (renderer is not VulkanRenderer vulkanRenderer)
            return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.ResourceLifetime);
        if (vulkanRenderer.IsDeviceLost)
        {
            lock (_retiredSwapchainsGate)
                _lastRetirementAdmissionBlockers = EOpenXrSwapchainRetirementBlockers.DeviceLost;
            return OpenXrSwapchainRetirementOutcome.FailedAfterDetachment;
        }

        Attach(host);

        DrainRetiredSwapchains(host, vulkanRenderer);
        lock (_retiredSwapchainsGate)
            for (int i = 0; i < _retiredSwapchainGenerations.Count; ++i)
                if (_retiredSwapchainGenerations[i].PermanentRecoveryFailureReason is not null)
                    return OpenXrSwapchainRetirementOutcome.FailedAfterDetachment;

        // View configuration is populated before extent validation and native
        // swapchain creation. A rejected configuration can therefore have views
        // without any native payload; it needs no fabricated GPU lifetime proof.
        if (_viewCount == 0 || !HasActiveSwapchainPayload())
            return HasPendingDeferredSwapchainRetirement
                ? DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.ChildResources)
                : OpenXrSwapchainRetirementOutcome.Retired;

        // Admission is decided before active handles are detached. A timed-out
        // retirement recovery must leave the currently usable generation intact.
        lock (_retiredSwapchainsGate)
        {
            for (int i = 0; i < _viewCount; ++i)
                if (_swapchains[i] != 0 && host.HasAcquiredImage(_swapchains[i]))
                {
                    return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.RuntimeImageAcquired);
                }
            if (_retiredSwapchainGenerations.Count >= RetiredSwapchainGenerationCapacity)
            {
                if (host.ObserveSmokeRetiredGenerationCapacity(
                    _retiredSwapchainGenerations.Count,
                    RetiredSwapchainGenerationCapacity,
                    "DeferredBeforeActiveDetachment:GenerationBudget"))
                {
                    return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.GenerationBudget);
                }
                RetiredOpenXrSwapchainGeneration oldest = _retiredSwapchainGenerations[0];
                Silk.NET.Vulkan.Result waitResult = Silk.NET.Vulkan.Result.Success;
                if (oldest.RequiresGpuCompletion)
                {
                    waitResult = vulkanRenderer.CommandRuntime.Synchronization.WaitForTimelineCompletion(
                        vulkanRenderer.CommandRuntime.Api,
                        vulkanRenderer.DeviceContext,
                        vulkanRenderer.CommandRuntime.ResourceRuntime.Lifetime.Tracker,
                        oldest.TimelineSemaphore,
                        oldest.TombstoneTimelineValue,
                        8_000_000UL);
                    RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeFenceForcedWait();
                }
                DrainRetiredSwapchainsCore(host, vulkanRenderer);
                if (waitResult != Silk.NET.Vulkan.Result.Success ||
                    _retiredSwapchainGenerations.Count >= RetiredSwapchainGenerationCapacity)
                    return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.GenerationBudget);
            }
        }

        uint viewCount = _viewCount;

        // Capture the exact latest accepted OpenXR submission receipt before
        // detaching the active generation. CurrentTimelineValue is only an
        // allocator cursor and cannot prove completion for this generation.
        bool requiresGpuCompletion = vulkanRenderer.CommandRuntime.OpenXrSubmissionTracker
            .TryGetLatestAcceptedCompletion(
                out Silk.NET.Vulkan.Semaphore timelineSemaphore,
                out ulong tombstoneValue);
        if (requiresGpuCompletion &&
            (timelineSemaphore.Handle == 0 || tombstoneValue == 0u))
        {
            return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.SubmissionCompletion);
        }
        if (!TryCaptureActiveSwapchainResourceLifetimeTicket(
                vulkanRenderer.CommandRuntime.ResourceRuntime.Lifetime.Tracker,
                viewCount,
                out VulkanRetirementTicket resourceLifetimeTicket,
                out Silk.NET.Vulkan.Image[] lifetimeImages))
        {
            // A tracker receipt only proves tracked eye submissions. Refuse
            // in-session replacement until every imported image has a
            // resource-generation use frontier covering arbitrary consumers.
            return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.ResourceLifetime);
        }
        ulong[] swapchainsToRetire = new ulong[viewCount];
        SwapchainImageVulkan2KHR*[] imagesToRetire = new SwapchainImageVulkan2KHR*[viewCount];
        uint[] countsToRetire = new uint[viewCount];

        bool hasValidSwapchain = false;
        for (int i = 0; i < viewCount; i++)
        {
            swapchainsToRetire[i] = _swapchains[i];
            imagesToRetire[i] = _swapchainImagesVK[i];
            countsToRetire[i] = _swapchainImageCounts[i];
            if (_swapchains[i] != 0)
                hasValidSwapchain = true;

        }

        if (!hasValidSwapchain)
            return OpenXrSwapchainRetirementOutcome.FailedAfterDetachment;

        if (!host.TryReserveRetirement(checked((int)viewCount), out OpenXrRetirementToken retirementToken))
            return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.GenerationBudget);

        bool committedRetirement = false;
        bool enteredQueueAdmission = false;
        try
        {
            vulkanRenderer.CommandRuntime.CommandBuffers.DeviceQueueAdmissionGate.EnterWriteLock();
            enteredQueueAdmission = true;
            // The preflight receipt above only rejects obvious invalid state.
            // Capture the generation receipt after exclusive admission closes
            // the submit path, otherwise a just-accepted submission can escape
            // the tombstone and resource-use frontier.
            requiresGpuCompletion = vulkanRenderer.CommandRuntime.OpenXrSubmissionTracker
                .TryGetLatestAcceptedCompletion(out timelineSemaphore, out tombstoneValue);
            if (requiresGpuCompletion && (timelineSemaphore.Handle == 0 || tombstoneValue == 0))
                return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.SubmissionCompletion);
            if (!TryCaptureActiveSwapchainResourceLifetimeTicket(
                    vulkanRenderer.CommandRuntime.ResourceRuntime.Lifetime.Tracker,
                    viewCount, out resourceLifetimeTicket, out lifetimeImages))
                return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.ResourceLifetime);

            if (!host.AreSwapchainImagesReleased(swapchainsToRetire))
                return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.RuntimeImageAcquired);

            for (int i = 0; i < viewCount; i++)
                if (_swapchains[i] != swapchainsToRetire[i])
                    return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.ResourceLifetime);

            VulkanResourceSlotHandle[] detachedLifetimeSlots = new VulkanResourceSlotHandle[lifetimeImages.Length];
            RetiredOpenXrSwapchainGeneration generation = new(
                swapchainsToRetire,
                imagesToRetire,
                countsToRetire,
                viewCount,
                tombstoneValue,
                timelineSemaphore,
                requiresGpuCompletion,
                resourceLifetimeTicket,
                true,
                lifetimeImages,
                detachedLifetimeSlots,
                false,
                default,
                true,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                Interlocked.Increment(ref _nextRetiredSwapchainGenerationId),
                retirementToken);

            // Reserve renderer storage before transferring native custody. The
            // list publication itself cannot allocate after the host commit.
            lock (_retiredSwapchainsGate)
            {
                if (_retiredSwapchainGenerations.Count >= RetiredSwapchainGenerationCapacity)
                    return DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers.GenerationBudget);
                _retiredSwapchainGenerations.EnsureCapacity(_retiredSwapchainGenerations.Count + 1);
                host.CommitRetirement(retirementToken, swapchainsToRetire);
                committedRetirement = true;
                _retiredSwapchainGenerations.Add(generation);
                ++_queuedSwapchainGenerationCount;
                _retiredSwapchainGenerationHighWater = Math.Max(
                    _retiredSwapchainGenerationHighWater, _retiredSwapchainGenerations.Count);
            }
            for (int i = 0; i < viewCount; i++)
                _swapchainImagesVK[i] = null;

            try
            {
                generation.ChildRetirementReceipt =
                    vulkanRenderer.OpenXrFrameLoop.RetireOpenXrSwapchainChildren(lifetimeImages);
            }
            catch (Exception ex)
            {
                generation.PermanentRecoveryFailureReason = $"Child retirement threw after native custody transfer: {ex}";
                Debug.VulkanWarning("[OpenXR] Swapchain child retirement failed after native custody transfer; entering session recovery: {0}", ex.Message);
                return OpenXrSwapchainRetirementOutcome.FailedAfterDetachment;
            }
            if (!generation.ChildRetirementReceipt.IsValid)
            {
                generation.PermanentRecoveryFailureReason = "Child retirement did not return a complete lifetime receipt after native custody transfer.";
                Debug.VulkanWarning("[OpenXR] Swapchain child retirement changed rendering resources without a complete lifetime receipt; entering session recovery.");
                return OpenXrSwapchainRetirementOutcome.FailedAfterDetachment;
            }

            try
            {
                vulkanRenderer.CommandRuntime.ResourceRuntime
                    .DetachExternalImageLifetimesForHandleReuse(lifetimeImages, detachedLifetimeSlots);
                generation.ExternalImageLifetimesDetached = true;
            }
            catch (Exception ex)
            {
                generation.PermanentRecoveryFailureReason = $"External image lifetime detach failed after child retirement: {ex}";
                Debug.VulkanWarning("[OpenXR] Imported swapchain image lifetime detach failed after child retirement; entering session recovery: {0}", ex.Message);
                return OpenXrSwapchainRetirementOutcome.FailedAfterDetachment;
            }

        }
        finally
        {
            try
            {
                if (!committedRetirement)
                    host.CancelRetirement(retirementToken);
            }
            finally
            {
                if (enteredQueueAdmission)
                    vulkanRenderer.CommandRuntime.CommandBuffers.DeviceQueueAdmissionGate.ExitWriteLock();
            }
        }

        return OpenXrSwapchainRetirementOutcome.Retired;
    }
    public bool HasPendingOpenXrSubmissionOwnership
        => Window?.Renderer is VulkanRenderer renderer
           && renderer.CommandRuntime.OpenXrSubmissionTracker.AcceptedPendingSubmissionCount > 0;

    public int PendingOpenXrSubmissionCount
        => Window?.Renderer is VulkanRenderer renderer
            ? renderer.CommandRuntime.OpenXrSubmissionTracker.AcceptedPendingSubmissionCount
            : 0;

    public string? PendingOpenXrSubmissionReceiptSource
        => "VulkanOpenXrSubmissionTracker.AcceptedInFlightSubmission";

    private OpenXrSwapchainRetirementOutcome DeferSwapchainRetirement(EOpenXrSwapchainRetirementBlockers blockers)
    {
        lock (_retiredSwapchainsGate)
        {
            ++_swapchainRetirementDeferralCount;
            _lastRetirementAdmissionBlockers = blockers;
        }
        return OpenXrSwapchainRetirementOutcome.DeferredBeforeDetachment;
    }

    /// <summary>Returns the last observed proof blockers without issuing GPU or runtime calls.</summary>
    public OpenXrSwapchainRetirementSnapshot CaptureSwapchainRetirementSnapshot()
    {
        lock (_retiredSwapchainsGate)
        {
            EOpenXrSwapchainRetirementBlockers blockers = _lastRetirementAdmissionBlockers;
            for (int i = 0; i < _retiredSwapchainGenerations.Count; ++i)
                blockers |= _retiredSwapchainGenerations[i].LastObservedBlockers;
            if (_runtimeAcquiredSwapchainHandles.Count != 0)
                blockers |= EOpenXrSwapchainRetirementBlockers.RuntimeImageAcquired;
            if (_retiredSwapchainGenerations.Count >= RetiredSwapchainGenerationCapacity)
                blockers |= EOpenXrSwapchainRetirementBlockers.GenerationBudget;
            return new OpenXrSwapchainRetirementSnapshot
            {
                Supported = true,
                PendingGenerationCount = _retiredSwapchainGenerations.Count,
                GenerationCapacity = RetiredSwapchainGenerationCapacity,
                GenerationHighWater = _retiredSwapchainGenerationHighWater,
                QueuedGenerationCount = _queuedSwapchainGenerationCount,
                DrainedGenerationCount = _drainedSwapchainGenerationCount,
                DeferralCount = _swapchainRetirementDeferralCount,
                RuntimeAcquiredSwapchainCount = _runtimeAcquiredSwapchainHandles.Count,
                LastObservedBlockers = blockers,
                DeviceLost = (blockers & EOpenXrSwapchainRetirementBlockers.DeviceLost) != 0,
            };
        }
    }

    public unsafe void DrainRetiredSwapchains(IOpenXrGraphicsHost host, VulkanRenderer vulkanRenderer)
    {
        if (vulkanRenderer.IsDeviceLost)
            return;

        vulkanRenderer.OpenXrFrameLoop.DrainOpenXrRetiredDependencies();
        lock (_retiredSwapchainsGate)
        {
            DrainRetiredSwapchainsCore(host, vulkanRenderer);
        }
    }

    private unsafe void DrainRetiredSwapchainsCore(IOpenXrGraphicsHost host, VulkanRenderer vulkanRenderer)
    {
        if (_retiredSwapchainGenerations.Count == 0 || vulkanRenderer.IsDeviceLost)
            return;

        if (host.ShouldHoldSmokeRetiredGenerations())
        {
            host.RecordSmokeRetiredGenerationObservation(
                _retiredSwapchainGenerations.Count,
                RetiredSwapchainGenerationCapacity,
                "HoldingRealRetiredGenerations");
            return;
        }

        for (int i = _retiredSwapchainGenerations.Count - 1; i >= 0; i--)
        {
            RetiredOpenXrSwapchainGeneration gen = _retiredSwapchainGenerations[i];
            if (gen.PermanentRecoveryFailureReason is string failureReason)
            {
                gen.LastObservedBlockers = gen.ChildRetirementReceipt.IsValid
                    ? EOpenXrSwapchainRetirementBlockers.ExternalImageLifetime
                    : EOpenXrSwapchainRetirementBlockers.ChildResources;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now - gen.LastBlockerDiagnosticTimestamp >= System.Diagnostics.Stopwatch.Frequency)
                {
                    gen.LastBlockerDiagnosticTimestamp = now;
                    Debug.VulkanWarning("[OpenXR.Retirement] Generation {0} cannot safely retry after partial renderer mutation. Native swapchains and their parent remain pinned until explicit device-loss abandonment. Reason={1}", gen.RetirementGenerationId, failureReason);
                }
                continue;
            }
            gen.RuntimeImagesReleased = host.AreSwapchainImagesReleased(gen.Swapchains);
            bool completed = !gen.RequiresGpuCompletion;
            Silk.NET.Vulkan.Result queryResult = Silk.NET.Vulkan.Result.Success;
            if (gen.RequiresGpuCompletion)
            {
                queryResult = vulkanRenderer.CommandRuntime.Synchronization.QueryTimelineCompletion(
                    vulkanRenderer.CommandRuntime.Api,
                    vulkanRenderer.DeviceContext,
                    vulkanRenderer.CommandRuntime.ResourceRuntime.Lifetime.Tracker,
                    gen.TimelineSemaphore,
                    gen.TombstoneTimelineValue,
                    out completed);
            }

            VulkanRetirementTicket resourceLifetimeTicket = gen.ResourceLifetimeTicket;
            bool resourceLifetimeCompleted = gen.HasResourceLifetimeAuthority &&
                vulkanRenderer.CommandRuntime.ResourceRuntime.Lifetime.Tracker
                    .IsRetirementReady(in resourceLifetimeTicket);
            bool detachedSlotsReady = vulkanRenderer.CommandRuntime.ResourceRuntime
                .AreDetachedExternalResourceSlotsReady(gen.DetachedLifetimeSlots);
            bool childrenDestroyed = gen.ChildRetirementReceipt.IsValid &&
                vulkanRenderer.CommandRuntime.ResourceRuntime.AreResourceGenerationsDestroyed(
                    gen.ChildRetirementReceipt.ResourceGenerations);
            gen.LastObservedBlockers = EOpenXrSwapchainRetirementBlockers.None;
            if (queryResult != Silk.NET.Vulkan.Result.Success || !completed)
                gen.LastObservedBlockers |= EOpenXrSwapchainRetirementBlockers.SubmissionCompletion;
            if (!resourceLifetimeCompleted)
                gen.LastObservedBlockers |= EOpenXrSwapchainRetirementBlockers.ResourceLifetime;
            if (!gen.RuntimeImagesReleased)
                gen.LastObservedBlockers |= EOpenXrSwapchainRetirementBlockers.RuntimeImageAcquired;
            if (!gen.ExternalImageLifetimesDetached)
                gen.LastObservedBlockers |= EOpenXrSwapchainRetirementBlockers.ExternalImageLifetime;
            if (!detachedSlotsReady)
                gen.LastObservedBlockers |= EOpenXrSwapchainRetirementBlockers.DetachedResourceSlots;
            if (!childrenDestroyed)
                gen.LastObservedBlockers |= EOpenXrSwapchainRetirementBlockers.ChildResources;
            if (queryResult != Silk.NET.Vulkan.Result.Success || !completed ||
                !resourceLifetimeCompleted || !gen.RuntimeImagesReleased ||
                !gen.ExternalImageLifetimesDetached || !detachedSlotsReady || !childrenDestroyed)
            {
                LogRetiredSwapchainBlocker(
                    vulkanRenderer.CommandRuntime.ResourceRuntime,
                    gen,
                    _retiredSwapchainGenerations.Count,
                    queryResult,
                    completed,
                    resourceLifetimeCompleted,
                    detachedSlotsReady,
                    childrenDestroyed);
                continue;
            }

            bool allDestroyed = true;
            for (int v = 0; v < gen.ViewCount; v++)
            {
                if (gen.DestroyedSwapchains[v])
                    continue;

                if (gen.Swapchains[v] != 0)
                {
                    int destroyResult = host.DestroyRetiredSwapchain(gen.RetirementToken, gen.Swapchains[v]);
                    if (destroyResult != 0)
                    {
                        Debug.VulkanWarning("[OpenXR] Deferred destruction of retired swapchain view {0}: {1}", v, destroyResult);
                        allDestroyed = false;
                        gen.LastObservedBlockers |= EOpenXrSwapchainRetirementBlockers.RuntimeDestroyFailure;
                        continue;
                    }
                }
                gen.Swapchains[v] = 0;
                if (gen.SwapchainImagesVK[v] != null)
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal((nint)gen.SwapchainImagesVK[v]);
                    gen.SwapchainImagesVK[v] = null;
                }
                gen.DestroyedSwapchains[v] = true;
            }
            if (allDestroyed)
            {
                for (int imageIndex = 0; imageIndex < gen.LifetimeImages.Length; ++imageIndex)
                    vulkanRenderer.CommandRuntime.ResourceRuntime
                        .CompleteDetachedExternalResourceDestruction(
                            Silk.NET.Vulkan.ObjectType.Image,
                            gen.LifetimeImages[imageIndex].Handle,
                            gen.DetachedLifetimeSlots[imageIndex],
                            forced: false);
                host.ReleaseRetirement(gen.RetirementToken);
                _retiredSwapchainGenerations.RemoveAt(i);
                ++_drainedSwapchainGenerationCount;
            }
        }
    }

    private void LogRetiredSwapchainBlocker(
        VulkanResourceRuntime resources,
        RetiredOpenXrSwapchainGeneration generation,
        int queuedGenerationCount,
        Silk.NET.Vulkan.Result timelineQueryResult,
        bool gpuCompleted,
        bool resourceLifetimeCompleted,
        bool detachedSlotsReady,
        bool childrenDestroyed)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - generation.LastBlockerDiagnosticTimestamp < System.Diagnostics.Stopwatch.Frequency)
            return;

        generation.LastBlockerDiagnosticTimestamp = now;
        if (!childrenDestroyed)
            resources.LogUndestroyedOpenXrChildren(generation.RetirementGenerationId,
                generation.ChildRetirementReceipt.ResourceGenerations);
        ulong leftSwapchain = generation.Swapchains.Length > 0 ? generation.Swapchains[0] : 0UL;
        ulong rightSwapchain = generation.Swapchains.Length > 1 ? generation.Swapchains[1] : 0UL;
        VulkanRetirementTicket ticket = generation.ResourceLifetimeTicket;
        Debug.VulkanWarning(
            "[OpenXR.Retirement] Blocked generation={0} owner=VulkanXrGraphicsBinding#{1} queued={2} views={3} swapchains=0x{4:X}/0x{5:X} gpu=(required={6},query={7},completed={8},timeline=0x{9:X}:{10}) lifetime=(authority={11},ready={12},resourceGeneration={13},graphics={14},transfer={15},other={16}) runtimeImagesReleased={17} externalDetached={18} detachedSlotsReady={19} children=(receiptValid={20},destroyed={21},count={22}).",
            generation.RetirementGenerationId,
            RuntimeHelpers.GetHashCode(this),
            queuedGenerationCount,
            generation.ViewCount,
            leftSwapchain,
            rightSwapchain,
            generation.RequiresGpuCompletion,
            timelineQueryResult,
            gpuCompleted,
            generation.TimelineSemaphore.Handle,
            generation.TombstoneTimelineValue,
            generation.HasResourceLifetimeAuthority,
            resourceLifetimeCompleted,
            ticket.ResourceGeneration,
            ticket.GraphicsSequence,
            ticket.TransferSequence,
            ticket.OtherSequence,
            generation.RuntimeImagesReleased,
            generation.ExternalImageLifetimesDetached,
            detachedSlotsReady,
            generation.ChildRetirementReceipt.IsValid,
            childrenDestroyed,
            generation.ChildRetirementReceipt.ResourceGenerations.Length);
    }

    private unsafe bool HasActiveSwapchainPayload()
    {
        lock (_retiredSwapchainsGate)
            if (_runtimeAcquiredSwapchainHandles.Count != 0)
                return true;
        for (uint viewIndex = 0; viewIndex < _viewCount; ++viewIndex)
            if (_swapchains[checked((int)viewIndex)] != 0 ||
                _swapchainImagesVK[viewIndex] != null ||
                _swapchainImageCounts[checked((int)viewIndex)] != 0)
                return true;
        return false;
    }

    private unsafe bool TryCaptureActiveSwapchainResourceLifetimeTicket(
        VulkanResourceLifetimeTracker tracker,
        uint viewCount,
        out VulkanRetirementTicket ticket,
        out Silk.NET.Vulkan.Image[] lifetimeImages)
    {
        ulong graphics = 0u;
        ulong transfer = 0u;
        ulong other = 0u;
        ulong generation = 0u;
        List<Silk.NET.Vulkan.Image> imageList = [];
        lock (tracker.SyncRoot)
        {
            for (uint viewIndex = 0u; viewIndex < viewCount; ++viewIndex)
            {
                SwapchainImageVulkan2KHR* swapchainImages = _swapchainImagesVK[viewIndex];
                uint imageCount = _swapchainImageCounts[checked((int)viewIndex)];
                if (swapchainImages is null || imageCount == 0u)
                {
                    ticket = default;
                    lifetimeImages = [];
                    return false;
                }
                for (uint imageIndex = 0u; imageIndex < imageCount; ++imageIndex)
                {
                    Silk.NET.Vulkan.Image image = new(swapchainImages[imageIndex].Image);
                    VulkanResourceLifetimeKey key = new(Silk.NET.Vulkan.ObjectType.Image, image.Handle);
                    if (image.Handle == 0 || !tracker.ResourceLifetimes.TryGetValue(
                            key, out VulkanResourceLifetimeRecord? resource))
                    {
                        ticket = default;
                        lifetimeImages = [];
                        return false;
                    }
                    imageList.Add(image);
                    graphics = Math.Max(graphics, resource.Pins.LastGraphicsSequence);
                    transfer = Math.Max(transfer, resource.Pins.LastTransferSequence);
                    other = Math.Max(other, resource.Pins.LastOtherSequence);
                    generation = Math.Max(generation, resource.Generation);
                }
            }
        }
        ticket = new VulkanRetirementTicket(
            graphics, transfer, other, System.Diagnostics.Stopwatch.GetTimestamp(),
            generation, ExternalOwnershipPending: false);
        lifetimeImages = imageList.ToArray();
        return true;
    }

    private static unsafe void RegisterOpenXrSwapchainImageLifetimes(
        VulkanRenderer renderer,
        SwapchainImageVulkan2KHR* images,
        uint imageCount)
    {
        if (images is null || imageCount == 0u)
            throw new ArgumentOutOfRangeException(nameof(imageCount));

        List<Silk.NET.Vulkan.Image> registered = new(checked((int)imageCount));
        try
        {
            for (uint imageIndex = 0u; imageIndex < imageCount; ++imageIndex)
            {
                Silk.NET.Vulkan.Image image = new(images[imageIndex].Image);
                if (image.Handle == 0)
                    throw new InvalidOperationException("OpenXR returned a null Vulkan swapchain image.");
                renderer.CommandRuntime.ResourceRuntime.RegisterResource(
                    Silk.NET.Vulkan.ObjectType.Image,
                    image.Handle,
                    "OpenXR.RuntimeSwapchainImage",
                    externallyOwned: true);
                registered.Add(image);
            }
        }
        catch
        {
            if (registered.Count != 0)
            {
                VulkanResourceSlotHandle[] slots = renderer.CommandRuntime.ResourceRuntime
                    .DetachExternalImageLifetimesForHandleReuse(registered.ToArray());
                for (int i = 0; i < registered.Count; ++i)
                    renderer.CommandRuntime.ResourceRuntime.CompleteDetachedExternalResourceDestruction(
                        Silk.NET.Vulkan.ObjectType.Image,
                        registered[i].Handle,
                        slots[i],
                        forced: true);
            }
            throw;
        }
    }

    public unsafe void CleanupSwapchains(IOpenXrGraphicsHost host)
    {
        Attach(host);
        for (int i = 0; i < _swapchainImagesVK.Length; i++)
        {
            if (_swapchainImagesVK[i] is null)
                continue;

            System.Runtime.InteropServices.Marshal.FreeHGlobal(
                (nint)_swapchainImagesVK[i]);
            _swapchainImagesVK[i] = null;
        }

        if (Window?.Renderer is VulkanRenderer vr)
            DrainRetiredSwapchains(host, vr);
    }

    public bool WaitForGpuIdle(IOpenXrGraphicsHost host, AbstractRenderer renderer)
    {
        VulkanRenderer vulkanRenderer = (VulkanRenderer)renderer;
        if (vulkanRenderer.IsDeviceLost)
            return false;

        bool drained = vulkanRenderer.CommandRuntime.OpenXrSubmissionTracker.DrainAll(timeoutMs: 1000u);
        if (!drained)
            return false;

        // This terminal path runs on the owning render thread after pacing has
        // stopped. A final exhausted production-frame scan allowance must not
        // prevent completion-proven children from ever retiring. Keep the
        // ordinary per-class budgets; only their accounting interval advances.
        vulkanRenderer.CommandRuntime.ResourceRuntime.BeginTerminalRetirementMeteringInterval();
        host.ReleaseSmokeRetiredGenerationHoldForTerminalDrain();
        DrainRetiredSwapchains(host, vulkanRenderer);
        if (HasPendingDeferredSwapchainRetirement)
            return false;

        if (!vulkanRenderer.OpenXrFrameLoop.MeshOperationRequests
                .TryReleaseInactiveCapturePublicationLeasesAfterGpuIdle())
            return false;

        vulkanRenderer.OpenXrFrameLoop.MeshOperationRequests
            .ReleaseDrainedCanonicalPublicationLeasesAfterGpuIdle();
        return true;
    }

    public void PollDeferredSwapchainRetirement(IOpenXrGraphicsHost host, AbstractRenderer renderer)
    {
        if (renderer is VulkanRenderer vulkanRenderer && !vulkanRenderer.IsDeviceLost)
            DrainRetiredSwapchains(host, vulkanRenderer);
    }

    public int BeginFrame(IOpenXrGraphicsHost host)
    {
        Attach(host);
        using VulkanOpenXrRuntimeQueueLease lease = GetCommandRuntime(host)
            .EnterSerializedOpenXrCommandSection("xrBeginFrame");
        return host.GraphicsCalls.BeginFrame();
    }

    public int AcquireSwapchainImage(IOpenXrGraphicsHost host, ulong swapchain, out uint imageIndex)
    {
        Attach(host);
        using VulkanOpenXrRuntimeQueueLease lease = GetCommandRuntime(host)
            .EnterSerializedOpenXrCommandSection("xrAcquireSwapchainImage");
        int result = host.GraphicsCalls.AcquireSwapchainImage(swapchain, out imageIndex);
        if (result == 0)
        {
            lock (_retiredSwapchainsGate)
                _runtimeAcquiredSwapchainHandles.Add(swapchain);
        }
        return result;
    }

    public int WaitSwapchainImage(IOpenXrGraphicsHost host, ulong swapchain, long timeoutNs)
    {
        Attach(host);
        return host.GraphicsCalls.WaitSwapchainImage(swapchain, timeoutNs);
    }

    public int ReleaseSwapchainImage(IOpenXrGraphicsHost host, ulong swapchain)
    {
        Attach(host);
        using VulkanOpenXrRuntimeQueueLease lease = GetCommandRuntime(host)
            .EnterSerializedOpenXrCommandSection("xrReleaseSwapchainImage");
        int result = host.GraphicsCalls.ReleaseSwapchainImage(swapchain);
        if (result == 0)
        {
            lock (_retiredSwapchainsGate)
                _runtimeAcquiredSwapchainHandles.Remove(swapchain);
        }
        return result;
    }

    public int EndFrame(IOpenXrGraphicsHost host, bool submitLayer)
    {
        Attach(host);
        using VulkanOpenXrRuntimeQueueLease lease = GetCommandRuntime(host)
            .EnterSerializedOpenXrCommandSection("xrEndFrame");
        return host.GraphicsCalls.EndFrame(submitLayer);
    }

    private static VulkanCommandRuntime GetCommandRuntime(IOpenXrGraphicsHost host)
        => ((VulkanRenderer)host.Window!.Renderer!).CommandRuntime;

    public void RenderViews(IOpenXrGraphicsHost host, uint viewIndex)
    {
        // Rendering remains coordinated by the backend-neutral frame lifecycle.
    }

    public unsafe bool TryRenderViewsBatch(IOpenXrGraphicsHost host, out bool handled)
    {
        Attach(host);
        return TryRenderVulkanEyesBatch(out handled);
    }

    public bool TryRenderEye(
        IOpenXrGraphicsHost host,
        uint viewIndex,
        uint imageIndex,
        OpenXrRenderToEyeCallback? renderCallback)
    {
        Attach(host);
        bool rendered = TryRenderVulkanEye(viewIndex, imageIndex);
        if (rendered)
            host.StageProjectionView(viewIndex);
        return rendered;
    }

    public bool ShouldPrewarmEyeResources(IOpenXrGraphicsHost host, uint viewIndex)
    {
        Attach(host);
        return ShouldPrewarmVulkanEyeResources(viewIndex);
    }

    public void PrewarmEyeResources(IOpenXrGraphicsHost host, uint viewIndex)
    {
        Attach(host);
        PrewarmVulkanEyeResources(viewIndex);
    }

    public bool TryRenderDesktopMirrorComposition(
        IOpenXrGraphicsHost host,
        uint targetWidth,
        uint targetHeight)
    {
        Attach(host);
        return TryRenderVulkanDesktopMirrorComposition(
            (VulkanRenderer)host.Window!.Renderer!,
            targetWidth,
            targetHeight);
    }

    public void EnsureStereoViewport(IOpenXrGraphicsHost host, uint width, uint height)
    {
        Attach(host);
        EnsureOpenXrStereoViewport(width, height);
    }

    public void ResetBackendDiagnostics(IOpenXrGraphicsHost host)
    {
        Attach(host);
        ResetStrictSpsBoundaryCaptureDiagnostics();
    }

    public void DestroyBackendResources(IOpenXrGraphicsHost host)
    {
        Attach(host);
        DestroyVulkanEyeMirrorTargets();
        DestroyVulkanStereoRenderTarget();
        DestroyOpenXrPreviewTargets();
        DestroyViewportMirrorTargets();
    }
}
