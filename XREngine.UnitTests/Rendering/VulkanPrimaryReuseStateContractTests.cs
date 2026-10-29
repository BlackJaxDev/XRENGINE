using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

public class VulkanPrimaryReuseStateContractTests
{
    [Test]
    public void EqualEntryStates_AreReusableAndIgnoreTelemetrySerial()
    {
        VulkanImageAccessState expected = CreateState(serial: 7);
        VulkanImageAccessState actual = CreateState(serial: 99);

        VulkanImageEntryStateContract.Compare(actual, expected)
            .ShouldBe(EVulkanPrimaryEntryStateMismatch.None);
    }

    [Test]
    public void BroaderRecordedSourceMasks_CoverTheActualEntryState()
    {
        VulkanImageAccessState expected = CreateState(
            stages: PipelineStageFlags2.VertexShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            access: AccessFlags2.ShaderReadBit |
                AccessFlags2.MemoryReadBit);
        VulkanImageAccessState actual = CreateState(
            stages: PipelineStageFlags2.FragmentShaderBit,
            access: AccessFlags2.ShaderReadBit);

        VulkanImageEntryStateContract.Compare(actual, expected)
            .ShouldBe(EVulkanPrimaryEntryStateMismatch.None);
    }

    [Test]
    public void NarrowRecordedStageMask_RejectsReuse()
    {
        VulkanImageAccessState expected = CreateState(
            stages: PipelineStageFlags2.FragmentShaderBit);
        VulkanImageAccessState actual = CreateState(
            stages: PipelineStageFlags2.ComputeShaderBit);

        VulkanImageEntryStateContract.Compare(actual, expected)
            .ShouldBe(EVulkanPrimaryEntryStateMismatch.StageMask);
    }

    [Test]
    public void NarrowRecordedAccessMask_RejectsReuse()
    {
        VulkanImageAccessState expected = CreateState(
            access: AccessFlags2.ShaderReadBit);
        VulkanImageAccessState actual = CreateState(
            access: AccessFlags2.ShaderWriteBit);

        VulkanImageEntryStateContract.Compare(actual, expected)
            .ShouldBe(EVulkanPrimaryEntryStateMismatch.AccessMask);
    }

    [TestCase(ImageLayout.General, EVulkanPrimaryEntryStateMismatch.Layout)]
    [TestCase(ImageLayout.Undefined, EVulkanPrimaryEntryStateMismatch.UnknownActualLayout)]
    public void LayoutChanges_AreClassifiedPrecisely(
        ImageLayout actualLayout,
        EVulkanPrimaryEntryStateMismatch expectedMismatch)
    {
        VulkanImageEntryStateContract.Compare(
                CreateState(layout: actualLayout),
                CreateState())
            .ShouldBe(expectedMismatch);
    }

    [Test]
    public void RecreatedImageGeneration_RejectsReuse()
    {
        VulkanImageEntryStateContract.Compare(
                CreateState(resourceGeneration: 42),
                CreateState(resourceGeneration: 41))
            .ShouldBe(EVulkanPrimaryEntryStateMismatch.ResourceGeneration);
    }

    [Test]
    public void PerImageQueueOwnership_RejectsAConflictingFamily()
    {
        VulkanImageEntryStateContract.Compare(
                CreateState(queueFamily: 2),
                CreateState(queueFamily: 1))
            .ShouldBe(EVulkanPrimaryEntryStateMismatch.QueueFamily);
    }

    [Test]
    public void OpenXrExternalOwnership_RejectsAReleasePendingImageAtAcquireEntry()
    {
        VulkanImageEntryStateContract.Compare(
                CreateState(
                    externalOwnership:
                        EVulkanExternalImageOwnership.OpenXrRuntimeReleasePending),
                CreateState(
                    externalOwnership:
                        EVulkanExternalImageOwnership.OpenXrRuntimeAcquired))
            .ShouldBe(EVulkanPrimaryEntryStateMismatch.ExternalOwnership);
    }

