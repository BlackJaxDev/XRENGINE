using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Reports queue acceptance independently from post-submit bookkeeping.</summary>
internal readonly record struct VulkanSubmissionReceipt(
    Result Result,
    bool SubmissionAccepted,
    bool LifetimePinsTransferred,
    bool PostSubmissionPublicationSucceeded)
{
    public static VulkanSubmissionReceipt Rejected(Result result)
        => new(result, false, false, true);
}
