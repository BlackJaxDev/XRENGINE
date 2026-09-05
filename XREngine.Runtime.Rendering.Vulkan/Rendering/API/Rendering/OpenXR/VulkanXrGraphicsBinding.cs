using Silk.NET.OpenXR;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Vulkan implementation of the OpenXR graphics binding contract.
/// </summary>
internal sealed partial class VulkanXrGraphicsBinding : IXrGraphicsBinding
{
    private OpenXRAPI? _host;

    private OpenXRAPI Host
        => _host ?? throw new InvalidOperationException("The Vulkan OpenXR binding is not attached to an API host.");

    private void Attach(OpenXRAPI api)
        => _host = api;

    public RendererBackendId BackendId => RendererBackendId.Vulkan;
    public string BackendName => "Vulkan";
    public XRTexture2D? PreviewLeftEyeTexture => _previewLeftEyeTexture;
    public XRTexture2D? PreviewRightEyeTexture => _previewRightEyeTexture;
    public XRTexture2D? DesktopMirrorTexture => _viewportMirrorColor;

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
        OpenXRAPI.OpenXrRuntimeState runtimeState,
        bool runtimeLossPending)
        => runtimeState != OpenXRAPI.OpenXrRuntimeState.SessionRunning || runtimeLossPending ||
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
        out XR? rendererOwnedApi,
        out Instance rendererOwnedInstance,
        out string[] rendererOwnedExtensions)
    {
        bool success = ((VulkanRenderer)renderer).DeviceContext.TryGetOpenXrBootstrapInstance(
            out XR api,
            out rendererOwnedInstance,
            out rendererOwnedExtensions);
        rendererOwnedApi = success ? api : null;
        return success;
    }

    public bool InvalidateRendererOwnedInstance(AbstractRenderer renderer, string reason)
        => ((VulkanRenderer)renderer).DeviceContext.InvalidateOpenXrBootstrapInstance(reason);

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
        OpenXRAPI api,
        out VrViewRenderModeResolution resolution)
    {
        Attach(api);
        return TryResolveOpenXrViewRenderModeForCurrentBackend(out resolution);
    }

    public bool TryCreateSession(OpenXRAPI api, AbstractRenderer renderer)
    {
        Attach(api);
        CreateVulkanSession();
        return true;
    }

    public void CreateSwapchains(OpenXRAPI api, AbstractRenderer renderer)
    {
        Attach(api);
        InitializeVulkanSwapchains((VulkanRenderer)renderer);
    }

    private readonly List<RetiredOpenXrSwapchainGeneration> _retiredSwapchainGenerations = new(4);
    private readonly object _retiredSwapchainsGate = new();
    // OpenXR requires every acquired image to be released before its
    // swapchain is destroyed. Keep this runtime state separate from GPU
    // completion: a completed Vulkan submission cannot prove xrRelease.
    private readonly HashSet<ulong> _runtimeAcquiredSwapchainHandles = [];

    public unsafe bool TryRetireSwapchainsForDeferredDestruction(OpenXRAPI api, AbstractRenderer renderer)
    {
        if (renderer is not VulkanRenderer vulkanRenderer || vulkanRenderer.IsDeviceLost)
            return false;

        Attach(api);

        DrainRetiredSwapchains(api, vulkanRenderer);

        if (_viewCount == 0)
            return !HasPendingDeferredSwapchainRetirement;

        // Admission is decided before active handles are detached. A timed-out
        // retirement recovery must leave the currently usable generation intact.
        lock (_retiredSwapchainsGate)
        {
            for (int i = 0; i < _viewCount; ++i)
                if (_swapchains[i].Handle != 0 &&
                    _runtimeAcquiredSwapchainHandles.Contains(_swapchains[i].Handle))
                {
                    return false;
                }
            if (_retiredSwapchainGenerations.Count >= 4)
            {
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
                DrainRetiredSwapchainsCore(api, vulkanRenderer);
                if (waitResult != Silk.NET.Vulkan.Result.Success ||
                    _retiredSwapchainGenerations.Count >= 4)
                    return false;
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
            return false;
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
            return false;
        }
        Swapchain[] swapchainsToRetire = new Swapchain[viewCount];
        SwapchainImageVulkan2KHR*[] imagesToRetire = new SwapchainImageVulkan2KHR*[viewCount];
        uint[] countsToRetire = new uint[viewCount];

        bool hasValidSwapchain = false;
        for (int i = 0; i < viewCount; i++)
        {
            swapchainsToRetire[i] = _swapchains[i];
            imagesToRetire[i] = _swapchainImagesVK[i];
            countsToRetire[i] = _swapchainImageCounts[i];
            if (_swapchains[i].Handle != 0)
                hasValidSwapchain = true;

        }

        if (!hasValidSwapchain)
            return false;

        vulkanRenderer.CommandRuntime.CommandBuffers.DeviceQueueAdmissionGate.EnterWriteLock();
        try
        {
            // The preflight receipt above only rejects obvious invalid state.
            // Capture the generation receipt after exclusive admission closes
            // the submit path, otherwise a just-accepted submission can escape
            // the tombstone and resource-use frontier.
            requiresGpuCompletion = vulkanRenderer.CommandRuntime.OpenXrSubmissionTracker
                .TryGetLatestAcceptedCompletion(out timelineSemaphore, out tombstoneValue);
            if ((requiresGpuCompletion &&
                 (timelineSemaphore.Handle == 0 || tombstoneValue == 0)) ||
                !TryCaptureActiveSwapchainResourceLifetimeTicket(
                    vulkanRenderer.CommandRuntime.ResourceRuntime.Lifetime.Tracker,
                    viewCount, out resourceLifetimeTicket, out lifetimeImages))
                return false;

            lock (_retiredSwapchainsGate)
                for (int i = 0; i < viewCount; i++)
                    if (_swapchains[i].Handle != 0 &&
                        _runtimeAcquiredSwapchainHandles.Contains(_swapchains[i].Handle))
                        return false;

            VulkanResourceSlotHandle[] detachedLifetimeSlots = new VulkanResourceSlotHandle[lifetimeImages.Length];
            VulkanOpenXrSwapchainChildRetirementReceipt childReceipt =
                vulkanRenderer.OpenXrFrameLoop.RetireOpenXrSwapchainChildren(lifetimeImages);
            if (!childReceipt.IsValid)
            {
                Debug.VulkanWarning("[OpenXR] Retaining active swapchains because Vulkan child lifetime closure could not be proven.");
                return false;
            }

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
                true,
                childReceipt,
                true,
                System.Diagnostics.Stopwatch.GetTimestamp());

            try
            {
                vulkanRenderer.CommandRuntime.ResourceRuntime
                    .DetachExternalImageLifetimesForHandleReuse(lifetimeImages, detachedLifetimeSlots);
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning("[OpenXR] Imported swapchain image lifetime detach failed; retaining active swapchains for a later retry: {0}", ex.Message);
                return false;
            }

            for (int i = 0; i < viewCount; i++)
            {
                _swapchains[i] = default;
                _swapchainImagesVK[i] = null;
                _swapchainImageCounts[i] = 0;
            }
            lock (_retiredSwapchainsGate)
                _retiredSwapchainGenerations.Add(generation);
        }
        finally
        {
            vulkanRenderer.CommandRuntime.CommandBuffers.DeviceQueueAdmissionGate.ExitWriteLock();
        }

        return true;
    }

    public unsafe void DrainRetiredSwapchains(OpenXRAPI api, VulkanRenderer vulkanRenderer)
    {
        if (vulkanRenderer.IsDeviceLost)
            return;

        vulkanRenderer.OpenXrFrameLoop.DrainOpenXrRetiredDependencies();
        lock (_retiredSwapchainsGate)
        {
            DrainRetiredSwapchainsCore(api, vulkanRenderer);
        }
    }

    private unsafe void DrainRetiredSwapchainsCore(OpenXRAPI api, VulkanRenderer vulkanRenderer)
    {
        if (_retiredSwapchainGenerations.Count == 0 || vulkanRenderer.IsDeviceLost)
            return;

        for (int i = _retiredSwapchainGenerations.Count - 1; i >= 0; i--)
        {
            RetiredOpenXrSwapchainGeneration gen = _retiredSwapchainGenerations[i];
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
            if (queryResult != Silk.NET.Vulkan.Result.Success || !completed ||
                !resourceLifetimeCompleted || !gen.RuntimeImagesReleased ||
                !gen.ExternalImageLifetimesDetached ||
                !detachedSlotsReady || !childrenDestroyed)
                continue;

            bool allDestroyed = true;
            for (int v = 0; v < gen.ViewCount; v++)
            {
                if (gen.DestroyedSwapchains[v])
                    continue;

                if (gen.Swapchains[v].Handle != 0)
                {
                    Result destroyResult = api.Api.DestroySwapchain(gen.Swapchains[v]);
                    if (destroyResult != Result.Success)
                    {
                        Debug.VulkanWarning("[OpenXR] Deferred destruction of retired swapchain view {0}: {1}", v, destroyResult);
                        allDestroyed = false;
                        continue;
                    }
                }
                gen.Swapchains[v] = default;
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
                _retiredSwapchainGenerations.RemoveAt(i);
            }
        }
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
                uint imageCount = _swapchainImageCounts[viewIndex];
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

    public unsafe void CleanupSwapchains(OpenXRAPI api)
    {
        Attach(api);
        for (int i = 0; i < _swapchainImagesVK.Length; i++)
        {
            if (_swapchainImagesVK[i] is null)
                continue;

            System.Runtime.InteropServices.Marshal.FreeHGlobal(
                (nint)_swapchainImagesVK[i]);
            _swapchainImagesVK[i] = null;
        }

        if (Window?.Renderer is VulkanRenderer vr)
            DrainRetiredSwapchains(api, vr);
    }

    public bool WaitForGpuIdle(OpenXRAPI api, AbstractRenderer renderer)
    {
        VulkanRenderer vulkanRenderer = (VulkanRenderer)renderer;
        if (vulkanRenderer.IsDeviceLost)
            return false;

        bool drained = vulkanRenderer.CommandRuntime.OpenXrSubmissionTracker.DrainAll(timeoutMs: 1000u);
        if (!drained)
            return false;

        DrainRetiredSwapchains(api, vulkanRenderer);
        return !HasPendingDeferredSwapchainRetirement;
    }

    public void PollDeferredSwapchainRetirement(OpenXRAPI api, AbstractRenderer renderer)
    {
        if (renderer is VulkanRenderer vulkanRenderer && !vulkanRenderer.IsDeviceLost)
            DrainRetiredSwapchains(api, vulkanRenderer);
    }

    public Result AcquireSwapchainImage(OpenXRAPI api, Swapchain swapchain, out uint imageIndex)
    {
        SwapchainImageAcquireInfo acquireInfo = new()
        {
            Type = StructureType.SwapchainImageAcquireInfo
        };
        imageIndex = 0;
        Result result = api.Api.AcquireSwapchainImage(swapchain, in acquireInfo, ref imageIndex);
        if (result == Result.Success)
        {
            lock (_retiredSwapchainsGate)
                _runtimeAcquiredSwapchainHandles.Add(swapchain.Handle);
        }
        return result;
    }

    public Result WaitSwapchainImage(OpenXRAPI api, Swapchain swapchain, long timeoutNs)
    {
        SwapchainImageWaitInfo waitInfo = new()
        {
            Type = StructureType.SwapchainImageWaitInfo,
            Timeout = timeoutNs
        };
        return api.Api.WaitSwapchainImage(swapchain, in waitInfo);
    }

    public Result ReleaseSwapchainImage(OpenXRAPI api, Swapchain swapchain)
    {
        SwapchainImageReleaseInfo releaseInfo = new()
        {
            Type = StructureType.SwapchainImageReleaseInfo
        };
        Result result = api.Api.ReleaseSwapchainImage(swapchain, in releaseInfo);
        if (result == Result.Success)
        {
            lock (_retiredSwapchainsGate)
                _runtimeAcquiredSwapchainHandles.Remove(swapchain.Handle);
        }
        return result;
    }

    public void RenderViews(
        OpenXRAPI api,
        in CompositionLayerProjectionView projectionView,
        uint viewIndex)
    {
        // Rendering remains coordinated by the backend-neutral frame lifecycle.
    }

    public unsafe bool TryRenderViewsBatch(
        OpenXRAPI api,
        nint projectionViews,
        out bool handled)
    {
        Attach(api);
        return TryRenderVulkanEyesBatch(
            (CompositionLayerProjectionView*)projectionViews,
            out handled);
    }

    public bool TryRenderEye(
        OpenXRAPI api,
        uint viewIndex,
        uint imageIndex,
        OpenXRAPI.DelRenderToFBO? renderCallback)
    {
        Attach(api);
        return TryRenderVulkanEye(viewIndex, imageIndex);
    }

    public bool ShouldPrewarmEyeResources(OpenXRAPI api, uint viewIndex)
    {
        Attach(api);
        return ShouldPrewarmVulkanEyeResources(viewIndex);
    }

    public void PrewarmEyeResources(OpenXRAPI api, uint viewIndex)
    {
        Attach(api);
        PrewarmVulkanEyeResources(viewIndex);
    }

    public bool TryRenderDesktopMirrorComposition(
        OpenXRAPI api,
        uint targetWidth,
        uint targetHeight)
    {
        Attach(api);
        return TryRenderVulkanDesktopMirrorComposition(
            (VulkanRenderer)api.Window!.Renderer!,
            targetWidth,
            targetHeight);
    }

    public void EnsureStereoViewport(OpenXRAPI api, uint width, uint height)
    {
        Attach(api);
        EnsureOpenXrStereoViewport(width, height);
    }

    public void ResetBackendDiagnostics(OpenXRAPI api)
    {
        Attach(api);
        ResetStrictSpsBoundaryCaptureDiagnostics();
    }

    public void DestroyBackendResources(OpenXRAPI api)
    {
        Attach(api);
        DestroyVulkanEyeMirrorTargets();
        DestroyVulkanStereoRenderTarget();
        DestroyOpenXrPreviewTargets();
        DestroyViewportMirrorTargets();
    }
}
