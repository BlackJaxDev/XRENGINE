using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Exact native backing selected for one immutable material-table publication.</summary>
internal readonly record struct VulkanMaterialTablePreparedBinding(
    Silk.NET.Vulkan.Buffer Buffer,
    ulong NativeGeneration,
    ulong Range,
    uint RowByteStride,
    ulong TableOwnerId,
    ulong RowGeneration,
    ulong DescriptorClosureGeneration);
