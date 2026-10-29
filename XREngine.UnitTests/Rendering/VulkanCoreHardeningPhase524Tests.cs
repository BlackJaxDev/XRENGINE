using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCoreHardeningPhase524Tests
{
    [Test]
    public void IndirectCountSubmissionEnablesAndRequiresCoreIndirectFeatures()
    {
        string logicalDevice = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");
        string extensions = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceMutableCapabilities.cs");

        logicalDevice.ShouldContain("supportedFeatures.MultiDrawIndirect");
        logicalDevice.ShouldContain("deviceFeatures.MultiDrawIndirect = Vk.True");
        logicalDevice.ShouldContain("supportedFeatures.DrawIndirectFirstInstance");
        logicalDevice.ShouldContain("deviceFeatures.DrawIndirectFirstInstance = Vk.True");
        logicalDevice.ShouldContain("bool indirectCountCoreFeaturesReady =");
        logicalDevice.ShouldContain("_deviceContext.MutableCapabilities._supportsMultiDrawIndirect &&");
        logicalDevice.ShouldContain("_deviceContext.MutableCapabilities._supportsDrawIndirectFirstInstance;");
        logicalDevice.ShouldContain("_deviceContext.MutableCapabilities._usesCoreDrawIndirectCountCommands = true");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Recording/VulkanRenderer.DrawAndComputeRecording.cs");
        recording.ShouldContain("Api!.CmdDrawIndexedIndirectCount(");
        recording.ShouldContain("else if (DeviceContext.ExtensionFunctions.KhrDrawIndirectCount is { } ext)");
        extensions.ShouldContain("internal bool _supportsMultiDrawIndirect;");
        extensions.ShouldContain("internal bool _supportsDrawIndirectFirstInstance;");
    }

    [Test]
    public void ZeroReadbackAtlasTiersUseStableRendererInstances()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        source.ShouldContain("private readonly XRMeshRenderer?[] _zeroReadbackTierRenderers");
        source.ShouldContain("XRMeshRenderer? tierRenderer = ConfigureIndirectRendererForTier(scene, (EAtlasTier)tier);");
        source.ShouldContain("_zeroReadbackTierRenderers[tierIndex] = vaoRenderer;");
        source.ShouldContain("Name = $\"ZeroReadbackAtlasTier_{tier}\"");
        source.ShouldNotContain("ConfigureIndirectRendererForTier(scene, vaoRenderer");
    }

    [Test]
    public void DeferredComputeDispatchCapturesImmutableBufferGenerationsWithoutExtraSnapshotMaps()
    {
        string snapshot = ReadWorkspaceFile(VulkanRoot + "Commands/FrameOps/ComputeDispatchSnapshot.cs");
        string binding = ReadWorkspaceFile(VulkanRoot + "Commands/FrameOps/VulkanComputeBufferBinding.cs");
        string program = SourceContractWorkspace.ReadPartialType(
            VulkanRoot + "BackendObjects/Programs/VkRenderProgram.cs");

        snapshot.ShouldContain("Dictionary<uint, VulkanComputeBufferBinding> Buffers");
        snapshot.ShouldContain("Dictionary<string, VulkanComputeBufferBinding> BuffersByName");
        snapshot.ShouldNotContain("VulkanBuffers");
        binding.ShouldContain("XRDataBuffer Data");
        binding.ShouldContain("Buffer Buffer");
        binding.ShouldContain("ulong Range");
        program.ShouldContain("TryCaptureComputeBufferSnapshot");
        program.ShouldContain("Buffer = snapshot.Buffer");
        program.ShouldNotContain("snapshot.VulkanBuffers");
    }

    [Test]
    public void VulkanIndirectGeometryUsesRobustAccessAndRejectsInvalidAtlasIndices()
    {
        string logicalDevice = ReadWorkspaceFile(
            VulkanRoot + "Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");
        string atlas = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AtlasManagement.cs");

        logicalDevice.ShouldContain("supportedFeatures.RobustBufferAccess");
        logicalDevice.ShouldContain("deviceFeatures.RobustBufferAccess = Vk.True");
        atlas.ShouldContain("TriangleIndicesFitVertexRange");
        atlas.ShouldContain("contains a triangle index outside vertex range");
    }

    [Test]
    public void RecordingUsesCapacityBackedCommandLocalDependencyAndLayoutStorage()
    {
        string batch = ReadWorkspaceFile(TrackingBatchPath);
        string tracking = ReadWorkspaceFile(TrackingBatchServicesPath);
        string coordination = ReadWorkspaceFile(ResourceCoordinationPath);

        batch.ShouldContain("HashSet<VulkanResourceLifetimeKey> Dependencies = new(64)");
        batch.ShouldContain("List<VulkanImageAccessRangeDelta> ImageAccessDeltas = new(32)");
        batch.ShouldContain("VulkanCommandBufferImageAccessIndex LatestImageAccessStates = new(32)");
        coordination.ShouldContain("internal void TrackCommandBufferResource(");
        coordination.ShouldContain("batch.RecordDependency(resourceKey);");
        tracking.ShouldContain("private bool TryRecordImageAccessDelta(");
        tracking.ShouldContain("batch.RecordImageAccess(new VulkanImageAccessRangeDelta(image.Handle, range, resolved));");
        tracking.ShouldContain("private void FlushCommandBufferTrackingBatch(CommandBuffer commandBuffer)");
        batch.ShouldNotContain("for (int i = batch.ImageAccessDeltas.Count - 1;");
        tracking.ShouldNotContain("for (int i = batch.ImageAccessDeltas.Count - 1;");
    }

    [Test]
    public void PendingImageAccessIndex_UsesLatestStateAcrossSubresources()
    {
        VulkanCommandBufferImageAccessIndex index = new();
        const ulong imageHandle = 0x1234UL;
        ImageSubresourceRange bothLayers = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 2,
        };
        ImageSubresourceRange secondLayer = bothLayers with
        {
            BaseArrayLayer = 1,
            LayerCount = 1,
        };
        VulkanImageAccessState sampled = VulkanCommandSynchronizationState.ResolveVulkanImageAccessState(
            ImageLayout.ShaderReadOnlyOptimal,
            ImageAspectFlags.ColorBit,
            serial: 10);
        VulkanImageAccessState storage = VulkanCommandSynchronizationState.ResolveVulkanImageAccessState(
            ImageLayout.General,
            ImageAspectFlags.ColorBit,
            serial: 20);

        index.Record(imageHandle, bothLayers, sampled);
        index.TryGet(imageHandle, bothLayers, out VulkanImageAccessState initial).ShouldBeTrue();
        initial.Layout.ShouldBe(ImageLayout.ShaderReadOnlyOptimal);
        initial.Serial.ShouldBe(10UL);

        index.Record(imageHandle, secondLayer, storage);
        index.TryGet(imageHandle, secondLayer, out VulkanImageAccessState latest).ShouldBeTrue();
        latest.Layout.ShouldBe(ImageLayout.General);
        latest.Serial.ShouldBe(20UL);
        index.TryGet(imageHandle, bothLayers, out _).ShouldBeFalse();
        index.Count.ShouldBe(2);
    }

    [Test]
    public void PendingImageAccessIndex_RequiresEveryRequestedAspect()
    {
        VulkanCommandBufferImageAccessIndex index = new();
        const ulong imageHandle = 0x5678UL;
        ImageSubresourceRange depthOnly = new()
        {
            AspectMask = ImageAspectFlags.DepthBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        ImageSubresourceRange depthStencil = depthOnly with
        {
            AspectMask = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
        };
        VulkanImageAccessState depthRead = VulkanCommandSynchronizationState.ResolveVulkanImageAccessState(
            ImageLayout.DepthStencilReadOnlyOptimal,
            ImageAspectFlags.DepthBit,
            serial: 30);

        index.Record(imageHandle, depthOnly, depthRead);

        index.TryGet(imageHandle, depthOnly, out VulkanImageAccessState found).ShouldBeTrue();
        found.Layout.ShouldBe(ImageLayout.DepthStencilReadOnlyOptimal);
        index.TryGet(imageHandle, depthStencil, out _).ShouldBeFalse();
    }

    [Test]
    public void PendingImageAccessIndex_RepeatedCurrentStateAccessDoesNotAllocate()
    {
        VulkanCommandBufferImageAccessIndex index = new();
        const ulong imageHandle = 0x9ABCUL;
        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        VulkanImageAccessState sampled = VulkanCommandSynchronizationState.ResolveVulkanImageAccessState(
            ImageLayout.ShaderReadOnlyOptimal,
            ImageAspectFlags.ColorBit,
            serial: 1);
        index.Record(imageHandle, range, sampled);
        index.TryGet(imageHandle, range, out _).ShouldBeTrue();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            index.Record(imageHandle, range, sampled with { Serial = (ulong)i + 2UL });
            if (!index.TryGet(imageHandle, range, out _))
                throw new AssertionException("The indexed image state unexpectedly disappeared.");
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        allocatedBytes.ShouldBe(0L);
    }

    [Test]
    public void FramebufferRecordingReusesAttachmentAndLayoutScratchStorage()
    {
        string frameBuffer = ReadWorkspaceFile(
            VulkanRoot + "BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string scratch = ReadWorkspaceFile(
            VulkanRoot + "Commands/CommandBuffers/Recording/Scratch/VulkanRenderer.CommandBufferRecordingScratch.cs");
        string recording = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "GetFboAttachmentLayoutScratch(",
            "FboLayoutTracking.Clear()");

        frameBuffer.ShouldContain("AttachmentBuildInfo[] _attachmentBuildScratch = []");
        frameBuffer.ShouldContain("_attachmentBuildScratch.AsSpan(0, colorAttachmentCount)");
        frameBuffer.ShouldNotContain("List<AttachmentBuildInfo> colorAttachments");
        frameBuffer.ShouldNotContain("HashSet<uint> usedColorSlots");
        frameBuffer.ShouldContain("Span<bool> touchedAttachments = stackalloc bool[planned.Length]");
        frameBuffer.ShouldContain("Span<int> matchingIndices = stackalloc int[planned.Length]");
        frameBuffer.ShouldContain("TryGetFrameBufferSlot(usage.ResourceName, frameBufferName");
        frameBuffer.ShouldNotContain("HashSet<int> touchedAttachments");
        frameBuffer.ShouldNotContain("List<int> colorMatches");
        frameBuffer.ShouldNotContain("string slot = usage.ResourceName[prefix.Length..]");

        scratch.ShouldContain("Dictionary<XRFrameBuffer, FboAttachmentLayoutScratch>");
        scratch.ShouldContain("Dictionary<XRFrameBuffer, ImageLayout[]> FboLayoutTracking");
        recording.ShouldContain("GetFboAttachmentLayoutScratch(fbo, count)");
        recording.ShouldContain("recordingScratch.FboAttachmentLayouts.TryGetValue");
        recording.ShouldContain("recordingState.FboLayoutTracking = recordingState.RecordingScratch.FboLayoutTracking;");
        recording.ShouldContain("recordingState.FboLayoutTracking.Clear()");
        recording.ShouldContain("VkFrameBuffer.WriteFinalLayouts(recordingState.RenderScope.AttachmentSignature, finalLayouts)");
        recording.ShouldNotContain("ImageLayout[] layouts = new ImageLayout[count]");
    }

    [Test]
    public void HotDependencyAndBarrierPathsAvoidGlobalLocks()
    {
        string coordination = ReadWorkspaceFile(ResourceCoordinationPath);
        string encoder = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanTrackedCommandEncoder.cs");
        string barriers = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.BarrierServices.cs");

        // The dependency fast path records into the command-local batch. Only a
        // generation race with retirement takes the shared lifetime path.
        string dependencyMethod = Slice(
            coordination,
            "internal void TrackCommandBufferResource(",
            "private void LinkNativeCommandArtifactDependency(");
        dependencyMethod.ShouldContain("batch.RecordDependency(resourceKey);");
        dependencyMethod.IndexOf("batch.RecordDependency(resourceKey);", StringComparison.Ordinal)
            .ShouldBeLessThan(dependencyMethod.IndexOf("PublishCommandBufferDependencyAfterGenerationRace(", StringComparison.Ordinal));
        dependencyMethod.ShouldNotContain("Tracker.SyncRoot");

        string layoutMethod = Slice(
            barriers,
            "private void RecordImageAccess(",
            "private static VulkanImageAccessState ResolveCommandImageAccessState(");
        layoutMethod.ShouldContain("PrimaryCommandEncoder.RecordImageAccess(commandBuffer, image, in range, in next);");
        layoutMethod.ShouldNotContain("_vulkanImageLayoutLock");

        string encoderImageAccess = Slice(
            encoder,
            "internal void RecordImageAccess(",
            "internal void BindPipeline(");
        encoderImageAccess.ShouldContain("lock (batch)");
        encoderImageAccess.ShouldContain("batch.RecordImageAccess(new VulkanImageAccessRangeDelta(");
        encoderImageAccess.ShouldNotContain("_vulkanImageLayoutLock");
        encoderImageAccess.ShouldNotContain("Tracker.SyncRoot");
    }

    [Test]
    public void DescriptorReferencesPublishImmutableGenerationsAndExpandOncePerRecording()
    {
        string manager = ReadWorkspaceFile(VulkanRoot + "Descriptors/VulkanDescriptorManager.cs");
        string tracker = ReadWorkspaceFile(VulkanRoot + "Resources/Lifetime/VulkanResourceLifetimeTracker.cs");
        string submission = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs");
        string batch = ReadWorkspaceFile(TrackingBatchPath);

        manager.ShouldContain("VulkanPublishedDescriptorSetSnapshot snapshot = new(");
        manager.ShouldContain("tracker.PublishDescriptorSnapshotNoLock(");
        tracker.ShouldContain("internal void PublishDescriptorSnapshotNoLock(");
        tracker.ShouldContain("PublishedDescriptorSets[descriptorSetHandle] = snapshot;");
        batch.ShouldContain("ExpandedDescriptorGenerations");
        batch.ShouldContain("MarkDescriptorExpanded");

        // Descriptor contents can change per frame slot. The submission gateway
        // expands each published snapshot once into the command's touched list.
        string propagation = Slice(
            submission,
            "private static bool RefreshSubmittedDescriptorDependencies_NoLock(",
            "private static bool CanRestoreSealedSubmissionContractAfterRefreshNoLock(");
        propagation.ShouldContain("commandLifetime.RefreshTouchedDependencies();");
        propagation.ShouldContain("out VulkanPublishedDescriptorSetSnapshot snapshot)");
        propagation.ShouldContain("for (int referenceIndex = 0; referenceIndex < snapshot.References.Length; referenceIndex++)");
        propagation.ShouldContain("TryAppendSubmittedDescriptorDependency_NoLock(");
        propagation.ShouldNotContain("CommandBufferLifetimes");
    }

    [Test]
    public void RetirementPublishesCommandLocalDependenciesBeforeCapturingPins()
    {
        string tracking = ReadWorkspaceFile(TrackingBatchServicesPath);
        string resourceRuntime = ReadWorkspaceFile(VulkanRoot + "Resources/Authority/VulkanResourceRuntime.cs");
        string ledger = ReadWorkspaceFile(VulkanRoot + "Resources/Authority/VulkanResourceRuntime.LifetimeLedger.cs");
        string port = ReadWorkspaceFile(VulkanRoot + "Resources/Authority/VulkanRetirementDependencyPublicationPort.cs");
        string descriptors = ReadWorkspaceFile(VulkanRoot + "Descriptors/VulkanDescriptorLifetimeAuthority.cs");

        tracking.ShouldContain("internal void PublishTrackingDependenciesBeforeResourceRetirement(");
        tracking.ShouldContain("batch.Dependencies.Contains(resourceKey)");
        tracking.ShouldContain("TryFlushCommandBufferTrackingBatch(commandBuffer, out string failureReason)");
        resourceRuntime.ShouldContain("batch.Dependencies.Clear();");
        tracking.ShouldNotContain("PublishedDependencyCount");
        resourceRuntime.ShouldNotContain("PublishedDependencyCount");
        port.ShouldContain("_commandRuntime.PublishTrackingDependenciesBeforeResourceRetirement(");

        int retirementStart = ledger.IndexOf("internal VulkanRetirementTicket CaptureRetirementTicket(", StringComparison.Ordinal);
        int publication = ledger.IndexOf("Lifetime.PublishTrackingDependenciesBeforeRetirement(key);", retirementStart, StringComparison.Ordinal);
        int retirementLock = ledger.IndexOf("lock (Lifetime.Tracker.SyncRoot)", retirementStart, StringComparison.Ordinal);
        retirementStart.ShouldBeGreaterThanOrEqualTo(0);
        publication.ShouldBeGreaterThan(retirementStart);
        publication.ShouldBeLessThan(retirementLock);

        int poolRetirementStart = descriptors.IndexOf("private VulkanRetirementTicket CaptureDescriptorPoolRetirementTicket(", StringComparison.Ordinal);
        int poolSetRetirement = descriptors.IndexOf("CaptureRetirementTicket(\n                setKey,", poolRetirementStart, StringComparison.Ordinal);
        int setRetirementStart = descriptors.IndexOf("private VulkanRetirementTicket CaptureRetirementTicket(", StringComparison.Ordinal);
        int setPublication = descriptors.IndexOf("_lifetime.PublishTrackingDependenciesBeforeRetirement(key);", setRetirementStart, StringComparison.Ordinal);
        int setPendingState = descriptors.IndexOf("resource.State |= EVulkanResourceLifetimeState.PendingRetirement", setRetirementStart, StringComparison.Ordinal);
        poolSetRetirement.ShouldBeGreaterThan(poolRetirementStart);
        setPublication.ShouldBeGreaterThan(setRetirementStart);
        setPublication.ShouldBeLessThan(setPendingState);
    }

    [Test]
    public void DebugDescriptorValidationCachesDescriptorAndLayoutGenerations()
    {
        string batch = ReadWorkspaceFile(TrackingBatchPath);

        batch.ShouldContain("ValidatedDescriptorGenerations");
        batch.ShouldContain("(ulong DescriptorGeneration, ulong LayoutVersion)");
        batch.ShouldContain("public bool MarkDescriptorValidated(ulong descriptorSetHandle, ulong descriptorGeneration)");
        batch.ShouldContain("(ulong DescriptorGeneration, ulong LayoutVersion) key = (descriptorGeneration, LayoutVersion);");

        // Each recorded image access advances the layout version, so a cached
        // validation result is invalid after any later layout change.
        string recordImageAccess = Slice(
            batch,
            "public void RecordImageAccess(in VulkanImageAccessRangeDelta delta)",
            "public void RecordExecutedSecondaryImageAccess(");
        recordImageAccess.ShouldContain("LayoutVersion++;");
        batch.ShouldContain("ValidatedDescriptorGenerations.Clear();");
    }

    [Test]
    public void ImageRangesCoalesceBeforeOneBulkPublication()
    {
        string batch = ReadWorkspaceFile(TrackingBatchPath);
        string layouts = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.ImageLayouts.cs");

        batch.ShouldContain("TryMergeRanges");
        batch.ShouldContain("LevelCount = Math.Max(left.LevelCount, 1u) + Math.Max(right.LevelCount, 1u)");
        batch.ShouldContain("LayerCount = Math.Max(left.LayerCount, 1u) + Math.Max(right.LayerCount, 1u)");
        layouts.ShouldContain("for (int index = batch.PublishedImageDeltaCount; index < batch.ImageAccessDeltas.Count; index++)");
        layouts.ShouldContain("recorded.RefreshTouchedSubresources()");
        layouts.ShouldContain("batch.PublishedImageDeltaCount = batch.ImageAccessDeltas.Count;");
    }

    [Test]
    public void SubmissionConsumesTouchedArraysInsteadOfGlobalDictionaryScans()
    {
        string submission = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs");
        string lifetimeRecord = ReadWorkspaceFile(
            VulkanRoot + "Frame/Resources/Lifetime/VulkanRenderer.VulkanCommandBufferLifetimeRecord.cs");
        string layoutState = ReadWorkspaceFile(
            VulkanRoot + "Frame/Synchronization/VulkanRenderer.VulkanRecordedImageLayoutState.cs");

        lifetimeRecord.ShouldContain("List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> TouchedDependencies = new(64)");
        lifetimeRecord.ShouldContain("TouchedDependencies.EnsureCapacity(Dependencies.Count);");
        lifetimeRecord.ShouldNotContain("TouchedDependencies = Dependencies.ToArray()");
        submission.ShouldContain("foreach ((VulkanResourceLifetimeKey key, ulong generation) in lifetime.TouchedDependencies)");
        submission.ShouldNotContain("lifetime.Dependencies.ToArray()");
        layoutState.ShouldContain("List<KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState>> TouchedSubresources = new(32)");
        layoutState.ShouldContain("TouchedSubresources.EnsureCapacity(Subresources.Count);");
        layoutState.ShouldNotContain("TouchedSubresources = Subresources.ToArray()");
        submission.ShouldContain("foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> touched in\n                         recorded.TouchedSubresources)");
        submission.ShouldNotContain("recorded.Subresources.ToArray()");
    }

    [Test]
    public void ProfilerExposesCompactionCacheAndContentionCounters()
    {
        string capture = ReadWorkspaceFile("XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs");
        foreach (string field in new[]
                 {
                     "vulkan_tracking_dependency_binds",
                     "vulkan_tracking_unique_dependencies",
                     "vulkan_tracking_image_access_writes",
                     "vulkan_tracking_compact_image_ranges",
                     "vulkan_descriptor_expansion_cache_hits",
                     "vulkan_descriptor_expansion_cache_misses",
                     "vulkan_lifetime_lock_contentions",
                     "vulkan_layout_lock_contentions"
                 })
        {
            capture.ShouldContain(field);
        }
    }

    [Test]
    public void BenchmarkCanForceAnExplicitDesktopOnlyCohort()
    {
        string harness = ReadWorkspaceFile("Tools/Measure-GameLoopRenderPipeline.ps1");
        harness.ShouldContain("[string]$UnitTestVrMode = 'Configured'");
        harness.ShouldContain("Set-BenchmarkEnvValue 'XRE_UNIT_TEST_VR_MODE' $UnitTestVrMode");
    }

    [Test]
    public void BenchmarkRequestsEditorShutdownOnlyOnce()
    {
        string harness = ReadWorkspaceFile("Tools/Measure-GameLoopRenderPipeline.ps1");
        int shutdownStart = harness.IndexOf("function Stop-EditorGracefully", StringComparison.Ordinal);
        int shutdownEnd = harness.IndexOf("function Measure-Variant", shutdownStart, StringComparison.Ordinal);
        string shutdown = harness[shutdownStart..shutdownEnd];

        shutdown.ShouldContain("if (-not $requested) {");
        shutdown.ShouldContain("$requested = [XREngineMeasurementNativeWindow]::PostCloseToProcess($Process.Id)");
        shutdown.ShouldNotContain("PostCloseToProcess($Process.Id) -or $requested");
    }

    [Test]
    public void McpVulkanScreenshotReadback_UsesQueuedSafeTransferSubmission()
    {
        string viewportActions = ReadWorkspaceFile("XREngine.Editor/Mcp/Actions/EditorMcpActions.Viewport.cs");
        int captureStart = viewportActions.IndexOf("static void BeginCapture", StringComparison.Ordinal);
        int captureEnd = viewportActions.IndexOf("var tcs =", captureStart, StringComparison.Ordinal);
        string capture = viewportActions[captureStart..captureEnd];

        capture.ShouldContain("renderer.TryQueueScreenshotReadback(");
        capture.ShouldContain("renderer.TryQueueCompositedScreenshotReadback(");
        capture.ShouldContain("bool queued = includeScreenSpaceUi");
        capture.ShouldContain("false, CompleteReadback, out queueFailure");
        capture.ShouldContain("if (!result.Succeeded || result.Image is null)");
        capture.ShouldContain("if (!queued)");
        capture.ShouldContain("queueFailure ?? \"The renderer rejected the screenshot readback request.\"");
    }

    [Test]
    public void DynamicBufferDescriptorsNeverRequestUpdateAfterBind()
    {
        string source = ReadWorkspaceFile(VulkanRoot + "Resources/Authority/VulkanDescriptorLayoutCache.cs");
        int switchStart = source.IndexOf("return descriptorType switch", StringComparison.Ordinal);
        int switchEnd = source.IndexOf("};", switchStart, StringComparison.Ordinal);
        switchStart.ShouldBeGreaterThanOrEqualTo(0);
        switchEnd.ShouldBeGreaterThan(switchStart);

        // Each non-dynamic buffer type follows its own device feature bit.
        string capabilitySwitch = source[switchStart..switchEnd];
        capabilitySwitch.ShouldContain("DescriptorType.UniformBuffer =>\n                DeviceContext.MutableCapabilities._supportsDescriptorBindingUniformBufferUpdateAfterBind");
        capabilitySwitch.ShouldContain("DescriptorType.StorageBuffer =>\n                DeviceContext.MutableCapabilities._supportsDescriptorBindingStorageBufferUpdateAfterBind");
        capabilitySwitch.ShouldNotContain("DescriptorType.UniformBufferDynamic");
        capabilitySwitch.ShouldNotContain("DescriptorType.StorageBufferDynamic");
        source.ShouldContain("bool hasDynamicBufferBinding = Array.Exists(");
        source.ShouldContain("if (!hasDynamicBufferBinding && CanUseUpdateAfterBind(");
    }

    [Test]
    public void DescriptorVariantsRemainOwnedUntilRendererCleanupAndTrackPerSetCapabilities()
    {
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string program = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");

        descriptors.ShouldNotContain("RetireSupersededDescriptorAllocations");
        descriptors.ShouldContain("DescriptorSetUsesUpdateAfterBind((uint)setIndex)");
        program.ShouldContain("bool[] _descriptorSetUsesUpdateAfterBind");
        program.ShouldContain("DescriptorSetUsesUpdateAfterBind(uint setIndex)");
    }

    private const string VulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
    private const string TrackingBatchPath = VulkanRoot + "Commands/Authority/VulkanCommandBufferTrackingBatch.cs";
    private const string TrackingBatchServicesPath = VulkanRoot + "Commands/CommandBuffers/Tracking/VulkanRenderer.CommandBufferTrackingBatch.cs";
    private const string ResourceCoordinationPath = VulkanRoot + "Commands/Authority/VulkanCommandRuntime.ResourceCoordination.cs";

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    /// <summary>Returns the source text from the start marker up to the end marker.</summary>
    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Missing start marker '{startMarker}'.");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Missing end marker '{endMarker}'.");
        return source[start..end];
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
