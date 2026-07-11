namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Represents a key used to cache compute descriptor sets based on the schema and binding.
    /// </summary>
    /// <param name="SchemaKey">The key representing the schema of the compute descriptor set.</param>
    /// <param name="BindingKey">The key representing the binding within the compute descriptor set.</param>
    private readonly record struct ComputeDescriptorCacheKey(ulong SchemaKey, ulong BindingKey);
}
