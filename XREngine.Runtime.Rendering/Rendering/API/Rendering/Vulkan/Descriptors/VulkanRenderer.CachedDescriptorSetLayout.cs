using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Represents a cached Vulkan descriptor set layout, including its layout, signature, schema hash, and usage flags.
    /// </summary>
    private sealed class CachedDescriptorSetLayout
    {
        /// <summary>
        /// The Vulkan descriptor set layout associated with this cached entry.
        /// </summary>
        public required DescriptorSetLayout Layout;
        /// <summary>
        /// The signature of the descriptor layout, representing the bindings and their types.
        /// </summary>
        public required DescriptorLayoutBindingSignature[] Signature;
        /// <summary>
        /// The hash of the descriptor set layout schema, used for quick comparisons and cache validation.
        /// </summary>
        public required ulong SchemaHash;
        /// <summary>
        /// Indicates whether the descriptor set layout uses the update-after-bind feature.
        /// </summary>
        public required bool UsesUpdateAfterBind;
        /// <summary>
        /// Indicates whether the descriptor set layout uses a variable descriptor count.
        /// </summary>
        public required bool UsesVariableDescriptorCount;
        /// <summary>
        /// The reference count for this cached descriptor set layout, used for managing its lifetime.
        /// </summary>
        public int RefCount;
    }
}
