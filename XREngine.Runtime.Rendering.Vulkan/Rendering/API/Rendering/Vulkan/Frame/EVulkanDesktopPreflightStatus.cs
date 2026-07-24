namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures the mutually exclusive result of desktop preflight inspection.
/// </summary>
internal enum EVulkanDesktopPreflightStatus
{
    Ready,
    Reentrant,
    ZeroSurface,
    ResizePending,
    ResourceMismatch,
    InteractiveSlotBusy,
    SurfaceUnavailable,
}
