using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Planner-owned reader for immutable resource-planner publications.
/// </summary>
/// <remarks>
/// Resource wrappers pass their concrete planner generation to this reader.
/// Consequently publication lookup cannot observe another command thread's
/// scoped planner state and this type does not retain command-thread storage.
/// </remarks>
internal sealed class VulkanResourcePlannerPublicationReader(VulkanFramePlanner owner)
{
    private readonly VulkanFramePlanner _owner = owner;

    internal ResourcePlannerRuntimeGeneration GetPublishedGeneration()
        => _owner.GetPublishedResourcePlannerGeneration();

    internal bool TryGetPhysicalImageGroup(
        ResourcePlannerRuntimeGeneration generation,
        string resourceName,
        out VulkanPhysicalImageGroup? group)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return generation.State.ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out group);
    }

    /// <summary>Publishes a named buffer wrapper for render-graph resolution.</summary>
    internal void TrackBufferBinding(XRDataBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        string name = string.IsNullOrWhiteSpace(buffer.AttributeName)
            ? buffer.Name ?? string.Empty
            : buffer.AttributeName;
        if (!string.IsNullOrWhiteSpace(name))
            _owner.TrackedBuffersByName[name] = buffer;
    }
}
