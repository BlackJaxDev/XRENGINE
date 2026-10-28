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
        string resources = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");
        string frameSlots = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.FrameSlots.cs");
        string openXrResources = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.OpenXR.ResourcesPressure.cs");

        resources.ShouldContain("internal bool TryBeginDestroyPipelineLayout(");
        resources.ShouldContain("tracker.FenceResourceRecordingAdmission(key, owner);");
        resources.ShouldContain("Lifetime.PublishTrackingDependenciesBeforeRetirement(key);");
        resources.ShouldContain("new VulkanRetiredPipelineLayout(pipelineLayout, ticket");
        resources.ShouldContain("Lifetime.Tracker.IsRetirementReady(candidate.Ticket)");
        resources.ShouldContain("internal unsafe void DrainRetiredPipelineLayouts(");
        resources.ShouldContain("CompleteSimpleResourceDestruction(\n                ObjectType.PipelineLayout");
        resources.ShouldNotContain("Pipeline-layout destruction deferred until shutdown");
        frameSlots.ShouldContain("ResourceRuntime.DrainRetiredPipelineLayouts(");
        openXrResources.ShouldContain("ResourceRuntime.DrainRetiredPipelineLayouts(Api!, _deviceContext.Device, i, retirementBudgetPerType)");
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
        string submission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs");
        string method = SliceBetween(
            submission,
            "internal VulkanSubmissionReceipt SubmitToQueueTrackedWithDisposition(",
            "internal unsafe VulkanSubmissionReceipt SubmitToGraphicsTimelineTrackedWithDisposition(");

        int imageValidation = method.IndexOf("ValidateOrderedCommandBufferImageStateContracts", StringComparison.Ordinal);
        int lifetimeValidation = method.IndexOf("TryAcquireSubmissionLifetimePins", StringComparison.Ordinal);
        int dispatch = method.IndexOf("SubmitNative", StringComparison.Ordinal);
        int successfulUse = method.IndexOf("PublishSuccessfulSubmissionLifetime", StringComparison.Ordinal);

        imageValidation.ShouldBeGreaterThanOrEqualTo(0);
        lifetimeValidation.ShouldBeGreaterThan(imageValidation);
        dispatch.ShouldBeGreaterThan(lifetimeValidation);
        successfulUse.ShouldBeGreaterThan(dispatch);
        method.ShouldContain("submit-rejected-validation");
        // The successful-use publication transfers the submission lifetime pins.
        method.ShouldContain("out lifetimePinsTransferred,");
        string publication = SliceBetween(
            submission,
            "private unsafe VulkanLifetimeSubmission PublishSuccessfulSubmissionLifetime(",
            "private void QuarantineUnpublishedAcceptedSubmission(");
        publication.ShouldContain("out bool lifetimeOwnershipPublished,");
        publication.ShouldContain("lifetimeOwnershipPublished = true;");
    }

    [Test]
    public void CommandPoolHostOperations_AreExternallySynchronized()
    {
        string commandPools = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Allocation/VulkanRenderer.CommandPool.cs");
        string lifetimeNative = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.LifetimeNativeServices.cs");
        string commandRuntime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.cs");

        commandPools.ShouldContain("internal void DestroyCommandPoolHostSynchronized(CommandPool pool)");
        commandPools.ShouldContain("internal Result ResetVulkanCommandPoolTracked(CommandPool pool, string owner)");
        Regex.IsMatch(
            commandPools,
            @"VulkanFrameLockScope\.Enter\(\s*CommandPoolsGate,\s*EVulkanFrameWaitReason\.CommandPool\)",
            RegexOptions.CultureInvariant).ShouldBeTrue("Command pool host operations must hold the command pool gate.");
        commandPools.ShouldContain("batch.IsRecording ||\n                                            batch.QueuedSubmissionCount != 0");
        lifetimeNative.ShouldContain("internal unsafe Result AllocateCommandBuffersWithLifetime(");
        lifetimeNative.ShouldContain("lock (Pools.Gate)");
        int nativeAllocation = lifetimeNative.IndexOf("Api.AllocateCommandBuffers(", StringComparison.Ordinal);
        int registration = lifetimeNative.IndexOf("ResourceRuntime.RegisterAllocatedCommandBuffer(", StringComparison.Ordinal);
        registration.ShouldBeGreaterThan(nativeAllocation);
        commandRuntime.ShouldContain("internal Result BeginTrackedCommandBuffer(");
        string beginTracked = SliceBetween(
            commandRuntime,
            "private Result BeginTrackedCommandBuffer(",
            "private ulong InitializeCommandBufferBindState(");
        int poolGate = Regex.Match(
            beginTracked,
            @"VulkanFrameLockScope\.Enter\(\s*Pools\.Gate,\s*EVulkanFrameWaitReason\.CommandPool\)",
            RegexOptions.CultureInvariant).Index;
        poolGate.ShouldBeGreaterThan(0, "The native begin call must run while the command pool gate is held.");
        beginTracked.IndexOf("api.BeginCommandBuffer(commandBuffer, ref beginInfo)", StringComparison.Ordinal)
            .ShouldBeGreaterThan(poolGate);
    }

    [Test]
    public void CommandBufferRetirement_WaitsForCpuRecordingAndQueueOwnership()
    {
        string commandRuntime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.cs");

        commandRuntime.ShouldContain("private bool IsCommandBufferRetirementReady(");
        commandRuntime.ShouldContain("batch.IsRecording || batch.QueuedSubmissionCount != 0");
        commandRuntime.ShouldContain("lifetime.QueuedSubmissionCount != 0");
        commandRuntime.ShouldContain("if (!IsCommandBufferRetirementReady(\n                        resourceRuntime,");
        commandRuntime.ShouldContain("return tracker.IsRetirementReadyNoLock(ticket);");
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
        string laneAttachments = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.RenderLaneAttachments.cs");
        string laneArena = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/VulkanLaneCommandFamilyArena.cs");
        string commandPools = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Allocation/VulkanRenderer.CommandPool.cs");

        lowering.ShouldContain("if (ownsPool && pool.Handle != 0)\n            MarkOwnedCommandChainSecondaryPoolPendingDestroy(pool);");

        // Worker lane arenas stop recording, then release their pools through the
        // deferred pool retirement path.
        string destroyWorkers = SliceBetween(
            workers,
            "private void DestroyCommandChainRecordingWorkers()",
            "private void DestroyCommandChainRecordingWorkerPools()");
        int quiesce = destroyWorkers.IndexOf("QuiesceCommandChainRecordingWorkersForRetirement();", StringComparison.Ordinal);
        quiesce.ShouldBeGreaterThanOrEqualTo(0);
        destroyWorkers.IndexOf("DestroyRenderLaneCommandAttachments();", StringComparison.Ordinal).ShouldBeGreaterThan(quiesce);

        int clear = laneAttachments.IndexOf("arena.ClearAfterRetirement();", StringComparison.Ordinal);
        clear.ShouldBeGreaterThanOrEqualTo(0);
        laneAttachments.IndexOf("DestroyCommandPoolHostSynchronized(transientPool);", clear, StringComparison.Ordinal).ShouldBeGreaterThan(clear);
        laneAttachments.IndexOf("DestroyCommandPoolHostSynchronized(retainedPool);", clear, StringComparison.Ordinal).ShouldBeGreaterThan(clear);
        laneArena.ShouldContain("still owns \" +");
        laneArena.ShouldContain("retained command artifact(s) during pool retirement.");

        string destroyPool = SliceBetween(
            commandPools,
            "internal void DestroyCommandPoolHostSynchronized(CommandPool pool)",
            "internal void DestroyCommandPool()");
        destroyPool.ShouldContain("QueueCommandPoolRetirementTracked(");
        destroyPool.ShouldNotContain("DestroyCommandPool(");
    }

    [Test]
    public void CompletionTracking_CoversTimelineFenceQueueAndDeviceIdlePaths()
    {
        string trackedSubmission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs");
        string rendererApi = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.RendererApi.cs");
        string openXrCompatibility = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.OpenXRCompatibility.cs");
        string openXrSubmission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanCommandRuntime.OpenXrSubmission.cs");
        string queueIdle = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.ImGuiOutputHost.cs");
        string synchronous = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanSynchronousResourceCommandSession.cs");

        trackedSubmission.ShouldContain("internal void CompleteTrackedTimeline(");
        trackedSubmission.ShouldContain("internal void CompleteTrackedFence(");
        trackedSubmission.ShouldContain("internal void CompleteTrackedQueue(");
        trackedSubmission.ShouldContain("internal void CompleteTrackedDevice()");
        rendererApi.ShouldContain("if (result == Result.Success)\n            {\n                _commandRuntime.CompleteTrackedDevice();");
        openXrCompatibility.ShouldContain("if (result == Result.Success)\n                _commandRuntime.CompleteTrackedDevice();");
        openXrSubmission.ShouldContain("CompleteTrackedTimeline(");
        queueIdle.ShouldContain("_commandRuntime.CompleteTrackedQueue(queue);");
        synchronous.ShouldContain("_commands.CompleteTrackedFence(fence);");
    }

    [Test]
    public void RetirementQueues_CoverEveryPhase4ObjectFamily()
    {
        string queue = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Retirement/VulkanResourceRetirementQueue.cs");
        string drains = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");

        queue.ShouldContain("List<RetiredImageResourceEntry>[] Images");
        queue.ShouldContain("List<RetiredFramebuffer>[] Framebuffers");
        queue.ShouldContain("List<RetiredBuffer>[] Buffers");
        queue.ShouldContain("List<RetiredBufferView>[] BufferViews");
        queue.ShouldContain("List<RetiredDescriptorSet>[] DescriptorSets");
        queue.ShouldContain("List<RetiredDescriptorPool>[] DescriptorPools");
        queue.ShouldContain("List<RetiredCommandBuffer>[] CommandBuffers");
        queue.ShouldContain("List<RetiredCommandPool>[] CommandPools");
        queue.ShouldContain("List<RetiredPipeline>[] Pipelines");
        queue.ShouldContain("List<VulkanRetiredPipelineLayout>[] PipelineLayouts");
        queue.ShouldContain("List<VulkanRetiredDescriptorSetLayout>[] DescriptorSetLayouts");
        queue.ShouldContain("List<RetiredQueryPool>[] QueryPools");
        drains.ShouldContain("Lifetime.Tracker.IsRetirementReady(candidate.Ticket)");
    }

    [Test]
    public void VulkanDestruction_ForRuntimeFamilies_IsCentralizedExceptPresentationlessTargetOwnedQueryPools()
    {
        AssertRawVulkanCallOnlyIn(
            "DestroyFramebuffer(device",
            "Resources/Authority/VulkanResourceRuntime.cs");
        AssertRawVulkanCallOnlyIn(
            "FreeDescriptorSets(device",
            "Resources/Authority/VulkanResourceRuntime.cs");
        AssertRawVulkanCallOnlyIn(
            "DestroyQueryPool(device",
            "Resources/Authority/VulkanResourceRuntime.cs",
            "Bootstrap/Targets/VulkanPresentationlessTargetDriver.cs");
        AssertRawVulkanCallOnlyIn(
            "DestroyBufferView(device",
            "Resources/Authority/VulkanResourceRuntime.cs");
    }

    [Test]
    public void DescriptorLifetime_PreventsIllegalMutationAndTracksPoolChildren()
    {
        string authority = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs");

        authority.ShouldContain("Cannot update in-flight Vulkan descriptor set");
        authority.ShouldContain("CaptureDescriptorPoolRetirementTicket(");
        authority.ShouldContain("ValidateAndRecordWritesNoLock(");
        authority.ShouldContain("tracker.DescriptorSetsByPool");
        authority.ShouldContain("RemoveRetiredDescriptorSetsForPoolNoLock(");
        authority.ShouldContain("_lifetime.PublishTrackingDependenciesBeforeRetirement(key);");
    }

    [Test]
    public void DeviceLossTeardown_ForcesDestructionWithoutCompletingTimelines()
    {
        string deviceLoss = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.DeviceLoss.cs");
        string lifecycle = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.LifetimeLedger.cs");
        string tracker = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Lifetime/VulkanResourceLifetimeTracker.cs");

        deviceLoss.ShouldContain("_resourceRuntime.Lifetime.Tracker.DeviceLost = true;");
        deviceLoss.ShouldNotContain("CompleteTrackedDevice");
        lifecycle.ShouldContain("_resourceRuntime.BeginForcedRetirementDrain();");
        lifecycle.ShouldContain("_resourceRuntime.EndForcedRetirementDrain();");
        lifetime.ShouldContain("internal void MarkDeviceLost()");
        tracker.ShouldContain("ForcedResourceDestructionCount");
    }

    [Test]
    public void DescriptorSetLayout_DuplicateReleaseIsSkipped()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.DescriptorSetLayoutLifetime.cs");
        string cache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanDescriptorLayoutCache.cs");

        lifetime.ShouldContain("!Descriptors.LiveDescriptorSetLayoutHandles.TryRemove(layout.Handle, out _)");
        lifetime.ShouldContain("Lifetime.Retirement.AllDescriptorSetLayoutHandles.Contains(layout.Handle)");
        lifetime.ShouldContain("VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(");
        cache.ShouldContain("ResourceRuntime.DestroyDescriptorSetLayout(");
        cache.ShouldNotContain("Api.DestroyDescriptorSetLayout");
    }

    [Test]
    public void CommandRecording_TracksSecondaryCopyBlitAndDescriptorDependencies()
    {
        string encoder = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanTrackedCommandEncoder.cs");
        string native = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs");
        string coordination = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.ResourceCoordination.cs");
        string resources = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");

        encoder.ShouldContain("Runtime.TrackCommandBufferResource(");
        encoder.ShouldContain("Runtime.TrackExecutedCommandBuffers(");
        native.ShouldContain("CmdExecuteCommandsTracked");
        native.ShouldContain("CmdCopyBufferTracked");
        coordination.ShouldContain("internal void TrackCommandBufferResource(");
        coordination.ShouldContain("internal void TrackExecutedCommandBuffers(");
        coordination.ShouldContain("PublishCommandBufferDependencyAfterGenerationRace(");
        coordination.ShouldContain("tracker.GetPublishedGeneration(resourceKey) == observedGeneration");
        coordination.ShouldContain("!batch.IsRecording || batch.QueuedSubmissionCount != 0");
        resources.ShouldContain("internal void RegisterFramebuffer(");
        int fence = resources.IndexOf("tracker.FenceResourceRecordingAdmission(key, owner);", StringComparison.Ordinal);
        int publish = resources.IndexOf("Lifetime.PublishTrackingDependenciesBeforeRetirement(key);", fence, StringComparison.Ordinal);
        publish.ShouldBeGreaterThan(fence);
        AssertRawVulkanCallOnlyIn(
            "BeginCommandBuffer(",
            "Commands/Authority/VulkanCommandRuntime.cs");
    }

    [Test]
    public void PipelineCreation_RegistersEverySuccessfulNativeHandleGeneration()
    {
        (string File, string CreationPattern, string RegistrationPattern)[] contracts =
        [
            ("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiOutputPipelineService.cs", @"CreateGraphicsPipelines\(", @"\bresources\.RegisterPipeline\("),
            ("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Compute.cs", @"CreateComputePipelinesSynchronized\(", @"(?:ProgramCreationPort|request\.ProgramServices)\.RegisterPipeline\("),
            ("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.GraphicsPipelines.cs", @"CreateGraphicsPipelinesSynchronized\(", @"ProgramCreationPort\.RegisterPipeline\("),
            ("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgramPipeline.cs", @"Create(?:Graphics|Compute)PipelinesSynchronized\(", @"ProgramCreationPort\.RegisterPipeline\("),
            ("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanGraphicsPipelineFactory.cs", @"CreateGraphicsPipelineWithCachePolicy\(", @"request\.ProgramServices\.RegisterPipeline\("),
        ];

        foreach ((string file, string creationPattern, string registrationPattern) in contracts)
        {
            string source = ReadWorkspaceFile(file);
            MatchCollection nativeCreates = Regex.Matches(source, creationPattern, RegexOptions.CultureInvariant);
            nativeCreates.Count.ShouldBeGreaterThan(0, $"Expected a pipeline creation site in {file}.");

            // Several creation branches can write the same native handle variable, and a
            // creation helper can return its handle to the caller that registers it. Each
            // native handle target must therefore have a registration of that same handle.
            HashSet<string> createdHandles = new(StringComparer.Ordinal);
            foreach (Match nativeCreate in nativeCreates)
            {
                int statementEnd = source.IndexOf(';', nativeCreate.Index);
                statementEnd.ShouldBeGreaterThan(nativeCreate.Index);
                Match outTarget = Regex.Match(
                    source[nativeCreate.Index..statementEnd],
                    @"\bout\s+(?:Pipeline\s+)?(?<handle>[A-Za-z_][\w.]*)",
                    RegexOptions.CultureInvariant);
                outTarget.Success.ShouldBeTrue($"Expected an output pipeline handle for a creation site in {file}.");
                createdHandles.Add(outTarget.Groups["handle"].Value);
            }

            int registrations = Regex.Matches(source, registrationPattern, RegexOptions.CultureInvariant).Count;
            registrations.ShouldBeGreaterThanOrEqualTo(
                createdHandles.Count,
                $"Every successful pipeline creation site in {file} must register its native handle generation.");
            foreach (string handle in createdHandles)
            {
                Regex.IsMatch(
                        source,
                        registrationPattern + @"\s*" + Regex.Escape(handle) + @"\s*,",
                        RegexOptions.CultureInvariant)
                    .ShouldBeTrue($"Pipeline handle '{handle}' created in {file} must register its native handle generation.");
            }
        }
    }

    [Test]
    public void ParentChildAndDescriptorOwnership_AreRetainedUntilSafeDestruction()
    {
        string tracker = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Lifetime/VulkanResourceLifetimeTracker.cs");
        string resources = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs");
        string retirementQueue = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Retirement/VulkanResourceRetirementQueue.cs");

        tracker.ShouldContain("CommandBuffersByPool");
        tracker.ShouldContain("DescriptorSetsByPool");
        tracker.ShouldContain("FramebufferAttachments");
        resources.ShouldContain("HasUndestroyedBufferView(");
        resources.ShouldContain("HasUndestroyedImageDependency(");
        resources.ShouldContain("UpdateResourceCompletionStateNoLock(");
        descriptors.ShouldContain("RemoveRetiredDescriptorSetsForPoolNoLock(");
        retirementQueue.ShouldContain("internal HashSet<ulong> AllPipelineHandles");
        retirementQueue.ShouldContain("internal HashSet<VulkanPinnedResourceGeneration> AllImageViewHandles");
        AssertRawVulkanCallOnlyIn(
            "AllocateCommandBuffers(",
            "Commands/Authority/VulkanCommandRuntime.LifetimeNativeServices.cs");
    }

    [Test]
    public void MipmapBarriers_UseTrackedResourceRecordingGateway()
    {
        string texture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.Mipmaps.cs");
        string wrapper = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanResourceCommandWrapperPort.cs");
        string mipmapMethod = SliceBetween(
            texture,
            "protected void GenerateMipmapsWithBlit()",
            "private ImageBlit CreateMipBlit");

        mipmapMethod.ShouldContain("ResourceCommandPort.GenerateMipmaps(");
        wrapper.ShouldContain("using VulkanSynchronousResourceCommandSession session = Begin(owner);");
        wrapper.ShouldContain("session.Encoder.PipelineBarrier(");
        wrapper.ShouldContain("session.Encoder.BlitImage(");
        wrapper.ShouldNotContain("Api.CmdPipelineBarrier(");
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
