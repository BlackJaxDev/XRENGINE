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
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string recording = SliceBetween(
            synchronization,
            "private void RecordImageAccess(",
            "internal void ClearTrackedImageLayouts(");
        string submit = SliceBetween(
            synchronization,
            "private Result SubmitToQueueTracked(",
            "internal Result WaitForQueueIdleTracked(");

        recording.ShouldContain("_recordedImageLayoutsByCommandBuffer");
        recording.ShouldContain("RecordImageAspectState(recorded");
        recording.ShouldNotContain("_trackedImageSubresourceStates[");
        submit.ShouldContain("if (result == Result.Success)");
        submit.ShouldContain("PublishRecordedImageLayouts(");
        submit.IndexOf("if (result == Result.Success)", StringComparison.Ordinal)
            .ShouldBeLessThan(submit.IndexOf("PublishRecordedImageLayouts", StringComparison.Ordinal));
    }

    [Test]
    public void SubmittedAndCompletedLayouts_UseResourceLifetimeCompletionWatermarks()
    {
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

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
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        synchronization.ShouldContain("uint QueueFamilyIndex");
        synchronization.ShouldContain("barrier.DstQueueFamilyIndex");
        synchronization.ShouldContain("MergeRecordedImageLayoutStates(");
        lifetime.ShouldContain("MergeRecordedImageLayoutStates(primary, secondaries)");
    }

    [Test]
    public void QueueOwnershipRequirement_ClassifiesAndPairsReleaseAcquire()
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 2,
            LevelCount = 2,
            BaseArrayLayer = 1,
            LayerCount = 3,
        };
        VulkanQueueOwnershipTransferRequirement release = new(
            ImageHandle: 19,
            range,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            SourceQueueFamilyIndex: 4,
            DestinationQueueFamilyIndex: 7,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.BottomOfPipeBit,
            AccessFlags2.None,
            ResourceGeneration: 11);
        VulkanQueueOwnershipTransferRequirement acquire = release with
        {
            SourceStageMask = PipelineStageFlags2.TopOfPipeBit,
            SourceAccessMask = AccessFlags2.None,
            DestinationStageMask =
                PipelineStageFlags2.FragmentShaderBit,
            DestinationAccessMask = AccessFlags2.ShaderReadBit,
        };

        release.ResolveRole(4)
            .ShouldBe(EVulkanQueueOwnershipTransferRole.Release);
        acquire.ResolveRole(7)
            .ShouldBe(EVulkanQueueOwnershipTransferRole.Acquire);
        acquire.ResolveRole(9)
            .ShouldBe(EVulkanQueueOwnershipTransferRole.Invalid);
        release.Contains(
            19,
            mipLevel: 3,
            arrayLayer: 3,
            ImageAspectFlags.ColorBit).ShouldBeTrue();
        release.IsPairedWith(
            in acquire,
            imageHandle: 19,
            mipLevel: 2,
            arrayLayer: 1,
            ImageAspectFlags.ColorBit).ShouldBeTrue();
        release.IsPairedWith(
            acquire with
            {
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
            },
            imageHandle: 19,
            mipLevel: 2,
            arrayLayer: 1,
            ImageAspectFlags.ColorBit).ShouldBeFalse();
    }

    [Test]
    public void QueueOwnershipRequirement_CoversExactMipLayerAndSplitAspects()
    {
        VulkanQueueOwnershipTransferRequirement requirement = new(
            ImageHandle: 0xF00DUL,
            new ImageSubresourceRange
            {
                AspectMask =
                    ImageAspectFlags.DepthBit |
                    ImageAspectFlags.StencilBit,
                BaseMipLevel = 2,
                LevelCount = 3,
                BaseArrayLayer = 4,
                LayerCount = 2,
            },
            ImageLayout.Undefined,
            ImageLayout.DepthStencilAttachmentOptimal,
            SourceQueueFamilyIndex: 1,
            DestinationQueueFamilyIndex: 2,
            PipelineStageFlags2.TopOfPipeBit,
            AccessFlags2.None,
            PipelineStageFlags2.EarlyFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentWriteBit,
            ResourceGeneration: 9);

        requirement.Contains(
            0xF00DUL,
            mipLevel: 2,
            arrayLayer: 4,
            ImageAspectFlags.DepthBit).ShouldBeTrue();
        requirement.Contains(
            0xF00DUL,
            mipLevel: 4,
            arrayLayer: 5,
            ImageAspectFlags.StencilBit).ShouldBeTrue();
        requirement.Contains(
            0xF00DUL,
            mipLevel: 5,
            arrayLayer: 5,
            ImageAspectFlags.DepthBit).ShouldBeFalse();
        requirement.Contains(
            0xF00DUL,
            mipLevel: 4,
            arrayLayer: 6,
            ImageAspectFlags.StencilBit).ShouldBeFalse();
        requirement.Contains(
            0xF00DUL,
            mipLevel: 2,
            arrayLayer: 4,
            ImageAspectFlags.ColorBit).ShouldBeFalse();
    }

    [Test]
    public void QueueSemaphoreRequirement_RequiresTimelineValueAndWaitScope()
    {
        VulkanQueueSemaphoreRequirement requirement = new(
            SemaphoreHandle: 23,
            Value: 41,
            PipelineStageFlags2.FragmentShaderBit |
                PipelineStageFlags2.ComputeShaderBit,
            SourceQueueFamilyIndex: 4,
            DestinationQueueFamilyIndex: 7);

        requirement.IsSatisfiedBy(
            semaphoreHandle: 23,
            value: 41,
            PipelineStageFlags2.FragmentShaderBit |
                PipelineStageFlags2.ComputeShaderBit).ShouldBeTrue();
        requirement.IsSatisfiedBy(
            semaphoreHandle: 23,
            value: 42,
            PipelineStageFlags2.AllCommandsBit).ShouldBeTrue();
        requirement.IsSatisfiedBy(
            semaphoreHandle: 23,
            value: 40,
            PipelineStageFlags2.AllCommandsBit).ShouldBeFalse();
        requirement.IsSatisfiedBy(
            semaphoreHandle: 23,
            value: 41,
            PipelineStageFlags2.FragmentShaderBit).ShouldBeFalse();
    }

    [Test]
    public void QueueOwnershipJournal_PublishesReleasePendingUntilValidatedAcquire()
    {
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string tracking = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferTrackingBatch.cs");
        string upload = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanRenderer.TextureUploadTransfer.cs");

        tracking.ShouldContain("QueueOwnershipTransfers");
        synchronization.ShouldContain(
            "VulkanPendingQueueOwnershipRelease?");
        synchronization.ShouldContain(
            "ValidateQueueOwnershipTransferRequirements(");
        synchronization.ShouldContain(
            "SubmissionSatisfiesQueueSemaphoreRequirement(");
        synchronization.ShouldContain(
            "completedGraphicsSequence");
        synchronization.ShouldContain(
            "state.PendingQueueOwnershipRelease = null");
        synchronization.ShouldContain(
            "QueueFamilyIndex =");
        upload.ShouldContain(
            "OldLayout = ImageLayout.TransferDstOptimal");
        upload.ShouldContain(
            "NewLayout = ImageLayout.ShaderReadOnlyOptimal");
    }
    [Test]
    public void ImportedTextureTransferQueue_UsesConcurrentSharingAndDescriptorLifetimePins()
    {
        string uploadImage = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.ImportedUpload.cs");
        string uploadTransfer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanRenderer.TextureUploadTransfer.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        uploadImage.ShouldContain("SharingMode.Concurrent");
        uploadImage.ShouldContain("QueueFamilyIndexCount = uploadQueueFamilyCount");
        uploadTransfer.ShouldNotContain("SrcQueueFamilyIndex = transferFamily");
        uploadTransfer.ShouldNotContain("DstQueueFamilyIndex = graphicsFamily");
        uploadTransfer.ShouldContain("OldLayout = ImageLayout.ShaderReadOnlyOptimal");
        lifetime.ShouldContain("AddVulkanDescriptorPinnedReferenceClosure_NoLock");
        lifetime.ShouldContain("resource.Pins.AddDescriptorReference()");
        lifetime.ShouldContain("ReleaseVulkanDescriptorSetGenerationPins_NoLock(state)");
    }

    [Test]
    public void ResourceRetirement_FencesLocalRecordingAdmissionBeforeDependencyPublication()
    {
        string tracker = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Lifetime/VulkanResourceLifetimeTracker.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        tracker.ShouldContain("FenceResourceRecordingAdmission");
        tracker.ShouldContain("PublishedResourceGenerations[key] = 0");
        lifetime.ShouldContain("ulong expectedGeneration = _resourceLifetimeTracker.GetPublishedGeneration(key)");
        lifetime.ShouldContain("ulong observedGeneration = _resourceLifetimeTracker.GetPublishedGeneration(key)");
        lifetime.ShouldContain("FenceResourceRecordingAdmission(key, owner);");
        lifetime.IndexOf("FenceResourceRecordingAdmission(key, owner);", StringComparison.Ordinal)
            .ShouldBeLessThan(lifetime.IndexOf("PublishCommandBufferTrackingDependenciesBeforeResourceRetirement(key);", StringComparison.Ordinal));
    }

    [Test]
    public void PipelineCacheHostAccess_IsExternallySynchronizedPerCache()
    {
        string cache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCache.cs");
        string programGraphics = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.GraphicsPipelines.cs");
        string programCompute = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Compute.cs");

        cache.ShouldContain("private readonly Lock _pipelineCacheHostAccessLock = new();");
        cache.ShouldContain("private readonly Lock _backgroundPipelineCacheHostAccessLock = new();");
        cache.ShouldContain("lock (_pipelineCacheHostAccessLock)\n            lock (_backgroundPipelineCacheHostAccessLock)");
        cache.ShouldContain("CreateGraphicsPipelinesSynchronized");
        cache.ShouldContain("CreateComputePipelinesSynchronized");
        cache.ShouldContain("lock (GetVulkanPipelineCacheHostAccessLock(pipelineCache))");
        programGraphics.ShouldNotContain("Api!.CreateGraphicsPipelines(");
        programCompute.ShouldNotContain("Api!.CreateComputePipelines(");
    }


    [Test]
    public void DescriptorBinding_ValidatesExactViewRangeAgainstRecordedLayout()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

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
        string readbackLayouts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Readback/VulkanRenderer.ReadbackLayouts.cs");
        string pixelReadback = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Readback/VulkanRenderer.PixelReadback.cs");
        string texture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.Mipmaps.cs");
        string mipmaps = SliceBetween(
            texture,
            "protected void GenerateMipmapsWithBlit()",
            "private ImageBlit CreateMipBlit");

        readbackLayouts.ShouldContain("ResolvePostTransferReadLayout");
        pixelReadback.ShouldContain("ImageLayout.TransferSrcOptimal,\n                    postTransferLayout");
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
        => SourceContractWorkspace.ReadFile(relativePath);

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
