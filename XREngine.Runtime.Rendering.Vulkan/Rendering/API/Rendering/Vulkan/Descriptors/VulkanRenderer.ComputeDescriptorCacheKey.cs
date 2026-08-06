namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies a reusable compute descriptor set by schema and binding.</summary>
internal readonly record struct ComputeDescriptorCacheKey(ulong SchemaKey, ulong BindingKey);
