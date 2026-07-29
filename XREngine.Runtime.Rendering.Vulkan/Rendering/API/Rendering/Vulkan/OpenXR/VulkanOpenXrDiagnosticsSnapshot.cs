namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable, allocation-free snapshot of Vulkan-owned OpenXR graphics state.
/// It intentionally excludes generic OpenXR session, input, pose, and pacing
/// policy.
/// </summary>
internal readonly record struct VulkanOpenXrDiagnosticsSnapshot(
    int SwapchainImageViewCount,
    int PrimaryCommandBufferVariantCount,
    int ResourcePlannerStateCount,
    int ActiveExternalSwapchainScopeCount,
    int SynchronousUploadBlockCount,
    int ActivePrewarmScopeCount,
    long RuntimeSessionDirtyWaitStartTimestamp,
    long RuntimeSessionPendingFrameWaitStartTimestamp);
