using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
internal sealed class VulkanStrictSpsFailureBoundaryTests
{
    [TestCase(
        VulkanRenderer.EOpenXrStrictSpsFaultInjectionStage.Recording,
        VulkanRenderer.EOpenXrStrictSpsFaultInjectionStage.Recording,
        true)]
    [TestCase(
        VulkanRenderer.EOpenXrStrictSpsFaultInjectionStage.LifetimeValidation,
        VulkanRenderer.EOpenXrStrictSpsFaultInjectionStage.Recording,
        false)]
    [TestCase(
        VulkanRenderer.EOpenXrStrictSpsFaultInjectionStage.None,
        VulkanRenderer.EOpenXrStrictSpsFaultInjectionStage.Recording,
        false)]
    public void FaultBoundary_ConsumesOnlyTheRequestedProductionSeam(
        VulkanRenderer.EOpenXrStrictSpsFaultInjectionStage requested,
        VulkanRenderer.EOpenXrStrictSpsFaultInjectionStage boundary,
        bool expected)
    {
        VulkanRenderer.IsOpenXrStrictSpsFaultBoundary(requested, boundary)
            .ShouldBe(expected);
    }

    [TestCase(VulkanRenderer.EVulkanQueueSubmissionDisposition.NotSubmitted, true)]
    [TestCase(VulkanRenderer.EVulkanQueueSubmissionDisposition.Completed, true)]
    [TestCase(VulkanRenderer.EVulkanQueueSubmissionDisposition.SubmittedIncomplete, false)]
    public void TemporaryPublishCommandBuffer_IsFreedUnlessQueueOwnsIncompleteWork(
        VulkanRenderer.EVulkanQueueSubmissionDisposition disposition,
        bool expectedFree)
    {
        VulkanRenderer.ShouldFreeTemporaryOpenXrCommandBuffer(disposition)
            .ShouldBe(expectedFree);
    }

    [Test]
    public void FaultHooks_AreOrderedAcrossRecordingValidationAndQueueDispatch()
    {
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");

        int recorded = openXr.IndexOf(
            "hasRecorded = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer",
            StringComparison.Ordinal);
        int recordingFault = openXr.IndexOf(
            "EOpenXrStrictSpsFaultInjectionStage.Recording",
            recorded,
            StringComparison.Ordinal);
        int publishRecord = openXr.IndexOf(
            "hasPublish = TryRecordStereoLayerBlitCommandBuffer",
            recorded,
            StringComparison.Ordinal);
        recorded.ShouldBeGreaterThanOrEqualTo(0);
        recordingFault.ShouldBeGreaterThan(recorded);
        publishRecord.ShouldBeGreaterThan(recordingFault);

        int gatewayPins = lifetime.IndexOf(
            "TryAcquireVulkanSubmissionGatewayPins",
            StringComparison.Ordinal);
        int lifetimeFault = lifetime.IndexOf(
            "EOpenXrStrictSpsFaultInjectionStage.LifetimeValidation",
            gatewayPins,
            StringComparison.Ordinal);
        int lifetimeValidationLoop = lifetime.IndexOf(
            "for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)",
            lifetimeFault,
            StringComparison.Ordinal);
        gatewayPins.ShouldBeGreaterThanOrEqualTo(0);
        lifetimeFault.ShouldBeGreaterThan(gatewayPins);
        lifetimeValidationLoop.ShouldBeGreaterThan(lifetimeFault);

        int lifetimeCall = synchronization.IndexOf(
            "ValidateVulkanSubmissionResourceLifetimes",
            StringComparison.Ordinal);
        int submitFault = synchronization.IndexOf(
            "EOpenXrStrictSpsFaultInjectionStage.Submit",
            lifetimeCall,
            StringComparison.Ordinal);
        int queueDispatch = synchronization.IndexOf(
            "SubmitToQueueSync2",
            submitFault,
            StringComparison.Ordinal);
        lifetimeCall.ShouldBeGreaterThanOrEqualTo(0);
        submitFault.ShouldBeGreaterThan(lifetimeCall);
        queueDispatch.ShouldBeGreaterThan(submitFault);
    }

    [Test]
    public void UnsubmittedRecording_InvalidatesCachedCommandsBeforeCanceledUploadsRetireResources()
    {
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        openXr.ShouldContain("MarkUnsubmittedOpenXrPrimaryCommandBufferDirty");
        openXr.ShouldContain("submissionDisposition == EVulkanQueueSubmissionDisposition.NotSubmitted");
        openXr.ShouldContain("variant.DirtyReason = reason");

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

    private static string ReadWorkspaceFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        while (directory is not null)
        {
            string fullPath = Path.Combine(directory.FullName, platformPath);
            if (File.Exists(fullPath))
                return File.ReadAllText(fullPath);

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not resolve workspace path '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }
}
