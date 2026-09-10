namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact native heap binding and lifetime identity. Descriptor contents have
/// separate publication identities; changing bytes inside a bound heap does not
/// require rebinding the heap itself.
/// </summary>
internal readonly record struct DescriptorHeapBindingIdentity(
    VulkanPinnedResourceGeneration SamplerBuffer,
    ulong SamplerAddress,
    ulong SamplerSize,
    ulong SamplerReservedOffset,
    ulong SamplerReservedSize,
    VulkanPinnedResourceGeneration ResourceBuffer,
    ulong ResourceAddress,
    ulong ResourceSize,
    ulong ResourceReservedOffset,
    ulong ResourceReservedSize)
{
    public bool IsComplete
        => SamplerBuffer.Key.IsValid && SamplerBuffer.Generation != 0 &&
           ResourceBuffer.Key.IsValid && ResourceBuffer.Generation != 0 &&
           SamplerAddress != 0 && SamplerSize != 0 &&
           ResourceAddress != 0 && ResourceSize != 0;
}
