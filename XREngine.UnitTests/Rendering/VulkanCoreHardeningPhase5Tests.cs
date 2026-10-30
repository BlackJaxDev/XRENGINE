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
        => ((int)VulkanCommandSynchronizationState.ResolveVulkanImageAccessIntent(layout, aspect)).ShouldBe(expected);

    [Test]
    public void AccessStateMapping_ProvidesReviewedSync2AndDescriptorState()
    {
        VulkanImageAccessState sampled = VulkanCommandSynchronizationState.ResolveVulkanImageAccessState(
            ImageLayout.ShaderReadOnlyOptimal,
            ImageAspectFlags.ColorBit,
            queueFamilyIndex: 3,
            serial: 17);
        VulkanImageAccessState depth = VulkanCommandSynchronizationState.ResolveVulkanImageAccessState(
            ImageLayout.DepthStencilReadOnlyOptimal,
            ImageAspectFlags.DepthBit);
        VulkanImageAccessState transfer = VulkanCommandSynchronizationState.ResolveVulkanImageAccessState(
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
        string imageLayouts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.ImageLayouts.cs");
        string submission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs");
        string recording = SliceBetween(
            imageLayouts,
            "private long FlushImageAccessBatch(",
            "private long EnterImageLayoutLockMeasured()");
        string submit = SliceBetween(
            submission,
            "internal VulkanSubmissionReceipt SubmitToQueueTrackedWithDisposition(",
            "private static string GetSealedSubmissionFallbackLogKey(");

        recording.ShouldContain("_recordedImageLayoutsByCommandBuffer");
        recording.ShouldContain("RecordImageAspectState(recorded");
        recording.ShouldNotContain("_trackedImageSubresourceStates[");
        submit.ShouldContain("if (submissionAccepted)");
        submit.ShouldContain("PublishRecordedImageLayouts(");
        submit.IndexOf("if (submissionAccepted)", StringComparison.Ordinal)
            .ShouldBeLessThan(submit.IndexOf("PublishRecordedImageLayouts", StringComparison.Ordinal));
    }

    [Test]
    public void SubmittedAndCompletedLayouts_UseResourceLifetimeCompletionWatermarks()
    {
        string subresourceState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Synchronization/VulkanRenderer.VulkanImageSubresourceState.cs");
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandSynchronizationState.cs");
        string submission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs");
        const string advance = "Synchronization.AdvanceCompletedImageLayouts(ResourceRuntime.Lifetime.Tracker);";

        subresourceState.ShouldContain("public VulkanImageAccessState Submitted");
        subresourceState.ShouldContain("public VulkanImageAccessState Completed");
        synchronization.ShouldContain("state.Completed = state.Submitted");
        synchronization.ShouldContain("state.GraphicsSequence <= completedGraphics");
        // Each completion source first advances the lifetime watermark and
        // then promotes the submitted image layouts that it completed.
        SliceBetween(submission, "internal void CompleteTrackedFence(Fence fence)", "internal void CompleteTrackedTimeline(")
            .ShouldContain("ResourceRuntime.CompleteSynchronousFence(fence);\n        " + advance);
        SliceBetween(submission, "internal void CompleteTrackedTimeline(Semaphore semaphore, ulong value)", "internal void CompleteTrackedQueue(")
            .ShouldContain("ResourceRuntime.CompleteTimeline(semaphore, value);\n        " + advance);
        SliceBetween(submission, "internal void CompleteTrackedQueue(Queue queue)", "internal void CompleteTrackedDevice(")
            .ShouldContain("ResourceRuntime.CompleteQueue(queue);\n        " + advance);
        SliceBetween(submission, "internal void CompleteTrackedDevice()", "internal void MarkTrackedDeviceLost(")
            .ShouldContain("ResourceRuntime.CompleteDevice();\n        " + advance);
    }

    [Test]
    public void QueueOwnershipAndSecondaryRecording_ArePartOfLayoutState()
    {
        string accessState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Synchronization/VulkanRenderer.VulkanImageAccessState.cs");
        string encoder = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanTrackedCommandEncoder.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs");
        string merge = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.DynamicUiOverlaySynchronization.cs");
        string executeCommands = SliceBetween(
            recording,
            "internal unsafe void CmdExecuteCommandsTracked(",
            "private unsafe void CmdBeginRenderPassTracked(");

        accessState.ShouldContain("uint QueueFamilyIndex");
        encoder.ShouldContain("QueueFamilyIndex = barrier.DstQueueFamilyIndex,");
        recording.ShouldContain("QueueFamilyIndex = barrier.DstQueueFamilyIndex,");
        // A secondary is never submitted, so the primary that executes it
        // takes its final image states and queue ownership transfers.
        executeCommands.ShouldContain("MergeSecondaryImageStatesForExecution(\n            primary,\n            secondaries,");
        executeCommands.IndexOf("MergeSecondaryImageStatesForExecution(", StringComparison.Ordinal)
            .ShouldBeLessThan(executeCommands.IndexOf("Api.CmdExecuteCommands(", StringComparison.Ordinal));
        merge.ShouldContain("MergeSecondaryImageState(primaryState, secondaryState, primaryBatch);");
        merge.ShouldContain("primaryState.QueueOwnershipTransfers.AddRange(\n            secondaryState.QueueOwnershipTransfers);");
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
        string subresourceState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Synchronization/VulkanRenderer.VulkanImageSubresourceState.cs");
        string submission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs");
        string tracking = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Tracking/VulkanRenderer.CommandBufferTrackingBatch.cs");
        string upload = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.TextureUploadTransfer.cs");
        string publish = SliceFrom(
            submission,
            "private unsafe void PublishRecordedImageLayouts(");

        tracking.ShouldContain("batch.QueueOwnershipTransfers.Add(transfers[i]);");
        subresourceState.ShouldContain("VulkanPendingQueueOwnershipRelease?");
        submission.ShouldContain("bool queueOwnershipValid = ValidateQueueOwnershipTransferRequirements(");
        submission.ShouldContain("private static unsafe bool SubmissionSatisfiesQueueSemaphoreRequirement(");
        submission.ShouldContain("completedGraphicsSequence = ResourceRuntime.Lifetime.Tracker.CompletedGraphicsSequence;");
        submission.ShouldContain(
            "EVulkanLifetimeQueueDomain.Graphics => submission.QueueSequence <= completedGraphicsSequence,");
        // A release keeps the source queue family and stays pending until a
        // validated acquire publishes the destination family.
        publish.ShouldContain("QueueFamilyIndex = ownershipRequirement.SourceQueueFamilyIndex,");
        publish.ShouldContain("state.PendingQueueOwnershipRelease = new VulkanPendingQueueOwnershipRelease(");
        publish.ShouldContain("state.PendingQueueOwnershipRelease = null;");
        upload.ShouldContain(
            "OldLayout = ImageLayout.TransferDstOptimal,\n                NewLayout = upload.FinalLayout,");
    }

    [Test]
    public void ImportedTextureTransferQueue_UsesConcurrentSharingAndDescriptorLifetimePins()
    {
        string uploadImage = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.ImportedUpload.cs");
        string uploadTransferSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.TextureUploadTransfer.cs");
        string uploadTransfer = SliceBetween(
            uploadTransferSource,
            "private unsafe void RecordImportedTextureTransferUpload(",
            "private static long CalculateUploadStagingBytes(");
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorManager.cs");
        string removeDescriptorSet = SliceBetween(
            descriptors,
            "internal static void RemoveDescriptorSetLifetimeNoLock(",
            "internal static void PublishSnapshotNoLock(");
        string publishSnapshot = SliceBetween(
            descriptors,
            "internal static void PublishSnapshotNoLock(",
            "private static void AddPinnedReferenceClosureNoLock(");

        uploadImage.ShouldContain("SharingMode.Concurrent");
        uploadImage.ShouldContain("QueueFamilyIndexCount = uploadQueueFamilyCount");
        // Concurrent sharing makes a queue family ownership transfer
        // unnecessary. The transfer queue moves the image directly from the
        // copy layout to its final sampled layout.
        uploadTransfer.ShouldNotContain("SrcQueueFamilyIndex = transferFamily");
        uploadTransfer.ShouldNotContain("DstQueueFamilyIndex = graphicsFamily");
        uploadTransfer.ShouldContain(
            "OldLayout = ImageLayout.TransferDstOptimal,\n                NewLayout = upload.FinalLayout,\n                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,\n                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,");
        // Descriptor sets pin the closure of their references (a view and its
        // backing image) and release those pins when the set is removed.
        descriptors.ShouldContain("private static void AddPinnedReferenceClosureNoLock(");
        descriptors.ShouldContain("pins.Add(new VulkanResourceLifetimeKey(ObjectType.Image, image));");
        publishSnapshot.ShouldContain("AddPinnedReferenceClosureNoLock(");
        publishSnapshot.ShouldContain("UpdateGenerationPinsNoLock(tracker, state, pinnedReferences);");
        descriptors.ShouldContain("resource.Pins.AddDescriptorReference();");
        removeDescriptorSet.ShouldContain("resource.Pins.ReleaseDescriptorReference();");
        removeDescriptorSet.ShouldContain("state.PinnedReferences.Clear();");
    }

    [Test]
    public void ResourceRetirement_FencesLocalRecordingAdmissionBeforeDependencyPublication()
    {
        string tracker = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Lifetime/VulkanResourceLifetimeTracker.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.LifetimeLedger.cs");

        tracker.ShouldContain("FenceResourceRecordingAdmission");
        tracker.ShouldContain("SetPublishedGenerationNoLock(key, 0UL);");
        tracker.ShouldContain("TryFenceResourceRecordingAdmission(");
        lifetime.ShouldContain("Lifetime.Tracker.FenceResourceRecordingAdmission(key, owner);");
        lifetime.ShouldContain("Lifetime.Tracker.TryFenceResourceRecordingAdmission(key, expectedGeneration)");
        lifetime.IndexOf("Lifetime.Tracker.FenceResourceRecordingAdmission(key, owner);", StringComparison.Ordinal)
            .ShouldBeLessThan(lifetime.IndexOf("Lifetime.PublishTrackingDependenciesBeforeRetirement(key);", StringComparison.Ordinal));
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

        string manager = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineManager.cs");
        string merge = SliceFrom(cache, "private Result MergeBackgroundPipelineCache()");
        string graphicsCreate = SliceBetween(
            cache,
            "internal Result CreateGraphicsPipelinesSynchronized(",
            "internal Result CreateComputePipelinesSynchronized(");
        string computeCreate = SliceBetween(
            cache,
            "internal Result CreateComputePipelinesSynchronized(",
            "private Lock GetVulkanPipelineCacheHostAccessLock(PipelineCache pipelineCache)");
        const string perCacheLock =
            "VulkanFrameLockScope.Enter(\n                   GetVulkanPipelineCacheHostAccessLock(pipelineCache),";

        manager.ShouldContain("internal readonly Lock _pipelineCacheHostAccessLock = new();");
        manager.ShouldContain("internal readonly Lock _backgroundPipelineCacheHostAccessLock = new();");
        // The merge reads the background cache and writes the foreground
        // cache, so it must hold both host access locks in a fixed order.
        int foregroundLock = merge.IndexOf("_pipelineCacheHostAccessLock,", StringComparison.Ordinal);
        int backgroundLock = merge.IndexOf("_backgroundPipelineCacheHostAccessLock,", StringComparison.Ordinal);
        foregroundLock.ShouldBeGreaterThanOrEqualTo(0);
        backgroundLock.ShouldBeGreaterThan(foregroundLock);
        graphicsCreate.ShouldContain(perCacheLock);
        computeCreate.ShouldContain(perCacheLock);
        graphicsCreate.IndexOf(perCacheLock, StringComparison.Ordinal)
            .ShouldBeLessThan(graphicsCreate.LastIndexOf("CreateGraphicsPipelines(", StringComparison.Ordinal));
        computeCreate.IndexOf(perCacheLock, StringComparison.Ordinal)
            .ShouldBeLessThan(computeCreate.LastIndexOf("CreateComputePipelines(", StringComparison.Ordinal));
        programGraphics.ShouldNotContain("Api!.CreateGraphicsPipelines(");
        programCompute.ShouldNotContain("Api!.CreateComputePipelines(");
    }


    [Test]
    public void DescriptorBinding_ValidatesExactViewRangeAgainstRecordedLayout()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs");
        string barrierEmission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Synchronization/VulkanRenderer.BarrierEmission.cs");
        string viewTransition = SliceBetween(
            barrierEmission,
            "private unsafe void TransitionDescriptorImageForSampling(",
            "private unsafe void TransitionDescriptorImageRangeForSampling(");
        string rangeTransition = SliceBetween(
            barrierEmission,
            "private unsafe void TransitionDescriptorImageRangeForSampling(",
            "private bool IsImageRangeAttachedToFrameBuffer(");

        // The descriptor write records the exact view and its expected layout.
        lifetime.ShouldContain(
            "VulkanDescriptorImageReference image = new(info.ImageView, info.ImageLayout, write.DescriptorType);");
        // Command recording resolves the exact view subresource range and
        // compares it with the layout that this command buffer recorded.
        viewTransition.ShouldContain(
            "TryGetDescriptorHeapImageViewCreateInfo(imageView, out ImageViewCreateInfo viewInfo)");
        viewTransition.ShouldContain("ImageSubresourceRange range = viewInfo.SubresourceRange;");
        rangeTransition.ShouldContain("TryGetRecordedImageAccessState(\n                    commandBuffer,\n                    image,\n                    range,");
        rangeTransition.ShouldContain(
            "throw new VulkanPlanPreconditionException(\"Descriptor sampling requires resolved image-view subresource counts.\");");
        rangeTransition.ShouldContain("RejectPartiallyKnownDescriptorAspects(commandBuffer, image, cell);");
        rangeTransition.ShouldContain(
            "throw new VulkanPlanPreconditionException(\"Descriptor sampling image state belongs to a retired native generation.\");");
        rangeTransition.ShouldContain("OldLayout = priorState.Layout,");
        rangeTransition.ShouldContain("NewLayout = targetLayout,");
        rangeTransition.ShouldContain("if (priorState.Layout == targetLayout)\n                return false;");
    }

    [Test]
    public void TransferReadbackAndMipmapGeneration_RestoreSampledLayouts()
    {
        string readbackLayouts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Readback/VulkanReadbackLayoutPolicy.cs");
        string pixelReadback = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Readback/VulkanCommandRuntime.PixelReadback.cs");
        string texture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.Mipmaps.cs");
        string resourceCommands = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanResourceCommandWrapperPort.cs");
        string mipmaps = SliceBetween(
            texture,
            "protected void GenerateMipmapsWithBlit()",
            "private ImageBlit CreateMipBlit");
        string mipmapCommands = SliceBetween(
            resourceCommands,
            "internal void GenerateMipmaps(",
            "internal bool TryDecompressBufferGDeflate(");

        readbackLayouts.ShouldContain("internal static ImageLayout ResolvePostTransfer(in BlitImageInfo info)");
        pixelReadback.ShouldContain("ImageLayout postTransferLayout = VulkanReadbackLayoutPolicy.ResolvePostTransfer(liveSource);");
        pixelReadback.ShouldContain("ImageLayout.TransferSrcOptimal,\n                    postTransferLayout");
        mipmaps.ShouldContain("ResourceCommandPort.GenerateMipmaps(");
        mipmapCommands.ShouldContain("barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal");
        mipmapCommands.ShouldContain("barrier.SubresourceRange.BaseMipLevel = mipLevels - 1");
        mipmaps.ShouldContain("_currentImageLayout = ImageLayout.ShaderReadOnlyOptimal");
    }

    [Test]
    public void ProbeCapture_OrdersCubemapMipsOctaEncodingAndIblConsumption()
    {
        string capture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs");
        string completion = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.Completion.cs");
        string probeIbl = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");
        string finalizeCapture = SliceFrom(
            capture,
            "public virtual ECaptureStepResult FinalizeCubemapCapture()");
        string finalizeProbe = SliceFrom(
            probeIbl,
            "public override ECaptureStepResult FinalizeCubemapCapture()");
        string captureEncoding = SliceFrom(
            completion,
            "private ECaptureStepResult CompleteCaptureEncoding()");

        // The octahedral encoding runs through the fenced capture encoding
        // step after the cubemap mip chain is generated.
        int generateMips = finalizeCapture.IndexOf("GenerateMipmapsGPU()", StringComparison.Ordinal);
        int encodeOcta = finalizeCapture.IndexOf("CompleteCaptureEncoding()", StringComparison.Ordinal);
        generateMips.ShouldBeGreaterThanOrEqualTo(0);
        encodeOcta.ShouldBeGreaterThan(generateMips);
        captureEncoding.ShouldContain("_captureEncodingProducer ??= EncodeEnvironmentToOctahedralMap");

        int baseFinalize = finalizeProbe.IndexOf("base.FinalizeCubemapCapture()", StringComparison.Ordinal);
        int synchronize = finalizeProbe.IndexOf("SynchronizeCaptureTextureWrites()", StringComparison.Ordinal);
        int generateIbl = finalizeProbe.IndexOf("CompleteIblGenerationAttempt", StringComparison.Ordinal);
        baseFinalize.ShouldBeGreaterThanOrEqualTo(0);
        synchronize.ShouldBeGreaterThan(baseFinalize);
        generateIbl.ShouldBeGreaterThan(synchronize);
    }


    private static string SliceFrom(string source, string startToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Expected start token '{startToken}'.");
        return source[start..];
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
