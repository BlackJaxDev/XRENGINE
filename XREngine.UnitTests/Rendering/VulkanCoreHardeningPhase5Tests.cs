using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCoreHardeningPhase5Tests
{
    [TestCase(ImageLayout.ColorAttachmentOptimal, ImageAspectFlags.ColorBit, 2)]
    [TestCase(ImageLayout.DepthStencilAttachmentOptimal, ImageAspectFlags.DepthBit, 3)]
    [TestCase(ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.ColorBit, 4)]
    [TestCase(ImageLayout.DepthStencilReadOnlyOptimal, ImageAspectFlags.DepthBit, 5)]
    [TestCase(ImageLayout.TransferSrcOptimal, ImageAspectFlags.ColorBit, 7)]
    [TestCase(ImageLayout.TransferDstOptimal, ImageAspectFlags.ColorBit, 8)]
    [TestCase(ImageLayout.General, ImageAspectFlags.ColorBit, 6)]
    public void AccessIntentMapping_CoversPhase5ImageUses(
        ImageLayout layout,
        ImageAspectFlags aspect,
        int expected)
        => ((int)VulkanRenderer.ResolveVulkanImageAccessIntent(layout, aspect)).ShouldBe(expected);

    [Test]
    public void AccessStateMapping_ProvidesReviewedSync2AndDescriptorState()
    {
        VulkanRenderer.VulkanImageAccessState sampled = VulkanRenderer.ResolveVulkanImageAccessState(
            ImageLayout.ShaderReadOnlyOptimal,
            ImageAspectFlags.ColorBit,
            queueFamilyIndex: 3,
            serial: 17);
        VulkanRenderer.VulkanImageAccessState depth = VulkanRenderer.ResolveVulkanImageAccessState(
            ImageLayout.DepthStencilReadOnlyOptimal,
            ImageAspectFlags.DepthBit);
        VulkanRenderer.VulkanImageAccessState transfer = VulkanRenderer.ResolveVulkanImageAccessState(
            ImageLayout.TransferDstOptimal,
            ImageAspectFlags.ColorBit);

        sampled.AccessMask.ShouldBe((AccessFlags2)(ulong)AccessFlags.ShaderReadBit);
        sampled.ExpectedDescriptorLayout.ShouldBe(ImageLayout.ShaderReadOnlyOptimal);
        sampled.QueueFamilyIndex.ShouldBe(3u);
        sampled.Serial.ShouldBe(17UL);
        depth.ExpectedDescriptorLayout.ShouldBe(ImageLayout.DepthStencilReadOnlyOptimal);
        transfer.ExpectedDescriptorLayout.ShouldBe(ImageLayout.Undefined);
        ((ulong)transfer.AccessMask & (ulong)AccessFlags.TransferWriteBit).ShouldNotBe(0UL);
    }

    [Test]
    public void RecordedLayouts_AreCommandBufferLocalUntilSuccessfulSubmission()
    {
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string recording = SliceBetween(
            synchronization,
            "private void RecordImageAccess(",
            "private void ClearTrackedImageLayouts(");
        string submit = SliceBetween(
            synchronization,
            "private Result SubmitToQueueTracked(",
            "internal Result WaitForQueueIdleTracked(");

        recording.ShouldContain("_recordedImageLayoutsByCommandBuffer");
        recording.ShouldContain("RecordImageAspectState(recorded");
        recording.ShouldNotContain("_trackedImageSubresourceStates[");
        submit.ShouldContain("if (result == Result.Success)");
        submit.ShouldContain("PublishRecordedImageLayouts(ref submitInfo, lifetimeSubmission)");
        submit.IndexOf("if (result == Result.Success)", StringComparison.Ordinal)
            .ShouldBeLessThan(submit.IndexOf("PublishRecordedImageLayouts", StringComparison.Ordinal));
    }

    [Test]
    public void SubmittedAndCompletedLayouts_UseResourceLifetimeCompletionWatermarks()
    {
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        synchronization.ShouldContain("public VulkanImageAccessState Submitted");
        synchronization.ShouldContain("public VulkanImageAccessState Completed");
        synchronization.ShouldContain("state.Completed = state.Submitted");
        synchronization.ShouldContain("state.GraphicsSequence <= completedGraphics");
        lifetime.ShouldContain("NotifyVulkanFenceCompleted");
        lifetime.ShouldContain("NotifyVulkanTimelineCompleted");
        lifetime.ShouldContain("NotifyVulkanQueueIdle");
        lifetime.ShouldContain("NotifyVulkanDeviceIdle");
        lifetime.ShouldContain("AdvanceCompletedImageLayouts();");
    }

    [Test]
    public void QueueOwnershipAndSecondaryRecording_ArePartOfLayoutState()
    {
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        synchronization.ShouldContain("uint QueueFamilyIndex");
        synchronization.ShouldContain("barrier.DstQueueFamilyIndex");
        synchronization.ShouldContain("MergeRecordedImageLayoutStates(");
        lifetime.ShouldContain("MergeRecordedImageLayoutStates(primary, secondaries)");
    }

    [Test]
    public void DescriptorBinding_ValidatesExactViewRangeAgainstRecordedLayout()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        lifetime.ShouldContain("VulkanDescriptorImageReference");
        lifetime.ShouldContain("TryGetDescriptorHeapImageViewCreateInfo(reference.View");
        lifetime.ShouldContain("TryGetRecordedImageLayout(commandBuffer, viewInfo.Image, range");
        lifetime.ShouldContain("Vulkan descriptor image layout mismatch at command recording");
        lifetime.ShouldContain("ImageLayout.TransferSrcOptimal");
        lifetime.ShouldContain("ImageLayout.TransferDstOptimal");
        lifetime.ShouldContain("System.Diagnostics.Debug.Fail(message)");
    }

    [Test]
    public void TransferReadbackAndMipmapGeneration_RestoreSampledLayouts()
    {
        string blit = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");
        string texture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string mipmaps = SliceBetween(
            texture,
            "protected void GenerateMipmapsWithBlit()",
            "private ImageBlit CreateMipBlit");

        blit.ShouldContain("ResolvePostTransferReadLayout");
        blit.ShouldContain("ImageLayout.TransferSrcOptimal,\n                    postTransferLayout");
        mipmaps.ShouldContain("barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal");
        mipmaps.ShouldContain("barrier.SubresourceRange.BaseMipLevel = ResolvedMipLevels - 1");
        mipmaps.ShouldContain("_currentImageLayout = ImageLayout.ShaderReadOnlyOptimal");
    }

    [Test]
    public void ProbeCapture_OrdersCubemapMipsOctaEncodingAndIblConsumption()
    {
        string capture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs");
        string probeIbl = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");
        string finalizeCapture = capture[capture.IndexOf(
            "public virtual void FinalizeCubemapCapture()",
            StringComparison.Ordinal)..];
        string finalizeProbe = probeIbl[probeIbl.IndexOf(
            "public override void FinalizeCubemapCapture()",
            StringComparison.Ordinal)..];

        int generateMips = finalizeCapture.IndexOf("GenerateMipmapsGPU()", StringComparison.Ordinal);
        int encodeOcta = finalizeCapture.IndexOf("EncodeEnvironmentToOctahedralMap()", StringComparison.Ordinal);
        generateMips.ShouldBeGreaterThanOrEqualTo(0);
        encodeOcta.ShouldBeGreaterThan(generateMips);

        int baseFinalize = finalizeProbe.IndexOf("base.FinalizeCubemapCapture()", StringComparison.Ordinal);
        int synchronize = finalizeProbe.IndexOf("SynchronizeCaptureTextureWrites()", StringComparison.Ordinal);
        int generateIbl = finalizeProbe.IndexOf("CompleteIblGenerationAttempt", StringComparison.Ordinal);
        baseFinalize.ShouldBeGreaterThanOrEqualTo(0);
        synchronize.ShouldBeGreaterThan(baseFinalize);
        generateIbl.ShouldBeGreaterThan(synchronize);
    }

    private static string SliceBetween(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Expected start token '{startToken}'.");
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Expected end token '{endToken}'.");
        return source[start..end];
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(
            ResolveRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{relativePath}'.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
