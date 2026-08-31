using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One fixed timestamp-query pair leased exclusively to a submitted imported
/// texture transfer batch. The default value means timing is unavailable.
/// </summary>
internal readonly struct VulkanTextureUploadGpuTimestampLease(
    int slot,
    QueryPool queryPool,
    uint validBits,
    double timestampPeriodNanoseconds)
{
    internal int Slot { get; } = slot;
    internal QueryPool QueryPool { get; } = queryPool;
    internal uint ValidBits { get; } = validBits;
    internal double TimestampPeriodNanoseconds { get; } = timestampPeriodNanoseconds;
    internal bool IsValid => Slot >= 0 && QueryPool.Handle != 0 && ValidBits != 0 && TimestampPeriodNanoseconds > 0.0;
}
