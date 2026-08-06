namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Typed outcome published by compute planning before command recording.
/// </summary>
internal enum EVulkanComputePreparationOutcome : byte
{
    Success,
    ProgramLinkFailed,
    PipelineUnavailable,
    PipelineCreationFailed,
    DescriptorPreparationFailed
}
