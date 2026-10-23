using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIBatchCohortReuseTests
{
    [TestCase(EGpuFenceSubmissionStatus.AwaitingSubmission, null, false, false)]
    [TestCase(EGpuFenceSubmissionStatus.Submitted, null, false, false)]
    [TestCase(EGpuFenceSubmissionStatus.Submitted, null, true, true)]
    [TestCase(EGpuFenceSubmissionStatus.Failed, EGpuFenceNativeSubmission.Unknown, false, false)]
    [TestCase(EGpuFenceSubmissionStatus.Failed, EGpuFenceNativeSubmission.Called, false, false)]
    [TestCase(EGpuFenceSubmissionStatus.Failed, EGpuFenceNativeSubmission.Accepted, false, false)]
    [TestCase(EGpuFenceSubmissionStatus.Failed, EGpuFenceNativeSubmission.NotCalled, false, true)]
    public void VulkanSubmissionProofControlsCohortRelease(
        EGpuFenceSubmissionStatus status, EGpuFenceNativeSubmission? nativeSubmission,
        bool signaled, bool expected)
    {
        UIBatchCollector.IsCompletedOrAbandonedVulkanSubmission(
            status, nativeSubmission, signaled).ShouldBe(expected);
    }
}
