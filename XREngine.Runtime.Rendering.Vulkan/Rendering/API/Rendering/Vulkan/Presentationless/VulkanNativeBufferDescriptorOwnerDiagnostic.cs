namespace XREngine.Rendering.Vulkan;

/// <summary>One descriptor owner that still pins an exact retired native buffer generation.</summary>
public readonly record struct VulkanNativeBufferDescriptorOwnerDiagnostic(
    ulong DescriptorSetHandle,
    ulong DescriptorSetGeneration,
    ulong DescriptorPoolHandle,
    string Owner);
