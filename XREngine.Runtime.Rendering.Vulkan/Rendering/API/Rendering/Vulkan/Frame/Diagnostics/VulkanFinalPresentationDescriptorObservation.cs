namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures the exact mutable frame-source descriptor payload selected for a draw.
/// </summary>
internal readonly record struct VulkanFinalPresentationDescriptorObservation(
    ulong Sequence,
    ulong FrameNumber,
    int DescriptorSlot,
    ulong CommandBuffer,
    ulong DescriptorSet,
    uint Set,
    uint Binding,
    string? BindingName,
    ulong ImageView,
    ulong Sampler,
    Silk.NET.Vulkan.ImageLayout ImageLayout,
    ulong ResourceSignature,
    bool WriteMatched,
    bool WriteSucceeded);
