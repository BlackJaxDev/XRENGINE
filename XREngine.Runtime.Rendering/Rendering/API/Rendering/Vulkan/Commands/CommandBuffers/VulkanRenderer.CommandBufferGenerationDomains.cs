namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly record struct CommandBufferGenerationDomains(
            ulong Structural,
            ulong FrameData,
            ulong CameraPose,
            ulong TargetSlot,
            ulong Descriptor,
            ulong ResourceAllocation,
            ulong Query,
            ulong Overlay,
            ulong Profiler);

    }
}
