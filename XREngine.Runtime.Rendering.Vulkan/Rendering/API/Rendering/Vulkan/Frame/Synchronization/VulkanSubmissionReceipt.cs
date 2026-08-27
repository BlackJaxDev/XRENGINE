using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Reports queue acceptance independently from post-submit bookkeeping.</summary>
internal readonly record struct VulkanSubmissionReceipt(
    Result Result,
    bool SubmissionAccepted,
    bool LifetimePinsTransferred,
    bool PostSubmissionPublicationSucceeded,
    TimeSpan QueueAdmissionWait,
    TimeSpan NativeDispatchElapsed)
{
    public static VulkanSubmissionReceipt Rejected(
        Result result,
        TimeSpan queueAdmissionWait = default,
        TimeSpan nativeDispatchElapsed = default)
        => new(
            result,
            false,
            false,
            true,
            queueAdmissionWait,
            nativeDispatchElapsed);
}
