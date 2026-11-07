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

    /// <summary>
    /// Returns the resource names indexed by <see cref="VulkanResourceId.Value"/>. Diagnostics use this table to
    /// show names for frozen barriers. Recording does not read it.
    /// </summary>
    internal string[] ToNameArray()
    {
        string[] names = new string[_ids.Count];
        foreach (KeyValuePair<string, VulkanResourceId> pair in _ids)
            names[pair.Value.Value] = pair.Key;
        return names;
    }
}
