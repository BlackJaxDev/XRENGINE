namespace XREngine.Rendering;

/// <summary>
/// Resource synchronization encoding selected for advanced pipeline stages.
/// </summary>
public enum EAdvancedSynchronizationMode
{
    None = 0,
    OpenGlMemoryBarrier,
    VulkanLegacyBarriers,
    VulkanSynchronization2,
    /// <summary>Ordered WebGPU compute/render passes and copies provide cross-domain visibility.</summary>
    WebGpuPassBoundaries,
}
