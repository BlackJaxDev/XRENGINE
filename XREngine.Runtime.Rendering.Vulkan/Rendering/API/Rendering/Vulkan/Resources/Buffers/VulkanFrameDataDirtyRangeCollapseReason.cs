namespace XREngine.Rendering.Vulkan;

/// <summary>Explains why a frame-data dirty-range set conservatively widened its flush coverage.</summary>
internal enum VulkanFrameDataDirtyRangeCollapseReason : byte
{
    None = 0,
    CapacityExceeded = 1,
}
