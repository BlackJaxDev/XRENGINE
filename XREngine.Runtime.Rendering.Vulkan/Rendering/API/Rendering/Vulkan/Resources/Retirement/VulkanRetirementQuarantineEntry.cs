namespace XREngine.Rendering.Vulkan;

/// <summary>Exceptional native-destruction failure retained for diagnostics while its queue entry is retried.</summary>
internal readonly record struct VulkanRetirementQuarantineEntry(
    EVulkanRetirementWorkClass WorkClass,
    ulong Handle,
    Exception Exception);
