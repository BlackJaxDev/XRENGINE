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
        VulkanRenderer.FrameOpContext first = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            outputTargetIdentity: 101,
            outputTargetName: "SwapchainImage[0]");
        VulkanRenderer.FrameOpContext rotated = first with
        {
            OutputTargetIdentity = 202,
            OutputTargetName = "SwapchainImage[1]",
        };

        VulkanRenderer.ResolveResourcePlanOutputTargetIdentity(first)
            .ShouldBe(VulkanRenderer.ResolveResourcePlanOutputTargetIdentity(rotated));
        VulkanRenderer.BuildFrameOpPlannerStateKey(first)
            .ShouldBe(VulkanRenderer.BuildFrameOpPlannerStateKey(rotated));
        VulkanRenderer.ComputeFrameOpContextRecordingFingerprint(first)
            .ShouldNotBe(VulkanRenderer.ComputeFrameOpContextRecordingFingerprint(rotated));
    }

    [Test]
    public void DesktopResizeAndEyeRotation_HaveOutputFamilyScopedPlanKeys()
    {
        VulkanRenderer.FrameOpContext desktop = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            outputTargetIdentity: 101,
            outputTargetName: "SwapchainImage[0]");
        VulkanRenderer.FrameOpContext resizedDesktop = desktop with
        {
            DisplayWidth = desktop.DisplayWidth + 1u,
        };

        int eyePlannerIdentity = VulkanRenderer.BuildOpenXrExternalSwapchainPlannerTargetIdentity(0u);
        VulkanRenderer.FrameOpContext eye = CreateContext(
            VulkanRenderer.EVulkanFrameOpContextKind.OpenXrEye,
            eyePlannerIdentity,
            "OpenXR.LeftEye",
            outputFrameBufferIdentity: 0);

        VulkanRenderer.BuildFrameOpPlannerStateKey(desktop)
            .ShouldNotBe(VulkanRenderer.BuildFrameOpPlannerStateKey(eye));
        VulkanRenderer.BuildFrameOpPlannerStateKey(desktop)
            .ShouldNotBe(VulkanRenderer.BuildFrameOpPlannerStateKey(resizedDesktop));
        VulkanRenderer.BuildFrameOpPlannerStateKey(eye)
            .ShouldBe(VulkanRenderer.BuildFrameOpPlannerStateKey(eye));
        VulkanRenderer.BuildOpenXrExternalSwapchainPlannerTargetIdentity(0u)
            .ShouldBe(eyePlannerIdentity);
        VulkanRenderer.BuildOpenXrExternalSwapchainPlannerTargetIdentity(1u)
            .ShouldNotBe(eyePlannerIdentity);
    }

    [Test]
    public void AlternatingExternalTargetsReachABoundedPlannerKeySet()
    {
        HashSet<VulkanRenderer.FrameOpPlannerStateKey> keys = [];
        int leftEyeIdentity = VulkanRenderer.BuildOpenXrExternalSwapchainPlannerTargetIdentity(0u);
        int rightEyeIdentity = VulkanRenderer.BuildOpenXrExternalSwapchainPlannerTargetIdentity(1u);

        for (int cycle = 0; cycle < 32; cycle++)
        {
            for (int slot = 0; slot < 3; slot++)
            {
                VulkanRenderer.FrameOpContext desktop = CreateContext(
                    VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
                    outputTargetIdentity: 1000 + slot,
                    outputTargetName: $"SwapchainImage[{slot}]");
                keys.Add(VulkanRenderer.BuildFrameOpPlannerStateKey(desktop));

                VulkanRenderer.FrameOpContext leftEye = CreateContext(
                    VulkanRenderer.EVulkanFrameOpContextKind.OpenXrEye,
                    leftEyeIdentity,
                    $"OpenXR.Left.Image[{slot}]",
                    outputFrameBufferIdentity: 0);
                keys.Add(VulkanRenderer.BuildFrameOpPlannerStateKey(leftEye));

                VulkanRenderer.FrameOpContext mirror = CreateContext(
                    VulkanRenderer.EVulkanFrameOpContextKind.OpenXrMirror,
                    rightEyeIdentity,
                    $"OpenXR.Mirror.Image[{slot}]",
                    outputFrameBufferIdentity: 0);
                keys.Add(VulkanRenderer.BuildFrameOpPlannerStateKey(mirror));
            }

            for (int face = 0; face < 6; face++)
            {
                VulkanRenderer.FrameOpContext probeFace = CreateContext(
                    VulkanRenderer.EVulkanFrameOpContextKind.LightProbeCapture,
                    outputTargetIdentity: 2000 + face,
                    outputTargetName: $"ProbeFace[{face}]",
                    outputFrameBufferIdentity: 3000 + face);
                keys.Add(VulkanRenderer.BuildFrameOpPlannerStateKey(probeFace));
            }
        }

        // One desktop family, one eye family, one mirror family, and six persistent probe faces.
        keys.Count.ShouldBe(9);
    }

    [Test]
    public void NormalSchedulingPathsContainNoGlobalDrainOrForceFlush()
    {
        string[] nonBlockingSources =
        [
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferAllocation.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs",
        ];

        foreach (string relativePath in nonBlockingSources)
        {
            string source = ReadWorkspaceFile(relativePath);
            source.ShouldNotContain("WaitForAllInFlightWork();");
            source.ShouldNotContain("ForceFlushAllRetiredResources");
            source.ShouldNotContain("DeviceWaitIdle();");
        }

        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string normalOpenXr = SliceBetween(
            openXr,
            "internal bool TryRenderOpenXrEyeSwapchain(",
            "internal void ResetOpenXrRenderingResourcesForRuntimeRecreate");
        normalOpenXr.ShouldNotContain("WaitForAllInFlightWork();");
        normalOpenXr.ShouldNotContain("ForceFlushAllRetiredResources");
        normalOpenXr.ShouldNotContain("DeviceWaitIdle();");
    }

    [TestCase(false, false, true, true,
        (int)VulkanRenderer.ERejectedDesktopFrameDisposition.SkipPresent,
        (int)VulkanRenderer.ERejectedDesktopFramePolicyReason.AcquireUnavailable)]
    [TestCase(true, true, true, true,
        (int)VulkanRenderer.ERejectedDesktopFrameDisposition.SkipPresent,
        (int)VulkanRenderer.ERejectedDesktopFramePolicyReason.DeviceLost)]
    [TestCase(true, false, false, false,
        (int)VulkanRenderer.ERejectedDesktopFrameDisposition.SkipPresent,
        (int)VulkanRenderer.ERejectedDesktopFramePolicyReason.ImageNeverPresented)]
    [TestCase(true, false, false, true,
        (int)VulkanRenderer.ERejectedDesktopFrameDisposition.SkipPresent,
        (int)VulkanRenderer.ERejectedDesktopFramePolicyReason.ImageNeverPresented)]
    [TestCase(true, false, true, false,
        (int)VulkanRenderer.ERejectedDesktopFrameDisposition.SkipPresent,
        (int)VulkanRenderer.ERejectedDesktopFramePolicyReason.NoCompletedFinalWrite)]
    [TestCase(true, false, true, true,
        (int)VulkanRenderer.ERejectedDesktopFrameDisposition.PresentLastCompletedContent,
        (int)VulkanRenderer.ERejectedDesktopFramePolicyReason.ReuseCompletedContent)]
    public void RejectedDesktopFramePolicy_OnlyPresentsKnownGoodCompletedContent(
        bool acquireAvailable,
        bool deviceLost,
        bool imageWasEverPresented,
        bool imageHasValidCompletedContent,
        int expectedDisposition,
        int expectedReason)
    {
        VulkanRenderer.RejectedDesktopFramePolicyDecision decision =
            VulkanRenderer.ResolveRejectedDesktopFramePolicy(
                acquireAvailable,
                deviceLost,
                imageWasEverPresented,
                imageHasValidCompletedContent);

        decision.Disposition.ShouldBe((VulkanRenderer.ERejectedDesktopFrameDisposition)expectedDisposition);
        decision.Reason.ShouldBe((VulkanRenderer.ERejectedDesktopFramePolicyReason)expectedReason);
        decision.ShouldPresent.ShouldBe(
            expectedDisposition == (int)VulkanRenderer.ERejectedDesktopFrameDisposition.PresentLastCompletedContent);
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
        VulkanRenderer.AddRegistryDescriptors(compatibleGeneration, desktop, overwrite: true);
        VulkanRenderer.AddRegistryDescriptors(compatibleGeneration, directionalShadow, overwrite: false);
        int activeShadowSignature = compatibleGeneration.DescriptorSignature;

        // A no-shadow-work frame refreshes the structural desktop source but does not remove the
        // accumulated directional source from this compatibility generation.
        VulkanRenderer.AddRegistryDescriptors(compatibleGeneration, desktop, overwrite: true);

        compatibleGeneration.DescriptorSignature.ShouldBe(activeShadowSignature);
        compatibleGeneration.TextureRecords.ShouldContainKey(
            "DirectionalShadow.0123456789abcdef0123456789abcdef.CascadeDepth");
    }

    [Test]
    public void PlannerCompatibilityExcludesRotatingDesktopTargetFromPhysicalPlanIdentity()
    {
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string recordingFingerprint = SliceBetween(
            planner,
            "internal static ulong ComputeFrameOpContextRecordingFingerprint",
            "private static XRFrameBuffer? ResolveFrameOpOutputFrameBuffer");
        string plannerFingerprint = SliceBetween(
            planner,
            "private static ulong ComputeResourcePlanCompatibilityFingerprint",
            "private static ResourcePlannerSignatureBreakdown ComputeResourcePlannerSignatureBreakdown");

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
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string merge = SliceBetween(
            planner,
            "private RenderResourceRegistry? BuildMergedFrameOpRegistry",
            "private List<RenderResourceRegistry> CollectUniqueFrameOpRegistries");
        string lookup = SliceBetween(
            planner,
            "private bool TryGetCachedMergedFrameOpRegistry",
            "private static int IndexOfFrameOpRegistryCacheSource");

        merge.ShouldContain("FrameOpPlannerStateKey ownerKey = BuildFrameOpPlannerStateKey(primaryContext);");
        merge.ShouldContain("retain its descriptors until the compatibility key changes");
        lookup.ShouldContain("!entry.OwnerKey.Equals(ownerKey)");
        lookup.ShouldContain("FrameOpRegistryCacheSource[] accumulatedSources = entry.Sources;");
        lookup.ShouldContain("AddRegistryDescriptors(entry.MergedRegistry, source, overwrite: true);");
        lookup.ShouldContain("AddFrameOpFrameBufferDescriptors(entry.MergedRegistry, ops, overwrite: true);");
        lookup.ShouldNotContain("entry.MergedRegistry = persistentMerged;");
        state.ShouldContain("public FrameOpPlannerStateKey OwnerKey { get; } = ownerKey;");
    }

    [Test]
    public void PlanReplacementUsesDeferredRetirementWithoutGlobalDrain()
    {
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string commit = SliceBetween(
            planner,
            "private void CommitPhysicalAllocatorPlan",
            "private VulkanPhysicalImageGroup? PreserveAutoExposureHistory");

        commit.ShouldContain("oldAllocator.TryRetirePhysicalResources");
        commit.ShouldContain("_lastResourcePlanReplacementRetiredImageCount");
        commit.ShouldNotContain("WaitForAllInFlightWork");
        commit.ShouldNotContain("DeviceWaitIdle");
        commit.ShouldNotContain("ForceFlush");
        planner.ShouldNotContain("ForceFlushAllRetiredResourcesAfterWaiting(\"ResourcePlanReplacement\")");
        planner.ShouldNotContain("TryPreReleaseActiveImagesForOpenXrMirrorTransition");
    }

    [Test]
    public void PlannerCapacityEvictionUsesTimelineRetirementWithoutGlobalDrain()
    {
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string prune = SliceBetween(
            planner,
            "private void PruneFrameOpResourcePlannerStatesToCapacity",
            "private static ulong GetFrameOpResourcePlannerStateLastUsedSerial");

        prune.ShouldContain("state.ResourceAllocator");
        prune.ShouldContain("TryRetirePhysicalResources(this)");
        prune.ShouldContain("PlannerEvictionDeferrals: retirementDeferralCount");
        prune.ShouldNotContain("WaitForAllInFlightWork");
        prune.ShouldNotContain("ForceFlush");
        prune.ShouldNotContain("DeviceWaitIdle");
    }

    [Test]
    public void PhysicalPlanCacheAndArenaTelemetryArePublished()
    {
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string telemetry = ReadWorkspaceFile(
            "XREngine.Runtime.Core/Settings/Contracts/Records/FrameOutputWorkTelemetry.cs");
        string packet = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");

        planner.ShouldContain("RecordPhysicalPlanCacheTelemetry(hit: true,");
        planner.ShouldContain("RecordPhysicalPlanCacheTelemetry(hit: false,");
        planner.ShouldContain("PhysicalPlanGenerations: 1");
        planner.ShouldContain("PhysicalPlanAliasReuses: reusedImageGroups?.Count ?? 0");
        planner.ShouldContain("PhysicalPlanCacheHits: hit ? 1 : 0");
        planner.ShouldContain("PhysicalPlanCacheMisses: hit ? 0 : 1");
        telemetry.ShouldContain("int PlannerArenaHighWater = 0");
        packet.ShouldContain("public int PlannerEvictionDeferralCount { get; set; }");
    }

    [Test]
    public void OpenXrPlannerStateRetirementDoesNotDrainOtherOutputFamilies()
    {
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string destroy = SliceBetween(
            openXr,
            "private void DestroyOpenXrResourcePlannerState()",
            "private bool SubmitAndWaitOpenXrCommandBuffer");

        destroy.ShouldContain("RetireResourcePlannerRuntimeStateAllocators(");
        destroy.ShouldNotContain("WaitForAllInFlightWork");
        destroy.ShouldNotContain("DeviceWaitIdle");
        destroy.ShouldNotContain("ForceFlush");
    }

    [Test]
    public void RejectedDesktopFramePublishesOnlyKnownGoodPriorContent()
    {
        string frameLoop = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string swapchain = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");
        string recovery = SliceBetween(
            frameLoop,
            "bool TryPresentAbortedDirtyFrame",
            "stageStartTimestamp = Stopwatch.GetTimestamp();");

        recovery.ShouldContain("ResolveRejectedDesktopFramePolicy(");
        recovery.ShouldContain("if (!policy.ShouldPresent)");
        recovery.ShouldContain("policy=SkipPresent");
        recovery.ShouldContain("policy=PresentLastCompletedContent");
        recovery.ShouldContain("finalTargetValid=");
        recovery.ShouldContain("swapchainWrites=");
        recovery.ShouldContain("rejectionStage=");
        recovery.ShouldContain("submitResult=");
        recovery.ShouldContain("lastReplacementAllocation");
        recovery.ShouldContain("_lastWindowPresentFrameOpContext ?? ActiveLastActiveFrameOpContext");
        recovery.ShouldContain("exposureOwnedByDesktop=");
        recovery.ShouldContain("exposureHistoryRetained=");
        recovery.ShouldContain("presentAccepted=");
        recovery.ShouldContain("Attempted presentation of previously completed content");
        recovery.IndexOf("ReleaseUnsubmittedTextureUploadCommandBuffer", StringComparison.Ordinal)
            .ShouldBeLessThan(recovery.IndexOf("if (!policy.ShouldPresent)", StringComparison.Ordinal));
        swapchain.ShouldContain("_swapchainImageHasValidPresentedContent = new bool[imageCount];");
        swapchain.ShouldContain("_swapchainImageHasValidPresentedContent = null;");
    }

    [Test]
    public void DescriptorPendingDesktopFrameUsesInitializationClearWithoutImmediateSwapchainRecreate()
    {
        string frameLoop = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string policy = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DesktopPresentationPolicy.cs");
        string deferredRecovery = SliceBetween(
            frameLoop,
            "bool TryPresentAbortedDirtyFrame",
            "stageStartTimestamp = Stopwatch.GetTimestamp();");

        deferredRecovery.ShouldContain("string.Equals(rejectionStage, \"RecordDeferred\"");
        deferredRecovery.ShouldContain("ERejectedDesktopFrameDisposition.PresentInitializationClear");
        deferredRecovery.ShouldContain("policy.ShouldClearBeforePresent");
        deferredRecovery.ShouldContain("CmdClearColorImageTracked");
        policy.ShouldContain("DeferredInitializationClear");
        policy.ShouldContain("public bool ShouldClearBeforePresent");
        frameLoop.ShouldNotContain("RecreateSwapchainImmediately(\"Command buffer recording deferred");
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
        VulkanRenderer.ShouldRefreshUnwrittenSwapchainForPresent(
            touchedSwapchain,
            transitionToPresent).ShouldBe(expected);
    }

    [Test]
    public void OpenXrOffscreenMirror_DoesNotTransitionDesktopSwapchainAndResetsReusedQueries()
    {
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string reuse = SliceBetween(
            openXr,
            "private bool TryReuseOpenXrMirrorPrimaryCommandBuffer",
            "private CommandBuffer RecordOpenXrMirrorPrimaryCommandBuffer");
        string record = SliceBetween(
            openXr,
            "private CommandBuffer RecordOpenXrMirrorPrimaryCommandBuffer",
            "private static int ResolveOpenXrFrameDataSlotCount");
        string stereoPublish = SliceBetween(
            openXr,
            "internal bool TryRenderAndBlitTextureArrayLayersToOpenXrSwapchainImages",
            "private bool TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer");

        reuse.ShouldContain("PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, ops)");
        record.ShouldContain("transitionSwapchainToPresent: false");
        stereoPublish.ShouldContain("using IDisposable sourcePlannerScope = EnterOpenXrResourcePlannerThreadScope(");
        stereoPublish.ShouldContain("EOpenXrResourcePlannerPurpose.Mirror");
        stereoPublish.IndexOf("sourcePlannerScope", StringComparison.Ordinal)
            .ShouldBeLessThan(stereoPublish.IndexOf("TryPrepareStereoLayerBlit", StringComparison.Ordinal));
    }

    [Test]
    public void DesktopSwapchainRecreation_PreservesExternalOpenXrCommandState()
    {
        string swapchain = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");
        string allocation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferAllocation.cs");
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");

        string swapchainDestroy = SliceBetween(
            swapchain,
            "private void DestroyAllSwapChainObjects",
            "private void DisableStreamlineFrameGenerationBeforeSwapchainMutation");
        string desktopDestroy = SliceBetween(
            state,
            "private void DestroySwapchainCommandBuffers(bool cancelCommandChainWorkers)",
            "private void DestroyCommandBufferVariants");
        string indexedCacheResize = SliceBetween(
            lowering,
            "private void EnsureIndexedCommandChainCaches",
            "internal void NotifyTextureDescriptorPublished");

        swapchainDestroy.ShouldContain("DestroySwapchainCommandBuffers();");
        swapchainDestroy.ShouldNotContain("DestroyCommandBuffers();");
        allocation.ShouldContain("DestroySwapchainCommandBuffers();");
        allocation.ShouldContain("EnsureCommandBufferFrameDataSlotCapacity(_commandBuffers.Length);");
        desktopDestroy.ShouldContain("DestroyIndexedCommandChainCaches();");
        desktopDestroy.ShouldNotContain("DestroyExternalCommandChainCaches");
        desktopDestroy.ShouldNotContain("DestroyOpenXrEyeCommandPools");
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

    private static VulkanRenderer.FrameOpContext CreateContext(
        VulkanRenderer.EVulkanFrameOpContextKind contextKind,
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
    {
        string path = Path.Combine(
            ResolveRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
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

        throw new DirectoryNotFoundException("Could not locate the XRENGINE repository root.");
    }
}
