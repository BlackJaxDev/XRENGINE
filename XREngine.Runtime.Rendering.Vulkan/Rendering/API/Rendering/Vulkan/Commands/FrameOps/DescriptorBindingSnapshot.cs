namespace XREngine.Rendering.Vulkan;

internal readonly record struct DescriptorBindingSnapshot(
    ulong DescriptorGeneration,
    int DescriptorSetCount,
    ulong DescriptorSetSignature);
