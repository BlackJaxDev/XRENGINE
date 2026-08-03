namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal enum EVulkanQueueSubmissionDisposition : byte
    {
        NotSubmitted = 0,
        SubmittedIncomplete,
        Completed,
    }
}
