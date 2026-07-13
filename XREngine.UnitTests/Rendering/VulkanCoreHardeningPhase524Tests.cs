using NUnit.Framework;
using Shouldly;

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
        source.ShouldContain("TryRecordCommandBufferDependency");
        source.ShouldContain("TryRecordImageAccessDelta");
        source.ShouldContain("FlushCommandBufferTrackingBatch");
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
