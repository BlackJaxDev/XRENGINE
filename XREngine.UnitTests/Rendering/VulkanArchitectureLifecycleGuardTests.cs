using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanArchitectureLifecycleGuardTests
{
    [Test]
    public void PipelineManager_WarmCacheLookupDoesNotAllocate()
    {
        VulkanPipelineManager manager = new();
        VulkanGraphicsPipelineKey key = default;
        Pipeline pipeline = new(7);
        manager.StoreSharedGraphicsPipeline(key, pipeline).Handle.ShouldBe(7UL);
        manager.TryGetSharedGraphicsPipeline(key, out _).ShouldBeTrue();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        ulong checksum = 0;
        for (int index = 0; index < 10_000; index++)
        {
            if (!manager.TryGetSharedGraphicsPipeline(key, out Pipeline cached))
                Assert.Fail("Warm pipeline lookup unexpectedly missed.");
            checksum += cached.Handle;
        }

        (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore).ShouldBe(0);
        checksum.ShouldBe(70_000UL);
    }

    [Test]
    public void PipelineManager_NativePipelineLayoutsHaveDistinctCacheIdentities()
    {
        VulkanPipelineManager manager = new();
        VulkanGraphicsPipelineKey firstKey = default;
        firstKey = firstKey with { PipelineLayoutHandle = 101 };
        VulkanGraphicsPipelineKey secondKey = firstKey with { PipelineLayoutHandle = 202 };

        manager.StoreSharedGraphicsPipeline(firstKey, new Pipeline(7));

        firstKey.ShouldNotBe(secondKey);
        manager.TryGetSharedGraphicsPipeline(firstKey, out Pipeline pipeline).ShouldBeTrue();
        pipeline.Handle.ShouldBe(7UL);
        manager.TryGetSharedGraphicsPipeline(secondKey, out _).ShouldBeFalse();
    }


    [Test]
    public void PipelineManager_DeviceLifetimeDrainRemovesAllCachedHandlesAndReservations()
    {
        VulkanPipelineManager manager = new();
        VulkanGraphicsPipelineKey pipelineKey = default;
        VulkanGraphicsPipelineLibraryKey libraryKey = default;

        manager.StoreSharedGraphicsPipeline(pipelineKey, new Pipeline(11));
        manager.TryGetOrReserveSharedGraphicsPipelineLibrary(
                libraryKey,
                out _,
                out bool creationReserved)
            .ShouldBeFalse();
        creationReserved.ShouldBeTrue();
        manager.CompleteSharedGraphicsPipelineLibraryCreation(libraryKey, new Pipeline(13));

        manager.DrainSharedGraphicsPipelines().Select(static pipeline => pipeline.Handle)
            .ShouldBe([11UL]);
        manager.DrainSharedGraphicsPipelineLibraries().Select(static pipeline => pipeline.Handle)
            .ShouldBe([13UL]);
        manager.TryGetSharedGraphicsPipeline(pipelineKey, out _).ShouldBeFalse();
        manager.TryGetOrReserveSharedGraphicsPipelineLibrary(
                libraryKey,
                out _,
                out creationReserved)
            .ShouldBeFalse();
        creationReserved.ShouldBeTrue();
    }

    [Test]
    public void RetirementQueues_AreIsolatedPerRendererLifetime()
    {
        VulkanResourceRetirementQueue first = new(frameSlotCount: 2);
        VulkanResourceRetirementQueue second = new(frameSlotCount: 2);

        first.AllPipelineHandles.Add(19);
        first.PipelineHandles[1].Add(19);

        first.AllPipelineHandles.ShouldBe([19UL]);
        first.PipelineHandles[1].ShouldBe([19UL]);
        second.AllPipelineHandles.ShouldBeEmpty();
        second.PipelineHandles[1].ShouldBeEmpty();
        first.SyncRoot.ShouldNotBeSameAs(second.SyncRoot);
    }

    [Test]
    public void PostAcquireRecoveryPolicy_IsReferenceFreeAndAllocationFree()
    {
        RuntimeHelpers.IsReferenceOrContainsReferences<VulkanDesktopRecoveryOutcome>()
            .ShouldBeFalse();

        _ = VulkanDesktopFramePolicy.ResolvePostAcquireFailure(
            EVulkanDesktopPostAcquireFailureStage.Recording,
            deviceLost: false,
            recreateSwapchainAfterResolution: true);

        int checksum = 0;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            checksum += (int)VulkanDesktopFramePolicy.ResolvePostAcquireFailure(
                    EVulkanDesktopPostAcquireFailureStage.Submission,
                    deviceLost: false,
                    recreateSwapchainAfterResolution: false)
                .Reason;
        }

        (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore).ShouldBe(0);
        checksum.ShouldBeGreaterThan(0);
    }
    [Test]
    public void CommandRecordingPreparation_IsAllocationFreeAfterWarmup()
    {
        VulkanCommandRecorder recorder = new();
        FrameOperationSequence operations = FrameOperationSequence.Empty;
        VulkanCommandRecordingContext warmup = new(
            0,
            new CommandBuffer(1),
            default,
            operations,
            0,
            null,
            false,
            true,
            null!,
            null,
            null,
            false,
            VulkanRenderGraphPlan.Empty,
            null,
            0UL);
        recorder.Prepare(ref warmup).ShouldBeTrue();

        int prepared = 0;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            VulkanCommandRecordingContext context = new(
                0,
                new CommandBuffer(1),
                default,
                operations,
                0,
                null,
                false,
                true,
                null!,
                null,
                null,
                false,
                VulkanRenderGraphPlan.Empty,
                null,
                0UL);
            if (recorder.Prepare(ref context))
                prepared++;
        }

        (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore).ShouldBe(0);
        prepared.ShouldBe(10_000);
    }

    [Test]
    public void DesktopFrameAttemptBookkeeping_RemainsOwnedByFrameLoop()
    {
        string frameLoop = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.cs");

        frameLoop.ShouldContain("internal bool TryEnter(out DesktopFrameIdentity identity)");
        frameLoop.ShouldContain("internal void Exit(in DesktopFrameIdentity identity)");
        frameLoop.ShouldNotContain("VulkanDesktopFrameCoordinator");
    }

    [Test]
    public void FrameOperationScheduling_UsesCallerWorkspaceWithoutAllocatingAfterWarmup()
    {
        VulkanFrameOperationScheduler scheduler = new();
        List<VulkanSecondaryRecordingBucket> buckets = new(4);
        FrameOperationSequence operations = FrameOperationSequence.Empty;
        scheduler.BuildSecondaryRecordingBuckets(operations, buckets);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
            scheduler.BuildSecondaryRecordingBuckets(operations, buckets);

        (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore).ShouldBe(0);
        buckets.ShouldBeEmpty();
    }
}
