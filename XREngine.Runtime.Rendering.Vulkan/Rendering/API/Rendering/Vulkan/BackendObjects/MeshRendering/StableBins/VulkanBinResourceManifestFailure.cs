namespace XREngine.Rendering.Vulkan;

/// <summary>Exact fail-closed reason for bin-manifest construction failure.</summary>
internal enum VulkanBinResourceManifestFailure : byte
{
    None = 0,
    InvalidCapacity = 1,
    CapacityExceeded = 2,
    QueueFamilyConflict = 3,
    ImageLayoutConflict = 4,
    NativeRangeConflict = 5,
}
