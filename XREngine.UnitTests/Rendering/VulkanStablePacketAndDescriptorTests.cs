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
    public void CommandChainContainers_RebuildWithoutSteadyStateAllocations()
    {
        const int drawCount = VulkanRenderer.MaxMeshDrawsPerRenderPacket;
        const string targetName = "SteadyTarget";
        RenderViewKey viewKey = new(1, 2, 0, RenderViewKind.Main, 0, -1);
        DrawPacket[] draws = new DrawPacket[drawCount];
        for (int i = 0; i < draws.Length; i++)
        {
            draws[i] = new DrawPacket(
                i,
                RendererIdentity: 3,
                MeshIdentity: i + 4,
                MaterialIdentity: 5,
                ProgramIdentity: 6,
                InstanceCount: 1,
                Transparent: false,
                StructuralSignature: (ulong)(i + 7),
                FrameDataSignature: (ulong)(i + 8));
        }

        CommandChainKey[] chainKeys = new CommandChainKey[drawCount];
        for (int i = 0; i < chainKeys.Length; i++)
            chainKeys[i] = new CommandChainKey(0, viewKey, 9, 10, false, i);

        RenderPacket packet = new();
        RenderPassChainGroup group = new();
        CommandChainSchedule schedule = new();
        RenderPassChainGroup[] groups = [group];
        DescriptorBindingSnapshot descriptors = new(11, 3, 12);
        ResourcePlanSnapshot resources = new(13, 14, 15, 16);

        ResetContainers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
            ResetContainers();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
        packet.DrawCount.ShouldBe(drawCount);
        packet.GetDraw(drawCount - 1).OpIndex.ShouldBe(drawCount - 1);
        group.ChainKeys.Length.ShouldBe(drawCount);
        schedule.Groups.Length.ShouldBe(1);

        void ResetContainers()
        {
            packet.Reset(
                viewKey,
                passIndex: 9,
                targetIdentity: 10,
                targetName,
                RenderPacketVolatility.FrameDataOnly,
                draws,
                ReadOnlySpan<DispatchPacket>.Empty,
                descriptors,
                resources,
                structuralSignature: 17,
                frameDataSignature: 18,
                sourceStartIndex: 0,
                sourceCount: drawCount,
                dynamicOverlay: false);
            group.Reset(9, 10, targetName, chainKeys, 17, supportsSecondaryCommandBuffers: true, dynamicOverlay: false);
            schedule.Reset(17, 13, groups);
        }
    }

    [Test]
    public void IndirectDrawStateCapabilityScope_IsAValueTypeToAvoidPerBucketAllocation()
        => typeof(IndirectDrawStateCapabilityScope).IsValueType.ShouldBeTrue();

    [Test]
    public void GpuIndirectCommandChains_KeepMutableArgumentStreamsOnPrimary()
    {
        VulkanRenderer.IndirectCommandChainSecondaryRecordingSafe.ShouldBeFalse();

        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        recording.ShouldContain("if (!IndirectCommandChainSecondaryRecordingSafe ||");
        recording.ShouldContain("RecordIndirectDrawIntoCommandBuffer(commandBuffer, indirectOp, opPassIndex);");
        recording.ShouldContain("usedSecondary: false");
    }

    [Test]
    public void MutableGpuDrivenPrimaries_AreNeverCleanReuseCandidates()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string diagnostics = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.FrameOpDiagnostics.cs");

        recording.ShouldContain("bool hasMutableGpuDrivenFrameOps = hasStaticFrameOps && HasMutableGpuDrivenFrameOps(ops);");
        recording.ShouldContain("!hasMutableGpuDrivenFrameOps &&");
        recording.ShouldContain("\"mutable-gpu-driven-frame-ops\"");
        diagnostics.ShouldContain("IndirectDrawOp or MeshTaskDispatchIndirectCountOp");
        diagnostics.ShouldNotContain("ComputeDispatchOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp");
    }

    [Test]
    public void VulkanPrimaryReuse_IsEnabledAfterPublicationGenerationsAreKeyed()
    {
        VulkanRenderer.VulkanPrimaryCommandBufferReuseSafe.ShouldBeTrue();

        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        state.ShouldContain("VulkanPrimaryCommandBufferReuseSafe &&");
        state.ShouldContain("immutable dependency");
        state.ShouldContain("RuntimeRenderingHostServices.Current.EnableVulkanPrimaryCommandBufferReuse");
    }

    [Test]
    public void MutableGpuDrivenFrames_BypassCommandChainSecondaries()
    {
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");

        lowering.ShouldContain("ResolveMeshSubmissionStrategy().IsGpuZeroReadbackStrategy()");
        lowering.ShouldContain("HasMutableGpuDrivenFrameOps(staticOps) || HasMutableGpuDrivenFrameOps(volatileOps)");
        lowering.ShouldContain("Vulkan.CommandChains.MutableGpuFrameInline");
        lowering.ShouldContain("command-chain publication generations are tracked");
    }

    [Test]
    public void AsyncBackendCompile_IsExplicitAndOptIn()
    {
        XRRenderProgram program = new();
        program.AllowAsyncBackendCompile.ShouldBeFalse();

        program.AllowAsyncBackendCompile = true;

        program.AllowAsyncBackendCompile.ShouldBeTrue();
    }

    [TestCase(XRRenderProgram.EShaderProgramBackendStage.SourceQueued)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Compiling)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Linking)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.QueueBackpressure)]
    public void IndirectProgramReadinessDeferral_IsNotAForbiddenFallback(
        XRRenderProgram.EShaderProgramBackendStage stage)
        => HybridRenderingManager.IsIndirectGraphicsProgramTerminalFailure(stage).ShouldBeFalse();

    [TestCase(XRRenderProgram.EShaderProgramBackendStage.BinaryUploadFailed)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Failed)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Abandoned)]
    public void IndirectProgramTerminalFailure_IsAForbiddenFallback(
        XRRenderProgram.EShaderProgramBackendStage stage)
        => HybridRenderingManager.IsIndirectGraphicsProgramTerminalFailure(stage).ShouldBeTrue();

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
        worker.ShouldContain("lock (_frameOpResourcePlannerReadbackLock)");
        worker.IndexOf("lock (_frameOpResourcePlannerReadbackLock)", StringComparison.Ordinal)
            .ShouldBeLessThan(worker.IndexOf("EnterFrameOpResourcePlannerReadbackScope(firstDraw.Context)", StringComparison.Ordinal));
        worker.IndexOf("EnterFrameOpResourcePlannerReadbackScope(firstDraw.Context)", StringComparison.Ordinal)
            .ShouldBeLessThan(worker.IndexOf("for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)", StringComparison.Ordinal));
        worker.ShouldContain("chain.State = CommandChainState.Recorded;");
        worker.ShouldContain("A prewarmed Vulkan command-chain draw became unavailable during secondary recording.");
        worker.ShouldNotContain("bool pipelinesReady");
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
    public void WorkerPoolAssignment_UsesStableMutableRendererAffinity()
    {
        var renderer = (VulkanRenderer.VkMeshRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(VulkanRenderer.VkMeshRenderer));
        VulkanRenderer.VulkanMeshFrameDataFamilyKey firstFamily = new(
            2, VulkanRenderer.EVulkanMeshFrameDataStreamKind.Primary, default,
            3, 4, 5, 6, 7, 8, false, false);
        VulkanRenderer.VulkanMeshFrameDataRendererFamilyKey firstKey = new(renderer, firstFamily);
        VulkanRenderer.VulkanMeshFrameDataRendererFamilyKey anotherFamilyOfSameRenderer = new(
            renderer,
            firstFamily with { ViewportIdentity = 99 });

        int first = VulkanRenderer.ResolveCommandChainRecordingWorkerIndex(firstKey, workerCount: 6);
        int afterOtherJobsDisappear = VulkanRenderer.ResolveCommandChainRecordingWorkerIndex(firstKey, workerCount: 6);

        first.ShouldBe(afterOtherJobsDisappear);
        VulkanRenderer.ResolveCommandChainRecordingWorkerIndex(anotherFamilyOfSameRenderer, workerCount: 6)
            .ShouldBe(first);
        first.ShouldBeInRange(0, 5);
        VulkanRenderer.ResolveCommandChainRecordingWorkerIndex(firstKey, workerCount: 1).ShouldBe(0);
    }

    [Test]
    public void CommandChainRendererFamily_MixedChainsRequireSerialRecording()
    {
        var firstRenderer = (VulkanRenderer.VkMeshRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(VulkanRenderer.VkMeshRenderer));
        var secondRenderer = (VulkanRenderer.VkMeshRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(VulkanRenderer.VkMeshRenderer));
        VulkanRenderer.FrameOpContext firstContext = new(1, 2, null, null, null);
        VulkanRenderer.FrameOpContext differentFamilyContext = firstContext with { ViewportIdentity = 3 };
        VulkanRenderer.PendingMeshDraw firstDraw = default(VulkanRenderer.PendingMeshDraw) with { Renderer = firstRenderer };
        VulkanRenderer.PendingMeshDraw secondDraw = default(VulkanRenderer.PendingMeshDraw) with { Renderer = secondRenderer };
        CommandChain chain = new(new CommandChainKey(
            0,
            new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1),
            0,
            0,
            false,
            1))
        {
            SourceStartIndex = 0,
            SourceCount = 2,
        };

        VulkanRenderer.FrameOp[] homogeneousOps =
        [
            new VulkanRenderer.MeshDrawOp(0, null, firstDraw, firstContext),
            new VulkanRenderer.MeshDrawOp(0, null, firstDraw, firstContext),
        ];
        VulkanRenderer.FrameOp[] mixedRendererOps =
        [
            homogeneousOps[0],
            new VulkanRenderer.MeshDrawOp(0, null, secondDraw, firstContext),
        ];
        VulkanRenderer.FrameOp[] mixedFamilyOps =
        [
            homogeneousOps[0],
            new VulkanRenderer.MeshDrawOp(0, null, firstDraw, differentFamilyContext),
        ];

        VulkanRenderer.TryResolveCommandChainRecordingRendererFamily(
                homogeneousOps,
                chain,
                frameDataSlot: 0,
                VulkanRenderer.EVulkanMeshFrameDataStreamKind.Primary,
                out VulkanRenderer.VulkanMeshFrameDataRendererFamilyKey rendererFamily)
            .ShouldBeTrue();
        rendererFamily.Renderer.ShouldBeSameAs(firstRenderer);
        VulkanRenderer.TryResolveCommandChainRecordingRendererFamily(
                mixedRendererOps,
                chain,
                frameDataSlot: 0,
                VulkanRenderer.EVulkanMeshFrameDataStreamKind.Primary,
                out _)
            .ShouldBeFalse();
        VulkanRenderer.TryResolveCommandChainRecordingRendererFamily(
                mixedFamilyOps,
                chain,
                frameDataSlot: 0,
                VulkanRenderer.EVulkanMeshFrameDataStreamKind.Primary,
                out _)
            .ShouldBeFalse();
    }

    [Test]
    public void WorkerDispatch_UsesStablePoolCapacityAndRejectsIncompleteBatches()
    {
        string workers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainWorkers.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        workers.ShouldContain("workerCount = workers.Length;");
        workers.ShouldContain("private const bool ParallelCommandChainWorkerRecordingSafe = false;");
        workers.ShouldContain("ParallelCommandChainWorkerRecordingSafe &&");
        workers.ShouldContain("batch.ActiveWorkerMask");
        workers.ShouldContain("_commandChainRecordingWorkerCountdown.Reset(activeWorkerCount);");
        recording.ShouldContain("TryResolveCommandChainRecordingRendererFamily(");
        recording.ShouldContain("recordJobWorkerIndices[jobIndex] < 0");
        recording.IndexOf("MarkCommandChainSecondaryCommandBufferInvalid(chain);", StringComparison.Ordinal)
            .ShouldBeLessThan(recording.IndexOf("DispatchCommandChainRecordingWorkers(batch, workers, workerCount)", StringComparison.Ordinal));
    }

    [Test]
    public void CommandBufferReuse_GuardsNativeResetAndReplacesPendingSecondaries()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string secondaries = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");

        lifetime.ShouldContain("private bool CanResetVulkanCommandBuffer(");
        lifetime.IndexOf("CanResetVulkanCommandBuffer(commandBuffer, out string reason)", StringComparison.Ordinal)
            .ShouldBeLessThan(lifetime.IndexOf("return Api!.ResetCommandBuffer(commandBuffer, 0);", StringComparison.Ordinal));
        lowering.ShouldContain("CanResetVulkanCommandBuffer(secondary, out _)");
        recording.ShouldNotContain("Api!.ResetCommandBuffer(");
        secondaries.ShouldNotContain("Api!.ResetCommandBuffer(");
    }

    [Test]
    public void CompatiblePublication_StillInvalidatesCommandBuffersThatRecordedAnUpdatedSet()
    {
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorSets.cs");
        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");

        lowering.ShouldContain("RenderResourceChangeKind.CompatibleContentPublication");
        descriptors.ShouldContain("TryCaptureDescriptorUpdateInvalidations_NoLock(");
        descriptors.ShouldContain("InvalidateCachedCommandBuffersByHandle(");
        descriptors.ShouldContain("setState.UsesUpdateAfterBind");
        pipeline.ShouldContain("ClassifyTextureBindingChange");
        pipeline.ShouldContain("RenderResourceChangeKind.StructuralLayout");
    }

    [Test]
    public void DescriptorAllocationIdentity_UsesImmutableResourcesOnlyWithoutUpdateAfterBind()
    {
        VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey immutableIdentity = new(
            LayoutFingerprint: 11,
            SchemaFingerprint: 12,
            ProgramBindingId: 13,
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
        VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey changedProgram = immutableIdentity with
        {
            ProgramBindingId = 14,
        };
        VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey updateAfterBindIdentity = immutableIdentity with
        {
            ImmutableResourceFingerprint = 0,
        };
        VulkanRenderer.VkMeshRenderer.DescriptorAllocationKey sameUpdateAfterBindIdentity = updateAfterBindIdentity with { };

        changedContent.ShouldNotBe(immutableIdentity);
        changedBinding.ShouldNotBe(immutableIdentity);
        changedProgram.ShouldNotBe(immutableIdentity);
        sameUpdateAfterBindIdentity.ShouldBe(updateAfterBindIdentity);
    }

    [Test]
    public void CapturedDescriptorReuse_RefreshesNonUpdateAfterBindSetsOnlyAfterTheirSlotCompletes()
    {
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");

        descriptors.ShouldContain(
            "bool allowCompletedDescriptorSlotRefresh = refreshFrameIndex is { } completedFrameIndex &&");
        descriptors.ShouldContain("Renderer.CanUpdateCompletedDescriptorFrameSlot(completedFrameIndex)");
        descriptors.ShouldContain("!Renderer.CanUpdateCompletedDescriptorFrameSlot(frameIndex)");
        descriptors.ShouldContain("captured descriptor frame slot {frameIndex} is still in flight");
        descriptors.ShouldContain("recordDescriptorTableGeneration: false");
        descriptors.ShouldContain("if (recordDescriptorTableGeneration)");
        descriptors.ShouldNotContain("captured descriptor allocation is immutable and requires a new resource snapshot");
        state.ShouldContain("internal bool CanUpdateCompletedDescriptorFrameSlot(int frameDataSlot)");
        state.ShouldContain("_swapchainImageTimelineValues");
        state.ShouldContain("_frameSlotTimelineValues");
        state.ShouldContain("HasTimelineValueCompleted(_graphicsTimelineSemaphore, completionValue)");
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
