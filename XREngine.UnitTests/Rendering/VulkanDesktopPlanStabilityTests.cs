using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanDesktopPlanStabilityTests
{
    [Test]
    public void MainViewportPlanKey_RemainsStableWhileConcreteTargetRecordingIdentityRotates()
    {
        FrameOpContext first = CreateContext(
            EVulkanFrameOpContextKind.MainViewport,
            outputTargetIdentity: 101,
            outputTargetName: "SwapchainImage[0]");
        FrameOpContext rotated = first with
        {
            OutputTargetIdentity = 202,
            OutputTargetName = "SwapchainImage[1]",
        };

        VulkanFramePlanner.ResolveResourcePlanOutputTargetIdentity(first)
            .ShouldBe(VulkanFramePlanner.ResolveResourcePlanOutputTargetIdentity(rotated));
        VulkanFramePlanner.BuildFrameOpPlannerStateKey(first)
            .ShouldBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(rotated));
        VulkanFramePlanner.ComputeFrameOpContextRecordingFingerprint(first)
            .ShouldNotBe(VulkanFramePlanner.ComputeFrameOpContextRecordingFingerprint(rotated));
    }

    [Test]
    public void DesktopResizeAndEyeRotation_HaveOutputFamilyScopedPlanKeys()
    {
        FrameOpContext desktop = CreateContext(
            EVulkanFrameOpContextKind.MainViewport,
            outputTargetIdentity: 101,
            outputTargetName: "SwapchainImage[0]");
        FrameOpContext resizedDesktop = desktop with
        {
            DisplayWidth = desktop.DisplayWidth + 1u,
        };

        int eyePlannerIdentity = VulkanFrameLoop.BuildOpenXrExternalSwapchainPlannerTargetIdentity(0u);
        FrameOpContext eye = CreateContext(
            EVulkanFrameOpContextKind.OpenXrEye,
            eyePlannerIdentity,
            "OpenXR.LeftEye",
            outputFrameBufferIdentity: 0);

        VulkanFramePlanner.BuildFrameOpPlannerStateKey(desktop)
            .ShouldNotBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(eye));
        VulkanFramePlanner.BuildFrameOpPlannerStateKey(desktop)
            .ShouldNotBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(resizedDesktop));
        VulkanFramePlanner.BuildFrameOpPlannerStateKey(eye)
            .ShouldBe(VulkanFramePlanner.BuildFrameOpPlannerStateKey(eye));
        VulkanFrameLoop.BuildOpenXrExternalSwapchainPlannerTargetIdentity(0u)
            .ShouldBe(eyePlannerIdentity);
        VulkanFrameLoop.BuildOpenXrExternalSwapchainPlannerTargetIdentity(1u)
            .ShouldNotBe(eyePlannerIdentity);
    }

    [Test]
    public void AlternatingExternalTargetsReachABoundedPlannerKeySet()
    {
        HashSet<VulkanFrameOpPlannerStateKey> keys = [];
        int leftEyeIdentity = VulkanFrameLoop.BuildOpenXrExternalSwapchainPlannerTargetIdentity(0u);
        int rightEyeIdentity = VulkanFrameLoop.BuildOpenXrExternalSwapchainPlannerTargetIdentity(1u);

        for (int cycle = 0; cycle < 32; cycle++)
        {
            for (int slot = 0; slot < 3; slot++)
            {
                FrameOpContext desktop = CreateContext(
                    EVulkanFrameOpContextKind.MainViewport,
                    outputTargetIdentity: 1000 + slot,
                    outputTargetName: $"SwapchainImage[{slot}]");
                keys.Add(VulkanFramePlanner.BuildFrameOpPlannerStateKey(desktop));

                FrameOpContext leftEye = CreateContext(
                    EVulkanFrameOpContextKind.OpenXrEye,
                    leftEyeIdentity,
                    $"OpenXR.Left.Image[{slot}]",
                    outputFrameBufferIdentity: 0);
                keys.Add(VulkanFramePlanner.BuildFrameOpPlannerStateKey(leftEye));

                FrameOpContext mirror = CreateContext(
                    EVulkanFrameOpContextKind.OpenXrMirror,
                    rightEyeIdentity,
                    $"OpenXR.Mirror.Image[{slot}]",
                    outputFrameBufferIdentity: 0);
                keys.Add(VulkanFramePlanner.BuildFrameOpPlannerStateKey(mirror));
            }

            for (int face = 0; face < 6; face++)
            {
                FrameOpContext probeFace = CreateContext(
                    EVulkanFrameOpContextKind.LightProbeCapture,
                    outputTargetIdentity: 2000 + face,
                    outputTargetName: $"ProbeFace[{face}]",
                    outputFrameBufferIdentity: 3000 + face);
                keys.Add(VulkanFramePlanner.BuildFrameOpPlannerStateKey(probeFace));
            }
        }

        // One desktop family, one eye family, one mirror family, and six persistent probe faces.
        keys.Count.ShouldBe(9);
    }

    [Test]
    public void NormalSchedulingPathsContainNoGlobalDrainOrForceFlush()
    {
        // Each entry is a path prefix below the Vulkan source root. A prefix can
        // name one file or a folder. The normal recording, planning, and frame
        // loop paths must not drain the device or force retirement.
        string[] nonBlockingSourcePrefixes =
        [
            "Commands/CommandBuffers/Allocation/VulkanRenderer.CommandBufferAllocation.cs",
            "Commands/CommandBuffers/Recording/",
            "Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Planning.cs",
            "Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Packetization.cs",
            "Commands/Scheduling/CommandChains/Cache/VulkanRenderer.CommandChains.ArtifactCache.cs",
            "RenderGraph/",
            "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlann",
            "Frame/Loop/Authority/VulkanFrameLoop.PlannerContext.cs",
            "Frame/Loop/Authority/VulkanResourceGenerationTransactionService.cs",
            "Frame/Loop/Authority/VulkanResourcePlannerSessionService",
            "BackendObjects/Textures/VkImageBackedTexture.cs",
            "Frame/Loop/VulkanRenderer.FrameLoop.",
        ];

        foreach (string relativePrefix in nonBlockingSourcePrefixes)
        {
            string source = ReadVulkanSourcesUnder(relativePrefix);
            source.ShouldNotContain("WaitForAllInFlightWork();");
            source.ShouldNotContain("ForceFlushAllRetiredResources");
            source.ShouldNotContain("DeviceWaitIdle();");
        }

        // The normal OpenXR eye path is separate from the runtime recreate and
        // teardown paths, which can legitimately drain the device.
        string normalOpenXr = ReadVulkanSourcesUnder(
            "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.EyeRe");
        normalOpenXr.ShouldContain("internal bool TryRenderOpenXrEyeSwapchain(");
        normalOpenXr.ShouldContain("private bool TryRenderOpenXrEyeSwapchainsWithParallelEyeWorkers(");
        normalOpenXr.ShouldNotContain("WaitForAllInFlightWork();");
        normalOpenXr.ShouldNotContain("ForceFlushAllRetiredResources");
        normalOpenXr.ShouldNotContain("DeviceWaitIdle();");
    }

    [TestCase(false, false, true, true,
        (int)ERejectedDesktopFrameDisposition.SkipPresent,
        (int)ERejectedDesktopFramePolicyReason.AcquireUnavailable)]
    [TestCase(true, true, true, true,
        (int)ERejectedDesktopFrameDisposition.SkipPresent,
        (int)ERejectedDesktopFramePolicyReason.DeviceLost)]
    [TestCase(true, false, false, false,
        (int)ERejectedDesktopFrameDisposition.SkipPresent,
        (int)ERejectedDesktopFramePolicyReason.ImageNeverPresented)]
    [TestCase(true, false, false, true,
        (int)ERejectedDesktopFrameDisposition.SkipPresent,
        (int)ERejectedDesktopFramePolicyReason.ImageNeverPresented)]
    [TestCase(true, false, true, false,
        (int)ERejectedDesktopFrameDisposition.SkipPresent,
        (int)ERejectedDesktopFramePolicyReason.NoCompletedFinalWrite)]
    [TestCase(true, false, true, true,
        (int)ERejectedDesktopFrameDisposition.PresentLastCompletedContent,
        (int)ERejectedDesktopFramePolicyReason.ReuseCompletedContent)]
    public void RejectedDesktopFramePolicy_OnlyPresentsKnownGoodCompletedContent(
        bool acquireAvailable,
        bool deviceLost,
        bool imageWasEverPresented,
        bool imageHasValidCompletedContent,
        int expectedDisposition,
        int expectedReason)
    {
        RejectedDesktopFramePolicyDecision decision =
            VulkanRejectedDesktopFramePolicy.Resolve(
                acquireAvailable,
                deviceLost,
                imageWasEverPresented,
                imageHasValidCompletedContent);

        decision.Disposition.ShouldBe((ERejectedDesktopFrameDisposition)expectedDisposition);
        decision.Reason.ShouldBe((ERejectedDesktopFramePolicyReason)expectedReason);
        decision.ShouldPresent.ShouldBe(
            expectedDisposition == (int)ERejectedDesktopFrameDisposition.PresentLastCompletedContent);
    }

    [Test]
    public void PersistentMergedRegistry_KeepsDirectionalShadowSpecsWhenExecutionTurnsOff()
    {
        RenderResourceRegistry desktop = new();
        desktop.RegisterTextureDescriptor(new TextureResourceDescriptor(
            "DesktopFinalColor",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(1920u, 1080u)));

        RenderResourceRegistry directionalShadow = new();
        directionalShadow.RegisterTextureDescriptor(new TextureResourceDescriptor(
            "DirectionalShadow.0123456789abcdef0123456789abcdef.CascadeDepth",
            RenderResourceLifetime.Persistent,
            RenderResourceSizePolicy.Absolute(2048u, 2048u)));

        RenderResourceRegistry compatibleGeneration = new();
        VulkanFramePlanner.AddRegistryDescriptors(compatibleGeneration, desktop, overwrite: true);
        VulkanFramePlanner.AddRegistryDescriptors(compatibleGeneration, directionalShadow, overwrite: false);
        int activeShadowSignature = compatibleGeneration.DescriptorSignature;

        // A no-shadow-work frame refreshes the structural desktop source but does not remove the
        // accumulated directional source from this compatibility generation.
        VulkanFramePlanner.AddRegistryDescriptors(compatibleGeneration, desktop, overwrite: true);

        compatibleGeneration.DescriptorSignature.ShouldBe(activeShadowSignature);
        compatibleGeneration.TextureRecords.ShouldContainKey(
            "DirectionalShadow.0123456789abcdef0123456789abcdef.CascadeDepth");
    }

    [Test]
    public void PlannerCompatibilityExcludesRotatingDesktopTargetFromPhysicalPlanIdentity()
    {
        string contextSignatures = ReadVulkanFile(
            "RenderGraph/Authority/VulkanFramePlanner.ContextSignatures.cs");
        string snapshotSignatures = ReadVulkanFile(
            "Commands/FrameOps/VulkanFrameOpSnapshotSignatures.cs");
        string planner = ReadVulkanFile("RenderGraph/VulkanRenderer.RenderGraphRebuild.cs");
        string recordingFingerprint = SliceBetween(
            snapshotSignatures,
            "internal static ulong ComputeRecordingFingerprint",
            "internal static VulkanFrameOpPlannerStateKey BuildPlannerStateKey");
        string plannerFingerprint = SliceBetween(
            planner,
            "private static ulong ComputeResourcePlanCompatibilityFingerprint",
            "internal static ResourcePlannerSignatureBreakdown ComputeResourcePlannerSignatureBreakdown");

        // The planner facade forwards the recording fingerprint to the snapshot
        // signatures, which include the concrete rotating target.
        contextSignatures.ShouldContain(
            "internal static ulong ComputeFrameOpContextRecordingFingerprint(in FrameOpContext context)\n" +
            "        => VulkanFrameOpSnapshotSignatures.ComputeRecordingFingerprint(context);");
        contextSignatures.ShouldContain("ResolveResourcePlanOutputTargetIdentity(context)");
        planner.ShouldContain("ResolveResourcePlanOutputTargetIdentity(context)");
        recordingFingerprint.ShouldContain("hash.Add(context.OutputTargetIdentity);");
        plannerFingerprint.ShouldContain("hash.Add(ResolveResourcePlanOutputTargetIdentity(context));");
        plannerFingerprint.ShouldNotContain("context.RecordingFingerprint");
        plannerFingerprint.ShouldNotContain("context.OutputTargetName");
    }

    [Test]
    public void ConditionalShadowRegistriesRemainStableAndOutputScoped()
    {
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourceRegistryPlanning.cs");
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/Authority/MergedFrameOpRegistryCacheEntry.cs");
        string merge = SliceBetween(
            planner,
            "internal RenderResourceRegistry? BuildMergedFrameOpRegistry",
            "private List<RenderResourceRegistry> CollectUniqueFrameOpRegistries");
        string lookup = SliceBetween(
            planner,
            "private bool TryGetCachedMergedFrameOpRegistry",
            "private static int IndexOfFrameOpRegistryCacheSource");

        merge.ShouldContain("VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(primaryContext);");
        merge.ShouldContain("retain its descriptors until the compatibility key changes");
        lookup.ShouldContain("!entry.OwnerKey.Equals(ownerKey)");
        lookup.ShouldContain("FrameOpRegistryCacheSource[] accumulatedSources = entry.Sources;");
        lookup.ShouldContain("entry.MergedRegistry = BuildMergedFrameOpRegistrySnapshot(");
        lookup.ShouldContain("AddRegistryDescriptors(merged, primaryRegistry, overwrite: true);");
        lookup.ShouldContain("AddRegistryDescriptors(merged, source, overwrite: false);");
        lookup.ShouldContain("CollectUniqueFrameOpFrameBuffers(ops),\n            overwrite: false);");
        lookup.ShouldNotContain("AddRegistryDescriptors(entry.MergedRegistry");
        lookup.ShouldNotContain("entry.MergedRegistry = persistentMerged;");
        state.ShouldContain("public VulkanFrameOpPlannerStateKey OwnerKey { get; } = ownerKey;");
    }

    [Test]
    public void PlanReplacementUsesDeferredRetirementWithoutGlobalDrain()
    {
        string physicalPlanning = ReadVulkanFile("RenderGraph/VulkanRenderer.PhysicalResourcePlanning.cs");
        string planner = SourceContractWorkspace.ReadPartialType(
            VulkanSourceRoot + "RenderGraph/Authority/VulkanFramePlanner.cs");
        string commit = SliceBetween(
            physicalPlanning,
            "private void CommitPhysicalAllocatorPlan",
            "private void EvictFrameOpResourcePlannerStatesReferencingAllocator");

        commit.ShouldContain("oldAllocator.TryRetirePhysicalResources");
        commit.ShouldContain("LastResourcePlanReplacementRetiredImageCount = retiredImageCount;");
        commit.ShouldContain("Deferring replaced physical resource plan retirement through frame-slot/timeline completion.");
        commit.ShouldNotContain("WaitForAllInFlightWork");
        commit.ShouldNotContain("DeviceWaitIdle");
        commit.ShouldNotContain("ForceFlush");
        foreach (string source in new[] { planner, physicalPlanning })
        {
            source.ShouldNotContain("ForceFlushAllRetiredResourcesAfterWaiting(\"ResourcePlanReplacement\")");
            source.ShouldNotContain("TryPreReleaseActiveImagesForOpenXrMirrorTransition");
        }
    }

    [Test]
    public void PlannerCapacityEvictionUsesTimelineRetirementWithoutGlobalDrain()
    {
        // Capacity eviction happens when a resource generation is published.
        // Evicted allocators retire through the timeline-gated allocator path.
        string transactions = ReadVulkanFile(
            "Frame/Loop/Authority/VulkanResourceGenerationTransactionService.cs");
        string frameSlots = ReadVulkanFile("Frame/Loop/VulkanRenderer.FrameLoop.FrameSlots.cs");
        string prune = SliceBetween(
            transactions,
            "private static void CollectEvictedAllocators",
            "private sealed class Transaction");
        string commit = SliceBetween(
            transactions,
            "public void Commit()",
            "private static void TryRetireAllocator");
        string retire = SliceBetween(
            transactions,
            "private static void TryRetireAllocator",
            "public void Dispose()");

        prune.ShouldContain("while (switchingState.States.Count > MaxPlannerStates)");
        prune.ShouldContain("retiredAllocators.Add(removed.ResourceAllocator);");
        commit.ShouldContain(
            "TryRetireAllocator(retiredAllocators[index], backendContext, pendingKey.ResourceGeneration);");
        retire.ShouldContain(
            "allocator.TryRetirePhysicalResources(backendContext, exceptImageGroups: exceptImageGroups);");
        retire.ShouldNotContain("immediate: true");
        frameSlots.ShouldContain("new FrameOutputWorkTelemetry(PlannerEvictionDeferrals: 1)");
        foreach (string source in new[] { prune, commit, retire })
        {
            source.ShouldNotContain("WaitForAllInFlightWork");
            source.ShouldNotContain("ForceFlush");
            source.ShouldNotContain("DeviceWaitIdle");
        }
    }

    [Test]
    public void PhysicalPlanCacheAndArenaTelemetryArePublished()
    {
        string planner = ReadVulkanFile(
            "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlanningUpdate.cs");
        string physicalPlanning = ReadVulkanFile("RenderGraph/VulkanRenderer.PhysicalResourcePlanning.cs");
        string telemetry = ReadWorkspaceFile(
            "XREngine.Runtime.Core/Settings/Contracts/Records/FrameOutputWorkTelemetry.cs");
        string packet = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");

        planner.ShouldContain("RecordPhysicalPlanCacheTelemetry(hit: true,");
        planner.ShouldContain("RecordPhysicalPlanCacheTelemetry(hit: false,");
        planner.ShouldContain("PhysicalPlanGenerations: 1");
        planner.ShouldContain("PhysicalPlanAliasReuses: result.AliasReuseCount");
        physicalPlanning.ShouldContain("AliasReuseCount: reusedImageGroups?.Count ?? 0");
        planner.ShouldContain("PhysicalPlanCacheHits: hit ? 1 : 0");
        planner.ShouldContain("PhysicalPlanCacheMisses: hit ? 0 : 1");
        telemetry.ShouldContain("int PlannerArenaHighWater = 0");
        packet.ShouldContain("public int PlannerEvictionDeferralCount { get; set; }");
    }

    [Test]
    public void OpenXrPlannerStateRetirementDoesNotDrainOtherOutputFamilies()
    {
        string openXr = ReadVulkanFile(
            "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.ResourcesPressure.cs");
        string plannerContext = ReadVulkanFile("Frame/Loop/Authority/VulkanFrameLoop.PlannerContext.cs");
        string destroy = SliceBetween(
            openXr,
            "private void DestroyOpenXrResourcePlannerState()",
            "RestoreResourcePlannerRuntimeState(previousState);");
        string retireAllocator = SliceBetween(
            plannerContext,
            "private void RetireResourcePlannerRuntimeStateAllocator(",
            "internal bool TryDescribeRecentResourceAllocationFailure");

        destroy.ShouldContain("OpenXrResourcePlannerStates.Clear();");
        destroy.ShouldContain("RetireResourcePlannerRuntimeStateAllocators(");
        retireAllocator.ShouldContain("allocator.TryRetirePhysicalResources(backendContext);");
        retireAllocator.ShouldNotContain("WaitForAllInFlightWork");
        retireAllocator.ShouldNotContain("DeviceWaitIdle");
        retireAllocator.ShouldNotContain("ForceFlush");
        destroy.ShouldNotContain("WaitForAllInFlightWork");
        destroy.ShouldNotContain("DeviceWaitIdle");
        destroy.ShouldNotContain("ForceFlush");
    }

    [Test]
    public void RejectedDesktopFramePublishesOnlyKnownGoodPriorContent()
    {
        string recovery = string.Join(
            "\n",
            ReadWorkspaceFile(
                "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Recovery.cs"),
            ReadWorkspaceFile(
                "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Recovery.Policy.cs"),
            ReadWorkspaceFile(
                "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Recovery.Recording.cs"),
            ReadWorkspaceFile(
                "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Recovery.Submission.cs"),
            ReadWorkspaceFile(
                "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Recovery.Presentation.cs"));
        string swapchain = ReadVulkanFile("Frame/Output/Authority/VulkanDesktopSwapchainService.cs");

        recovery.ShouldContain("VulkanRejectedDesktopFramePolicy.Resolve(");
        recovery.ShouldContain("if (!policy.ShouldPresent)");
        recovery.ShouldContain("policy=SkipPresent");
        recovery.ShouldContain("QueueDesktopPresentCore(");
        recovery.ShouldContain("RecordDesktopPresentBookkeeping(");
        recovery.ShouldContain("CompleteDesktopFrameSlot(ref attempt)");
        recovery.ShouldContain("rejectionStage=");
        recovery.ShouldContain("submitResult=");
        recovery.ShouldContain("_lastWindowPresentFrameOpContext");
        recovery.ShouldContain("ActiveLastActiveFrameOpContext");
        recovery.ShouldContain("RecordPhase524bInjectedDesktopRejection(");
        recovery.ShouldContain("ConsumedByRecoveryImagePendingPresent");
        recovery.ShouldContain(
            "desktop frame rejected without a legal recovery submit");
        recovery.ShouldContain(
            "CommitSubmittedDesktopTextureUpload(");
        recovery.ShouldContain(
            "attempt.TextureUploadCommandBuffer");
        swapchain.ShouldContain("_output.Desktop.ImageHasValidPresentedContent = new bool[imageCount];");
        swapchain.ShouldContain("_output.Desktop.ImageHasValidPresentedContent = null;");
    }

    [Test]
    public void DescriptorPendingDesktopFrameUsesInitializationClearWithoutImmediateSwapchainRecreate()
    {
        string recoverySource = string.Join(
            "\n",
            ReadWorkspaceFile(
                "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Recovery.cs"),
            ReadWorkspaceFile(
                "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Recovery.Policy.cs"),
            ReadWorkspaceFile(
                "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Recovery.Recording.cs"),
            ReadWorkspaceFile(
                "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Recovery.Submission.cs"));
        string policy = string.Join(
            "\n",
            ReadVulkanFile("Frame/Desktop/Policy/VulkanRenderer.RejectedDesktopFramePolicyDecision.cs"),
            ReadVulkanFile("Frame/Desktop/Policy/VulkanRenderer.ERejectedDesktopFramePolicyReason.cs"));

        recoverySource.ShouldContain("string.Equals(");
        recoverySource.ShouldContain("\"RecordDeferred\"");
        recoverySource.ShouldContain("ERejectedDesktopFrameDisposition");
        recoverySource.ShouldContain(".PresentInitializationClear");
        // A presenting rejection that cannot replay completed content records
        // the initialization clear through the tracked recovery encoder.
        recoverySource.ShouldContain(
            "if (!replayedPresentationSource)\n" +
            "                RecordRejectedDesktopInitializationClear(");
        recoverySource.ShouldContain("CreateRecoveryCommandEncoder().ClearColorImage(");
        recoverySource.ShouldContain("TryRecordRejectedDesktopRecoveryOverlay");
        recoverySource.ShouldContain("submittedCommandBufferCount");
        recoverySource.ShouldContain("new(0.06f, 0.015f, 0.08f, 1.0f)");
        policy.ShouldContain("DeferredInitializationClear");
        policy.ShouldContain("public bool ShouldClearBeforePresent");
        recoverySource.ShouldNotContain("TryRecreateSwapchainNow(\"Command buffer recording deferred");
    }

    [TestCase(false, true, true)]
    [TestCase(true, true, false)]
    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    public void UnwrittenSwapchainRefresh_IsLimitedToPresentationCommandBuffers(
        bool touchedSwapchain,
        bool transitionToPresent,
        bool expected)
    {
        VulkanCommandRuntime.ShouldRefreshUnwrittenSwapchainForPresent(
            touchedSwapchain,
            transitionToPresent).ShouldBe(expected);
    }

    [TestCase(false, true, false, false, true)]
    [TestCase(true, true, false, false, false)]
    [TestCase(false, false, false, false, false)]
    [TestCase(false, true, true, false, false)]
    [TestCase(false, true, false, true, false)]
    public void UninitializedUnwrittenSwapchain_IsClearedBeforeItsFirstPresent(
        bool hasRecordedSwapchainWrite,
        bool transitionToPresent,
        bool imageWasEverPresented,
        bool refreshedFromLastPresentSource,
        bool expected)
    {
        VulkanCommandRuntime.ShouldRecordUnwrittenSwapchainInitializationClear(
            hasRecordedSwapchainWrite,
            transitionToPresent,
            imageWasEverPresented,
            refreshedFromLastPresentSource).ShouldBe(expected);
    }

    [Test]
    public void FrameOpPlannerAllocatorReuse_UsesSessionAuthorityExclusiveOwnershipRule()
    {
        FrameOpResourcePlannerSwitchingState switchingState = new();
        VulkanResourceAllocator allocator = new();
        VulkanFrameOpPlannerStateKey ownerKey = default;
        VulkanFrameOpPlannerStateKey otherKey = ownerKey with { ViewportIdentity = 1 };

        switchingState.States.Add(ownerKey, ResourcePlannerRuntimeState.CreateEmpty() with
        {
            ResourceAllocator = allocator,
        });
        VulkanResourcePlannerSessionService.IsAllocatorExclusivelyOwnedByKey(
            switchingState, ownerKey, allocator).ShouldBeTrue();

        switchingState.States.Add(otherKey, ResourcePlannerRuntimeState.CreateEmpty() with
        {
            ResourceAllocator = allocator,
        });
        VulkanResourcePlannerSessionService.IsAllocatorExclusivelyOwnedByKey(
            switchingState, ownerKey, allocator).ShouldBeFalse();

        switchingState.States.Remove(otherKey);
        switchingState.PreparationState = ResourcePlannerRuntimeState.CreateEmpty() with
        {
            ResourceAllocator = allocator,
        };
        switchingState.HasPreparationState = true;
        VulkanResourcePlannerSessionService.IsAllocatorExclusivelyOwnedByKey(
            switchingState, ownerKey, allocator).ShouldBeFalse();
        VulkanResourcePlannerSessionService.IsAllocatorExclusivelyOwnedByKey(
            switchingState, ownerKey, allocator: null).ShouldBeFalse();
    }

    [Test]
    public void OpenXrOffscreenMirror_DoesNotTransitionDesktopSwapchainAndResetsReusedQueries()
    {
        string openXr = ReadVulkanFile("Frame/Loop/Authority/VulkanFrameLoop.OpenXR.MirrorPreview.cs");
        string mirrorRuntime = ReadVulkanFile("Commands/Authority/VulkanCommandRuntime.OpenXrMirrorPreview.cs");
        string primaryRecording = ReadVulkanFile(
            "Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs");
        string reuseSource = ReadVulkanFile(
            "Commands/CommandBuffers/FrameData/VulkanRenderer.CommandBufferReuse.FrameData.cs");
        string reuse = SliceBetween(
            reuseSource,
            "private bool TryReuseCleanCommandChainPrimaryVariant(",
            "private bool TryRevalidatePrimarySecondaryArtifactsAfterGlobalMutation(");
        string record = SliceBetween(
            openXr,
            "private bool TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer",
            "private static int ResolveOpenXrFrameDataSlotCount");
        string recordPrepared = SliceBetween(
            mirrorRuntime,
            "internal bool TryRecordPreparedOpenXrMirror(",
            "internal bool ExecuteOpenXrPreviewCopy(");
        string stereoPublish = SliceBetween(
            openXr,
            "internal unsafe bool TryRenderAndBlitTextureArrayLayersToOpenXrSwapchainImages",
            "private VulkanOpenXrSubmissionResult SubmitTrackedOpenXrMirrorSubmission");

        // The mirror primary records through the shared primary recorder. Its
        // clean-chain reuse path resets the reused query operations.
        record.ShouldContain("_commandRuntime.TryRecordPreparedOpenXrMirror(");
        recordPrepared.ShouldContain("RecordPrimary(in commandInput)");
        primaryRecording.ShouldContain("if (TryReuseCleanCommandChainPrimaryVariant(");
        reuse.ShouldContain("PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, ops)");
        record.ShouldContain("TransitionSwapchainToPresent: false");
        record.ShouldContain("ExcludeDesktopSwapchainBarriers: true");
        stereoPublish.ShouldContain("using IDisposable sourcePlannerScope = EnterOpenXrResourcePlannerThreadScope(");
        stereoPublish.ShouldContain("EVulkanOpenXrResourcePlannerPurpose.Mirror");
        stereoPublish.IndexOf("sourcePlannerScope", StringComparison.Ordinal)
            .ShouldBeLessThan(stereoPublish.IndexOf("TryPrepareStereoLayerBlit", StringComparison.Ordinal));
    }

    [Test]
    public void DesktopSwapchainRecreation_PreservesExternalOpenXrCommandState()
    {
        string swapchain = ReadVulkanFile("Frame/Output/Authority/VulkanDesktopSwapchainService.cs");
        string outputServices = ReadVulkanFile("Frame/Output/Authority/VulkanTargetOutputServices.cs");
        string artifacts = ReadVulkanFile("Commands/Authority/VulkanCommandRuntime.DesktopOutputArtifacts.cs");
        string lowering = ReadVulkanFile(
            "Commands/Scheduling/CommandChains/Cache/VulkanRenderer.CommandChains.ArtifactCache.cs");

        string swapchainRecreate = SliceBetween(
            swapchain,
            "internal bool TryRecreateGeneration()",
            "internal void CreateSwapchain(");
        string retireArtifacts = SliceBetween(
            swapchain,
            "private void RetireDesktopCommandArtifacts()",
            "internal void CreateLivePresentBridgeSemaphores(");
        string desktopDestroy = SliceBetween(
            artifacts,
            "internal void RetireDesktopOutputArtifacts(",
            "internal CommandBuffer[] CreateDesktopOutputArtifacts(");
        string reserveSlots = SliceBetween(
            outputServices,
            "public int ReserveOpenXrFrameDataSlots(int desktopImageCount)",
            "public void PublishDesktopImageTimelineValues(");
        string indexedCacheResize = SliceBetween(
            lowering,
            "private void EnsureIndexedCommandChainCaches",
            "internal void NotifyTextureDescriptorPublished");

        // Desktop swapchain recreation retires only the desktop-image-indexed
        // command artifacts and keeps the OpenXR command state.
        swapchainRecreate.ShouldContain("RetireDesktopCommandArtifacts();");
        swapchainRecreate.ShouldContain("_services.CreateDesktopOutputArtifacts(");
        swapchainRecreate.ShouldContain("_services.ReserveOpenXrFrameDataSlots(_output.Desktop.Images.Length);");
        swapchainRecreate.ShouldNotContain("DestroyCommandBuffers();");
        swapchainRecreate.ShouldNotContain("OpenXrPrimaryCommand");
        retireArtifacts.ShouldContain("_services.RetireDesktopOutputArtifacts(imguiOverlayCommandBuffers);");
        reserveSlots.ShouldContain("_commandRuntime.EnsureFrameDataSlotCapacity(totalSlots);");
        reserveSlots.ShouldContain("_outputRuntime.OpenXrBackend.EyeFrameDataSlotCount");
        desktopDestroy.ShouldContain("DestroyIndexedCommandChainCaches();");
        desktopDestroy.ShouldNotContain("DestroyExternalCommandChainCaches");
        desktopDestroy.ShouldNotContain("DestroyOpenXrEyeCommandPools");
        desktopDestroy.ShouldNotContain("DestroyOpenXrPrimaryCommandArtifacts");
        desktopDestroy.ShouldNotContain("MarkOpenXrPrimaryCommandBufferVariantsDirty");
        indexedCacheResize.ShouldContain("DestroyIndexedCommandChainCaches();");
        indexedCacheResize.ShouldNotContain("DestroyExternalCommandChainCaches");
        indexedCacheResize.ShouldNotContain("MarkOpenXrPrimaryCommandBufferVariantsDirty");
    }

    private static string SliceBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Missing method start '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Missing method end '{endMarker}'.");
        return source[start..end];
    }

    private static FrameOpContext CreateContext(
        EVulkanFrameOpContextKind contextKind,
        int outputTargetIdentity,
        string outputTargetName,
        int outputFrameBufferIdentity = 901)
        => new(
            PipelineIdentity: 17,
            ViewportIdentity: 31,
            PipelineInstance: null,
            ResourceRegistry: null,
            PassMetadata: null,
            DisplayWidth: 1920u,
            DisplayHeight: 1080u,
            InternalWidth: 1920u,
            InternalHeight: 1080u,
            OutputFrameBufferName: "MainViewportFBO",
            OutputTargetIdentity: outputTargetIdentity,
            OutputTargetName: outputTargetName,
            OutputFrameBufferIdentity: outputFrameBufferIdentity,
            ContextKind: contextKind,
            ContextId: 1UL,
            SubmissionQueueFamily: 2u,
            ResourceGeneration: 4UL,
            DescriptorGeneration: 5UL);

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    private const string VulkanSourceRoot =
        "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";

    private static string ReadVulkanFile(string vulkanRelativePath)
        => SourceContractWorkspace.ReadExactFile(VulkanSourceRoot + vulkanRelativePath);

    /// <summary>
    /// Reads every Vulkan source file whose path below the Vulkan source root
    /// starts with the given prefix. The prefix must match at least one file.
    /// </summary>
    private static string ReadVulkanSourcesUnder(string vulkanRelativePrefix)
    {
        string prefix = VulkanSourceRoot + vulkanRelativePrefix;
        string[] sources =
        [
            .. SourceContractWorkspace.GetVulkanSourceFiles()
                .Where(file => file.RelativePath.Replace('\\', '/')
                    .StartsWith(prefix, StringComparison.Ordinal))
                .Select(static file => file.Source),
        ];
        sources.Length.ShouldBeGreaterThan(0, $"No Vulkan source file starts with '{prefix}'.");
        return string.Join("\n", sources);
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

        throw new DirectoryNotFoundException("Could not locate the XRENGINE repository root.");
    }
}
