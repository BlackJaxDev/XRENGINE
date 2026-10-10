using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>Stores one resource blocker without keeping a viewport or generation alive.</summary>
internal readonly record struct VulkanResourceCatchUpBlockerSnapshot(
    ulong FrameId,
    int ViewportIndex,
    int ViewportIdentity,
    int PipelineInstanceId,
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    ResourceGenerationKey? ActiveKey,
    RenderResourceGenerationStatus? ActiveStatus,
    ResourceGenerationKey? PendingKey,
    RenderResourceGenerationStatus? PendingStatus,
    bool SkippedResizeCatchUp,
    bool WindowEventMinimized,
    bool WindowSurfaceMinimized,
    bool InteractiveResize,
    string Reason,
    string? LastRenderDeclineReason,
    string? LastResourceGenerationFailure);
