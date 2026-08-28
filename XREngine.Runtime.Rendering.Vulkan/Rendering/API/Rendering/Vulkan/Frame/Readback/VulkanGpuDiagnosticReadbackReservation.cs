namespace XREngine.Rendering.Vulkan;

/// <summary>Generation-safe handle for one diagnostic staging-ring reservation.</summary>
internal readonly record struct VulkanGpuDiagnosticReadbackReservation(
    int SlotIndex,
    ulong FrameIdentity);
