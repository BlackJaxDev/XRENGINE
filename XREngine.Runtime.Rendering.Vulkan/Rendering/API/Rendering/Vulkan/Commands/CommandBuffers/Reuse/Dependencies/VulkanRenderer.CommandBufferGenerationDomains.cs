namespace XREngine.Rendering.Vulkan;

internal readonly record struct CommandBufferGenerationDomains(
    ulong Structural,
    ulong FrameData,
    ulong CameraPose,
    ulong TargetSlot,
    ulong Descriptor,
    ulong ResourceAllocation,
    ulong Query,
    ulong Overlay,
    ulong Profiler);
