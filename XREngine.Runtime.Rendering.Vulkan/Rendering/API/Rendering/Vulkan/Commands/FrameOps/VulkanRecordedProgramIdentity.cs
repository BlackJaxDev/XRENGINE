namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact native pipeline and layout selected for one program use in a packet.
/// A program use without its eventual pipeline is intentionally incomplete.
/// </summary>
internal readonly record struct VulkanRecordedProgramIdentity(
    uint ProgramBindingId,
    ulong ProgramLinkGeneration,
    ulong PipelineLayoutHandle,
    ulong PipelineLayoutGeneration,
    ulong PipelineHandle,
    ulong PipelineGeneration)
{
    public bool IsComplete =>
        ProgramBindingId != 0u &&
        ProgramLinkGeneration != 0UL &&
        PipelineLayoutHandle != 0UL &&
        PipelineLayoutGeneration != 0UL &&
        PipelineHandle != 0UL &&
        PipelineGeneration != 0UL;
}
