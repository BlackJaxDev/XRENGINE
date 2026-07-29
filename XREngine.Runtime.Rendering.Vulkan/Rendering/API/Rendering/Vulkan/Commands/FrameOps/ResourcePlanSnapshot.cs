namespace XREngine.Rendering.Vulkan;

internal readonly record struct ResourcePlanSnapshot(
    ulong Revision,
    ulong PhysicalImageSignature,
    ulong FramebufferSignature,
    ulong PipelineGeneration);
