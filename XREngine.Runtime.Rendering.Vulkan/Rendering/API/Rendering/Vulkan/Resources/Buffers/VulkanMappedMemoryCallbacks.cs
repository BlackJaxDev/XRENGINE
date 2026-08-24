namespace XREngine.Rendering.Vulkan;

/// <summary>Consumes mapped bytes synchronously while the mapping lease is alive.</summary>
internal delegate bool VulkanMappedMemoryReadCallback(ReadOnlySpan<byte> bytes);

/// <summary>Consumes writable mapped bytes synchronously while the mapping lease is alive.</summary>
internal delegate bool VulkanMappedMemoryWriteCallback(Span<byte> bytes);
