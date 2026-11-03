namespace XREngine.Rendering;

/// <summary>A point in one frame that <see cref="FramePacingTimeline"/> records.</summary>
public enum EFramePacingMark : byte
{
    /// <summary>The collect thread starts a collect iteration.</summary>
    CollectStart,
    /// <summary>The collect thread starts to wait for the render thread to release the buffers.</summary>
    CollectWaitRenderStart,
    /// <summary>The render thread released the buffers to the collect thread.</summary>
    CollectWaitRenderEnd,
    /// <summary>The collect thread published the swapped generation.</summary>
    CollectPublished,
    /// <summary>The render thread starts to wait for a collect publication.</summary>
    RenderWaitCollectStart,
    /// <summary>A collect publication is available to the render thread.</summary>
    RenderWaitCollectEnd,
    /// <summary>The OpenXR render callback starts.</summary>
    XrRenderStart,
    /// <summary><c>xrWaitFrame</c> is called.</summary>
    XrWaitFrameStart,
    /// <summary><c>xrWaitFrame</c> returned.</summary>
    XrWaitFrameEnd,
    /// <summary><c>xrBeginFrame</c> returned.</summary>
    XrBeginFrameEnd,
    /// <summary><c>xrEndFrame</c> is called.</summary>
    XrEndFrameStart,
    /// <summary><c>xrEndFrame</c> returned.</summary>
    XrEndFrameEnd,
    /// <summary>The Vulkan eye batch render starts.</summary>
    EyeBatchStart,
    /// <summary><c>xrAcquireSwapchainImage</c> is called.</summary>
    SwapchainAcquireStart,
    /// <summary><c>xrAcquireSwapchainImage</c> returned.</summary>
    SwapchainAcquireEnd,
    /// <summary><c>xrWaitSwapchainImage</c> is called.</summary>
    SwapchainWaitStart,
    /// <summary><c>xrWaitSwapchainImage</c> returned.</summary>
    SwapchainWaitEnd,
    /// <summary><c>xrReleaseSwapchainImage</c> returned.</summary>
    SwapchainReleaseEnd,
    /// <summary>A tracked Vulkan queue submission starts.</summary>
    QueueSubmitStart,
    /// <summary>A tracked Vulkan queue submission returned.</summary>
    QueueSubmitEnd,
    /// <summary>A host wait on a timeline semaphore starts.</summary>
    TimelineWaitStart,
    /// <summary>A host wait on a timeline semaphore returned.</summary>
    TimelineWaitEnd,
    /// <summary>The window starts to render its desktop viewports.</summary>
    DesktopRenderStart,
    /// <summary>An independent desktop frame was admitted because it fits the XR slack.</summary>
    DesktopAdmitFits,
    /// <summary>An independent desktop frame was admitted at the minimum rate.</summary>
    DesktopAdmitMinimumRate,
    /// <summary>An independent desktop frame was admitted at the fallback rate.</summary>
    DesktopAdmitFallback,
    /// <summary>An independent desktop frame was admitted at the floor rate.</summary>
    DesktopAdmitFloor,
    /// <summary>The Vulkan desktop frame transaction starts.</summary>
    DesktopTransactionStart,
    /// <summary>The Vulkan desktop frame transaction ends.</summary>
    DesktopTransactionEnd,
    /// <summary>The desktop primary was handed to the worker lane.</summary>
    DesktopWorkerSubmit,
    /// <summary>The render thread joined the desktop worker recording.</summary>
    DesktopWorkerJoined,
    /// <summary>A split desktop first half prepared its frame slot.</summary>
    DesktopSplitSlotReady,
    /// <summary>A split desktop first half completed readiness.</summary>
    DesktopSplitReadinessDone,
    /// <summary>Desktop readiness finished shadow readiness.</summary>
    DesktopReadyShadow,
    /// <summary>Desktop readiness finished authored operation capture.</summary>
    DesktopReadyOpsCaptured,
    /// <summary>Desktop readiness finished visible texture readiness.</summary>
    DesktopReadyTextures,
    /// <summary>Desktop readiness finished mesh materialization.</summary>
    DesktopReadyMeshes,
    /// <summary>Desktop readiness finished stable-bin creation.</summary>
    DesktopReadyStableBins,
    /// <summary>Desktop readiness finished compute program preparation.</summary>
    DesktopReadyCompute,
    /// <summary>Desktop readiness finished target preparation and barrier freezing.</summary>
    DesktopReadyTargets,
    /// <summary>Desktop readiness finished frame plan build and seal.</summary>
    DesktopReadyPlanSealed,
    /// <summary>Desktop readiness finished plan texture readiness.</summary>
    DesktopReadyPlanTextures,
    /// <summary>Desktop readiness finished read-only storage preparation.</summary>
    DesktopReadyStorage,
    /// <summary>Desktop readiness finished pipeline preparation.</summary>
    DesktopReadyPipelines,
    /// <summary>Desktop readiness finished compute recording preparation.</summary>
    DesktopReadyComputeRecording,
    /// <summary>Desktop readiness finished plan acceptance.</summary>
    DesktopReadyAccepted,
}
