using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
internal sealed class VulkanStrictSpsFailureBoundaryTests
{
    [TestCase(
        EOpenXrStrictSpsFaultInjectionStage.Recording,
        EOpenXrStrictSpsFaultInjectionStage.Recording,
        true)]
    [TestCase(
        EOpenXrStrictSpsFaultInjectionStage.LifetimeValidation,
        EOpenXrStrictSpsFaultInjectionStage.Recording,
        false)]
    [TestCase(
        EOpenXrStrictSpsFaultInjectionStage.None,
        EOpenXrStrictSpsFaultInjectionStage.Recording,
        false)]
    public void FaultBoundary_ConsumesOnlyTheRequestedProductionSeam(
        EOpenXrStrictSpsFaultInjectionStage requested,
        EOpenXrStrictSpsFaultInjectionStage boundary,
        bool expected)
    {
        VulkanFrameLoop.IsOpenXrStrictSpsFaultBoundary(requested, boundary)
            .ShouldBe(expected);
    }

    [TestCase(EVulkanQueueSubmissionDisposition.NotSubmitted, true)]
    [TestCase(EVulkanQueueSubmissionDisposition.Completed, true)]
    [TestCase(EVulkanQueueSubmissionDisposition.SubmittedIncomplete, false)]
    public void TemporaryPublishCommandBuffer_IsFreedUnlessQueueOwnsIncompleteWork(
        EVulkanQueueSubmissionDisposition disposition,
        bool expectedFree)
    {
        VulkanFrameLoop.ShouldFreeTemporaryOpenXrCommandBuffer(disposition)
            .ShouldBe(expectedFree);
    }

    [Test]
    public void FaultHooks_AreOrderedAcrossRecordingValidationAndQueueDispatch()
    {
        // The strict-SPS mirror path records the eye, checks the recording fault
        // seam, and then records the publish blit.
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.OpenXR.MirrorPreview.cs");
        // The command runtime takes the submission gateway pins, checks the
        // lifetime fault seam, validates lifetimes, then checks the submit seam
        // before the native queue dispatch.
        string submission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs");

        int recordingFault = openXr.IndexOf(
            "EOpenXrStrictSpsFaultInjectionStage.Recording))",
            StringComparison.Ordinal);
        int recorded = openXr.LastIndexOf(
            "hasRecorded = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(",
            Math.Max(recordingFault, 0),
            StringComparison.Ordinal);
        int publishRecord = openXr.IndexOf(
            "hasPublish = TryRecordStereoLayerBlitCommandBuffer(",
            Math.Max(recordingFault, 0),
            StringComparison.Ordinal);
        recorded.ShouldBeGreaterThanOrEqualTo(0);
        recordingFault.ShouldBeGreaterThan(recorded);
        publishRecord.ShouldBeGreaterThan(recordingFault);

        int lifetimeMethod = submission.IndexOf(
            "private unsafe bool TryAcquireSubmissionLifetimePins(",
            StringComparison.Ordinal);
        int gatewayPins = submission.IndexOf(
            "lifetime.QueuedSubmissionCount++;",
            Math.Max(lifetimeMethod, 0),
            StringComparison.Ordinal);
        int lifetimeFault = submission.IndexOf(
            "EOpenXrStrictSpsFaultInjectionStage.LifetimeValidation)",
            Math.Max(gatewayPins, 0),
            StringComparison.Ordinal);
        int lifetimeValidationLoop = submission.IndexOf(
            "for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)",
            Math.Max(lifetimeFault, 0),
            StringComparison.Ordinal);
        lifetimeMethod.ShouldBeGreaterThanOrEqualTo(0);
        gatewayPins.ShouldBeGreaterThan(lifetimeMethod);
        lifetimeFault.ShouldBeGreaterThan(gatewayPins);
        lifetimeValidationLoop.ShouldBeGreaterThan(lifetimeFault);

        int lifetimeCall = submission.IndexOf(
            "lifetimePinsValid = TryAcquireSubmissionLifetimePins(",
            StringComparison.Ordinal);
        int submitFault = submission.IndexOf(
            "EOpenXrStrictSpsFaultInjectionStage.Submit)",
            Math.Max(lifetimeCall, 0),
            StringComparison.Ordinal);
        int queueDispatch = submission.IndexOf(
            "result = SubmitNative(queue, ref submitInfo, fence);",
            Math.Max(submitFault, 0),
            StringComparison.Ordinal);
        lifetimeCall.ShouldBeGreaterThanOrEqualTo(0);
        submitFault.ShouldBeGreaterThan(lifetimeCall);
        queueDispatch.ShouldBeGreaterThan(submitFault);
    }

    [Test]
    public void UnsubmittedRecording_InvalidatesCachedCommandsBeforeCanceledUploadsRetireResources()
    {
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.OpenXR.MirrorPreview.cs");
        string openXrOwners = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.OpenXrMirrorPreview.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Transfers/VulkanRenderer.TextureUploadRecording.cs");

        openXr.ShouldContain("_commandRuntime.MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(");
        openXr.ShouldContain("submissionDisposition == EVulkanQueueSubmissionDisposition.NotSubmitted");
        openXrOwners.ShouldContain("internal void MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(");
        openXrOwners.ShouldContain("owner.DirtyReason = reason");

        int cancel = recording.IndexOf("private void CancelRecordedTextureUploads", StringComparison.Ordinal);
        int invalidateChains = recording.IndexOf(
            "InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease",
            cancel,
            StringComparison.Ordinal);
        int release = recording.IndexOf("CancelRecordedTextureUpload(uploads[i], reason)", cancel, StringComparison.Ordinal);
        cancel.ShouldBeGreaterThanOrEqualTo(0);
        invalidateChains.ShouldBeGreaterThan(cancel);
        release.ShouldBeGreaterThan(invalidateChains);
    }

    [Test]
    public void StrictSpsTransferPublish_RegistersRuntimeOwnedSwapchainImages()
    {
        // The frame loop owns the strict-SPS stereo publish blit preparation.
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.OpenXR.Blit.cs");

        int prepare = openXr.IndexOf("private bool TryPrepareStereoLayerBlit(", StringComparison.Ordinal);
        int leftRegistration = openXr.IndexOf(
            "leftDestinationImage.Handle",
            prepare,
            StringComparison.Ordinal);
        int leftExternalOwnership = openXr.IndexOf(
            "externallyOwned: true",
            leftRegistration,
            StringComparison.Ordinal);
        int rightRegistration = openXr.IndexOf(
            "rightDestinationImage.Handle",
            leftExternalOwnership,
            StringComparison.Ordinal);
        int rightExternalOwnership = openXr.IndexOf(
            "externallyOwned: true",
            rightRegistration,
            StringComparison.Ordinal);
        int sourceLookup = openXr.IndexOf(
            "GetOrCreateAPIRenderObject(sourceTexture, generateNow: true)",
            rightExternalOwnership,
            StringComparison.Ordinal);

        prepare.ShouldBeGreaterThanOrEqualTo(0);
        leftRegistration.ShouldBeGreaterThan(prepare);
        leftExternalOwnership.ShouldBeGreaterThan(leftRegistration);
        rightRegistration.ShouldBeGreaterThan(leftExternalOwnership);
        rightExternalOwnership.ShouldBeGreaterThan(rightRegistration);
        sourceLookup.ShouldBeGreaterThan(rightExternalOwnership);
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);
}
