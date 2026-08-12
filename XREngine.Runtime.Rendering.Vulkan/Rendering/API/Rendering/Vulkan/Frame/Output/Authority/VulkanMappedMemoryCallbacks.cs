namespace XREngine.Rendering.Vulkan;

/// <summary>Consumes a scoped writable mapped-memory range before it is unmapped.</summary>
internal delegate void VulkanMappedMemoryWriteCallback<TState>(Span<byte> mappedMemory, TState state);

/// <summary>Consumes a scoped readable mapped-memory range before it is unmapped.</summary>
internal delegate void VulkanMappedMemoryReadCallback<TState>(ReadOnlySpan<byte> mappedMemory, TState state);