    [Test]
    public void OpenXrRecording_PublishesAcquireBeforeReuseAndReleaseBeforeEnd()
    {
        // OpenXR eyes record a fresh primary each frame. The acquire state must
        // be published before the frame loop reads the tracked target layout,
        // selects the primary owner, and freezes the recording input.
        string eyeRecording = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.OpenXR.EyeRendering.cs");
        string finalization = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Finalization.cs");
        string primaryRecording = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.cs");

        int acquire = eyeRecording.IndexOf(
            "_commandRuntime.PublishOpenXrExternalImageAcquireState(",
            StringComparison.Ordinal);
        int layoutRead = eyeRecording.IndexOf(
            "_commandRuntime.Synchronization.TryGetSubmittedImageLayout(",
            Math.Max(acquire, 0),
            StringComparison.Ordinal);
        int ownerLookup = eyeRecording.IndexOf(
            "GetOrCreateOpenXrPrimaryCommandBufferOwner(",
            Math.Max(layoutRead, 0),
            StringComparison.Ordinal);
        int freshRecording = eyeRecording.IndexOf(
            "AllowArtifactReuse: false,",
            Math.Max(ownerLookup, 0),
            StringComparison.Ordinal);
        acquire.ShouldBeGreaterThanOrEqualTo(0);
        layoutRead.ShouldBeGreaterThan(acquire);
        ownerLookup.ShouldBeGreaterThan(layoutRead);
        freshRecording.ShouldBeGreaterThan(ownerLookup);

        // The release-pending state is recorded during finalization, which runs
        // before the primary command buffer ends.
        int finalizationStart = finalization.IndexOf(
            "private unsafe void FinalizePrimaryCommandRecording(",
            StringComparison.Ordinal);
        int release = finalization.IndexOf(
            "RecordOpenXrExternalImageReleasePending(",
            Math.Max(finalizationStart, 0),
            StringComparison.Ordinal);
        finalizationStart.ShouldBeGreaterThanOrEqualTo(0);
        release.ShouldBeGreaterThan(finalizationStart);
        int finalize = primaryRecording.IndexOf(
            "FinalizePrimaryCommandRecording(ref recordingState);",
            StringComparison.Ordinal);
        int end = primaryRecording.IndexOf(
            "EndPrimaryCommandBuffer(ref recordingState)",
            Math.Max(finalize, 0),
            StringComparison.Ordinal);
        finalize.ShouldBeGreaterThanOrEqualTo(0);
        end.ShouldBeGreaterThan(finalize);
    }

    [Test]
    public void OpenXrRecording_SeedsUntouchedRuntimeImageStateAndAbandonsFailedRecordings()
    {
        // The release-pending state seeds from the recorded or inherited entry
        // state of an untouched runtime image.
        string synchronization = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs");
        string tracking = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Tracking/VulkanRenderer.CommandBufferTrackingBatch.cs");
        string resourceRuntime = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");
        string primaryRecording = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.cs");
        string eyeRecording = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanOpenXrCommandRecordingService.cs");
        string eyeOwners = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.OpenXrMirrorPreview.cs");

        synchronization.ShouldContain(
            "TryGetRecordedImageAccessState(\n                commandBuffer,\n                image,\n                in range,");
        synchronization.ShouldContain(
            "entryState.Layout != ImageLayout.Undefined");
        synchronization.ShouldContain(
            "EVulkanExternalImageOwnership.OpenXrRuntimeReleasePending");
        tracking.ShouldContain(
            "private bool TryAbandonCommandBufferRecording(CommandBuffer commandBuffer)");
        tracking.ShouldContain(
            "ResourceRuntime.AbandonCommandBufferRecording(commandBuffer);");
        resourceRuntime.ShouldContain(
            "lifetime.FrameDataLease.AbandonRecording();");

        // The shared primary recorder abandons a failed eye recording. The eye
        // recording service marks an unpublished eye owner dirty.
        primaryRecording.ShouldContain(
            "_ = TryAbandonCommandBufferRecording(recordingState.CommandBuffer);");
        eyeRecording.ShouldContain(
            "commandRuntime.MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(");
        eyeOwners.ShouldContain("owner.Dirty = true;");
    }

