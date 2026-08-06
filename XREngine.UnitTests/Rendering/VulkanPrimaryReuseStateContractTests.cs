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
        VulkanRenderer.VulkanImageAccessState expected = CreateState(serial: 7);
        VulkanRenderer.VulkanImageAccessState actual = CreateState(serial: 99);

        VulkanImageEntryStateContract.Compare(actual, expected)
            .ShouldBe(EVulkanPrimaryEntryStateMismatch.None);
    }

    [Test]
    public void BroaderRecordedSourceMasks_CoverTheActualEntryState()
    {
        VulkanRenderer.VulkanImageAccessState expected = CreateState(
            stages: PipelineStageFlags2.VertexShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            access: AccessFlags2.ShaderReadBit |
                AccessFlags2.MemoryReadBit);
        VulkanRenderer.VulkanImageAccessState actual = CreateState(
            stages: PipelineStageFlags2.FragmentShaderBit,
            access: AccessFlags2.ShaderReadBit);

        VulkanImageEntryStateContract.Compare(actual, expected)
            .ShouldBe(EVulkanPrimaryEntryStateMismatch.None);
    }

    [Test]
    public void NarrowRecordedStageMask_RejectsReuse()
    {
        VulkanRenderer.VulkanImageAccessState expected = CreateState(
            stages: PipelineStageFlags2.FragmentShaderBit);
        VulkanRenderer.VulkanImageAccessState actual = CreateState(
            stages: PipelineStageFlags2.ComputeShaderBit);

        VulkanImageEntryStateContract.Compare(actual, expected)
            .ShouldBe(EVulkanPrimaryEntryStateMismatch.StageMask);
    }

    [Test]
    public void NarrowRecordedAccessMask_RejectsReuse()
    {
        VulkanRenderer.VulkanImageAccessState expected = CreateState(
            access: AccessFlags2.ShaderReadBit);
        VulkanRenderer.VulkanImageAccessState actual = CreateState(
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
        string eyeRecording = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.EyeRendering.cs");
        string primaryRecording = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        int acquire = eyeRecording.IndexOf(
            "PublishOpenXrExternalImageAcquireState(",
            StringComparison.Ordinal);
        int reuse = eyeRecording.IndexOf(
            "TryReuseOpenXrPrimaryCommandBuffer(",
            acquire,
            StringComparison.Ordinal);
        acquire.ShouldBeGreaterThanOrEqualTo(0);
        reuse.ShouldBeGreaterThan(acquire);

        int release = primaryRecording.IndexOf(
            "RecordOpenXrExternalImageReleasePending(",
            StringComparison.Ordinal);
        int end = primaryRecording.IndexOf(
            "_commandRecorder.End(",
            release,
            StringComparison.Ordinal);
        release.ShouldBeGreaterThanOrEqualTo(0);
        end.ShouldBeGreaterThan(release);
    }

    [Test]
    public void OpenXrRecording_SeedsUntouchedRuntimeImageStateAndAbandonsFailedRecordings()
    {
        string synchronization = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string tracking = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferTrackingBatch.cs");
        string eyeRecording = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.EyeRendering.cs");

        synchronization.ShouldContain(
            "TryGetRecordedImageAccessState(\n                commandBuffer,\n                image,\n                range,");
        synchronization.ShouldContain(
            "entryState.Layout != ImageLayout.Undefined");
        synchronization.ShouldContain(
            "EVulkanExternalImageOwnership.OpenXrRuntimeReleasePending");
        tracking.ShouldContain(
            "private bool TryAbandonCommandBufferRecording(CommandBuffer commandBuffer)");
        tracking.ShouldContain(
            "lifetime.FrameDataLease.AbandonRecording();");
        eyeRecording.ShouldContain(
            "_ = TryAbandonCommandBufferRecording(variant.PrimaryCommandBuffer);");
        eyeRecording.ShouldContain("variant.Dirty = true;");
    }

    [Test]
    public void ImageJournalPublication_RequiresAcceptedSubmissionAndCurrentImageGeneration()
    {
        string synchronization = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string submit = SliceMethod(
            synchronization,
            "private VulkanSubmissionReceipt SubmitToQueueTrackedCore(",
            "internal Result WaitForQueueIdleTracked(");
        string publication = SliceMethod(
            synchronization,
            "private void PublishRecordedImageLayouts(",
            "private void AdvanceCompletedImageLayouts(");
        string clear = SliceMethod(
            synchronization,
            "internal void ClearTrackedImageLayouts(Image image)",
            "private int ClearAllTrackedImageLayouts()");
        string swapchain = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");

        int acceptedSubmission = submit.IndexOf(
            "if (submissionAccepted)",
            StringComparison.Ordinal);
        int publicationCall = submit.IndexOf(
            "PublishRecordedImageLayouts(",
            acceptedSubmission,
            StringComparison.Ordinal);
        int failedSubmission = submit.IndexOf(
            "else if (result == Result.ErrorDeviceLost)",
            publicationCall,
            StringComparison.Ordinal);
        acceptedSubmission.ShouldBeGreaterThanOrEqualTo(0);
        publicationCall.ShouldBeGreaterThan(acceptedSubmission);
        failedSubmission.ShouldBeGreaterThan(publicationCall);

        int generationRead = publication.IndexOf(
            "GetCurrentVulkanResourceGeneration(",
            StringComparison.Ordinal);
        int generationMismatch = publication.IndexOf(
            "currentGeneration != pair.Value.ResourceGeneration",
            generationRead,
            StringComparison.Ordinal);
        int submittedStateWrite = publication.IndexOf(
            "state.Submitted = publishedState;",
            generationMismatch,
            StringComparison.Ordinal);
        generationRead.ShouldBeGreaterThanOrEqualTo(0);
        generationMismatch.ShouldBeGreaterThan(generationRead);
        submittedStateWrite.ShouldBeGreaterThan(generationMismatch);

        clear.ShouldContain(
            "RemoveImageKeys(_trackedImageSubresourceStates, imageHandle);");
        clear.ShouldContain(
            "RemoveImageKeys(recorded.Subresources, imageHandle);");
        swapchain.ShouldContain(
            "ClearTrackedImageLayouts(swapChainImages[i]);");
    }

    [Test]
    public void DynamicUiSecondary_DefersBeforeResetAndAbandonsFailedRecordings()
    {
        string source = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");
        int methodStart = source.IndexOf(
            "private bool RecordDynamicUiBatchTextSecondaryCommandBuffer(",
            StringComparison.Ordinal);
        int methodEnd = source.IndexOf(
            "private bool TryRecordDynamicUiBatchTextOverlayCommandBuffer(",
            methodStart,
            StringComparison.Ordinal);
        methodStart.ShouldBeGreaterThanOrEqualTo(0);
        methodEnd.ShouldBeGreaterThan(methodStart);

        string method = source[methodStart..methodEnd];
        int prewarm = method.IndexOf(
            "TryPrewarmGraphicsPipelinesForRecording(",
            StringComparison.Ordinal);
        int reset = method.IndexOf(
            "ResetVulkanCommandBufferTracked(secondaryCommandBuffer)",
            StringComparison.Ordinal);
        int begin = method.IndexOf(
            "Api!.BeginCommandBuffer(secondaryCommandBuffer",
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
        string source = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");
        int methodStart = source.IndexOf(
            "private bool TryRecordDynamicUiBatchTextOverlayCommandBuffer(",
            StringComparison.Ordinal);
        int methodEnd = source.IndexOf(
            "private void RecordDynamicUiBatchTextStreamlineUi(",
            methodStart,
            StringComparison.Ordinal);
        methodStart.ShouldBeGreaterThanOrEqualTo(0);
        methodEnd.ShouldBeGreaterThan(methodStart);

        string method = source[methodStart..methodEnd];
        int resetPrimary = method.IndexOf(
            "ResetVulkanCommandBufferTracked(commandBuffer)",
            StringComparison.Ordinal);
        int releasePrimaryReferences = method.IndexOf(
            "ResetCommandBufferBindState(commandBuffer)",
            StringComparison.Ordinal);
        int releaseDeferredSecondaries = method.IndexOf(
            "ReleaseDeferredSecondaryCommandBuffers(imageIndex)",
            StringComparison.Ordinal);
        int rerecordSecondary = method.IndexOf(
            "RecordDynamicUiBatchTextSecondaryCommandBuffer(",
            StringComparison.Ordinal);

        resetPrimary.ShouldBeGreaterThanOrEqualTo(0);
        releasePrimaryReferences.ShouldBeGreaterThan(resetPrimary);
        releaseDeferredSecondaries.ShouldBeGreaterThan(releasePrimaryReferences);
        rerecordSecondary.ShouldBeGreaterThan(releaseDeferredSecondaries);
        method.LastIndexOf(
            "ResetCommandBufferBindState(commandBuffer)",
            StringComparison.Ordinal).ShouldBe(releasePrimaryReferences);
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
        string source = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "secondaryState.EntryStateFailure",
            "PublishRecordedImageLayouts(");

        source.ShouldContain("secondaryState.EntryStateFailure");
        source.ShouldContain("EVulkanPrimaryEntryStateMismatch.IncompleteSnapshot");
        source.ShouldContain("VulkanImageEntryStateContract.Compare(");
        source.ShouldContain("TryGetRecordedImageEntryStateMismatch(");
        source.ShouldContain("EVulkanPrimaryEntryStateMismatch.MissingSubmittedState");
        source.ShouldContain("HasCompleteRecordedImageEntrySnapshot(");
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
            "private void EmitInitialImageAspectBarriers(");

        method.ShouldContain("ImageLayout accessLayout = layout;");
        method.ShouldNotContain(
            "ImageLayout accessLayout = signature.ReferenceLayout != ImageLayout.Undefined");
        method.ShouldContain(
            "RecordImageAccess(\n                    commandBuffer,\n                    viewInfo.Image,\n                    range,\n                    layout,\n                    stageMask,\n                    accessMask,");
    }

    [Test]
    public void RecordedShaderReadLayout_CannotRetainAttachmentWriteMasks()
    {
        VulkanRenderer.VulkanImageAccessState state =
            VulkanRenderer.ResolveRecordedVulkanImageAccessState(
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
        VulkanRenderer.VulkanImageAccessState state =
            VulkanRenderer.ResolveRecordedVulkanImageAccessState(
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
        VulkanRenderer.VulkanImageAccessState state =
            VulkanRenderer.ResolveRecordedVulkanImageAccessState(
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

    private static VulkanRenderer.VulkanImageAccessState CreateState(
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
