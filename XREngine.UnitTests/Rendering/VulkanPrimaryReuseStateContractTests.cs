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
            "PublishRecordedImageLayouts(ref submitInfo, lifetimeSubmission)");

        source.ShouldContain("secondaryState.EntryStateFailure");
        source.ShouldContain("EVulkanPrimaryEntryStateMismatch.IncompleteSnapshot");
        source.ShouldContain("VulkanImageEntryStateContract.Compare(");
        source.ShouldContain("TryGetRecordedImageEntryStateMismatch(");
        source.ShouldContain("EVulkanPrimaryEntryStateMismatch.MissingSubmittedState");
        source.ShouldContain("HasCompleteRecordedImageEntrySnapshot(");
        source.ShouldContain("PublishRecordedImageLayouts(ref submitInfo, lifetimeSubmission)");
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
        ulong resourceGeneration = 1)
        => new(
            layout,
            stages,
            access,
            queueFamily,
            descriptorLayout,
            serial,
            resourceGeneration);

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
