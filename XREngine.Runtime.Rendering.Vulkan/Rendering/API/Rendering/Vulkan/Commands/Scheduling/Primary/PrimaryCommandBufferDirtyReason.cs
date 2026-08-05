namespace XREngine.Rendering.Vulkan;

[Flags]
internal enum PrimaryCommandBufferDirtyReason
{
    None = 0,
    ScheduleStructure = 1 << 0,
    GroupStructure = 1 << 1,
    SecondaryArtifactSequence = 1 << 2,
    ProfilerMode = 1 << 3,
}
