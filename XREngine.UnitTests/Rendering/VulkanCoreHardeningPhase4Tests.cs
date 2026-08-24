using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using System.Text.RegularExpressions;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCoreHardeningPhase4Tests
{
    [Test]
    public void RetirementTicketMerge_PreservesStrongestCompletionPoint()
    {
        VulkanRetirementTicket first = new(
            GraphicsSequence: 4,
            TransferSequence: 8,
            OtherSequence: 2,
            EnqueuedTimestamp: 200,
            ResourceGeneration: 7,
            ExternalOwnershipPending: false,
            PinSet: VulkanRetirementPinSet.Single(
                new VulkanResourceLifetimeKey(ObjectType.Buffer, 0xA),
                7));
        VulkanRetirementTicket second = new(
            GraphicsSequence: 9,
            TransferSequence: 3,
            OtherSequence: 5,
            EnqueuedTimestamp: 100,
            ResourceGeneration: 11,
            ExternalOwnershipPending: true,
            PinSet: VulkanRetirementPinSet.Single(
                new VulkanResourceLifetimeKey(ObjectType.ImageView, 0xB),
                11));

        VulkanRetirementTicket merged = first.Merge(second);

        merged.GraphicsSequence.ShouldBe(9UL);
        merged.TransferSequence.ShouldBe(8UL);
        merged.OtherSequence.ShouldBe(5UL);
        merged.EnqueuedTimestamp.ShouldBe(100L);
        merged.ResourceGeneration.ShouldBe(11UL);
        merged.ExternalOwnershipPending.ShouldBeTrue();
        merged.PinSet.ShouldNotBeNull().Count.ShouldBe(2);
    }

    [Test]
    public void PipelineLayoutsUseExactTicketDeferredRetirementInsteadOfShutdownRetention()
    {
        string layouts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Pipelines/VulkanRenderer.PipelineLayoutLifetime.cs");
        string frameSlots = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.FrameSlots.cs");
        string openXrResources = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.ResourcesPressure.cs");

        layouts.ShouldContain("RetiredPipelineLayout(");
        layouts.ShouldContain("CaptureVulkanRetirementTicket(");
        layouts.ShouldContain("IsVulkanRetirementReady(candidate.Ticket)");
        layouts.ShouldContain("DrainRetiredPipelineLayouts(int frameSlot, int maxItems)");
        layouts.ShouldContain("CompleteVulkanResourceDestruction(\n                ObjectType.PipelineLayout");
        layouts.ShouldNotContain("Pipeline-layout destruction deferred until shutdown");
        frameSlots.ShouldContain("DrainRetiredPipelineLayouts();");
        openXrResources.ShouldContain("DrainRetiredPipelineLayouts(i, int.MaxValue)");
    }

    [Test]
    public void LifetimeState_SeparatesCpuRecordedSubmittedExternalAndRetirementOwnership()
    {
        EVulkanResourceLifetimeState values =
            EVulkanResourceLifetimeState.CpuOwned |
            EVulkanResourceLifetimeState.Recorded |
            EVulkanResourceLifetimeState.Submitted |
            EVulkanResourceLifetimeState.Completed |
            EVulkanResourceLifetimeState.External |
            EVulkanResourceLifetimeState.PendingRetirement |
            EVulkanResourceLifetimeState.Destroyed |
            EVulkanResourceLifetimeState.Queued;

        Enum.GetValues<EVulkanResourceLifetimeState>()
            .Where(static value => value != EVulkanResourceLifetimeState.None)
            .ShouldAllBe(value => values.HasFlag(value));
    }

    [Test]
    public void LifetimeRejectionDiagnostic_NamesEveryRequiredRaceIdentity()
    {
        VulkanRetirementTicket ticket = new(
            GraphicsSequence: 13,
            TransferSequence: 2,
            OtherSequence: 0,
            EnqueuedTimestamp: 100,
            ResourceGeneration: 41,
            ExternalOwnershipPending: false);
        VulkanLifetimeRejectionDiagnostic diagnostic = new(
            new VulkanResourceLifetimeKey(ObjectType.Buffer, 0xCAFE),
            "StrictStereo.UniformBuffer",
            OldGeneration: 40,
            NewGeneration: 41,
            Output: "OpenXR.TrueSinglePassStereo",
            CommandBufferHandle: 0xBEEF,
            ticket,
            EVulkanResourceLifetimeState.PendingRetirement,
            "recorded dependency is pending retirement");

        diagnostic.ToString().ShouldBe(
            "resource=Buffer:0xCAFE owner=StrictStereo.UniformBuffer oldGeneration=40 newGeneration=41 " +
            "output=OpenXR.TrueSinglePassStereo commandBuffer=0xBEEF " +
            "retirementTicket=gfx:13/transfer:2/other:0/generation:41/external:False/pins:0 " +
            "state=PendingRetirement reason=recorded dependency is pending retirement");
    }

    [Test]
    public void QueueGateway_ValidatesDependenciesBeforeDispatchAndRecordsSuccessfulUse()
    {
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Synchronization/VulkanRenderer.Synchronization.cs");
        string method = SliceBetween(
            synchronization,
            "private Result SubmitToQueueTracked(",
            "internal Result WaitForQueueIdleTracked(");

        int validation = method.IndexOf("ValidateVulkanSubmissionResourceLifetimes", StringComparison.Ordinal);
        int dispatch = method.IndexOf("Api!.QueueSubmit", StringComparison.Ordinal);
        int successfulUse = method.IndexOf("RecordSuccessfulVulkanSubmissionLifetime", StringComparison.Ordinal);

        validation.ShouldBeGreaterThanOrEqualTo(0);
        dispatch.ShouldBeGreaterThan(validation);
        successfulUse.ShouldBeGreaterThan(dispatch);
        method.ShouldContain("submit-rejected-resource-lifetime");
    }

    [Test]
    public void CommandPoolHostOperations_AreExternallySynchronized()
    {
        string commandPools = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Allocation/VulkanRenderer.CommandPool.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Retirement/VulkanRenderer.ResourceRetirement.cs");

        commandPools.ShouldContain("private Result AllocateCommandBuffersHostSynchronized(");
        commandPools.ShouldContain("private void FreeCommandBuffersHostSynchronized(");
        commandPools.ShouldContain("internal void DestroyCommandPoolHostSynchronized(CommandPool pool)");
        commandPools.ShouldContain("lock (_commandPoolsLock)");
        lifetime.ShouldContain("AllocateCommandBuffersHostSynchronized(ref allocateInfo, commandBuffers)");
        lifetime.ShouldContain("FreeCommandBuffersHostSynchronized(commandPool, 1, &commandBuffer)");
        retirement.ShouldContain("FreeCommandBuffersHostSynchronized(entry.CommandPool, 1, &commandBuffer)");
    }

    [Test]
    public void CommandBufferRetirement_WaitsForCpuRecordingAndQueueOwnership()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Retirement/VulkanRenderer.ResourceRetirement.cs");

        lifetime.ShouldContain("private bool IsVulkanCommandBufferRetirementReady(");
        lifetime.ShouldContain("batch.IsRecording || batch.QueuedSubmissionCount != 0");
        lifetime.ShouldContain("lifetime.QueuedSubmissionCount != 0");
        lifetime.ShouldContain("if (!IsVulkanCommandBufferRetirementReady(commandBuffer, ticket))");
        retirement.ShouldContain("IsVulkanCommandBufferRetirementReady(\n                            candidate.CommandBuffer,");
    }

    [Test]
    public void WorkerCommandPools_WaitForDeferredCommandBufferRetirement()
    {
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/VulkanRenderer.CommandChainSecondaryBuffers.cs");
        string workers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/VulkanRenderer.CommandChainWorkers.cs");

        lowering.ShouldContain("TrackOwnedCommandChainSecondaryCommandBuffer(workerPool, secondary);");
        lowering.ShouldContain("TrackOwnedCommandChainSecondaryCommandBuffer(workerPool, replacement);");
        workers.ShouldContain("MarkOwnedCommandChainSecondaryPoolPendingDestroy(pool);");
        workers.ShouldContain("worker.Arena.ClearAfterPoolRetirement();");
        workers.ShouldNotContain("DestroyCommandPoolHostSynchronized(pool);");
    }

    [Test]
    public void CompletionTracking_CoversTimelineFenceQueueAndDeviceIdlePaths()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        string syncObjects = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Synchronization/VulkanRenderer.SyncObjects.cs");
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Synchronization/VulkanRenderer.Synchronization.cs");
        string initialization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string openXrSubmission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.Submission.cs");
        string presentationless = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Targets/VulkanPresentationlessTargetDriver.cs");
        string transfer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanRenderer.TextureUploadTransfer.cs");

        lifetime.ShouldContain("NotifyVulkanTimelineCompleted");
        lifetime.ShouldContain("NotifyVulkanFenceCompleted");
        lifetime.ShouldContain("NotifyVulkanQueueIdle");
        lifetime.ShouldContain("NotifyVulkanDeviceIdle");
        syncObjects.ShouldContain("NotifyVulkanTimelineCompleted(semaphore, currentValue)");
        synchronization.ShouldContain("NotifyVulkanQueueIdle(queue)");
        initialization.ShouldContain("NotifyVulkanDeviceIdle()");
        openXrSubmission.ShouldContain("NotifyVulkanFenceCompleted(fence)");
        presentationless.ShouldContain("NotifyVulkanFenceCompleted(fence)");
        transfer.ShouldContain("NotifyVulkanFenceCompleted(submitted.Fence)");
    }

    [Test]
    public void RetirementQueues_CoverEveryPhase4ObjectFamily()
    {
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Retirement/VulkanRenderer.ResourceRetirement.cs");

        retirement.ShouldContain("RetiredImageResourceEntry");
        retirement.ShouldContain("RetiredFramebuffer");
        retirement.ShouldContain("RetiredBuffer");
        retirement.ShouldContain("RetiredBufferView");
        retirement.ShouldContain("RetiredDescriptorSet");
        retirement.ShouldContain("RetiredDescriptorPool");
        retirement.ShouldContain("RetiredCommandBuffer");
        retirement.ShouldContain("RetiredPipeline");
        retirement.ShouldContain("RetiredQueryPool");
        retirement.ShouldContain("IsVulkanRetirementReady(candidate.Ticket)");
    }

    [Test]
    public void VulkanDestruction_ForRuntimeFamilies_IsCentralizedExceptPresentationlessTargetOwnedQueryPools()
    {
        AssertRawVulkanCallOnlyIn(
            "DestroyFramebuffer(device",
            "Frame/Resources/Retirement/VulkanRenderer.ResourceRetirement.cs");
        AssertRawVulkanCallOnlyIn(
            "FreeDescriptorSets(device",
            "Frame/Resources/Retirement/VulkanRenderer.ResourceRetirement.cs");
        AssertRawVulkanCallOnlyIn(
            "ResetDescriptorPool(device",
            "Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        AssertRawVulkanCallOnlyIn(
            "DestroyQueryPool(device",
            "Frame/Resources/Retirement/VulkanRenderer.ResourceRetirement.cs",
            "Bootstrap/Targets/VulkanPresentationlessTargetDriver.cs");
        AssertRawVulkanCallOnlyIn(
            "DestroyBufferView(device",
            "Frame/Resources/Retirement/VulkanRenderer.ResourceRetirement.cs");
    }

    [Test]
    public void DescriptorLifetime_PreventsIllegalMutationAndTracksPoolChildren()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        string descriptorSets = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorSets.cs");
        string commandState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/State/VulkanRenderer.CommandBufferState.cs");

        lifetime.ShouldContain("Cannot update in-flight Vulkan descriptor set");
        lifetime.ShouldContain("CaptureVulkanDescriptorPoolRetirementTicket");
        lifetime.ShouldContain("CanMutateVulkanDescriptorPool");
        descriptorSets.ShouldContain("ValidateAndRecordVulkanDescriptorWrites");
        commandState.ShouldContain("ResetVulkanDescriptorPoolTracked");
    }

    [Test]
    public void DeviceLossTeardown_ForcesDestructionWithoutCompletingTimelines()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Retirement/VulkanRenderer.ResourceRetirement.cs");
        string initialization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");

        lifetime.ShouldContain("NotifyVulkanResourceLifetimeDeviceLost");
        lifetime.ShouldContain("_resourceLifetimeTracker.ForcedResourceDestructionCount");
        retirement.ShouldContain("Force-destroying retired resources after device loss without waiting");
        initialization.ShouldContain("BeginForcedVulkanRetirementDrain");
        initialization.ShouldContain("EndForcedVulkanRetirementDrain");
    }

    [Test]
    public void DescriptorSetLayout_DuplicateReleaseIsSkipped()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorSetLayoutLifetime.cs");
        string cache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLayoutCache.cs");

        lifetime.ShouldContain("Skipping stale descriptor-set-layout destroy");
        lifetime.ShouldContain("_descriptorManager.LiveDescriptorSetLayoutHandles.TryRemove");
        cache.ShouldContain("TryBeginDestroyDescriptorSetLayout(layout, \"DescriptorLayoutCache.UncachedRelease\")");
        cache.ShouldNotContain("if (!_descriptorSetLayoutsByHandle.TryGetValue(layout.Handle, out CachedDescriptorSetLayout? cached))\n            {\n                Api!.DestroyDescriptorSetLayout");
    }

    [Test]
    public void CommandRecording_TracksSecondaryCopyBlitAndDescriptorDependencies()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        string commandState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/State/VulkanRenderer.CommandBufferState.cs");

        lifetime.ShouldContain("CmdExecuteCommandsTracked");
        lifetime.ShouldContain("CmdCopyBufferTracked");
        lifetime.ShouldContain("CmdCopyBufferToImageTracked");
        lifetime.ShouldContain("CmdCopyImageToBufferTracked");
        lifetime.ShouldContain("CmdCopyImageTracked");
        lifetime.ShouldContain("CmdBlitImageTracked");
        lifetime.ShouldContain("CommandBuffer.SecondaryExecution");
        lifetime.ShouldContain("RegisterVulkanFramebuffer");
        lifetime.ShouldContain("Framebuffer.Attachment");
        commandState.ShouldContain("TrackVulkanDescriptorSetBinding");
        commandState.ShouldContain("TrackVulkanCommandBufferResource");
    }

    [Test]
    public void PipelineCreation_RegistersEverySuccessfulNativeHandleGeneration()
    {
        string[] files =
        [
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgramPipeline.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs",
        ];

        foreach (string file in files)
        {
            string source = ReadWorkspaceFile(file);
            int nativeCreates = Regex.Matches(
                source,
                @"(?:Api!?\.Create(?:Graphics|Compute)Pipelines|Renderer\.Create(?:Graphics|Compute)PipelinesSynchronized|Renderer\.CreateGraphicsPipelineWithCachePolicy)\(",
                RegexOptions.CultureInvariant).Count;
            int registrations = Regex.Matches(
                source,
                @"RegisterVulkanPipeline\(",
                RegexOptions.CultureInvariant).Count;

            registrations.ShouldBe(
                nativeCreates,
                $"Every successful pipeline creation site in {file} must register its native handle generation.");
        }
    }

    [Test]
    public void ParentChildAndDescriptorOwnership_AreRetainedUntilSafeDestruction()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Retirement/VulkanRenderer.ResourceRetirement.cs");
        string retirementQueue = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Retirement/VulkanResourceRetirementQueue.cs");

        lifetime.ShouldContain("HasUndestroyedVulkanBufferViewReference");
        lifetime.ShouldContain("HasUndestroyedVulkanImageDependency");
        lifetime.ShouldContain("PropagateVulkanDescriptorSetSubmission_NoLock");
        lifetime.ShouldContain("UpdateVulkanResourceCompletionState_NoLock");
        retirement.ShouldContain("RemoveRetiredDescriptorSetsForPool_NoLock");
        retirement.ShouldContain("_resourceRetirementQueue.AllPipelineHandles");
        retirement.ShouldContain("_resourceRetirementQueue.AllImageViewHandles");
        retirementQueue.ShouldContain("internal HashSet<ulong> AllPipelineHandles");
        retirementQueue.ShouldContain("internal HashSet<VulkanRenderer.VulkanPinnedResourceGeneration> AllImageViewHandles");
        AssertRawVulkanCallOnlyIn(
            "AllocateCommandBuffers(device",
            "Commands/CommandBuffers/Allocation/VulkanRenderer.CommandPool.cs");
        AssertRawVulkanCallOnlyIn(
            "CreateImage(device",
            "Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        AssertRawVulkanCallOnlyIn(
            "DestroyImage(device",
            "Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs",
            "Frame/Resources/Retirement/VulkanRenderer.ResourceRetirement.cs",
            "Features/Upscaling/VulkanUpscaleBridgeSharedImage.cs");
    }

    [Test]
    public void MipmapBarriers_UseTrackedResourceRecordingGateway()
    {
        string texture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.Mipmaps.cs");
        string mipmapMethod = SliceBetween(
            texture,
            "protected void GenerateMipmapsWithBlit()",
            "private ImageBlit CreateMipBlit");

        mipmapMethod.ShouldContain("Renderer.CmdPipelineBarrierTracked");
        mipmapMethod.ShouldNotContain("Api.CmdPipelineBarrier(");
    }

    private static void AssertRawVulkanCallOnlyIn(string token, params string[] allowedRelativePaths)
    {
        string vulkanRoot = Path.Combine(
            ResolveRepoRoot(),
            "XREngine.Runtime.Rendering.Vulkan",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan");
        string[] offenders = Directory
            .EnumerateFiles(vulkanRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("VulkanUpscaleBridgeSidecar.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(vulkanRoot, path).Replace('\\', '/'))
            .Where(path => !allowedRelativePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        offenders.ShouldBeEmpty(
            $"Raw Vulkan call '{token}' must stay in {string.Join(", ", allowedRelativePaths)}.");
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
