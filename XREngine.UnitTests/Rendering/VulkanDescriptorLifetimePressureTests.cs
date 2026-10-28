using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanDescriptorLifetimePressureTests
{
    [Test]
    public void DescriptorPoolLifetimeUsesOwnedSetReverseIndexWithoutGlobalScans()
    {
        // The descriptor lifetime authority owns the pool-to-set reverse index.
        // Pool retirement, pool ownership checks, and pool removal use only that
        // index and never walk every tracked descriptor set.
        string tracker = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Lifetime/VulkanResourceLifetimeTracker.cs");
        string authority = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs");
        string manager = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorManager.cs");

        tracker.ShouldContain("internal Dictionary<ulong, HashSet<ulong>> DescriptorSetsByPool { get; } = new();");
        authority.ShouldContain("private void UpdatePoolIndexNoLock(");

        string capture = SliceMethod(
            authority,
            "private VulkanRetirementTicket CaptureDescriptorPoolRetirementTicket(",
            "private VulkanRetirementTicket CaptureRetirementTicket(");
        capture.ShouldContain("tracker.DescriptorSetsByPool.TryGetValue(pool.Handle, out HashSet<ulong>? sets)");
        capture.ShouldContain("ticket = ticket.Merge(setTicket);");
        capture.ShouldNotContain("in tracker.DescriptorSetLifetimes");

        string retirementTicket = SliceMethod(
            authority,
            "private VulkanRetirementTicket CaptureRetirementTicket(",
            "private static ulong[] CaptureCurrentGenerationDependentsNoLock(");
        retirementTicket.ShouldContain("dependentCommandBuffers = CaptureCurrentGenerationDependentsNoLock(tracker, key, generation);");
        retirementTicket.ShouldContain("_resources.SynchronousCommands.CommandRuntime.InvalidateCachedCommandBuffers(");
        string dependents = SliceMethod(
            authority,
            "private static ulong[] CaptureCurrentGenerationDependentsNoLock(",
            "return result;");
        dependents.ShouldContain("tracker.ResourceCommandBufferDependencies.TryGetValue(key, out HashSet<ulong>? dependents)");

        string ownership = SliceMethod(
            manager,
            "private bool PoolOwnsCurrentDescriptorSet(",
            "private bool ContainsCurrentDescriptorSet(");
        ownership.ShouldContain("tracker.DescriptorSetsByPool.TryGetValue(");
        ownership.ShouldNotContain("in tracker.DescriptorSetLifetimes");

        string remove = SliceMethod(
            manager,
            "internal static void RemoveDescriptorSetsOwnedByPoolNoLock(",
            "internal static void PublishSnapshotNoLock(");
        remove.ShouldContain("tracker.DescriptorSetsByPool.TryGetValue(poolHandle, out HashSet<ulong>? ownedSets)");
        remove.ShouldNotContain("in tracker.DescriptorSetLifetimes");
    }

    [Test]
    public void DuplicatePoolRetirementIsSuppressedBeforeTicketCapture()
    {
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs");
        string method = SliceMethod(
            retirement,
            "internal void RetireDescriptorPool(DescriptorPool descriptorPool)",
            "internal bool TryAcquireMeshDescriptorPoolSlab(");

        int duplicateGuard = method.IndexOf("_lifetime.Retirement.AllDescriptorPoolHandles.Add", StringComparison.Ordinal);
        duplicateGuard.ShouldBeGreaterThanOrEqualTo(0);
        duplicateGuard.ShouldBeLessThan(method.IndexOf("CaptureDescriptorPoolRetirementTicket", StringComparison.Ordinal));
    }

    [Test]
    public void MeshDescriptorsUseStructuralSharedSlabsAndFrameOnlySlots()
    {
        string key = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Descriptors/VkMeshRenderer.DescriptorAllocationKey.cs");
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string descriptorFingerprints = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorFingerprints.cs");
        string program = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Layouts.cs");
        // Mesh descriptor pool slabs moved into the descriptor lifetime
        // authority. Shared material-tier allocations moved into the
        // descriptor manager.
        string slabs = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs");
        string sharedAllocations = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorManager.cs");

        key.ShouldContain("ulong LayoutFingerprint");
        key.ShouldNotContain("ProgramIdentity");
        key.ShouldContain("int ViewFamilyIdentity");
        key.ShouldContain("ProgramBindingId");
        key.ShouldContain("BindingIdentityFingerprint");
        key.ShouldContain("ImmutableResourceFingerprint");
        descriptors.ShouldContain("_program.DescriptorLayoutFingerprint");
        descriptors.ShouldContain("_program.DescriptorSchemaFingerprint");
        program.ShouldContain("ComputeDescriptorLayoutFingerprint(result.Layouts),");
        program.ShouldContain("ComputeDescriptorSchemaFingerprint(result.Bindings, result.Layouts.Length));");
        program.ShouldContain("_descriptorLayoutFingerprint = entry.LayoutFingerprint;");
        program.ShouldContain("_descriptorSchemaFingerprint = entry.SchemaFingerprint;");
        descriptors.ShouldContain("int viewFamilyIdentity = MaterializationSnapshot.DescriptorViewFamilyIdentity;");
        descriptors.ShouldContain("allocation.ViewFamilyIdentity != viewFamilyIdentity");
        descriptors.ShouldContain("candidate.ViewFamilyIdentity != viewFamilyIdentity");
        descriptors.ShouldContain("DescriptorAllocationResourcesMatch(");
        descriptors.ShouldContain("ResourceFingerprint = resourceFingerprint,");
        descriptors.ShouldContain("DescriptorSlotResourceFingerprintMatches(allocation, descriptorSlotIndex, resourceFingerprint)");
        descriptors.ShouldNotContain("_descriptorDirty && DescriptorSlotResourceFingerprintMatches");
        descriptorFingerprints.ShouldContain("AddTextureDescriptorResourceFingerprint(ref hash, texture);");
        descriptors.ShouldContain("ViewFamilyIdentity = viewFamilyIdentity");
        descriptors.ShouldNotContain("ProgramIdentity = programIdentity");
        descriptors.ShouldContain("ReferenceEquals(allocation.Program, _program)");
        descriptors.ShouldNotContain("MaxDescriptorAllocationVariants");
        descriptors.ShouldContain("int descriptorFrameSlotCount = frameCount;");
        descriptors.ShouldNotContain("frameCount * Math.Max(drawUniformSlot");
        descriptors.ShouldContain("Array.Fill(descriptorSets, Array.Empty<DescriptorSet>())");
        descriptors.ShouldContain("private bool EnsureDescriptorSlotReady(");
        descriptors.ShouldContain("DescriptorSlotResourceFingerprintMatches");
        descriptors.ShouldContain("TryAcquireMeshDescriptorPoolSlab");
        descriptors.ShouldContain("UsesSharedMaterialTier");
        descriptors.ShouldContain("BackendContext.Resources.Descriptors.TryAcquireSharedMeshDescriptorAllocation(");
        descriptors.ShouldContain("usesSharedMaterialTier: true");
        sharedAllocations.ShouldContain("internal bool TryAcquireSharedMeshDescriptorAllocation(");
        sharedAllocations.ShouldContain("_sharedMeshDescriptorAllocations");
        sharedAllocations.ShouldContain("candidate.SharedReferenceCount++");
        sharedAllocations.ShouldContain("allocation.SharedReferenceCount--");
        slabs.ShouldContain("MeshDescriptorPoolSlabAllocationCapacity = 64");
        slabs.ShouldContain("DescriptorPoolCreateFlags.FreeDescriptorSetBit");
        slabs.ShouldContain("RetireMeshDescriptorSetOrQueue(pool, sets[setIndex]);");
        slabs.ShouldContain("RetireDescriptorPool(pool);");
        descriptors.ShouldContain("if (diagnosticIndex > 64)");
        descriptors.ShouldContain(
            "index={0}/64 program='{1}' layout=0x{2:X16} material='{3}' output={4} frameSlots={5} sets={6} sharedMaterial={7}");

    }

    [Test]
    public void ExactRetirementInvalidationResetsCompletedRecordingsBeforeResourceDrain()
    {
        string runtime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.cs");
        string trackingBatch = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandBufferTrackingBatch.cs");
        string resources = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");
        string frameSlots = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.FrameSlots.cs");

        runtime.ShouldContain("CommandBuffers.InvalidatedBuffersPendingReset.TryAdd(");
        string drain = SliceMethod(
            runtime,
            "internal void DrainInvalidatedCommandBufferRecordings(",
            "internal Result ResetCommandBufferWithLifetime(");
        drain.ShouldContain("int maxItems = 64)");
        drain.ShouldContain("if (!TryResetCommandBufferWithLifetime(");
        drain.ShouldContain("CommandBuffers.InvalidatedBuffersPendingReset.TryRemove(handle, out _);");

        // The canonical reset predicate rejects a recording or queued buffer,
        // resets it natively, and then releases its recorded dependencies.
        string reset = SliceMethod(
            runtime,
            "private bool TryResetCommandBufferWithLifetime(",
            "internal unsafe void DrainRetiredCommandBuffers(");
        reset.ShouldContain("if (batch.IsRecording || batch.QueuedSubmissionCount != 0 ||");
        reset.ShouldContain("!ResourceRuntime.CanResetCommandBufferNoLock(handle)");
        reset.ShouldContain("result = Api.ResetCommandBuffer(commandBuffer, 0);");
        reset.ShouldContain("ResourceRuntime.CompleteCommandBufferReset(handle);");
        string completeReset = SliceMethod(
            resources,
            "internal void CompleteCommandBufferReset(ulong handle)",
            "lifetime.InvalidateSealedSubmissionContract();");
        completeReset.ShouldContain("ReleaseCommandBufferDependenciesNoLock(handle, lifetime);");
        trackingBatch.ShouldContain("public bool IsRecording;");

        int drainInvalidated = frameSlots.IndexOf(
            "_commandRuntime.DrainInvalidatedCommandBufferRecordings(",
            StringComparison.Ordinal);
        drainInvalidated.ShouldBeGreaterThanOrEqualTo(0);
        drainInvalidated.ShouldBeLessThan(frameSlots.IndexOf(
            "ResourceRuntime.DrainRetiredDescriptorPools(",
            StringComparison.Ordinal));
    }

    [Test]
    public void PhysicalPlanReplacementUsesExactResourceRetirementInsteadOfGlobalDescriptorRelease()
    {
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.PhysicalResourcePlanning.cs");
        string tracker = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Lifetime/VulkanResourceLifetimeTracker.cs");
        string authority = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs");
        string manager = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorManager.cs");
        string resources = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");

        string commit = SliceMethod(
            planner,
            "private void CommitPhysicalAllocatorPlan(",
            "private void EvictFrameOpResourcePlannerStatesReferencingAllocator(");
        commit.ShouldContain("_ = oldAllocator.TryRetirePhysicalResources(");
        commit.ShouldNotContain("ReleaseDescriptorReferencesForPhysicalResourceDestruction");

        // Exact retirement invalidates only the descriptor sets and command
        // buffers that reference the retiring resource generation.
        tracker.ShouldContain("DescriptorSetsByReferencedResource { get; }");
        manager.ShouldContain("internal int InvalidateResourceReferencesNoLock(");
        authority.ShouldContain("invalidatedDescriptorSetCount = _descriptors.InvalidateResourceReferencesNoLock(_lifetime, key);");
        authority.ShouldContain("$\"retiring {key} generation {generation}\"");
        resources.ShouldContain("internal void PublishCommandBufferDependencyNoLock(");
        authority.ShouldContain("Vulkan.ResourceLifetime.RetirementInvalidation.{key.Type}");
        authority.ShouldNotContain("RetirementInvalidation.{key.Type}.{key.Handle}");
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
        string ledger = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.LifetimeLedger.cs");
        string images = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Images/VulkanImageResourceService.cs");
        string resources = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");
        string samplers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanSamplerResourceService.cs");
        string lifecycle = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs");

        string beginDestroy = SliceMethod(
            ledger,
            "internal bool TryBeginDestroyResourceGeneration(",
            "internal void CompleteResourceDestruction(");
        beginDestroy.ShouldContain("bool forced = Lifetime.Tracker.ForcedRetirementDrainDepth > 0;");
        beginDestroy.ShouldContain("(!forced &&");

        // Enqueue keeps the sampler registered. Only native destruction
        // removes it from the live sampler set.
        string enqueue = SliceMethod(
            images,
            "internal void RetireOwnedResources(",
            "internal Result CreateOwnedImage(");
        enqueue.ShouldContain("lifetime.Retirement.SamplerHandles[frameSlot].Add(sampler.Handle);");
        enqueue.ShouldNotContain("UnregisterLiveSampler");

        string drain = SliceMethod(
            resources,
            "internal unsafe void DrainRetiredImages(",
            "private bool AreRetiredImageViewsRegistered(");
        int destroyIndex = drain.IndexOf("api.DestroySampler(device, resources.Sampler, null);", StringComparison.Ordinal);
        int unregisterIndex = drain.IndexOf("Descriptors.UnregisterLiveSampler(resources.Sampler);", StringComparison.Ordinal);
        destroyIndex.ShouldBeGreaterThanOrEqualTo(0);
        unregisterIndex.ShouldBeGreaterThan(destroyIndex);

        samplers.ShouldContain("internal unsafe int DestroyRemaining(Vk api, Device device)");
        samplers.ShouldContain("ulong[] handles = descriptors.TakeLiveSamplerHandles();");
        lifecycle.ShouldContain("_resourceRuntime.Samplers.DestroyRemaining(Api, _deviceContext.Device);");

        int computeTransient = lifecycle.IndexOf(
            "RunCleanupStep(\"compute transient resources\", _commandRuntime.DestroyComputeTransientResources, failures);",
            StringComparison.Ordinal);
        int computeDescriptorCaches = lifecycle.IndexOf(
            "RunCleanupStep(\"compute descriptor caches\", () => _ = _resourceRuntime.RetireComputeDescriptorCachesForShutdown(), failures);",
            StringComparison.Ordinal);
        int initialDrain = lifecycle.IndexOf(
            "RunCleanupStep(\"initial retirement drain\", ForceFlushAllRetiredResources, failures);",
            StringComparison.Ordinal);
        computeTransient.ShouldBeGreaterThanOrEqualTo(0);
        computeDescriptorCaches.ShouldBeGreaterThan(computeTransient);
        initialDrain.ShouldBeGreaterThan(computeDescriptorCaches);
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
