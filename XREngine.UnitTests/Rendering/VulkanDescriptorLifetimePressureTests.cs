using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanDescriptorLifetimePressureTests
{
    [Test]
    public void DescriptorPoolLifetimeUsesOwnedSetReverseIndexWithoutGlobalScans()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        lifetime.ShouldContain("_vulkanDescriptorSetsByPool");
        lifetime.ShouldContain("UpdateVulkanDescriptorSetPoolIndex_NoLock");

        string capture = SliceMethod(
            lifetime,
            "private VulkanRetirementTicket CaptureVulkanDescriptorPoolRetirementTicket(",
            "private bool IsVulkanRetirementReady(");
        capture.ShouldContain("_vulkanDescriptorSetsByPool.TryGetValue");
        capture.ShouldContain("_vulkanResourceCommandBufferDependencies.TryGetValue");
        capture.ShouldContain("dependentCommandBuffers");
        capture.ShouldContain("One aggregate exact invalidation");
        capture.ShouldNotContain("in _vulkanDescriptorSetLifetimes");

        string mutate = SliceMethod(
            lifetime,
            "private bool CanMutateVulkanDescriptorPool(",
            "private Result ResetVulkanDescriptorPoolTracked(");
        mutate.ShouldContain("_vulkanDescriptorSetsByPool.TryGetValue");
        mutate.ShouldNotContain("in _vulkanDescriptorSetLifetimes");

        string remove = SliceMethod(
            lifetime,
            "private void RemoveDescriptorSetsOwnedByPool_NoLock(",
            "private void RemoveDescriptorSetLifetime_NoLock(");
        remove.ShouldContain("_vulkanDescriptorSetsByPool.TryGetValue");
        remove.ShouldNotContain("in _vulkanDescriptorSetLifetimes");
    }

    [Test]
    public void DuplicatePoolRetirementIsSuppressedBeforeTicketCapture()
    {
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");
        string method = SliceMethod(
            retirement,
            "internal void RetireDescriptorPool(DescriptorPool descriptorPool)",
            "private void RemoveRetiredDescriptorSetsForPool_NoLock(");

        method.IndexOf("_retiredDescriptorPoolHandlesAll.Add", StringComparison.Ordinal)
            .ShouldBeLessThan(method.IndexOf("CaptureVulkanDescriptorPoolRetirementTicket", StringComparison.Ordinal));
    }

    [Test]
    public void MeshDescriptorsUseStructuralPoolsBoundedVariantsAndLazySlots()
    {
        string key = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorAllocationKey.cs");
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");

        key.ShouldContain("ulong LayoutFingerprint");
        key.ShouldNotContain("ProgramBindingId");
        key.ShouldNotContain("ResourceFingerprint");
        descriptors.ShouldContain("ComputeDescriptorLayoutFingerprint");
        descriptors.ShouldContain("MaxDescriptorAllocationVariants = 32");
        descriptors.ShouldContain("TrimDescriptorAllocationVariantsForInsert");
        descriptors.ShouldContain("Array.Fill(descriptorSets, Array.Empty<DescriptorSet>())");
        descriptors.ShouldContain("MeshRendererDescriptorSets.AllocatedLazySlot");
        descriptors.ShouldContain("DescriptorSlotResourceFingerprintMatches");
    }

    [Test]
    public void PhysicalPlanReplacementUsesExactResourceRetirementInsteadOfGlobalDescriptorRelease()
    {
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        string commit = SliceMethod(
            planner,
            "private void CommitPhysicalAllocatorPlan(",
            "private VulkanPhysicalImageGroup? PreserveAutoExposureHistory(");
        commit.ShouldContain("oldAllocator.TryRetirePhysicalResources");
        commit.ShouldNotContain("ReleaseDescriptorReferencesForPhysicalResourceDestruction");

        lifetime.ShouldContain("_vulkanDescriptorSetsByReferencedResource");
        lifetime.ShouldContain("InvalidateVulkanDescriptorSetsReferencingResource_NoLock");
        lifetime.ShouldContain("InvalidateCachedCommandBuffersForRetiringResource");
        lifetime.ShouldContain("TryTrackVulkanCommandBufferResource_NoLock");
        lifetime.ShouldContain("Vulkan.ResourceLifetime.RetirementInvalidation.{key.Type}");
        lifetime.ShouldNotContain("RetirementInvalidation.{key.Type}.{key.Handle}");
    }

    [Test]
    public void OpenXrBenchmarkGatesDescriptorCreationAndLifetimeHighWaterMarks()
    {
        string harness = ReadWorkspaceFile("Tools/Measure-GameLoopRenderPipeline.ps1");

        harness.ShouldContain("MaxSteadyStateVulkanLiveResources");
        harness.ShouldContain("MaxSteadyStateVulkanDescriptorSets");
        harness.ShouldContain("vulkan_descriptor_pool_create_count");
        harness.ShouldContain("vulkan_lifetime_live_resource_count");
        harness.ShouldContain("vulkan_tracked_descriptor_set_count");
        harness.ShouldContain("[ValidateSet('Configured', 'Desktop', 'Emulated', 'MonadoOpenXR', 'OpenVR', 'OpenXR')]");
    }

    [Test]
    public void ForcedIdleRetirementDestroysPinnedSamplersAndTracksThemUntilDestruction()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");
        string samplerLifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Samplers/VulkanRenderer.SamplerLifetime.cs");

        string beginDestroy = SliceMethod(
            lifetime,
            "private bool TryBeginDestroyVulkanResourceGeneration(",
            "private bool HasUndestroyedVulkanBufferViewReference(");
        beginDestroy.ShouldContain("bool forced = _vulkanForcedRetirementDrainDepth > 0;");
        beginDestroy.ShouldContain("(!forced &&");

        string enqueue = SliceMethod(
            retirement,
            "internal void RetireImageResources(in RetiredImageResources resources)",
            "private ImageView[] FilterRetiredAttachmentViews(");
        enqueue.ShouldNotContain("UnregisterLiveSampler");

        string drain = SliceMethod(
            retirement,
            "private void DrainRetiredImages(int frameSlot, int maxItems)",
            "private void CompleteRetiredImageDeduplication(");
        int destroyIndex = drain.IndexOf("Api!.DestroySampler(device, r.Sampler, null);", StringComparison.Ordinal);
        int unregisterIndex = drain.IndexOf("UnregisterLiveSampler(r.Sampler);", StringComparison.Ordinal);
        destroyIndex.ShouldBeGreaterThanOrEqualTo(0);
        unregisterIndex.ShouldBeGreaterThan(destroyIndex);

        samplerLifetime.ShouldContain("private void DestroyRemainingTrackedSamplers()");

        string initialization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string cleanupPrefix = initialization[
            initialization.IndexOf("public override void CleanUp()", StringComparison.Ordinal)..
            initialization.IndexOf("// Drain all deferred-deletion queues now that the GPU is idle.", StringComparison.Ordinal)];
        cleanupPrefix.ShouldContain("DestroyComputeTransientResources();\n            DestroyComputeDescriptorCaches();");
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Missing method start '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Missing method end '{endMarker}'.");
        return source[start..end];
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
