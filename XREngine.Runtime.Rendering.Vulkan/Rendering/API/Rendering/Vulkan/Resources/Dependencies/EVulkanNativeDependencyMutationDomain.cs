namespace XREngine.Rendering.Vulkan;

/// <summary>Separates structural artifact replacement from data-only publication.</summary>
internal enum EVulkanNativeDependencyMutationDomain : byte
{
    Content,
    Topology,
    Retirement,
}
