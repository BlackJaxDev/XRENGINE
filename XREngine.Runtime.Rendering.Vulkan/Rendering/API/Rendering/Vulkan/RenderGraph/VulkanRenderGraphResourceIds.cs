namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Cold-publication resource identity table shared by the structural graph and
/// its frozen barrier plan.  Names never escape into either recording payload.
/// </summary>
internal sealed class VulkanRenderGraphResourceIds
{
    private readonly Dictionary<string, VulkanResourceId> _ids =
        new(StringComparer.OrdinalIgnoreCase);

    internal VulkanResourceId GetOrAdd(string resourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        if (!_ids.TryGetValue(resourceName, out VulkanResourceId id))
        {
            id = new VulkanResourceId(_ids.Count);
            _ids.Add(resourceName, id);
        }

        return id;
    }
}
