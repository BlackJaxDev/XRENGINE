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
}
