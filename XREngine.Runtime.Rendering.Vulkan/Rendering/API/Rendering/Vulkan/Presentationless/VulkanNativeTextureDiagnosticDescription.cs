namespace XREngine.Rendering.Vulkan;

/// <summary>Cold native identity for bounded, exact-generation texture readback.</summary>
public readonly record struct VulkanNativeTextureDiagnosticDescription(
    ulong ImageHandle,
    ulong PublishedGeneration,
    ulong DescriptorGeneration,
    uint Width,
    uint Height,
    uint MipLevels);
