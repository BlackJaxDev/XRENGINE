using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable descriptor source identity retained for a published heap range.</summary>
internal readonly record struct DescriptorHeapPublishedSource(
    ulong Handle,
    ulong Generation,
    ulong Offset,
    ulong Range,
    ImageLayout Layout);

/// <summary>One never-overwritten heap publication. Entries remain valid until device teardown.</summary>
internal sealed class DescriptorHeapPublishedBinding(
    DescriptorType descriptorType,
    bool samplerHeap,
    DescriptorHeapPublishedSource[] sources,
    uint firstIndex)
{
    public DescriptorType DescriptorType { get; } = descriptorType;
    public bool SamplerHeap { get; } = samplerHeap;
    public DescriptorHeapPublishedSource[] Sources { get; } = sources;
    public uint FirstIndex { get; } = firstIndex;
}
