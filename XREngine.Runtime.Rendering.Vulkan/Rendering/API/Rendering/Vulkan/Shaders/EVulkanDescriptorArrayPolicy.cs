namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Indexing policy compiled from a reflected descriptor declaration.
/// </summary>
internal enum EVulkanDescriptorArrayPolicy : byte
{
    Single = 0,
    FixedCount,
}
