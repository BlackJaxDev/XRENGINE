namespace XREngine.Rendering.Vulkan;

/// <summary>Non-blocking lifecycle of one diagnostic staging-ring slot.</summary>
internal enum EVulkanGpuDiagnosticReadbackSidecarSlotState : byte
{
    Idle,
    Reserved,
    Submitted,
    Decoding,
}