    [Test]
    public void ImageJournalPublication_RequiresAcceptedSubmissionAndCurrentImageGeneration()
    {
        // The command runtime owns tracked submission and the image journal.
        string trackedSubmission = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs");
        string imageLayouts = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.ImageLayouts.cs");
        string submit = SliceMethod(
            trackedSubmission,
            "internal VulkanSubmissionReceipt SubmitToQueueTrackedWithDisposition(",
            "private static string GetSealedSubmissionFallbackLogKey(");
        string publication = SliceMethod(
            trackedSubmission,
            "private unsafe void PublishRecordedImageLayouts(",
            "private void PublishSealedImageLayouts(");
        string clear = SliceMethod(
            imageLayouts,
            "internal void ClearTrackedImageLayouts(Image image)",
            "internal int ReconcileResourcePlannerImageLayouts(");
        string clearNoLock = SliceMethod(
            imageLayouts,
            "private void ClearTrackedImageLayoutsNoLock(ulong imageHandle)",
            "private void RetireTrackedImageSlotsNoLock(");
        string swapchain = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Output/Authority/VulkanDesktopSwapchainService.cs");
        string platformWindowOutput = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Output/Authority/VulkanImGuiPlatformWindowOutputAuthority.cs");

        // A rejected submit returns before the journal is published.
        int acceptedSubmission = submit.IndexOf(
            "submissionAccepted = result == Result.Success;",
            StringComparison.Ordinal);
        int rejectedSubmission = submit.IndexOf(
            "if (!submissionAccepted)",
            Math.Max(acceptedSubmission, 0),
            StringComparison.Ordinal);
        int rejectedReturn = submit.IndexOf(
            "return VulkanSubmissionReceipt.Rejected(",
            Math.Max(rejectedSubmission, 0),
            StringComparison.Ordinal);
        int publicationCall = submit.IndexOf(
            "PublishRecordedImageLayouts(",
            Math.Max(rejectedReturn, 0),
            StringComparison.Ordinal);
        acceptedSubmission.ShouldBeGreaterThanOrEqualTo(0);
        rejectedSubmission.ShouldBeGreaterThan(acceptedSubmission);
        rejectedReturn.ShouldBeGreaterThan(rejectedSubmission);
        publicationCall.ShouldBeGreaterThan(rejectedReturn);

        int generationRead = publication.IndexOf(
            "ResourceRuntime.GetPublishedGeneration(ObjectType.Image, pair.Key.ImageHandle)",
            StringComparison.Ordinal);
        int generationMismatch = publication.IndexOf(
            "generation != pair.Value.ResourceGeneration",
            Math.Max(generationRead, 0),
            StringComparison.Ordinal);
        int submittedStateWrite = publication.IndexOf(
            "state.Submitted = publishedState;",
            Math.Max(generationMismatch, 0),
            StringComparison.Ordinal);
        generationRead.ShouldBeGreaterThanOrEqualTo(0);
        generationMismatch.ShouldBeGreaterThan(generationRead);
        submittedStateWrite.ShouldBeGreaterThan(generationMismatch);

        clear.ShouldContain("ClearTrackedImageLayoutsNoLock(imageHandle);");
        clearNoLock.ShouldContain(
            "RemoveImageKeys(Synchronization._trackedImageSubresourceStates, imageHandle);");
        clearNoLock.ShouldContain(
            "RemoveImageKeys(recorded.Subresources, imageHandle);");
        // Desktop swapchain replacement detaches the exact image generations, so
        // the journal rejects a reused handle by generation. Detached platform
        // windows clear their image journal entries directly.
        swapchain.ShouldContain(
            "_resources.DetachExternalImageLifetimesForHandleReuse(oldImages);");
        platformWindowOutput.ShouldContain(
            "commandRuntime.ClearTrackedImageLayouts(images[index]);");
    }

    [Test]
    public void DynamicUiSecondary_DefersBeforeResetAndAbandonsFailedRecordings()
    {
        string method = SliceDynamicUiSecondaryRecording();
        int prewarm = method.IndexOf(
            "TryPrewarmGraphicsPipelinesForRecording(",
            StringComparison.Ordinal);
        int reset = method.IndexOf(
            "ResetVulkanCommandBufferTracked(secondaryCommandBuffer)",
            StringComparison.Ordinal);
        int begin = method.IndexOf(
            "BeginTrackedCommandBuffer(\n                        secondaryCommandBuffer,",
            StringComparison.Ordinal);

        prewarm.ShouldBeGreaterThanOrEqualTo(0);
        reset.ShouldBeGreaterThan(prewarm);
        begin.ShouldBeGreaterThan(reset);
        method.ShouldContain("variant.DynamicUiSecondaryRecorded = false;");
        method.ShouldContain("TryAbandonCommandBufferRecording(secondaryCommandBuffer);");
    }

