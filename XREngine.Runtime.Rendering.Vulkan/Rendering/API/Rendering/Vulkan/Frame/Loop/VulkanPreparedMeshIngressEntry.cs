namespace XREngine.Rendering.Vulkan;

/// <summary>One transactionally staged mesh operation for the current frame.</summary>
internal readonly record struct VulkanPreparedMeshIngressEntry(
    int PassIndex,
    XRFrameBuffer? Target,
    PendingMeshDraw Draw,
    FrameOpContext Context,
    bool PreserveSubmissionOrder,
    bool IsDynamicUi,
    int ResourceUseOffset,
    int ResourceUseCount);
