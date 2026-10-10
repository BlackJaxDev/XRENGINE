using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns native layouts and immutable reflection for one linked interface.</summary>
internal sealed class VulkanProgramInterfaceEntry(
    VulkanProgramInterfaceKey key,
    ulong generation,
    VulkanBackendObjectContext context,
    DescriptorLayoutBuildResult descriptors,
    DescriptorSetLayout[] layoutsBeforeGlobalMaterial,
    bool hasGlobalTextureArrayOnlySet,
    bool canBindGlobalTextureArraySeparately,
    AutoUniformBlockInfo[] autoUniformBlocks,
    DescriptorHeapProgramLayout? descriptorHeapLayout,
    PipelineLayout pipelineLayout,
    ulong layoutFingerprint,
    ulong schemaFingerprint)
{
    internal VulkanProgramInterfaceKey Key { get; } = key;
    internal ulong Generation { get; } = generation;
    internal VulkanBackendObjectContext Context { get; } = context;
    internal DescriptorLayoutBuildResult Descriptors { get; } = descriptors;
    internal DescriptorSetLayout[] LayoutsBeforeGlobalMaterial { get; } = layoutsBeforeGlobalMaterial;
    internal bool HasGlobalTextureArrayOnlySet { get; } = hasGlobalTextureArrayOnlySet;
    internal bool CanBindGlobalTextureArraySeparately { get; } = canBindGlobalTextureArraySeparately;
    internal AutoUniformBlockInfo[] AutoUniformBlocks { get; } = autoUniformBlocks;
    internal DescriptorHeapProgramLayout? DescriptorHeapLayout { get; } = descriptorHeapLayout;
    internal PipelineLayout PipelineLayout { get; } = pipelineLayout;
    internal ulong LayoutFingerprint { get; } = layoutFingerprint;
    internal ulong SchemaFingerprint { get; } = schemaFingerprint;
    internal int RetainCount;
    internal bool Cached;
}