    [Test]
    public void DynamicUiOverlay_ReleasesPreviousSecondaryReferenceBeforeRerecording()
    {
        // The delayed overlay primary is the parent of the dynamic UI secondary.
        // The secondary recorder resets that parent through the lifetime-aware
        // reset, which releases its recorded references, before it resets and
        // rerecords the secondary.
        string method = SliceDynamicUiSecondaryRecording();
        string commandRuntime = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.cs");

        int overlayParent = method.IndexOf(
            "parentCommandBuffer = overlays[imageIndex];",
            StringComparison.Ordinal);
        int resetParent = method.IndexOf(
            "TryResetCommandBufferWithLifetime(\n                        parentCommandBuffer,",
            Math.Max(overlayParent, 0),
            StringComparison.Ordinal);
        int resetSecondary = method.IndexOf(
            "ResetVulkanCommandBufferTracked(secondaryCommandBuffer)",
            Math.Max(resetParent, 0),
            StringComparison.Ordinal);
        overlayParent.ShouldBeGreaterThanOrEqualTo(0);
        resetParent.ShouldBeGreaterThan(overlayParent);
        resetSecondary.ShouldBeGreaterThan(resetParent);

        string lifetimeReset = SliceMethod(
            commandRuntime,
            "private bool TryResetCommandBufferWithLifetime(",
            "internal unsafe void DrainRetiredCommandBuffers(");
        int completeReset = lifetimeReset.IndexOf(
            "ResourceRuntime.CompleteCommandBufferReset(handle);",
            StringComparison.Ordinal);
        int clearState = lifetimeReset.IndexOf(
            "ClearCommandBufferStateAfterSuccessfulReset(commandBuffer);",
            Math.Max(completeReset, 0),
            StringComparison.Ordinal);
        completeReset.ShouldBeGreaterThanOrEqualTo(0);
        clearState.ShouldBeGreaterThan(completeReset);
        SliceMethod(
                commandRuntime,
                "private void ClearCommandBufferStateAfterSuccessfulReset(CommandBuffer commandBuffer)",
                "internal void DeferSecondaryCommandBufferFree(")
            .ShouldContain("CommandBuffers.ClearBindStateAfterSuccessfulReset(commandBuffer);");
    }


    [Test]
    public void DescriptorLayoutContract_IsNotIncidental()
    {
        VulkanImageEntryStateContract.Compare(
                CreateState(descriptorLayout: ImageLayout.General),
                CreateState())
            .ShouldBe(EVulkanPrimaryEntryStateMismatch.DescriptorLayout);
    }

    [Test]
    public void SecondaryMergeAndReuse_KeepTypedUnknownAndConflictReasons()
    {
        // Entry-state classification lives in the native recording services and
        // the command-chain snapshot check, beside the overlay merge and publication.
        string source = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "secondaryState.EntryStateFailure",
            "PublishRecordedImageLayouts(",
            "private bool TryGetRecordedImageEntryStateMismatch(",
            "private bool HasCompleteCommandChainImageEntrySnapshot(");

