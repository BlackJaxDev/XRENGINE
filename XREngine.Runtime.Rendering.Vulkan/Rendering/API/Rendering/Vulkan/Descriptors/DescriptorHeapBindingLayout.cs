using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed record DescriptorHeapBindingLayout(
    DescriptorHeapBindingKey Key,
    DescriptorType DescriptorType,
    DescriptorType ResourceDescriptorType,
    uint DescriptorCount,
    bool HasResource,
    bool HasSampler,
    uint ResourcePushOffset,
    uint SamplerPushOffset,
    uint ResourceStride,
    uint SamplerStride);