using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable Vulkan identity for one OpenXR eye, mirror, preview, or prewarm
/// operation. The context is passed through recording and submission so those
/// paths do not reconstruct target identity from ambient renderer state.
/// </summary>
internal readonly record struct VulkanOpenXrFrameContext(
    int ResourcePlannerStateIndex,
    uint ViewIndex,
    uint ImageIndex,
    Extent2D Extent,
    int TargetIdentity,
    string? TargetName,
    EVulkanFrameOpContextKind ContextKind,
    bool IsPrewarm = false)
{
    internal bool HasExternalTarget => Extent.Width != 0 && Extent.Height != 0;

    internal BoundingRectangle TargetRegion =>
        HasExternalTarget && Extent.Width <= int.MaxValue && Extent.Height <= int.MaxValue
            ? new BoundingRectangle(0, 0, (int)Extent.Width, (int)Extent.Height)
            : default;
}
