namespace XREngine.Rendering.Vulkan;

/// <summary>Unproven retirement or native destruction retained for diagnostics and teardown safety.</summary>
internal readonly record struct VulkanRetirementQuarantineEntry(
    EVulkanRetirementWorkClass WorkClass,
    ulong Handle,
    Exception Exception,
    object? RetainedOwner = null);
