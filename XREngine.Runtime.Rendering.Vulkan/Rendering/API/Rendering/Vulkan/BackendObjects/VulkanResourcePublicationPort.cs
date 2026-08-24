namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable planner publication reader bound only to wrappers that consume
/// physical resource groups. It prevents the backend identity context from
/// retaining the frame-planning authority.
/// </summary>
internal sealed class VulkanResourcePublicationPort(
    VulkanResourcePlannerPublicationReader publications,
    VulkanCommandThreadWorkspace commandWorkspace)
{
    private readonly VulkanResourcePlannerPublicationReader _publications = publications;
    private readonly VulkanCommandThreadWorkspace _commandWorkspace = commandWorkspace;

    /// <summary>
    /// Selects the immutable generation installed for the current operation,
    /// falling back to the committed planner generation outside a planning scope.
    /// </summary>
    internal ResourcePlannerRuntimeGeneration GetCurrentGeneration()
    {
        if (_commandWorkspace.TryGetCurrent(out VulkanCommandThreadContext context) &&
            context.ResourcePlannerRuntimeGeneration is { } scopedGeneration)
        {
            return scopedGeneration;
        }

        return _publications.GetPublishedGeneration();
    }

    internal bool TryGetPhysicalImageGroup(
        ResourcePlannerRuntimeGeneration generation,
        string resourceName,
        out VulkanPhysicalImageGroup? group)
        => _publications.TryGetPhysicalImageGroup(generation, resourceName, out group);
}