        source.ShouldContain("secondaryState.EntryStateFailure");
        source.ShouldContain("EVulkanPrimaryEntryStateMismatch.IncompleteSnapshot");
        source.ShouldContain("VulkanImageEntryStateContract.Compare(");
        source.ShouldContain("TryGetRecordedImageEntryStateMismatch(");
        source.ShouldContain("EVulkanPrimaryEntryStateMismatch.MissingSubmittedState");
        source.ShouldContain("HasCompleteCommandChainImageEntrySnapshot(");
        // Receipt-based publication adds a containment scope; preserve the
        // semantic requirement without coupling this reuse contract to it.
        source.ShouldContain("PublishRecordedImageLayouts(");
    }

    [Test]
    public void FramebufferFinalState_DerivesMasksFromThePublishedFinalLayout()
    {
        string source = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "private void RecordFboAttachmentAccessState(");
        string method = SliceMethod(
            source,
            "private void RecordFboAttachmentAccessState(",
            "private unsafe void EmitPlannedResourceBarrierBatch(");

        method.ShouldContain("ImageLayout accessLayout = layout;");
        method.ShouldNotContain(
            "ImageLayout accessLayout = signature.ReferenceLayout != ImageLayout.Undefined");
        method.ShouldContain(
            "RecordImageAccess(\n                    commandBuffer,\n                    viewInfo.Image,\n                    range,\n                    layout,\n                    stageMask,\n                    accessMask,");
    }

    [Test]
    public void RecordedShaderReadLayout_CannotRetainAttachmentWriteMasks()
    {
        VulkanImageAccessState state =
        VulkanCommandSynchronizationState.ResolveRecordedVulkanImageAccessState(
                ImageLayout.ShaderReadOnlyOptimal,
                ImageAspectFlags.ColorBit,
                PipelineStageFlags.ColorAttachmentOutputBit,
                AccessFlags.ColorAttachmentReadBit |
                    AccessFlags.ColorAttachmentWriteBit,
                Vk.QueueFamilyIgnored,
                serial: 3,
                resourceGeneration: 4);

        state.Layout.ShouldBe(ImageLayout.ShaderReadOnlyOptimal);
        state.StageMask.ShouldBe(
            PipelineStageFlags2.VertexShaderBit |
            PipelineStageFlags2.FragmentShaderBit |
            PipelineStageFlags2.ComputeShaderBit);
        state.AccessMask.ShouldBe(AccessFlags2.ShaderReadBit);
        state.ExpectedDescriptorLayout.ShouldBe(
            ImageLayout.ShaderReadOnlyOptimal);
    }

    [Test]
    public void RecordedShaderReadLayout_PreservesACompatibleFragmentReadScope()
    {
        VulkanImageAccessState state =
        VulkanCommandSynchronizationState.ResolveRecordedVulkanImageAccessState(
                ImageLayout.ShaderReadOnlyOptimal,
                ImageAspectFlags.ColorBit,
                PipelineStageFlags.FragmentShaderBit,
                AccessFlags.ShaderReadBit,
                Vk.QueueFamilyIgnored,
                serial: 3,
                resourceGeneration: 4);

        state.StageMask.ShouldBe(PipelineStageFlags2.FragmentShaderBit);
        state.AccessMask.ShouldBe(AccessFlags2.ShaderReadBit);
    }

    [Test]
    public void RecordedGeneralLayout_PreservesItsExplicitAccessDomain()
    {
        VulkanImageAccessState state =
        VulkanCommandSynchronizationState.ResolveRecordedVulkanImageAccessState(
                ImageLayout.General,
                ImageAspectFlags.ColorBit,
                PipelineStageFlags.TransferBit,
                AccessFlags.TransferWriteBit,
                Vk.QueueFamilyIgnored,
                serial: 3,
                resourceGeneration: 4);

        state.StageMask.ShouldBe(PipelineStageFlags2.TransferBit);
        state.AccessMask.ShouldBe(AccessFlags2.TransferWriteBit);
    }

    [Test]
    public void OpenXrPrimaryReuse_DefaultsToTheValidatedProductionPolicy()
    {
        string source = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "OpenXrVulkanPrimaryReuseOverride ?? true");

        source.ShouldContain("OpenXrVulkanPrimaryReuseOverride ?? true");
        source.ShouldContain("VulkanPrimaryCommandBufferReuseEnabled &&");
        source.ShouldNotContain(
            "string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanPrimaryReuse), \"1\"");
    }

    private static VulkanImageAccessState CreateState(
        ImageLayout layout = ImageLayout.ShaderReadOnlyOptimal,
        PipelineStageFlags2 stages = PipelineStageFlags2.FragmentShaderBit,
        AccessFlags2 access = AccessFlags2.ShaderReadBit,
        uint queueFamily = 0,
        ImageLayout descriptorLayout = ImageLayout.ShaderReadOnlyOptimal,
        ulong serial = 1,
        ulong resourceGeneration = 1,
        EVulkanExternalImageOwnership externalOwnership =
            EVulkanExternalImageOwnership.EngineOwned)
        => new(
            layout,
            stages,
            access,
            queueFamily,
            descriptorLayout,
            serial,
            resourceGeneration,
            externalOwnership);

    private static string SliceDynamicUiSecondaryRecording()
        => SliceMethod(
            SourceContractWorkspace.ReadFile(
                "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/VulkanRenderer.SecondaryCommandBuffers.cs"),
            "private unsafe bool RecordDynamicUiBatchTextSecondaryCommandBuffer(",
            "internal static ulong ComputeCommandChainUniformSlotSignature(");

    private static string SliceMethod(
        string source,
        string startMarker,
        string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        return source[start..end];
    }
}
