namespace XREngine.Rendering.Vulkan;

[Flags]
internal enum CommandChainDirtyReason
{
    None = 0,
    Structure = 1 << 0,
    ResourcePlan = 1 << 1,
    DescriptorGeneration = 1 << 2,
    PipelineGeneration = 1 << 3,
    ProfilerMode = 1 << 4,
    FrameDataRefreshFailed = 1 << 5,
    VolatileCommand = 1 << 6,
    SecondaryCommandBufferInvalid = 1 << 7,
    BenchmarkForced = 1 << 8,
}
