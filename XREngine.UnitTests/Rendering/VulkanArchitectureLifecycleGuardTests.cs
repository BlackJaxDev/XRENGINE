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
        VulkanRenderer.VkMeshRenderer.PipelineKey key = default;
        Pipeline pipeline = new(7);
        manager.StoreSharedGraphicsPipeline(key, pipeline).Handle.ShouldBe(7UL);
        manager.TryGetSharedGraphicsPipeline(key, out _).ShouldBeTrue();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        ulong checksum = 0;
        for (int index = 0; index < 10_000; index++)
        {
            manager.TryGetSharedGraphicsPipeline(key, out Pipeline cached).ShouldBeTrue();
            checksum += cached.Handle;
        }

        (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore).ShouldBe(0);
        checksum.ShouldBe(70_000UL);
    }

    [Test]
    public void PipelineManager_DeviceLifetimeDrainRemovesAllCachedHandlesAndReservations()
    {
        VulkanPipelineManager manager = new();
        VulkanRenderer.VkMeshRenderer.PipelineKey pipelineKey = default;
        VulkanRenderer.VkMeshRenderer.GraphicsPipelineLibraryKey libraryKey = default;

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
}
