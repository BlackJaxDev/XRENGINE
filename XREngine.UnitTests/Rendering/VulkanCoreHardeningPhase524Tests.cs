using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCoreHardeningPhase524Tests
{
    [Test]
    public void RecordingUsesCapacityBackedCommandLocalDependencyAndLayoutStorage()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferTrackingBatch.cs");

        source.ShouldContain("HashSet<VulkanResourceLifetimeKey> Dependencies = new(64)");
        source.ShouldContain("List<VulkanImageAccessRangeDelta> ImageAccessDeltas = new(32)");
        source.ShouldContain("VulkanCommandBufferImageAccessIndex LatestImageAccessStates = new(32)");
        source.ShouldContain("TryRecordCommandBufferDependency");
        source.ShouldContain("TryRecordImageAccessDelta");
        source.ShouldContain("FlushCommandBufferTrackingBatch");
        source.ShouldNotContain("for (int i = batch.ImageAccessDeltas.Count - 1;");
    }

    [Test]
    public void PendingImageAccessIndex_UsesLatestStateAcrossSubresources()
    {
        VulkanRenderer.VulkanCommandBufferImageAccessIndex index = new();
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
        VulkanRenderer.VulkanImageAccessState sampled = VulkanRenderer.ResolveVulkanImageAccessState(
            ImageLayout.ShaderReadOnlyOptimal,
            ImageAspectFlags.ColorBit,
            serial: 10);
        VulkanRenderer.VulkanImageAccessState storage = VulkanRenderer.ResolveVulkanImageAccessState(
            ImageLayout.General,
            ImageAspectFlags.ColorBit,
            serial: 20);

        index.Record(imageHandle, bothLayers, sampled);
        index.TryGet(imageHandle, bothLayers, out VulkanRenderer.VulkanImageAccessState initial).ShouldBeTrue();
        initial.Layout.ShouldBe(ImageLayout.ShaderReadOnlyOptimal);
        initial.Serial.ShouldBe(10UL);

        index.Record(imageHandle, secondLayer, storage);
        index.TryGet(imageHandle, secondLayer, out VulkanRenderer.VulkanImageAccessState latest).ShouldBeTrue();
        latest.Layout.ShouldBe(ImageLayout.General);
        latest.Serial.ShouldBe(20UL);
        index.TryGet(imageHandle, bothLayers, out _).ShouldBeFalse();
        index.Count.ShouldBe(2);
    }

    [Test]
    public void PendingImageAccessIndex_RequiresEveryRequestedAspect()
    {
        VulkanRenderer.VulkanCommandBufferImageAccessIndex index = new();
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
        VulkanRenderer.VulkanImageAccessState depthRead = VulkanRenderer.ResolveVulkanImageAccessState(
            ImageLayout.DepthStencilReadOnlyOptimal,
            ImageAspectFlags.DepthBit,
            serial: 30);

        index.Record(imageHandle, depthOnly, depthRead);

        index.TryGet(imageHandle, depthOnly, out VulkanRenderer.VulkanImageAccessState found).ShouldBeTrue();
        found.Layout.ShouldBe(ImageLayout.DepthStencilReadOnlyOptimal);
        index.TryGet(imageHandle, depthStencil, out _).ShouldBeFalse();
    }

    [Test]
    public void PendingImageAccessIndex_RepeatedCurrentStateAccessDoesNotAllocate()
    {
        VulkanRenderer.VulkanCommandBufferImageAccessIndex index = new();
        const ulong imageHandle = 0x9ABCUL;
        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        VulkanRenderer.VulkanImageAccessState sampled = VulkanRenderer.ResolveVulkanImageAccessState(
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
    public void HotDependencyAndBarrierPathsAvoidGlobalLocks()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string layouts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");

        int dependencyStart = lifetime.IndexOf("private void TrackVulkanCommandBufferResource(", StringComparison.Ordinal);
        int dependencyEnd = lifetime.IndexOf("private void TrackVulkanCommandBufferResource_NoLock(", dependencyStart, StringComparison.Ordinal);
        string dependencyMethod = lifetime[dependencyStart..dependencyEnd];
        dependencyMethod.ShouldContain("TryRecordCommandBufferDependency");
        dependencyMethod.IndexOf("TryRecordCommandBufferDependency", StringComparison.Ordinal)
            .ShouldBeLessThan(dependencyMethod.IndexOf("lock (_vulkanResourceLifetimeLock)", StringComparison.Ordinal));

        int layoutStart = layouts.IndexOf("private void RecordImageAccess(", StringComparison.Ordinal);
        int layoutEnd = layouts.IndexOf("private bool FlushCommandBufferImageAccessBatch(", layoutStart, StringComparison.Ordinal);
        string layoutMethod = layouts[layoutStart..layoutEnd];
        layoutMethod.ShouldContain("TryRecordImageAccessDelta");
    }

    [Test]
    public void DescriptorReferencesPublishImmutableGenerationsAndExpandOncePerRecording()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string batch = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferTrackingBatch.cs");

        lifetime.ShouldContain("VulkanPublishedDescriptorSetSnapshot");
        lifetime.ShouldContain("PublishVulkanDescriptorSetSnapshot_NoLock");
        lifetime.ShouldContain("MarkDescriptorExpanded(descriptorSet.Handle, snapshot.Generation)");
        batch.ShouldContain("ExpandedDescriptorGenerations");
        batch.ShouldContain("MarkDescriptorExpanded");

        int propagationStart = lifetime.IndexOf("private void ValidateAndPropagateVulkanDescriptorReference_NoLock(", StringComparison.Ordinal);
        int propagationEnd = lifetime.IndexOf("private static void PropagateVulkanDescriptorSetSubmission_NoLock(", propagationStart, StringComparison.Ordinal);
        lifetime[propagationStart..propagationEnd].ShouldNotContain("_vulkanCommandBufferLifetimes");
    }

    [Test]
    public void RetirementPublishesCommandLocalDependenciesBeforeCapturingPins()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string batch = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferTrackingBatch.cs");

        batch.ShouldContain("PublishCommandBufferTrackingDependenciesBeforeResourceRetirement");
        batch.ShouldContain("batch.Dependencies.Contains(resourceKey)");
        batch.ShouldContain("TryFlushCommandBufferTrackingBatch(commandBuffer, out string failureReason)");

        int retirementStart = lifetime.IndexOf("private VulkanRetirementTicket CaptureVulkanRetirementTicket(", StringComparison.Ordinal);
        int retirementLock = lifetime.IndexOf("lock (_vulkanResourceLifetimeLock)", retirementStart, StringComparison.Ordinal);
        int publication = lifetime.IndexOf("PublishCommandBufferTrackingDependenciesBeforeResourceRetirement(key)", retirementStart, StringComparison.Ordinal);
        retirementStart.ShouldBeGreaterThanOrEqualTo(0);
        publication.ShouldBeGreaterThan(retirementStart);
        publication.ShouldBeLessThan(retirementLock);

        int poolRetirementStart = lifetime.IndexOf("private VulkanRetirementTicket CaptureVulkanDescriptorPoolRetirementTicket(", StringComparison.Ordinal);
        int poolSetRetirement = lifetime.IndexOf("PublishCommandBufferTrackingDependenciesBeforeResourceRetirement(", poolRetirementStart, StringComparison.Ordinal);
        int poolPendingState = lifetime.IndexOf("setResource.State |= EVulkanResourceLifetimeState.PendingRetirement", poolRetirementStart, StringComparison.Ordinal);
        poolSetRetirement.ShouldBeGreaterThan(poolRetirementStart);
        poolSetRetirement.ShouldBeLessThan(poolPendingState);
    }

    [Test]
    public void DebugDescriptorValidationCachesDescriptorAndLayoutGenerations()
    {
        string batch = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferTrackingBatch.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        batch.ShouldContain("ValidatedDescriptorGenerations");
        batch.ShouldContain("(ulong DescriptorGeneration, ulong LayoutVersion)");
        lifetime.ShouldContain("batch.MarkDescriptorValidated(descriptorSet.Handle, snapshot.Generation)");
        lifetime.ShouldContain("Vulkan descriptor image layout mismatch at command recording");
    }

    [Test]
    public void ImageRangesCoalesceBeforeOneBulkPublication()
    {
        string batch = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferTrackingBatch.cs");
        string layouts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");

        batch.ShouldContain("TryMergeRanges");
        batch.ShouldContain("LevelCount = Math.Max(left.LevelCount, 1u) + Math.Max(right.LevelCount, 1u)");
        batch.ShouldContain("LayerCount = Math.Max(left.LayerCount, 1u) + Math.Max(right.LayerCount, 1u)");
        layouts.ShouldContain("for (int deltaIndex = batch.PublishedImageDeltaCount;");
        layouts.ShouldContain("recorded.RefreshTouchedSubresources()");
    }

    [Test]
    public void SubmissionConsumesTouchedArraysInsteadOfGlobalDictionaryScans()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string layouts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");

        lifetime.ShouldContain("TouchedDependencies");
        lifetime.ShouldContain("List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> TouchedDependencies = new(64)");
        lifetime.ShouldNotContain("TouchedDependencies = lifetime.Dependencies.ToArray()");
        lifetime.ShouldContain("foreach ((VulkanResourceLifetimeKey key, ulong recordedGeneration) in commandLifetime.TouchedDependencies)");
        layouts.ShouldContain("TouchedSubresources");
        layouts.ShouldContain("List<KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState>> TouchedSubresources = new(32)");
        layouts.ShouldNotContain("TouchedSubresources = recorded.Subresources.ToArray()");
        layouts.ShouldContain("foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in recorded.TouchedSubresources)");
    }

    [Test]
    public void ProfilerExposesCompactionCacheAndContentionCounters()
    {
        string capture = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfileCapture.cs");
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
    public void DynamicBufferDescriptorsNeverRequestUpdateAfterBind()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLayoutCache.cs");
        int switchStart = source.IndexOf("return descriptorType switch", StringComparison.Ordinal);
        int switchEnd = source.IndexOf("};", switchStart, StringComparison.Ordinal);
        switchStart.ShouldBeGreaterThanOrEqualTo(0);
        switchEnd.ShouldBeGreaterThan(switchStart);

        string capabilitySwitch = source[switchStart..switchEnd];
        capabilitySwitch.ShouldContain("DescriptorType.UniformBuffer => true");
        capabilitySwitch.ShouldContain("DescriptorType.StorageBuffer => true");
        capabilitySwitch.ShouldNotContain("DescriptorType.UniformBufferDynamic");
        capabilitySwitch.ShouldNotContain("DescriptorType.StorageBufferDynamic");
        source.ShouldContain("bool hasDynamicBufferBinding = Array.Exists(");
        source.ShouldContain("if (!hasDynamicBufferBinding && CanUseUpdateAfterBind(");
    }

    [Test]
    public void DescriptorVariantsRemainOwnedUntilRendererCleanupAndTrackPerSetCapabilities()
    {
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string program = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");

        descriptors.ShouldNotContain("RetireSupersededDescriptorAllocations");
        descriptors.ShouldContain("DescriptorSetUsesUpdateAfterBind((uint)setIndex)");
        program.ShouldContain("bool[] _descriptorSetUsesUpdateAfterBind");
        program.ShouldContain("DescriptorSetUsesUpdateAfterBind(uint setIndex)");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(ResolveRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
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
