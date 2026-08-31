using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact published identity of a bounded staging allocation.  A chunk may only
/// record against this generation until its fence-completion retirement path.
/// </summary>
internal readonly record struct VulkanStagingBufferLease(
    Buffer Buffer,
    DeviceMemory Memory,
    ulong Capacity,
    ulong AllocationGeneration,
    bool ForegroundReserved)
{
    public bool IsValid => Buffer.Handle != 0 && Memory.Handle != 0 && AllocationGeneration != 0;
}
