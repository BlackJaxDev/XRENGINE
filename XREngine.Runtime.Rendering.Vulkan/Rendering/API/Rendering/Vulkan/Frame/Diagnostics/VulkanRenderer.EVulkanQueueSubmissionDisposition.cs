namespace XREngine.Rendering.Vulkan;

internal enum EVulkanQueueSubmissionDisposition : byte
{
    NotSubmitted = 0,
    SubmittedIncomplete,
    Completed,
}
