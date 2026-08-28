namespace XREngine.Rendering.Vulkan;

/// <summary>Exact dirty edge emitted synchronously at the native mutation point.</summary>
internal readonly record struct VulkanNativeDependencyInvalidationRecord(
    EVulkanNativeDependencyOwner SourceOwner,
    VulkanNativeDependencyHandle Source,
    EVulkanNativeDependencyOwner DependentOwner,
    VulkanNativeDependencyHandle Dependent,
    EVulkanNativeDependencyMutationDomain Domain,
    string Reason);
