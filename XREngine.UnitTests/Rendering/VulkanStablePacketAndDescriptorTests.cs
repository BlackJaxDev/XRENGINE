using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanStablePacketAndDescriptorTests
{
    [Test]
    public void StableMeshPackets_StartAtTenDrawsAndRemainBounded()
    {
        VulkanRenderer.MinMeshDrawsPerRenderPacket.ShouldBe(10);
        VulkanRenderer.MaxMeshDrawsPerRenderPacket.ShouldBeGreaterThanOrEqualTo(
            VulkanRenderer.MinMeshDrawsPerRenderPacket);
    }

    [Test]
    public void DescriptorChanges_HaveExplicitContentIdentityAndLayoutClasses()
    {
        RenderResourceChangeKind.FrameData.ShouldNotBe(RenderResourceChangeKind.CompatibleContentPublication);
        RenderResourceChangeKind.CompatibleContentPublication.ShouldNotBe(RenderResourceChangeKind.BindingIdentity);
        RenderResourceChangeKind.BindingIdentity.ShouldNotBe(RenderResourceChangeKind.StructuralLayout);
    }

    [Test]
    public void VulkanRecording_UsesPacketSecondariesWithoutSimultaneousUse()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string secondarySource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");
        int workerStart = secondarySource.IndexOf("private void RecordScheduledMeshCommandChainWorker", StringComparison.Ordinal);
        int workerEnd = secondarySource.IndexOf("private bool TryRecordSecondaryBucket", workerStart, StringComparison.Ordinal);
        string worker = secondarySource[workerStart..workerEnd];

        source.ShouldContain("scheduledOpCount += chain.SourceCount;");
        source.ShouldContain("CmdExecuteCommandsTracked(commandBuffer, (uint)secondaryCount, secondaryPtr)");
        worker.ShouldContain("Flags = CommandBufferUsageFlags.RenderPassContinueBit,");
        worker.ShouldNotContain("SimultaneousUseBit");
        worker.ShouldContain("using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(firstDraw.Context);");
        worker.IndexOf("EnterFrameOpResourcePlannerReadbackScope(firstDraw.Context)", StringComparison.Ordinal)
            .ShouldBeLessThan(worker.IndexOf("for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)", StringComparison.Ordinal));
        worker.ShouldContain("chain.State = allDrawsRecorded ? CommandChainState.Recorded : CommandChainState.NotReady;");
        source.ShouldContain("CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.OneTimeSubmitBit");
    }

    [Test]
    public void WorkerDispatch_RecordsOnlyCommandBuffersOwnedByThatWorkerPool()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainWorkers.cs");

        source.ShouldContain("public int[] RecordJobWorkerIndices = [];");
        source.ShouldContain("if (batch.RecordJobWorkerIndices[jobIndex] != worker.WorkerIndex)");
        source.ShouldContain("RecordScheduledMeshCommandChainWorker(batch, chainIndex);");
    }

    [Test]
    public void WorkerPoolAssignment_IsStableWhenTheDirtySubsetChanges()
    {
        CommandChainKey key = new(
            FrameSlot: 2,
            new RenderViewKey(11, 12, 0, RenderViewKind.Main, 0, -1),
            PassIndex: 4,
            TargetIdentity: 13,
            DynamicOverlay: false,
            ChainOrdinal: 14);

        int first = VulkanRenderer.ResolveCommandChainRecordingWorkerIndex(key, workerCount: 6);
        int afterOtherJobsDisappear = VulkanRenderer.ResolveCommandChainRecordingWorkerIndex(key, workerCount: 6);

        first.ShouldBe(afterOtherJobsDisappear);
        first.ShouldBeInRange(0, 5);
        VulkanRenderer.ResolveCommandChainRecordingWorkerIndex(key, workerCount: 1).ShouldBe(0);
    }

    [Test]
    public void CompatiblePublication_RefreshesDescriptorsWithoutDirtyingEveryPrimary()
    {
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");

        lowering.ShouldContain("RenderResourceChangeKind.CompatibleContentPublication");
        lowering.ShouldContain("dirtyReason & ~CommandChainDirtyReason.DescriptorGeneration");
        pipeline.ShouldContain("ClassifyTextureBindingChange");
        pipeline.ShouldContain("RenderResourceChangeKind.StructuralLayout");
    }

    [Test]
    public void DescriptorAllocationIdentity_UsesImmutableResourcesOnlyWithoutUpdateAfterBind()
    {
        VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey immutableIdentity = new(
            LayoutFingerprint: 11,
            SchemaFingerprint: 12,
            DescriptorFrameSlotCount: 3,
            SetCount: 4,
            MaterialIdentity: 5,
            MaterialBindingLayoutVersion: 6,
            ViewFamilyIdentity: 7,
            DrawUniformSlot: 8,
            BindingIdentityFingerprint: 9,
            ImmutableResourceFingerprint: 20);
        VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey changedContent = immutableIdentity with
        {
            ImmutableResourceFingerprint = 21,
        };
        VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey changedBinding = immutableIdentity with
        {
            BindingIdentityFingerprint = 10,
        };
        VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey updateAfterBindIdentity = immutableIdentity with
        {
            ImmutableResourceFingerprint = 0,
        };
        VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey sameUpdateAfterBindIdentity = updateAfterBindIdentity with { };

        changedContent.ShouldNotBe(immutableIdentity);
        changedBinding.ShouldNotBe(immutableIdentity);
        sameUpdateAfterBindIdentity.ShouldBe(updateAfterBindIdentity);
    }

    [Test]
    public void CompatiblePublication_UpdatesOnlyTheCompletedDescriptorSlot()
    {
        const ulong previousResource = 41;
        const ulong publishedResource = 42;
        ulong[] slotFingerprints = [previousResource, previousResource, previousResource];

        for (int completedSlot = 0; completedSlot < slotFingerprints.Length; completedSlot++)
        {
            VulkanRenderer.VkMaterial.DescriptorSlotRequiresPublication(
                    slotFingerprints,
                    completedSlot,
                    publishedResource)
                .ShouldBeTrue();

            slotFingerprints[completedSlot] = publishedResource;
            for (int occupiedSlot = completedSlot + 1; occupiedSlot < slotFingerprints.Length; occupiedSlot++)
                slotFingerprints[occupiedSlot].ShouldBe(previousResource);
        }

        slotFingerprints.ShouldAllBe(static value => value == publishedResource);
    }

    [Test]
    public void MaterialDescriptorPublication_IsPerSlotAndWorkerSafe()
    {
        string material = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");

        material.ShouldContain("lock (_stateSync)");
        material.ShouldContain("UpdateFrameDescriptorSet(state, resolvedFrame)");
        material.ShouldContain("state.SlotResourceFingerprints[resolvedFrame] = resourceFingerprint;");
        material.ShouldNotContain("UpdateDescriptorSets(state)");
    }

    [Test]
    public void DescriptorContents_AreSnapshottedPerSubmissionNotBakedIntoCommandDependencies()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        lifetime.ShouldContain("commandLifetime.RefreshTouchedDependencies();");
        lifetime.ShouldContain("TryAppendSubmittedDescriptorDependency_NoLock");
        lifetime.ShouldContain("ResourceKey(ObjectType.Image, backingImageHandle)");
        lifetime.ShouldNotContain("batch.RecordDependency(snapshot.References[i])");
        lifetime.ShouldNotContain("TrackVulkanCommandBufferResource_NoLock(commandBufferHandle, pair.First");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return File.ReadAllText(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
