namespace XREngine.Rendering.Vulkan;

/// <summary>Cold observation of an already prepared native material-table bank.</summary>
public sealed record VulkanMaterialTableDiagnosticSnapshot(
    ulong BufferHandle, ulong NativeGeneration, ulong Range, uint RowByteStride,
    ulong TableOwnerId, ulong RowGeneration, ulong DescriptorClosureGeneration, byte[] Bytes);
