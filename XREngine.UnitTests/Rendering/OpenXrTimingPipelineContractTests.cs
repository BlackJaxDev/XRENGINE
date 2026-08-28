using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Vulkan;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OpenXrTimingPipelineContractTests
{
    [Test]
    public void FrameOutputCompletionTelemetry_UsesObservedCompletionAndRealDeadlines()
    {
        long first = System.Diagnostics.Stopwatch.Frequency;
        long second = first + (System.Diagnostics.Stopwatch.Frequency / 2);

        double observedRate = RuntimeEngine.Rendering.Stats.FrameOutputs.UpdateObservedCompletionRateHz(
            previousRateHz: 0.0,
            previousCompletionTimestamp: first,
            completionTimestamp: second);

        observedRate.ShouldBe(2.0, tolerance: 0.01);
        RuntimeEngine.Rendering.Stats.FrameOutputs.CalculateCompletionIntervalMilliseconds(first, second)
            .ShouldBe(500.0, tolerance: 0.01);
        RuntimeEngine.Rendering.Stats.FrameOutputs.ResolveEffectiveDeadlineMilliseconds(
                ERenderOutputClass.XrCritical,
                requestedDeadlineMs: 500.0,
                activeBudgetMs: 1000.0 / 90.0)
            .ShouldBe(1000.0 / 90.0, tolerance: 0.01);
        RuntimeEngine.Rendering.Stats.FrameOutputs.IsCompletedOutputDeadlineMissed(
            isDue: true,
            completed: true,
            completedFrameMs: 250.0,
            deadlineMs: 1000.0 / 90.0,
            hardDeadline: true).ShouldBeTrue();
        RuntimeEngine.Rendering.Stats.FrameOutputs.IsCompletedOutputDeadlineMissed(
            isDue: true,
            completed: false,
            completedFrameMs: 0.0,
            deadlineMs: 1000.0 / 90.0,
            hardDeadline: true).ShouldBeTrue();
        RuntimeEngine.Rendering.Stats.FrameOutputs.IsCompletedOutputDeadlineMissed(
            isDue: true,
            completed: true,
            completedFrameMs: 8.0,
            deadlineMs: 1000.0 / 90.0,
            hardDeadline: true).ShouldBeFalse();

        string frameOutputs = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.FrameOutputs.cs");
        frameOutputs.ShouldContain("destination[copied++] = CreateObservedCompletionSnapshot(");
        frameOutputs.ShouldContain("bool completed = IsOutputFamilyCompleted(output);");
        frameOutputs.ShouldContain("candidate.Request.ViewFamilyId == output.Request.ViewFamilyId");
    }

    [Test]
    public void FrameTiming_UsesDedicatedPacingThreadByDefault()
    {
        string frameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string runtimeDefaults = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServiceDefaults.cs");
        string runtimeSettings = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Settings/RuntimeEngine.Rendering.EngineSettings.cs");
        string engineSettings = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Settings/RuntimeEngine.Rendering.EngineSettings.cs");
        string editorProgram = ReadWorkspaceFile("XREngine.Editor/Program.cs");
        string environmentVariables = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string vulkanOpenXr = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding.Implementation.cs");
        string vrState = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs");

        frameLifecycle.ShouldContain("internal void EnginePostRenderTick()");
        frameLifecycle.ShouldContain("private void Window_PostRenderViewportsCallback()");
        frameLifecycle.ShouldContain("OpenXrRenderPacingMode.PostRenderCallback");
        frameLifecycle.ShouldContain("OpenXrRenderPacingMode.DedicatedThread");
        frameLifecycle.ShouldContain("OpenXrRenderPacingMode.CollectVisibleThread");
        frameLifecycle.ShouldContain("EnsureOpenXrPacingThreadStarted();");
        frameLifecycle.ShouldContain("OpenXrPrepareFrameAfterDesktopRender");
        frameLifecycle.ShouldContain("PrepareNextFrameForPacingOwner();");
        frameLifecycle.ShouldContain("EndFrameWithTiming(in frameEndInfo)");
        runtimeDefaults.ShouldContain("OpenXrRenderPacingMode.DedicatedThread");
        runtimeSettings.ShouldContain("RuntimeRenderingHostServiceDefaults.OpenXrRenderPacingMode");
        engineSettings.ShouldContain("RuntimeRenderingHostServiceDefaults.OpenXrRenderPacingMode");
        environmentVariables.ShouldContain("XRE_OPENXR_RENDER_PACING_MODE");
        environmentVariables.ShouldContain("XRE_OPENXR_VULKAN_MIRROR_FBO");
        environmentVariables.ShouldContain("XRE_OPENXR_VULKAN_PREWARM_EYES");
        environmentVariables.ShouldContain("XRE_OPENXR_VULKAN_SERIAL_EYE_SUBMIT");
        vulkanOpenXr.ShouldContain("OpenXrVulkanPrewarmEyes");
        vulkanOpenXr.ShouldNotContain("OpenXrVulkanTrueStereoOverride");
        vulkanOpenXr.ShouldContain("Sequential/per-eye fallback is forbidden");
        vulkanOpenXr.ShouldContain("Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanMirrorFbo)");
        vulkanOpenXr.ShouldContain("\"1\"");
        vulkanOpenXr.ShouldContain("leave it unset for direct swapchain rendering");
        vulkanOpenXr.ShouldContain("ShouldPrewarmVulkanEyeResources");
        vulkanOpenXr.ShouldContain("MarkVulkanEyeResourceWarmupComplete");
        editorProgram.ShouldContain("ApplyOpenXrRenderPacingOverride");
        editorProgram.ShouldContain("XREngineEnvironmentVariables.OpenXrRenderPacingMode");
        editorProgram.ShouldContain("IsVulkanOpenXrUnitTestingLaunch");

        vrState.ShouldContain("PostRenderViewportsCallback += PostRender");
        vrState.ShouldContain("OpenXRApi is IOpenXrApplicationLifecycle openXrLifecycle");
        vrState.ShouldContain("openXrLifecycle.PostRender();");
    }

    [Test]
    public void RuntimeMonitoring_RetainsOpenXrApiUntilSessionBecomesActive()
    {
        string vrState = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs");

        vrState.ShouldContain("_openXRApi ??= new OpenXRAPI();");
        vrState.ShouldContain("((IOpenXrApplicationLifecycle)_openXRApi).EnableRuntimeMonitoring();");
        vrState.ShouldContain("DeactivateOpenXRRuntime();");
        vrState.ShouldContain("if (!_openXrRuntimeMonitoring || _openXRApi is null)");
        vrState.ShouldNotContain("RuntimeEngine.VRState.OpenXRApi = IsOpenXRActive ? _openXRApi : null;");
    }

    [Test]
    public void VulkanOpenXr_EyeSubmitRecordsBothEyesBeforeOneFenceWait()
    {
        string frameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string vulkanOpenXrApi = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding.Implementation.cs");
        string vulkanRendererOpenXr = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "TryRenderAndPublishOpenXrEyeMirrorFrameBuffers",
            "TryRenderOpenXrEyeSwapchains",
            "TryCopyOpenXrEyeSwapchainImageToTexture",
            "TryPrepareOpenXrEyeSwapchainCommandBuffer",
            "ComputeOpenXrPrimaryCommandBufferGroupHandleSignature",
            "TryRecordPreparedOpenXrMirror",
            "TryPrepareOpenXrFrameDataSlot",
            "MarkAllOpenXrPrimaryCommandArtifactsDirty",
            "SubmitAndWaitOpenXrCommandBuffers");
        string vulkanCommandBufferState = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs")
            + ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.OwnedCommandChainSecondaryPool.cs");
        string vulkanCommandChainLowering = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "private Dictionary<CommandChainKey, CommandChain> GetCommandChainCache",
            "private void DestroyCommandChainSecondaryCommandBuffer");
        string vulkanCommandChainSecondaryBuffers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/VulkanRenderer.CommandChainSecondaryBuffers.cs");
        string vulkanCommandChainWorkers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/VulkanRenderer.CommandChainWorkers.cs");
        string vkDataBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        string vulkanComputeDescriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VulkanProgramWrapperPort.cs");
        string vulkanMappedFrameArena = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanMappedFrameArena.cs");
        string vulkanResourceRetirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.LifetimeNativeServices.cs");
        string vulkanFrameLoop = ReadVulkanDesktopFrameLoopSources();
        string renderPipelineGpuProfiler = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderPipelineGpuProfiler.cs");
        string vulkanInitialization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs");
        string unitTestUi = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs");
        string defaultPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");
        string defaultPipelineMain = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");

        frameLifecycle.ShouldContain("StartProfileScope(\"OpenXR.RenderFrame.TryRenderVulkanEyesBatch\")");
        frameLifecycle.ShouldContain("binding.TryRenderViewsBatch(");
        frameLifecycle.ShouldContain("out vulkanBatchHandled");

        vulkanOpenXrApi.ShouldContain("OpenXrVulkanSerialEyeSubmit");
        vulkanOpenXrApi.ShouldContain("AcquireAndWaitOpenXrEyeImage(0");
        vulkanOpenXrApi.ShouldContain("AcquireAndWaitOpenXrEyeImage(1");
        vulkanOpenXrApi.ShouldContain("TryRenderVulkanEyeBatchToSwapchains");
        vulkanOpenXrApi.ShouldContain("bool permitSequentialFallback = modeResolution.RequestedMode != EVrViewRenderMode.SinglePassStereo;");
        vulkanOpenXrApi.ShouldContain("requestSequentialFallback = permitSequentialFallback;");
        vulkanOpenXrApi.ShouldContain("Sequential/per-eye fallback is forbidden");
        vulkanOpenXrApi.ShouldContain("handled = false;");
        vulkanOpenXrApi.ShouldContain("EnsureVulkanEyeMirrorTargets(renderer, width, height)");
        vulkanOpenXrApi.ShouldContain("OpenXrEyeMirrorRenderRequest");
        vulkanOpenXrApi.ShouldContain("renderer.OpenXrFrameLoop.TryRenderAndPublishOpenXrEyeMirrorFrameBuffers(");
        vulkanOpenXrApi.ShouldContain("renderer.OpenXrFrameLoop.TryRenderOpenXrEyeSwapchains(leftRequest, rightRequest)");
        vulkanOpenXrApi.ShouldContain("ReleaseOpenXrEyeImageIfAcquired(1");
        vulkanOpenXrApi.ShouldContain("ReleaseOpenXrEyeImageIfAcquired(0");
        vulkanOpenXrApi.ShouldContain("previewFlippedY=False");
        vulkanOpenXrApi.ShouldContain("ShouldCopyDirectVulkanEyeSwapchainPreview");
        vulkanOpenXrApi.ShouldContain("bool copiedPreview = shouldCopyPreview &&");
        vulkanOpenXrApi.ShouldContain("VulkanCaptureEyeOutputs");
        vulkanOpenXrApi.ShouldContain("RuntimeRenderingHostServices.Presentation.VrCopyEyePreviewTextures");
        vulkanOpenXrApi.ShouldContain("RuntimeRenderingHostServices.Presentation.VrMirrorComposeFromEyeTextures");

        vulkanRendererOpenXr.ShouldContain("OpenXrEyeMirrorRenderRequest");
        vulkanRendererOpenXr.ShouldContain("TryRenderOpenXrEyeMirrorFrameBuffers");
        vulkanRendererOpenXr.ShouldContain("TryRenderAndPublishOpenXrEyeMirrorFrameBuffers");
        vulkanRendererOpenXr.ShouldContain("TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer");
        vulkanRendererOpenXr.ShouldContain("BuildOpenXrMirrorPrimaryCommandBufferCacheKey");
        vulkanRendererOpenXr.ShouldContain("GetOrCreateOpenXrPrimaryCommandBufferOwner(");
        vulkanRendererOpenXr.ShouldContain("owner.PrimaryCommandPlan.Build(");
        vulkanRendererOpenXr.ShouldContain("_commandRuntime.TryRecordPreparedOpenXrMirror(");
        vulkanRendererOpenXr.ShouldContain("OwnedByOpenXrPrimaryCache: true");
        vulkanRendererOpenXr.ShouldContain("TryRenderOpenXrEyeSwapchains");
        vulkanCommandBufferState.ShouldContain("OpenXrVulkanPrimaryReuseEnabled");
        vulkanCommandBufferState.ShouldContain("OpenXrVulkanPrimaryReuseOverride ?? true");
        vulkanCommandBufferState.ShouldContain("VulkanPrimaryCommandBufferReuseEnabled &&");
        vulkanRendererOpenXr.ShouldContain("OpenXrEyePreviewCopyRequest");
        vulkanRendererOpenXr.ShouldContain("ExecuteOpenXrPreviewCopy(in plan)");
        vulkanRendererOpenXr.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye");
        vulkanRendererOpenXr.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye");
        vulkanRendererOpenXr.ShouldContain("TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in firstPrepared");
        vulkanRendererOpenXr.ShouldContain("TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in secondPrepared");
        vulkanRendererOpenXr.ShouldContain("CaptureFrameOpsExcludingTextureUploads(request.EmitFrameOps, out _)");
        vulkanRendererOpenXr.ShouldContain("CaptureFrameOpsExcludingTextureUploads(");
        vulkanRendererOpenXr.ShouldContain("request.FrameOpEmitter,");
        vulkanRendererOpenXr.ShouldContain("in emission,");
        vulkanRendererOpenXr.ShouldContain("recordingService.TryRecordPreparedEye(");
        vulkanRendererOpenXr.ShouldContain("OpenXrExternalSwapchainTargetImageIndex");
        vulkanRendererOpenXr.ShouldContain("imageIndex: OpenXrExternalSwapchainTargetImageIndex");
        vulkanRendererOpenXr.ShouldContain("frameDataImageIndexOverride: recordImageIndex");
        vulkanRendererOpenXr.ShouldContain("arena.TryResetFrameSlot(");
        vulkanRendererOpenXr.ShouldContain("private bool TryPrepareOpenXrFrameDataSlot");
        vulkanRendererOpenXr.ShouldContain("ResolveOpenXrFrameDataSlotCount");
        vulkanRendererOpenXr.ShouldContain("ResolveOpenXrDesktopFrameDataSlotCount");
        vulkanRendererOpenXr.ShouldContain("desktopFrameDataSlotCount + eyeIndex");
        vulkanRendererOpenXr.ShouldContain("internal void ReserveOpenXrFrameDataSlotsIfRequired");
        vulkanRendererOpenXr.ShouldContain("RuntimeEngine.GameSettings?.VRRuntime == EVRRuntime.OpenXR");
        vulkanRendererOpenXr.ShouldContain("MarkOpenXrPrimaryCommandArtifactOwnersDirty");
        vulkanRendererOpenXr.ShouldContain("MarkAllOpenXrPrimaryCommandArtifactsDirty");
        vulkanRendererOpenXr.ShouldContain("EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);");
        vulkanRendererOpenXr.ShouldContain("EnsureOpenXrDescriptorFrameSlotFloor(");
        vulkanRendererOpenXr.ShouldContain("EnsureCommandBufferFrameDataSlotCapacity(");
        vulkanInitialization.ShouldContain("ReserveOpenXrFrameDataSlotsIfRequired(\"initialization\");");
        vulkanRendererOpenXr.ShouldContain("TryPrepareOpenXrFrameDataSlot(");
        vulkanRendererOpenXr.ShouldContain("\"eye swapchain render\"");
        vulkanRendererOpenXr.ShouldContain("\"eye mirror render\"");
        vulkanRendererOpenXr.ShouldContain("ComputeOpenXrPrimaryCommandBufferGroupHandleSignature");
        vulkanRendererOpenXr.ShouldContain("TryComputeOpenXrPrimaryCommandBufferGroupSignature");
        vulkanRendererOpenXr.ShouldContain("OpenXrPrimaryCommandChainScheduleIsReusable");
        vulkanRendererOpenXr.ShouldContain("chain.State is not (CommandChainState.Reused or CommandChainState.FrameDataRefreshed)");
        vulkanRendererOpenXr.ShouldContain("chain.FrameDataRefreshTouchedDescriptors");
        string openXrPrimarySignature = SliceMethod(
            vulkanRendererOpenXr,
            "private static ulong ComputeOpenXrPrimaryCommandBufferGroupHandleSignature",
            "private void FreeOpenXrRecordedEyeCommandBuffer");
        openXrPrimarySignature.ShouldContain("VulkanRecordedCommandArtifactReference artifact =");
        openXrPrimarySignature.ShouldContain("chain.RecordedArtifact.CreateReference();");
        openXrPrimarySignature.ShouldContain("artifact.AddTo(ref hash);");
        openXrPrimarySignature.ShouldNotContain("DescriptorGeneration");
        openXrPrimarySignature.ShouldNotContain("DescriptorSetSignature");
        openXrPrimarySignature.ShouldNotContain("FrameDataSignature");
        openXrPrimarySignature.ShouldNotContain("DirtyReason");
        vulkanRendererOpenXr.ShouldContain("RecordPrimary(in commandInput)");
        vulkanRendererOpenXr.ShouldContain("commandInput.FramePlan.IsSealed");
        vulkanRendererOpenXr.ShouldContain("SubmitAndWaitOpenXrCommandBuffers(");
        vulkanRendererOpenXr.ShouldContain("commandBuffers[0] = firstRecorded.CommandBuffer");
        vulkanRendererOpenXr.ShouldContain("commandBuffers[1] = secondRecorded.CommandBuffer");
        vulkanRendererOpenXr.ShouldContain("commandBuffers[2] = publishCommandBuffer");
        vulkanRendererOpenXr.ShouldContain("OpenXR.Vulkan.SubmitTimelineWait");
        vulkanCommandBufferState.ShouldContain("internal void EnsureCommandBufferFrameDataSlotCapacity");
        vulkanCommandBufferState.ShouldContain("private bool EnsureDescriptorFrameSlotFrameCountFloor");
        vulkanCommandBufferState.ShouldContain("ResourceRuntime.Descriptors.EnsureFrameSlotCountFloor(frameSlotCount)");
        vulkanCommandBufferState.ShouldContain("MarkCommandBuffersDirty();");
        vulkanCommandBufferState.ShouldContain("lock (CommandBuffers.OpenXrPrimaryOwnersGate)");
        vulkanCommandBufferState.ShouldContain("owner.Dirty = true;");
        vulkanCommandBufferState.ShouldContain("Array.Resize(ref _computeTransientResources, frameDataSlotCount);");
        vulkanCommandBufferState.ShouldContain("Array.Resize(ref _deferredSecondaryCommandBuffers, frameDataSlotCount);");
        vulkanCommandBufferState.ShouldContain("private Dictionary<ulong, OwnedCommandChainSecondaryPool> _ownedCommandChainSecondaryPools => _commandRuntime.CommandBuffers.OwnedSecondaryPools;");
        vulkanCommandBufferState.ShouldContain("DestroyTrackedCommandChainSecondaryPools();");
        vulkanCommandBufferState.ShouldContain("DiscardDeferredSecondaryCommandBuffersForPool(pool);");
        vulkanCommandBufferState.ShouldContain("UntrackOwnedCommandChainSecondaryCommandBuffer(entry.Pool, entry.CommandBuffer);");
        vulkanCommandBufferState.ShouldContain("public bool PendingDestroy { get; set; }");
        vulkanCommandBufferState.ShouldContain("private void MarkOwnedCommandChainSecondaryPoolPendingDestroy");
        vulkanCommandBufferState.ShouldContain("private void DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty");
        vulkanCommandBufferState.ShouldContain("DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(entry.Pool);");
        string trackedSecondaryPoolTeardown = SliceMethod(
            vulkanCommandBufferState,
            "private void DestroyTrackedCommandChainSecondaryPools",
            "private void DiscardDeferredSecondaryCommandBuffersForPool");
        trackedSecondaryPoolTeardown.ShouldContain("DestroyCommandPoolHostSynchronized(pool);");
        trackedSecondaryPoolTeardown.ShouldNotContain("!_deviceLost && pool.Handle != 0");
        string commandChainCacheSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Cache/VulkanRenderer.CommandChains.ArtifactCache.cs");
        string commandChainCache = SliceMethod(
            commandChainCacheSource,
            "private Dictionary<CommandChainKey, CommandChain> GetCommandChainCache",
            "InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease()");
        commandChainCache.ShouldContain("DestroyIndexedCommandChainCaches();");
        commandChainCache.ShouldNotContain("DestroyCommandChainCaches();");
        vulkanResourceRetirement.ShouldContain("internal int ReleaseDescriptorRecordingReferences()");
        vulkanResourceRetirement.ShouldContain("InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease();");
        vulkanResourceRetirement.ShouldContain("lock (CommandBuffers.OpenXrPrimaryOwnersGate)");
        vulkanResourceRetirement.ShouldContain("owner.DirtyReason = \"descriptor references released\";");
        vulkanResourceRetirement.IndexOf("InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease();", StringComparison.Ordinal)
            .ShouldBeLessThan(vulkanResourceRetirement.IndexOf("lock (CommandBuffers.OpenXrPrimaryOwnersGate)", StringComparison.Ordinal));
        vulkanCommandChainLowering.ShouldContain("TrackOwnedCommandChainSecondaryCommandBuffer(pool, secondary);");
        vulkanCommandChainLowering.ShouldContain("TrackOwnedCommandChainSecondaryCommandBuffer(pool, replacement);");
        string commandChainSecondaryTeardown = SliceMethod(
            vulkanCommandChainSecondaryBuffers,
            "private void DestroyCommandChainSecondaryCommandBuffer",
            "chain.RecordedArtifact.MarkRetired();");
        commandChainSecondaryTeardown.ShouldContain("chain.RecordedArtifact.CaptureRetirement();");
        commandChainSecondaryTeardown.ShouldContain("MarkOwnedCommandChainSecondaryPoolPendingDestroy(pool);");
        commandChainSecondaryTeardown.ShouldContain("ResolveCommandBufferImageIndex(secondary);");
        commandChainSecondaryTeardown.ShouldContain("DeferRecordedCommandArtifactRetirement(");
        commandChainSecondaryTeardown.ShouldContain("FreeVulkanCommandBufferTracked(pool, ref secondary");
        commandChainSecondaryTeardown.ShouldContain("DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(pool);");
        commandChainSecondaryTeardown.ShouldNotContain("DiscardDeferredSecondaryCommandBuffersForPool(pool);");
        commandChainSecondaryTeardown.ShouldNotContain("DestroyCommandPoolHostSynchronized(pool);");
        commandChainSecondaryTeardown.ShouldNotContain("Api!.DestroyCommandPool(device, pool, null);");
        commandChainSecondaryTeardown.ShouldNotContain("ownsPool && pool.Handle != 0 && !_deviceLost");
        vulkanCommandChainWorkers.ShouldContain("MarkOwnedCommandChainSecondaryPoolPendingDestroy(pool);");
        vulkanCommandChainWorkers.ShouldContain("worker.Arena.ClearAfterPoolRetirement();");
        vulkanCommandChainWorkers.ShouldNotContain("DestroyCommandPoolHostSynchronized(pool);");
        vulkanCommandChainWorkers.ShouldNotContain("!_deviceLost");
        vkDataBuffer.ShouldContain("BackendContext.Resources.Buffers.Retire(");
        vkDataBuffer.ShouldContain("BackendContext.Resources.PlannerPublications.TrackBufferBinding(Data);");
        vkDataBuffer.ShouldNotContain("Renderer.MarkCommandBuffersDirty(");
        vkDataBuffer.ShouldNotContain("Renderer.MarkOpenXrPrimaryCommandBufferVariantsDirty();");
        vulkanFrameLoop.ShouldContain("Command buffer for image {0} was dirtied after recording and before submit");
        vulkanFrameLoop.ShouldContain("Command buffer dirtied before submit - recovering timeline/present state");
        vulkanComputeDescriptors.ShouldContain("private static bool TryGetOrCreateComputeDescriptorSetsCore(");
        vulkanComputeDescriptors.ShouldContain("ComputeDescriptorImageCache[]? caches = descriptors.Compute.Caches;");
        vulkanComputeDescriptors.ShouldContain("Array.Resize(ref caches, requiredCount);");
        vulkanComputeDescriptors.ShouldContain("descriptors.Compute.Caches = caches;");
        vulkanMappedFrameArena.ShouldContain("internal void EnsureFrameSlotCount(int requiredFrameSlots)");
        vulkanMappedFrameArena.ShouldContain("Array.Resize(ref _chunks, requiredFrameSlots);");

        renderPipelineGpuProfiler.ShouldContain("private const ulong LiveSnapshotMergeWindowFrames");
        renderPipelineGpuProfiler.ShouldContain("FrameCapture snapshotFrame = CreateMergedSnapshotFrameNoLock(currentFrameId, best);");
        renderPipelineGpuProfiler.ShouldContain("RecordTimingHistoryNoLock(best);");
        renderPipelineGpuProfiler.ShouldContain("RemoveFramesOlderThanNoLock(best.FrameId, LiveSnapshotMergeWindowFrames);");
        renderPipelineGpuProfiler.ShouldContain("!IsWithinLiveSnapshotMergeWindow(best.FrameId, frameId)");

        defaultPipeline.ShouldContain("private static bool ShouldUseViewportTargetCommands()");
        defaultPipeline.ShouldContain("if (viewport is null)");
        defaultPipeline.ShouldContain("if (RuntimeEngine.Rendering.State.RenderingTargetOutputFBO is null)");
        defaultPipeline.ShouldContain("return RuntimeEngine.Rendering.State.IsStereoPass");
        string defaultPipelineFinalOutput = SliceMethod(
            defaultPipeline,
            "private void AppendStandardViewportFinalOutputCommands",
            "private static string ResolveStandardFinalOutputFboName");
        defaultPipelineFinalOutput.ShouldContain("RuntimeEnableFxaa || RuntimeEnableDeclaredSmaa || RuntimeNeedsTsrUpscale");
        defaultPipelineFinalOutput.ShouldNotContain("RuntimeEnableFxaa || RuntimeEnableSmaa || RuntimeNeedsTsrUpscale");
        defaultPipelineFinalOutput.ShouldContain("CreateFinalBlitCommands(FxaaFBOName");
        defaultPipelineFinalOutput.ShouldContain("CreateFinalBlitCommands(SmaaFBOName");
        defaultPipelineFinalOutput.ShouldContain("CreateFinalBlitCommands(TsrUpscaleFBOName");
        defaultPipelineFinalOutput.ShouldNotContain("OpenXrVulkanSafeFinalOutput");
        defaultPipelineFinalOutput.ShouldNotContain("UseOpenXrVulkanDesktopStartupSafePath");

        string defaultPipelineFxaaChain = SliceMethod(
            defaultPipeline,
            "private void AppendFxaaTsrUpscaleChain",
            "private void AppendExposureUpdate");
        defaultPipelineFxaaChain.ShouldContain("RuntimeEnableFxaa || RuntimeEnableDeclaredSmaa || RuntimeNeedsTsrUpscale");
        defaultPipelineFxaaChain.ShouldNotContain("if (UseOpenXrVulkanDesktopStartupSafePath)\n            return;");

        defaultPipelineMain.ShouldContain("private static bool RuntimeEnableDeclaredSmaa");
        defaultPipelineMain.ShouldContain("=> RuntimeEnableSmaa;");
        defaultPipelineMain.ShouldContain("internal static bool RuntimeEnableMsaaDeferred");
        defaultPipelineMain.ShouldContain("&& !UseOpenXrVulkanDesktopStartupSafePath\n        && (RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline as DefaultRenderPipeline)?.EnableDeferredMsaa == true;");

        string defaultPipelineResources = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Resources.cs").Replace("\r\n", "\n");
        defaultPipelineResources.ShouldContain("profile.AntiAliasingMode == EAntiAliasingMode.Fxaa;");
        defaultPipelineResources.ShouldContain("profile.AntiAliasingMode == EAntiAliasingMode.Smaa;");
        defaultPipelineResources.ShouldContain("Texture(builder, SmaaEdgeTextureName");
        defaultPipelineResources.ShouldContain("Texture(builder, SmaaBlendTextureName");
        defaultPipelineResources.ShouldContain("Texture(builder, SmaaOutputTextureName");
        defaultPipelineResources.ShouldContain("builder.FrameBuffer(SmaaFBOName)");
        defaultPipelineResources.ShouldNotContain("!UsesOpenXrVulkanDesktopSafePath(profile) && profile.AntiAliasingMode == EAntiAliasingMode.Fxaa;");
        defaultPipelineResources.ShouldContain("bool useOpenXrVulkanSafePath = UseOpenXrVulkanDesktopStartupSafePathForViewport(viewport);");
        defaultPipelineResources.ShouldContain("if (EnableDeferredMsaa && !useOpenXrVulkanSafePath)");
        defaultPipelineResources.ShouldContain("&& !UsesOpenXrVulkanDesktopSafePath(profile)\n        && profile.AntiAliasingMode == EAntiAliasingMode.Msaa");

        unitTestUi.ShouldContain("ShouldFlipOpenXrVulkanStereoPreviewUv");
        unitTestUi.ShouldContain("RuntimeEngine.VRState.IsOpenXRActive");
        unitTestUi.ShouldContain("RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan");
        unitTestUi.ShouldContain("target.FlipVerticalUVCoord = flipVerticalUVCoord;");

        string directEyeRecord = SliceMethod(
            vulkanRendererOpenXr,
            "private bool TryRecordOpenXrEyeSwapchainCommandBuffer",
            "private bool TryRecordPreparedOpenXrEyeSwapchainCommandBuffer");
        directEyeRecord.IndexOf("TryPrepareOpenXrFrameDataSlot(", StringComparison.Ordinal)
            .ShouldBeLessThan(directEyeRecord.IndexOf("TryResetFrameSlot(", StringComparison.Ordinal));

        string mirrorEyeRecord = SliceMethod(
            vulkanRendererOpenXr,
            "private bool TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer",
            "private static int ResolveOpenXrFrameDataSlotCount");
        mirrorEyeRecord.IndexOf("TryPrepareOpenXrFrameDataSlot(", StringComparison.Ordinal)
            .ShouldBeLessThan(mirrorEyeRecord.IndexOf("CaptureFrameOpsExcludingTextureUploads(request.EmitFrameOps, out _);", StringComparison.Ordinal));

        mirrorEyeRecord.ShouldContain("FrameDataImageIndexOverride: recordImageIndex");
    }

    [Test]
    public void VulkanOpenXr_EyePreviewCopyUpdatesReadyTargetsDuringAllocatorPressure()
    {
        string vulkanRendererOpenXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.OpenXR.PreviewPublish.cs");

        string copyMethod = SliceMethod(
            vulkanRendererOpenXr,
            "internal bool TryCopyOpenXrEyeSwapchainImageToTexture",
            "private bool TryPrepareOpenXrEyeSwapchainPreviewCopy");

        copyMethod.ShouldContain("ShouldDeferOpenXrEyePreviewCopyWork");
        copyMethod.ShouldContain("allowDestinationGeneration: false");
        copyMethod.ShouldContain("allowDestinationGeneration: true");
        copyMethod.ShouldContain("_commandRuntime.ExecuteOpenXrPreviewCopy(in plan)");
        copyMethod.IndexOf("allowDestinationGeneration: false", StringComparison.Ordinal)
            .ShouldBeLessThan(copyMethod.IndexOf("Debug.VulkanWarningEvery", StringComparison.Ordinal));

        string prepareMethod = SliceMethod(
            vulkanRendererOpenXr,
            "private bool TryPrepareOpenXrEyeSwapchainPreviewCopy",
            "internal bool TryPublishOpenXrEyeMirrorTextures");

        prepareMethod.ShouldContain("bool allowDestinationGeneration");
        prepareMethod.ShouldContain("GetOrCreateAPIRenderObject(destinationTexture, generateNow: true)");
        prepareMethod.ShouldContain("TryGetAPIRenderObject(destinationTexture, out destinationObject)");
        prepareMethod.ShouldContain("!destinationObject.IsGenerated");
    }

    [Test]
    public void VulkanOpenXr_PreviewTargetFormatFallbackDoesNotRecurse()
    {
        string vulkanOpenXrApi = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding.Implementation.cs");

        string previewTargets = SliceMethod(
            vulkanOpenXrApi,
            "private void EnsureVulkanOpenXrPreviewTargets",
            "private void LogOpenXrViewRenderModeResolution");

        previewTargets.ShouldContain("EnsureOpenXrPreviewTargets(renderer, width, height);");
        previewTargets.ShouldNotContain("EnsureVulkanOpenXrPreviewTargets(renderer, width, height);");
    }

    [Test]
    public void VulkanOpenXr_EyeRenderingGateDoesNotInheritDesktopAllocationBackoff()
    {
        string resourcePressure = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.OpenXR.ResourcesPressure.cs");
        string eyeRendering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.OpenXR.EyeRendering.cs");

        string eyeRenderingGate = SliceMethod(
            resourcePressure,
            "internal bool ShouldDeferOpenXrEyeRenderingWork",
            "internal bool ShouldDeferTextureUploadPreparationForOpenXrPriority");

        eyeRenderingGate.ShouldContain("TryDescribeBlockingOpenXrEyeTextureWork");
        eyeRenderingGate.ShouldNotContain("TryDescribeRecentResourceAllocationFailure");

        string directEyePrepare = SliceMethod(
            eyeRendering,
            "private bool TryPrepareOpenXrEyeSwapchainCommandBuffer",
            "private bool TryRecordPreparedOpenXrEyeSwapchainCommandBuffer");

        directEyePrepare.IndexOf("EnterOpenXrResourcePlannerThreadScope", StringComparison.Ordinal)
            .ShouldBeLessThan(directEyePrepare.IndexOf("TryDescribeRecentResourceAllocationFailure", StringComparison.Ordinal));
        directEyePrepare.ShouldContain("PrepareResourcePlannerForFrameOps(ops)");

        string textureUploadGate = SliceMethod(
            resourcePressure,
            "internal bool ShouldDeferTextureUploadPreparationForOpenXrPriority",
            "private bool TryDescribeOpenXrVulkanAllocatorPressure");

        textureUploadGate.ShouldContain("TryDescribeRecentResourceAllocationFailure");
    }

    [Test]
    public void VulkanOpenXr_SafeEyePassDoesNotReplacePostProcessFallbacksWithAtmosphereFogOutputs()
    {
        string commandChain = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");
        string resources = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Resources.cs");

        string atmosphereGate = SliceMethod(
            commandChain,
            "private static bool ShouldRunAtmosphericScattering()",
            "private static bool ShouldRunVolumetricFog()");
        atmosphereGate.ShouldContain("if (UseOpenXrVulkanDesktopStartupSafePath)\n            return false;");
        atmosphereGate.IndexOf("UseOpenXrVulkanDesktopStartupSafePath", StringComparison.Ordinal)
            .ShouldBeLessThan(atmosphereGate.IndexOf("GetActivePostProcessState", StringComparison.Ordinal));

        string fogGate = SliceMethod(
            commandChain,
            "private static bool ShouldRunVolumetricFog()",
            "private void AppendVoxelConeTracingPass");
        fogGate.ShouldContain("if (UseOpenXrVulkanDesktopStartupSafePath)\n            return false;");
        fogGate.IndexOf("UseOpenXrVulkanDesktopStartupSafePath", StringComparison.Ordinal)
            .ShouldBeLessThan(fogGate.IndexOf("GetActivePostProcessState", StringComparison.Ordinal));

        resources.ShouldContain("DeclareColorTexture(builder, AtmosphereColorTextureName, full");
        resources.ShouldContain("CreateAtmosphereColorTexture, predicate");
        resources.ShouldContain("DeclareColorTexture(builder, VolumetricFogColorTextureName, full");
        resources.ShouldContain("CreateVolumetricFogColorTexture, predicate");
        resources.ShouldContain("Texture(builder, AtmosphereColorTextureName, RenderResourceSizePolicy.Absolute(1u, 1u), SampledColorAttachment");
        resources.ShouldContain("Texture(builder, VolumetricFogColorTextureName, RenderResourceSizePolicy.Absolute(1u, 1u), SampledColorAttachment");
        resources.ShouldContain("CreateAtmosphereColorFallbackTexture");
        resources.ShouldContain("CreateVolumetricFogColorFallbackTexture");
    }

    [Test]
    public void DefaultPipelineScaledFactories_MatchPlannerRoundingForOddEyeExtents()
    {
        string pipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        string resources = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Resources.cs");

        pipeline.ShouldContain("System.MathF.Round(System.Math.Max(extent, 1u) / (float)System.Math.Max(divisor, 1u))");
        pipeline.ShouldContain("ScaleInternalExtent(InternalHeight, 2u)");
        resources.ShouldContain("MathF.Round(Math.Max(extent, 1u) / (float)Math.Max(divisor, 1))");
        resources.ShouldContain("ScaleGtaoScratchExtent(internalWidth, divisor)");
        resources.ShouldContain("ScaleGtaoScratchExtent(internalHeight, divisor)");
    }

    [Test]
    public void VulkanFboLayoutQuery_FallsBackToAttachmentSourceTrackedLayout()
    {
        string barrierEmission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Synchronization/VulkanRenderer.BarrierEmission.cs");
        string renderScopes = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.RenderScopes.cs");

        string queryCurrentLayouts = SliceMethod(
            barrierEmission,
            "private ImageLayout[]? QueryCurrentAttachmentLayouts",
            "private bool TryGetExactTrackedFboAttachmentLayout");

        queryCurrentLayouts.ShouldContain("TryGetExactTrackedFboAttachmentLayout");
        queryCurrentLayouts.ShouldContain(": ImageLayout.Undefined;");
        queryCurrentLayouts.ShouldNotContain("ResolveFboAttachmentOldLayout");

        string beginRenderingForTarget = renderScopes;

        beginRenderingForTarget.ShouldContain("ImageLayout[]? trackedLayouts = QueryCurrentAttachmentLayouts(");
        beginRenderingForTarget.ShouldContain("recordingState.CommandBuffer);");
        beginRenderingForTarget.ShouldContain("ResolveAttachmentSignatureForPass(");
        beginRenderingForTarget.IndexOf("ImageLayout[]? trackedLayouts = QueryCurrentAttachmentLayouts(", StringComparison.Ordinal)
            .ShouldBeLessThan(beginRenderingForTarget.IndexOf("ResolveAttachmentSignatureForPass(", StringComparison.Ordinal));
    }

    [Test]
    public void VulkanDynamicRenderingFboTransition_UsesRecordedStateOrUndefinedForFreshImages()
    {
        string commandRecording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Synchronization/VulkanRenderer.BarrierEmission.cs");

        string transition = SliceMethod(
            commandRecording,
            "private unsafe void TransitionFboAttachmentsForDynamicRendering",
            "private static ImageLayout NormalizeFboAttachmentLayout");

        transition.ShouldContain("TryGetRecordedImageAccessState(");
        transition.ShouldContain("oldLayout = NormalizeFboAttachmentLayout(signature, recordedState.Layout);");
        transition.ShouldContain("oldLayout = ImageLayout.Undefined;");
        transition.IndexOf("TryGetRecordedImageAccessState(", StringComparison.Ordinal)
            .ShouldBeLessThan(transition.IndexOf("oldLayout = ImageLayout.Undefined;", StringComparison.Ordinal));
        transition.ShouldContain("includeEntryState: false");
    }

    [Test]
    public void VulkanPhysicalImageLayoutSignature_IncludesPhysicalSubresourceLayoutsWithoutAllocating()
    {
        string allocator = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/VulkanPhysicalImageGroup.cs");

        string appendSignature = SliceMethod(
            allocator,
            "internal void AppendLayoutSignature(ref FrameOpSignatureHasher hash)",
            "internal void RestoreLayoutSnapshot");

        appendSignature.ShouldContain("for (uint mipLevel = 0; mipLevel < MipLevels; mipLevel++)");
        appendSignature.ShouldContain("for (uint arrayLayer = 0; arrayLayer < layers; arrayLayer++)");
        appendSignature.ShouldContain("hash.Add(mipLevel);");
        appendSignature.ShouldContain("hash.Add(arrayLayer);");
        appendSignature.ShouldContain("hash.Add((int)layout);");
        appendSignature.ShouldNotContain("CaptureLayoutSnapshot()");

        string captureSnapshot = SliceMethod(
            allocator,
            "internal LayoutSnapshot CaptureLayoutSnapshot()",
            "internal void RestoreLayoutSnapshot");

        captureSnapshot.ShouldContain("Array.Sort(");
        captureSnapshot.ShouldContain("left.MipLevel.CompareTo(right.MipLevel)");
        captureSnapshot.ShouldContain("left.ArrayLayer.CompareTo(right.ArrayLayer)");
    }

    [Test]
    public void ShadowAtlasSettings_DirtyRendererResourcesForOpenXrPrimaryReuse()
    {
        string engineSettings = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Settings/RuntimeEngine.Rendering.EngineSettings.cs");
        string descriptorRelease = string.Join("\n", new[]
        {
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.LifetimeLedger.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.LifetimeNativeServices.cs"),
        });

        string spotAtlasSetter = SliceMethod(
            engineSettings,
            "public bool UseSpotShadowAtlas",
            "public bool UseDirectionalShadowAtlas");
        spotAtlasSetter.ShouldContain("MarkShadowAtlasRenderResourcesChanged(nameof(UseSpotShadowAtlas));");

        string directionalAtlasSetter = SliceMethod(
            engineSettings,
            "public bool UseDirectionalShadowAtlas",
            "public bool UsePointShadowAtlas");
        directionalAtlasSetter.ShouldContain("MarkShadowAtlasRenderResourcesChanged(nameof(UseDirectionalShadowAtlas));");

        string pointAtlasSetter = SliceMethod(
            engineSettings,
            "public bool UsePointShadowAtlas",
            "private static void MarkShadowAtlasRenderResourcesChanged");
        pointAtlasSetter.ShouldContain("MarkShadowAtlasRenderResourcesChanged(nameof(UsePointShadowAtlas));");

        string helper = SliceMethod(
            engineSettings,
            "private static void MarkShadowAtlasRenderResourcesChanged",
            "public uint ShadowAtlasPageSize");
        helper.ShouldContain("AbstractRenderer.Current?.NotifyRenderResourcesChanged(settingName)");

        descriptorRelease.ShouldContain("internal int ReleaseDescriptorRecordingReferences()");
        descriptorRelease.ShouldContain("_trackedImageSubresourceStates.Clear();");
        descriptorRelease.ShouldContain("_recordedImageLayoutsByCommandBuffer.Clear();");
        descriptorRelease.ShouldContain("InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease();");
        descriptorRelease.ShouldContain("CommandBuffers.OpenXrPrimaryOwners.Values");
        descriptorRelease.ShouldContain("MarkCommandBuffersDirty(\"descriptor references released\");");
    }

    [Test]
    public void VulkanIndexedViewportScissor_StateChangesDoNotForceDirtyCachedPrimaries()
    {
        string renderStateApi = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.RenderStateApi.cs");
        string renderStateMutation = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.RenderStateMutation.cs");

        renderStateApi.ShouldContain("ActiveState.SetIndexedViewportScissors(viewports[..count], scissors[..count]);");
        renderStateApi.ShouldContain("ActiveState.ClearIndexedViewportScissors();");
        renderStateApi.ShouldNotContain("MarkCommandBuffersDirty");
        renderStateMutation.ShouldContain("public bool SetIndexedViewportScissors(");
        renderStateMutation.ShouldContain("if (regionsUnchanged && _indexedViewportScissorCount == count)");
        renderStateMutation.ShouldContain("return false;");
        renderStateMutation.ShouldContain("public bool ClearIndexedViewportScissors()");
    }

    [Test]
    public void VulkanImageViews_AreTrackedAndSweptBeforeLogicalDeviceDestroy()
    {
        string lifetime = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanLifetimeAuthority.cs");
        string imageLifetime = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Images/VulkanImageResourceService.cs");
        string resourceRuntime = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");
        string lifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs");
        string openXr = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrOutputResourceService.cs");
        string imageBackedTexture = SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string textureView = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");
        string renderProgram = SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string renderProgramPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgramPipeline.cs");
        string renderBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkRenderBuffer.cs");
        string swapchainViews = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Output/Authority/VulkanDesktopSwapchainService.ImageViews.cs");

        lifetime.ShouldContain("VulkanImageViewLifetimeState ImageViews { get; } = new();");
        lifetime.ShouldContain("ConcurrentDictionary<ulong, string> LivePipelineLayoutHandles { get; } = new();");
        imageLifetime.ShouldContain("internal void RegisterView(ImageView imageView, in ImageViewCreateInfo createInfo, string owner)");
        imageLifetime.ShouldContain("Views.LiveHandles[imageView.Handle] = owner;");
        imageLifetime.ShouldContain("lifetime.Tracker.RegisterResource(");
        imageLifetime.ShouldContain("internal bool TryBeginDestroy(ImageView imageView, string owner)");
        imageLifetime.ShouldContain("if (!RequireResourceRuntime().IsRetirementReady(ticket))");
        imageLifetime.ShouldContain("internal unsafe int DestroyRemaining(Vk api, Device device)");

        resourceRuntime.ShouldContain("internal void TrackPipelineLayout(PipelineLayout pipelineLayout, string owner)");
        resourceRuntime.ShouldContain("Lifetime.LivePipelineLayoutHandles[pipelineLayout.Handle] = owner;");
        resourceRuntime.ShouldContain("internal bool TryBeginDestroyPipelineLayout(PipelineLayout pipelineLayout, string owner)");
        resourceRuntime.ShouldContain("internal unsafe int DestroyRemainingTrackedPipelineLayouts(Vk api, Device device)");

        int finalFlushIndex = lifecycle.IndexOf("RunCleanupStep(\"late retirement drain\"", StringComparison.Ordinal);
        int finalImageViewsIndex = lifecycle.IndexOf("RunCleanupStep(\"remaining images\"", StringComparison.Ordinal);
        int finalPipelineLayoutsIndex = lifecycle.IndexOf("RunCleanupStep(\"tracked pipeline layouts\"", StringComparison.Ordinal);
        int finalAllocationsIndex = lifecycle.IndexOf("RunCleanupStep(\"tracked allocations\"", StringComparison.Ordinal);
        finalFlushIndex.ShouldBeGreaterThanOrEqualTo(0);
        finalImageViewsIndex.ShouldBeGreaterThan(finalFlushIndex);
        finalPipelineLayoutsIndex.ShouldBeGreaterThan(finalImageViewsIndex);
        finalAllocationsIndex.ShouldBeGreaterThan(finalPipelineLayoutsIndex);

        openXr.ShouldContain("_services.TrackLiveImageView(imageView, in viewInfo, \"OpenXR.SwapchainImageView\");");
        openXr.ShouldContain("_services.TrackLiveImageView(view, in viewInfo, \"OpenXR.DepthTarget\");");
        openXr.ShouldContain("_resources.Images.RetireOwnedResources(");
        imageBackedTexture.ShouldContain("BackendContext.Resources.Images.RegisterView(");
        imageBackedTexture.ShouldContain("\"VkImageBackedTexture.View:");
        textureView.ShouldContain("BackendContext.Resources.Images.TryAcquireInternedView(BackendContext, in viewInfo, \"VkTextureView.View\", out _view)");
        textureView.ShouldContain("BackendContext.Resources.Images.TryAcquireInternedView(BackendContext, in depthOnlyViewInfo, \"VkTextureView.DepthOnlyDescriptor\", out _depthOnlyView)");
        textureView.ShouldContain("BackendContext.Resources.Images.ReleaseInternedView(_view)");
        textureView.ShouldContain("private readonly object _viewLifetimeLock = new();");
        renderProgram.ShouldContain("ProgramCreationPort.TrackPipelineLayout(_pipelineLayout, \"VkRenderProgram.PipelineLayout\");");
        renderProgram.ShouldContain("ProgramCreationPort.TryBeginDestroyPipelineLayout(pipelineLayout, owner)");
        renderProgramPipeline.ShouldContain("ProgramCreationPort.TrackPipelineLayout(_pipelineLayout, \"VkRenderProgramPipeline.PipelineLayout\");");
        renderProgramPipeline.ShouldContain("ProgramCreationPort.TryBeginDestroyPipelineLayout(pipelineLayout, owner)");
        renderBuffer.ShouldContain("BackendContext.Resources.Images.RegisterView(_view, in viewInfo, \"VkRenderBuffer.View\");");
        swapchainViews.ShouldContain("_services.TrackLiveImageView(view, in createInfo, \"Swapchain.Color\");");
    }

    [Test]
    public void VulkanCommandChains_DoNotBroadDirtyForRepeatedSkippedMeshPreparation()
    {
        string dirtyReasons = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferDirtyReasons.cs");
        string meshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string meshUniforms = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs");

        dirtyReasons.ShouldContain("VulkanPrimaryCommandBufferReuseEnabled || CommandChainsEnabledForCurrentRecording");
        dirtyReasons.ShouldNotContain("t_frameOpCapture is not null");

        string onRenderRequested = SliceMethod(
            meshRenderer,
            "private void OnRenderRequested",
            "RenderingParameters? matOpts");

        onRenderRequested.ShouldContain("CommandOperations.MarkCommandBuffersDirtyForLegacyMeshState();");
        onRenderRequested.ShouldNotContain("Renderer.MarkCommandBuffersDirty();");

        string ensureUniformSlots = SliceMethod(
            meshUniforms,
            "internal void EnsureUniformDrawSlotCapacity",
            "private int ResolveUniformBufferIndex");

        ensureUniformSlots.ShouldContain("CPU-side logical reservation only");
        ensureUniformSlots.ShouldNotContain("Renderer.MarkCommandBuffersDirtyForLegacyMeshState();");
        ensureUniformSlots.ShouldNotContain("Renderer.MarkCommandBuffersDirty();");
    }

    [Test]
    public void VulkanTextureUploads_AutoGeneratedMipmapsUploadOnlyBaseLevelAndValidateCopyBounds()
    {
        string[] textureUploadFiles =
        [
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture1D.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture1DArray.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture2D.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture2DArray.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture3D.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureCube.cs",
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureCubeArray.cs",
        ];

        foreach (string file in textureUploadFiles)
        {
            string source = ReadWorkspaceFile(file);

            source.ShouldContain("uint levelCount = Data.AutoGenerateMipmaps");
            source.ShouldContain("? 1u");
            source.ShouldContain(": Math.Min((uint)mipmaps.Length, ResolvedMipLevels);");
            source.ShouldContain("RecreateImageForFullTextureDataUpload(");
        }

        string imageBackedTexture = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");

        imageBackedTexture.ShouldContain("if (!ValidateCopyBufferToImageRegion(mipLevel, baseArrayLayer, layerCount, extent))");
        imageBackedTexture.ShouldContain("layerCount > arrayLayerCount - baseArrayLayer");
        imageBackedTexture.ShouldContain("extent.Width == 0 || extent.Height == 0 || extent.Depth == 0");
        imageBackedTexture.ShouldContain("private static Extent3D ResolveMipExtent");
        imageBackedTexture.ShouldContain("protected void RecreateImageForFullTextureDataUpload(string reason)");
        imageBackedTexture.ShouldNotContain("WaitForInFlightWorkBeforeImportedTextureReplacement");
        imageBackedTexture.ShouldContain("Destruction is generation-safe and deferred by exact resource tickets");
    }

    [Test]
    public void VulkanCommandChains_DescriptorReuseTracksConcreteImageIdentityAndMutableFrameSources()
    {
        string descriptors = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string canReuse = SliceMethod(
            descriptors,
            "private bool CanReuseRecordedDescriptorSets(",
            "private string BuildDescriptorAllocationMissReason");

        canReuse.ShouldContain("ulong schemaFingerprint = _program.DescriptorSchemaFingerprint;");
        canReuse.ShouldContain("ulong resourceFingerprint = ComputeDescriptorResourceFingerprint(");
        canReuse.ShouldContain("drawUniformSlot,");
        canReuse.ShouldContain("usesSharedMaterialTier);");
        descriptors.ShouldContain("DescriptorSlotResourceFingerprintMatches(allocation, descriptorSlotIndex, resourceFingerprint)");
        descriptors.ShouldContain("EnsureDescriptorSlotReady(");
        canReuse.ShouldContain("TryActivateReusableDescriptorSetsForCapturedResources(");
        canReuse.ShouldContain("schemaFingerprint,");
        canReuse.ShouldContain("viewFamilyIdentity,");
        canReuse.ShouldContain("bindingIdentityFingerprint,");
        canReuse.ShouldContain("resourceFingerprint,");

        string capturedReuse = SliceMethod(
            descriptors,
            "private bool TryActivateReusableDescriptorSetsForCapturedResources",
            "private bool TryActivateReusableDescriptorSetsFast");

        capturedReuse.ShouldContain("DescriptorSlotResourceFingerprintMatches(allocation, descriptorSlotIndex, resourceFingerprint)");
        capturedReuse.ShouldContain("TryRefreshCapturedDescriptorAllocationResources");
        capturedReuse.ShouldContain("ComputeDescriptorResourceFingerprintDetails(material, BackendContext.Resources.Descriptors.FrameSlotCount, currentBindings)");

        string frameSourceFingerprint = SliceMethod(
            descriptors,
            "private void AddFrameSourceSamplerDescriptorResourceFingerprint",
            "private bool TryRefreshFrameSourceDescriptorSetsForDraw");

        frameSourceFingerprint.ShouldContain("hash.Add(VulkanMeshRenderingConventions.FrameSourceMutableDescriptorSignature);");
        frameSourceFingerprint.ShouldContain("AddTextureDescriptorResourceFingerprint(ref hash, texture);");
        frameSourceFingerprint.ShouldNotContain("texture?.GetHashCode()");
        string textureResourceFingerprint = SliceMethod(
            descriptors,
            "private void AddTextureDescriptorResourceFingerprint",
            "private bool TryResolveBuffers");
        textureResourceFingerprint.ShouldContain("hash.Add(snapshot.View.Handle);");
        textureResourceFingerprint.ShouldContain("hash.Add(snapshot.Sampler.Handle);");
        textureResourceFingerprint.ShouldContain("hash.Add(imageSource.DescriptorGeneration);");
        textureResourceFingerprint.ShouldContain("hash.Add(texelSource.DescriptorBufferView.Handle);");
        descriptors.ShouldContain("private bool TryRefreshFrameSourceDescriptorSetsForDraw");
        descriptors.ShouldContain("BackendContext.Resources.DescriptorLifetime.TryUpdateDescriptorSets(");
        descriptors.ShouldContain("reason = $\"frame-source sampler '{binding.Name}' update deferred: {updateFailureReason}\";");
        descriptors.ShouldContain("Deferred frame-source sampler descriptor update because a render-resource generation retired concurrently");
        descriptors.ShouldContain("FrameSourceDescriptorWriteMatches(");
        descriptors.ShouldContain("RecordFrameSourceDescriptorWriteSignature(");
        descriptors.ShouldContain("descriptorCount,");
        descriptors.ShouldContain("resolvedImageInfos);");
        descriptors.ShouldContain("ComputeDescriptorImageInfoSignature(binding.DescriptorType, imageInfos)");
        descriptors.ShouldContain("BindingResolvesPipelineResourceTexture(binding)");
        descriptors.ShouldContain("SnapshotHasFrameSourceSampler(snapshot, pipeline)");
        descriptors.ShouldContain("DescriptorBindingsHaveFrameSourceSampler(material, _program.DescriptorBindings, snapshot)");
        descriptors.ShouldContain("IsFrameSourceSamplerBinding(material, binding, snapshot)");
        descriptors.ShouldContain("capturedSnapshot.TryGetSamplerTexture");

        string frameOperationSemantics = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/VulkanFrameOperationSemantics.cs");
        string meshRenderingConventions = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanMeshRenderingConventions.cs");
        string samplerUnitHashing = SliceMethod(
            frameOperationSemantics,
            "internal static ulong HashSamplerUnitBindings",
            "internal static ulong HashSamplerNameBindings");
        string samplerNameHashing = SliceMethod(
            frameOperationSemantics,
            "internal static ulong HashSamplerNameBindings",
            "internal static ulong HashImageBindings");
        string snapshotHashing = samplerUnitHashing + samplerNameHashing;

        meshRenderingConventions.ShouldContain("internal static bool IsMutableFrameSourceSamplerName(string? name, XRRenderPipelineInstance? pipeline)");
        meshRenderingConventions.ShouldContain("string.Equals(name, \"SourceTexture0\", StringComparison.Ordinal)");
        meshRenderingConventions.ShouldContain("string.Equals(name, \"SourceTexture1\", StringComparison.Ordinal)");
        meshRenderingConventions.ShouldContain("pipeline.TryGetTexture(name, out XRTexture? texture)");
        frameOperationSemantics.ShouldContain("PendingMeshDraw draw = ops.GetMeshDraw(i).Draw;");
        frameOperationSemantics.ShouldContain("HashProgramBindingLayoutSnapshot(ref hash, draw.ProgramBindingSnapshot);");
        frameOperationSemantics.ShouldContain("HashProgramBindingLayoutSnapshot(ref hash, compute.Snapshot);");
        frameOperationSemantics.ShouldContain("HashSamplerUnitBindings(snapshot.Samplers, snapshot.SamplerNamesByUnit, snapshot.DescriptorSignatures, pipeline, includeMutableFrameSourceDescriptors)");
        samplerUnitHashing.ShouldContain("samplerNamesByUnit.TryGetValue(pair.Key");
        samplerUnitHashing.ShouldContain("IsMutableFrameSourceSamplerName(samplerName, pipeline)");
        samplerNameHashing.ShouldContain("IsMutableFrameSourceSamplerName(pair.Key, pipeline)");
        frameOperationSemantics.ShouldContain("private static ulong ComputeCommandBufferDataBufferSignature(VkDataBuffer? buffer)");
        frameOperationSemantics.ShouldContain("buffer.BufferHandle?.Handle ?? 0UL");
        frameOperationSemantics.ShouldContain("buffer.UploadedByteCount");
        frameOperationSemantics.ShouldContain("hash.Add(ComputeCommandBufferDataBufferSignature(indirect.IndirectBuffer));");
        frameOperationSemantics.ShouldContain("hash.Add(ComputeCommandBufferDataBufferSignature(meshTaskDispatch.CountBuffer));");
        snapshotHashing.ShouldContain("!includeMutableFrameSourceDescriptors");
        snapshotHashing.ShouldContain("AddFrameSourceTextureDescriptorSignature(ref item, pair.Value);");
        frameOperationSemantics.ShouldContain("hash.Add(FrameSourceMutableDescriptorSignature);");
        snapshotHashing.ShouldContain("descriptorSignatures.AddSignature(ref item, pair.Value);");
        snapshotHashing.ShouldNotContain("source.DescriptorGeneration");
        snapshotHashing.ShouldNotContain("source.DescriptorImage.Handle");
        snapshotHashing.ShouldNotContain("source.DescriptorView.Handle");
        frameOperationSemantics.ShouldContain("descriptorSignatures.AddSignature(ref item, binding.Texture);");

        string drawing = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        drawing.ShouldContain("TryRefreshFrameSourceDescriptorSetsForDraw(imageIndex, drawUniformSlot, material, draw.ProgramBindingSnapshot");
        drawing.ShouldContain("bool frameSourceDescriptorsReady = TryRefreshFrameSourceDescriptorSetsForDraw(");
        drawing.ShouldContain("draw.ProgramBindingSnapshot,");
        drawing.ShouldContain("recordedSecondaryCommandBuffer,");
        drawing.ShouldContain("out string frameSourceDescriptorReason);");

        string frameOpSignatures = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOpSignatures.cs");
        frameOpSignatures.ShouldContain("AddProgramBindingSignatureParts(parts, opIndex, opType, \"program\", draw.ProgramBindingSnapshot, meshDraw.Context.PipelineInstance);");
        frameOpSignatures.ShouldContain("VulkanFrameOpSnapshotSignatures.HashSamplerUnitBindings(snapshot.Samplers, snapshot.SamplerNamesByUnit, snapshot.DescriptorSignatures, includeMutableFrameSourceDescriptors: false)");
        frameOpSignatures.ShouldContain("VulkanFrameOpSnapshotSignatures.HashSamplerNameBindings(snapshot.SamplersByName, snapshot.DescriptorSignatures, includeMutableFrameSourceDescriptors: false)");
        frameOpSignatures.ShouldContain("VulkanFrameOpSnapshotSignatures.HashImageBindings(snapshot.Images, snapshot.DescriptorSignatures)");
        frameOpSignatures.ShouldContain("ComputeCommandBufferDataBufferSignature(indirect.IndirectBuffer)");
        frameOpSignatures.ShouldContain("indirectBuffer=0x{indirect.IndirectBuffer.BufferHandle?.Handle");

        string commandChainLowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.CommandChainServices.cs");
        commandChainLowering.ShouldContain("HashCommandChainProgramBindingSnapshot(");
        commandChainLowering.ShouldContain("VulkanFrameOpSnapshotSignatures.HashSamplerUnitBindings(");
        commandChainLowering.ShouldContain("includeMutableFrameSourceDescriptors: true");
        commandChainLowering.ShouldContain("VulkanFrameOpSnapshotSignatures.HashImageBindings(");
        commandChainLowering.ShouldContain("VulkanFrameOpSnapshotSignatures.HashBufferBindings(");

        string program = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string programSamplerFingerprint = SliceMethod(
            program,
            "private ulong ComputeSamplerResourceFingerprintItem",
            "private ulong ComputeBoundBufferResourceFingerprintItem");

        programSamplerFingerprint.ShouldContain("source.DescriptorGeneration");
        programSamplerFingerprint.ShouldContain("source.DescriptorImage.Handle");
        programSamplerFingerprint.ShouldContain("source.DescriptorView.Handle");
    }

    [Test]
    public void VulkanReusableMaterialDescriptors_SkipFingerprintOnlyForCleanPublishedSlots()
    {
        string material = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string validatedReuse = SliceMethod(
            material,
            "internal bool TryGetValidatedReusableMaterialDescriptorSet",
            "internal static bool DescriptorSlotRequiresPublication");

        validatedReuse.ShouldContain("_materialDirty");
        validatedReuse.ShouldContain("state.Dirty");
        validatedReuse.ShouldContain("state.ProgramLinkGeneration != program.LinkGeneration");
        validatedReuse.ShouldContain("state.SlotUniformValueGenerations[resolvedFrame] != Volatile.Read(ref _parameterValueGeneration)");
        validatedReuse.ShouldContain("state.SlotResourceFingerprints[resolvedFrame] != state.ResourceFingerprint");

        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string sharedRefresh = SliceMethod(
            descriptors,
            "private bool TryRefreshSharedMaterialDescriptorSetForReusableFrame",
            "internal int GetRecordedDescriptorSetCount");

        sharedRefresh.ShouldContain("capturedResourcesValidated");
        sharedRefresh.ShouldContain("TryGetValidatedReusableMaterialDescriptorSet");
        sharedRefresh.ShouldContain("TryGetMaterialDescriptorSet");
    }

    [Test]
    public void PoseThreading_UsesLockedCachesAndExplicitRecalcTiming()
    {
        string sceneViews = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.SceneViews.cs");
        string state = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs");
        string frameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string runtimeVrState = ReadWorkspaceFile("XREngine.Input/RuntimeVrStateServices.cs");
        string engineVrState = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs");

        string collectCameraUpdate = SliceMethod(
            sceneViews,
            "private float UpdateOpenXrEyeCameraFromView",
            "private void ApplyOpenXrEyePoseForRenderThread");
        string collectVisible = SliceMethod(
            frameLifecycle,
            "private void OpenXrCollectVisible()",
            "private bool CollectOpenXrStereoVisible");
        string prepareNextFrame = SliceMethod(
            frameLifecycle,
            "private void PrepareNextFrameForPacingOwner()",
            "private void EndBegunFrameWithoutLayers");

        collectCameraUpdate.ShouldContain("TryGetOpenXrViewPoseAndFov(");
        collectCameraUpdate.ShouldContain("OpenXrPoseTiming.Predicted");
        sceneViews.ShouldContain("TryGetCachedOpenXrViewForTiming");
        sceneViews.ShouldContain("TryGetCachedOpenXrViewForTimingNoLock");
        state.ShouldContain("TryGetEyeLocalPose(OpenXrPoseTiming.Predicted");
        state.ShouldContain("_openXrPredLeftEyeLocalPose");
        state.ShouldContain("_openXrPredRightEyeLocalPose");
        state.ShouldContain("_openXrPredictedViews");
        state.ShouldContain("_openXrLateViews");
        collectCameraUpdate.ShouldNotContain("_views[");

        collectVisible.ShouldContain("OpenXR.CollectVisible.ApplyPredictedVrRigPose");
        collectVisible.ShouldContain("InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Predicted)");
        prepareNextFrame.ShouldNotContain("InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Predicted)");
        frameLifecycle.ShouldNotContain("InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Late)");
        frameLifecycle.ShouldContain("ApplyOpenXrEyePoseForRenderThread instead");
        sceneViews.ShouldContain("private void ApplyOpenXrEyePoseForRenderThread");
        sceneViews.ShouldContain("localPose * rootRender");
        runtimeVrState.ShouldContain("Action<RuntimeVrPoseTiming>?");
        engineVrState.ShouldNotContain("PoseTimingForRecalc");
    }

    [Test]
    public void TimingStats_AreRecordedAndSurfacedThroughProfiler()
    {
        string xrCalls = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs");
        string stats = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vr.cs");
        string packet = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string sender = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Engine/Engine.ProfilerSender.cs");
        string editorSource = ReadWorkspaceFile("XREngine.Editor/EngineProfilerDataSource.cs");
        string panel = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");

        xrCalls.ShouldContain("ConvertWin32PerformanceCounterToTime");
        xrCalls.ShouldContain("RecordDeadlineStatus");
        xrCalls.ShouldContain("RecordVrXrWaitFrameBlockTime");
        xrCalls.ShouldContain("RuntimeRenderingHostServices.Statistics.RecordRenderVrXrEndFrameSubmitTime(");

        stats.ShouldContain("VrXrPredictedDisplayLeadTimeMs");
        stats.ShouldContain("VrXrPredictedToLatePoseDeltaMillimeters");
        stats.ShouldContain("VrXrMissedDeadlineFrames");
        stats.ShouldContain("VrXrTrackingLossFrames");

        packet.ShouldContain("VrXrWaitFrameBlockTimeMs");
        sender.ShouldContain("VrXrWaitFrameBlockTimeMs");
        editorSource.ShouldContain("VrXrWaitFrameBlockTimeMs");
        panel.ShouldContain("OpenXR / VR");
        panel.ShouldContain("VrXrPredictedDisplayLeadTimeMs");
    }

    [Test]
    public void VulkanFrameLoop_ReleasesCollectBeforeBlockingDesktopPresent()
    {
        string coordinator = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string submission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Submission.cs");
        string presentation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Presentation.cs");

        submission.ShouldContain("RuntimeRenderingHostServices.Scheduling");
        submission.ShouldContain(".MarkRenderFrameReadyForCollect(DesktopWsiOutput.Window);");
        submission.ShouldContain("attempt.CollectReleased = true;");
        presentation.ShouldContain("Vulkan.FrameLifecycle.QueuePresent");
        coordinator.IndexOf("SubmitDesktopFrame(ref attempt)", StringComparison.Ordinal)
            .ShouldBeLessThan(coordinator.IndexOf("PresentSubmittedDesktopFrame(ref attempt)", StringComparison.Ordinal));
    }

    [Test]
    public void VulkanOpenXr_HotPathSuccessLogsAreDiagnosticGated()
    {
        string commandState = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        string commandRecording = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string frameLoop = ReadVulkanDesktopFrameLoopSources();
        string resourcePlannerState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.ResourcePlannerSwitching.cs");
        string rendererOpenXr = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "private static bool TraceOpenXrStereoBlits",
            "internal bool TryBlitTextureArrayLayerToOpenXrSwapchainImage",
            "private bool TryPrepareStereoLayerBlit",
            "OpenXR.Vulkan.RecordEye.PlanAndSchedule");
        string openXrVulkan = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding.Implementation.cs");

        commandState.ShouldContain("private static bool VulkanFrameDiagnosticsTraceEnabled");
        commandState.ShouldContain("CommandRecordingDiagnosticsEnabled ||");
        commandState.ShouldContain("XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||");
        commandState.ShouldContain("XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw");

        string fboTransitionTrace = SliceMethod(
            commandRecording,
            "bool traceDynamicFboTransition =",
            "barriers[checked((int)barrierCount++)] = barrier;");
        fboTransitionTrace.ShouldContain("CommandRecordingDiagnosticsEnabled");
        fboTransitionTrace.ShouldContain("XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw");
        fboTransitionTrace.ShouldContain("XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw");
        fboTransitionTrace.ShouldNotContain("vkFbo.MultiviewViewMask != 0u ||");

        commandRecording.ShouldContain("if (!VulkanFrameDiagnosticsTraceEnabled)");
        commandRecording.ShouldContain("if (VulkanFrameDiagnosticsTraceEnabled)");
        commandRecording.ShouldContain("Vulkan.FrameOps.");
        commandRecording.ShouldNotContain("Vulkan.RecordCommandBuffer.NormalizeFrameOps.Sort");
        commandRecording.ShouldNotContain("Vulkan.RecordCommandBuffer.NormalizeFrameOps.SplitDynamicUiBatchText");
        commandRecording.ShouldNotContain("Vulkan.RecordCommandBuffer.NormalizeFrameOps.Signature");
        commandRecording.ShouldContain("bool preservingOverlayOnlyFrame =");
        commandRecording.ShouldContain("bool preservingPresentedSwapchainImage =");
        commandRecording.ShouldContain("imageWasEverPresentedAtRecordStart");
        commandRecording.ShouldContain("!preservingOverlayOnlyFrame");
        commandRecording.ShouldContain("!preservingPresentedSwapchainImage");
        frameLoop.ShouldContain("if (VulkanFrameDiagnosticsTraceEnabled)");
        frameLoop.ShouldContain("Vulkan.Frame.{GetHashCode()}.Sizes");
        frameLoop.ShouldContain("Vulkan.Frame.{GetHashCode()}.Acquire");
        frameLoop.ShouldContain("Vulkan.Frame.{GetHashCode()}.Submit");
        frameLoop.ShouldContain("Vulkan.Frame.{GetHashCode()}.Present");
        frameLoop.ShouldContain("Vulkan.DynamicUiText.LateOverlayDecision");
        resourcePlannerState.ShouldContain("if (VulkanFrameDiagnosticsTraceEnabled)");
        resourcePlannerState.ShouldContain("Debug.Vulkan(");
        resourcePlannerState.ShouldContain("[VulkanResourcePlanner] Lazy physical-image rebuild");

        rendererOpenXr.ShouldContain("private static bool TraceOpenXrStereoBlits");
        rendererOpenXr.ShouldContain("StartProfileScope(\"OpenXR.Vulkan.RecordEye.PlanAndSchedule\")");
        string singleLayerBlit = SliceMethod(
            rendererOpenXr,
            "internal bool TryBlitTextureArrayLayerToOpenXrSwapchainImage",
            "internal bool TryBlitTextureArrayLayersToOpenXrSwapchainImages");
        singleLayerBlit.ShouldContain("if (TraceOpenXrStereoBlits)");

        string batchedLayerBlit = SliceMethod(
            rendererOpenXr,
            "private bool TryPrepareStereoLayerBlit",
            "private void RecordStereoLayerBlits");
        batchedLayerBlit.ShouldContain("if (TraceOpenXrStereoBlits)");
        batchedLayerBlit.ShouldContain("CommandBuffer recordedSourceCommandBuffer");
        batchedLayerBlit.ShouldContain("TryGetRecordedImageLayout(");
        batchedLayerBlit.ShouldContain("recordedSourceCommandBuffer");

        string trueStereoPublish = SliceMethod(
            openXrVulkan,
            "private bool TryRenderVulkanTrueSinglePassStereoToSwapchains",
            "private bool TryRenderVulkanEyeParallelCommandBufferRecordingToSwapchains");
        trueStereoPublish.ShouldContain("VulkanCaptureEyeOutputs || OpenXrDebugLifecycle || XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw");
        trueStereoPublish.ShouldContain("OpenXR.Vulkan.TrueSinglePassStereo.Rendered");
    }

    [Test]
    public void PoseAndInputPolicies_AreConfigurable()
    {
        string state = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs");
        string collectVisiblePosePolicy = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.OpenXrCollectVisiblePosePolicy.cs");
        string settings = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Settings/RuntimeEngine.Rendering.EngineSettings.cs");
        string defaults = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServiceDefaults.cs");
        string runtimeSettings = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Settings/RuntimeEngine.Rendering.EngineSettings.cs");
        string hostInterface = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderPresentationServices.cs");
        string environmentVariables = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string editorProgram = ReadWorkspaceFile("XREngine.Editor/Program.cs");
        string input = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Input.cs");
        string xrCalls = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs");

        state.ShouldContain("OpenXrCollectVisiblePosePolicy");
        collectVisiblePosePolicy.ShouldContain("RelocatePredicted");
        collectVisiblePosePolicy.ShouldContain("PaddedFrustum");
        state.ShouldContain("OpenXrTrackingLossPolicy");
        state.ShouldContain("OpenXrActionSyncPolicy");

        settings.ShouldContain("OpenXrCollectVisibleFrustumPaddingDegrees");
        settings.ShouldContain("OpenXrPoseTimeOffsetMs");
        settings.ShouldContain("OpenXrTrackingLossPolicy");
        settings.ShouldContain("OpenXrActionSyncPolicy");
        defaults.ShouldContain("OpenXrPoseTimeOffsetMs = 0.0f");
        runtimeSettings.ShouldContain("OpenXrPoseTimeOffsetMs");
        hostInterface.ShouldContain("float OpenXrPoseTimeOffsetMs");
        environmentVariables.ShouldContain("XRE_OPENXR_POSE_TIME_OFFSET_MS");
        editorProgram.ShouldContain("ApplyOpenXrPoseTimeOffsetOverride");

        input.ShouldContain("OpenXrActionSyncHandling == OpenXrActionSyncPolicy.PredictedAndLate");
        input.ShouldContain("ResolveOpenXrPoseDisplayTime(timing)");
        input.ShouldContain("_openXrActionsSyncedFrameNumber");
        input.ShouldContain("Result.ErrorPathUnsupported");
        input.ShouldContain("optional Vive tracker role paths are not supported");
        xrCalls.ShouldContain("ViewStateFlags.PositionValidBit");
        xrCalls.ShouldContain("RecordVrXrTrackingLossFrame");
        xrCalls.ShouldContain("ResolveOpenXrPoseDisplayTime(OpenXrPoseTiming timing)");
        xrCalls.ShouldContain("StoreLocatedViewsToTimingCache(timing)");
    }

    [Test]
    public void OpenXrControllerPoseBindings_AreSuggestedWithRuntimeNeutralBindings()
    {
        string input = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Input.cs");
        string runtimeNeutral = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Input.RuntimeNeutral.cs");

        string defaultBindings = SliceMethod(input, "private void SuggestDefaultBindings", "private void SuggestForProfile");
        defaultBindings.ShouldContain("SuggestRuntimeNeutralBindings();");
        defaultBindings.ShouldNotContain("SuggestForProfile(\"/interaction_profiles/valve/index_controller\"");
        defaultBindings.ShouldNotContain("new ActionSuggestedBinding[2]");

        string neutralBindings = SliceMethod(runtimeNeutral, "private void SuggestRuntimeNeutralBindings", "private void SuggestRuntimeBindingsForProfile");
        CountOccurrences(neutralBindings, "SuggestRuntimeBindingsForProfile(").ShouldBe(5);
        CountOccurrences(neutralBindings, "(_handGripPoseAction, \"/user/hand/left/input/grip/pose\")").ShouldBe(5);
        CountOccurrences(neutralBindings, "(_handGripPoseAction, \"/user/hand/right/input/grip/pose\")").ShouldBe(5);
        neutralBindings.ShouldContain("(_handAimPoseAction, \"/user/hand/left/input/aim/pose\")");
        neutralBindings.ShouldContain("(_handAimPoseAction, \"/user/hand/right/input/aim/pose\")");
        input.ShouldContain("if (SyncActionsForFrame())\n                Volatile.Write(ref _openXrActionsSyncedFrameNumber, frameNo);");
    }

    [Test]
    public void AllocationAudit_FlagsOpenXrFormattedLoggingCandidates()
    {
        string script = ReadWorkspaceFile("Tools/Reports/Find-NewAllocations.ps1");

        script.ShouldContain("FailOnOpenXrHotPathAllocations");
        script.ShouldContain("OpenXR hot-path formatted logging candidates");
        script.ShouldContain("OpenXRAPI*.cs");
        script.ShouldContain("Debug\\.(Out|Log|LogWarning|LogException)");
    }

    [Test]
    public void MonadoSmokeTooling_UsesPerProcessRuntimeSelectionAndLoaderPreflight()
    {
        string finder = ReadWorkspaceFile("Tools/OpenXR/Find-MonadoRuntime.ps1");
        string installer = ReadWorkspaceFile("Tools/OpenXR/Install-Monado.ps1");
        string service = ReadWorkspaceFile("Tools/OpenXR/Start-MonadoService.ps1");
        string runner = ReadWorkspaceFile("Tools/OpenXR/Run-OpenXrMonadoSmoke.ps1");
        string tasks = ReadWorkspaceFile(".vscode/tasks.json");

        finder.ShouldContain(XREngineEnvironmentVariables.XrRuntimeJson);
        finder.ShouldContain(XREngineEnvironmentVariables.MonadoRuntimeJson);
        finder.ShouldContain("openxr_monado-dev.json");
        finder.ShouldContain("No registry values were read or written by this script.");
        finder.ShouldNotContain("Set-ItemProperty");
        finder.ShouldNotContain("New-ItemProperty");

        installer.ShouldContain("https://github.com/BlackJaxDev/Monado.git");
        installer.ShouldContain("https://github.com/microsoft/vcpkg.git");
        installer.ShouldContain("XRT_FEATURE_SERVICE=ON");
        installer.ShouldContain("openxr_loader.dll");
        installer.ShouldContain(XREngineEnvironmentVariables.MonadoRuntimeJson);
        installer.ShouldContain("SetUserEnvironment");
        installer.ShouldNotContain("Set-ItemProperty");
        installer.ShouldNotContain("New-ItemProperty");

        service.ShouldContain("ownedByRunner");
        service.ShouldContain("monado-service.exe");
        service.ShouldContain("-WindowStyle Hidden");
        service.ShouldContain("SIMULATED_HMD_POSE_MODE");
        service.ShouldContain("SimulatedHmdPoseMode = \"stationary\"");

        runner.ShouldContain("xrEnumerateApiLayerProperties");
        runner.ShouldContain("xrEnumerateInstanceExtensionProperties");
        runner.ShouldContain("XR_KHR_opengl_enable");
        runner.ShouldContain(XREngineEnvironmentVariables.XrRuntimeJson);
        runner.ShouldContain("--smoke-frames");
        runner.ShouldContain(XREngineEnvironmentVariables.UnitTestVrMode);
        runner.ShouldContain("MonadoOpenXR");
        runner.ShouldContain("Build\\_AgentValidation");
        runner.ShouldContain("-FailOnOpenXrHotPathAllocations");
        runner.ShouldContain("RequireOwnedService");

        tasks.ShouldContain("Start-Editor-UnitTesting-OpenXR-Monado-NoDebug");
        tasks.ShouldContain("Install-Monado");
        tasks.ShouldContain("Test-OpenXR-Monado-Smoke");
        tasks.ShouldContain("Test-OpenXR-SceneOnlyVR-Smoke");
    }

    [Test]
    public void Phase524bValidation_NormalizesRuntimePoseBasisAndKeepsScriptedRootMotion()
    {
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs");
        string xrCalls = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs");
        string cameraIntegration = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.SceneViews.cs");
        string validationScene = string.Join("\n", new[]
        {
            ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Builders/BootstrapPhase524bValidationBuilder.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Phase524bScenarioComponent.cs"),
        });

        state.ShouldContain("_phase524bFrozenRuntimePoseInitialized");
        xrCalls.ShouldContain("Phase524bTemporalStateDiagnostics.Enabled");
        xrCalls.ShouldContain("CreatePhase524bDeterministicRuntimePoseBasis");
        xrCalls.ShouldContain("Frozen the first valid OpenXR runtime FOV basis with a deterministic centered");
        cameraIntegration.ShouldContain("TryGetPhase524bFrozenViewPoseAndFov");
        validationScene.ShouldContain("CalculateTemporalHeadTranslation(sequenceFrame)");
        validationScene.ShouldContain("CalculateTemporalHeadYawDegrees(sequenceFrame)");
    }

    [Test]
    public void Phase524bValidation_DeterministicRuntimePoseBasisIsCenteredAndStereo()
    {
        OpenXRAPI.CreatePhase524bDeterministicRuntimePoseBasis(
            out Matrix4x4 leftEye,
            out Matrix4x4 rightEye,
            out Matrix4x4 head);

        float halfIpd = OpenXRAPI.Phase524bValidationIpdMeters * 0.5f;
        leftEye.Translation.ShouldBe(new Vector3(-halfIpd, 0.0f, 0.0f));
        rightEye.Translation.ShouldBe(new Vector3(halfIpd, 0.0f, 0.0f));
        Vector3.Distance(leftEye.Translation, rightEye.Translation)
            .ShouldBe(OpenXRAPI.Phase524bValidationIpdMeters, 0.000001f);
        head.ShouldBe(Matrix4x4.Identity);
    }

    [Test]
    public void VulkanSdkInstaller_UsesOfficialVersionChecksumAndExecToolEntry()
    {
        string installer = ReadWorkspaceFile("Tools/Dependencies/Install-LatestVulkanSdk.ps1");
        string execTool = ReadWorkspaceFile("ExecTool.bat");

        installer.ShouldContain("https://vulkan.lunarg.com/sdk/latest/windows.json");
        installer.ShouldContain("https://sdk.lunarg.com/sdk/download/$version/windows/vulkan_sdk.exe");
        installer.ShouldContain("https://sdk.lunarg.com/sdk/sha/$version/windows/vulkan_sdk.exe.json");
        installer.ShouldContain("Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256");
        installer.ShouldContain("Get-AuthenticodeSignature -LiteralPath $installerPath");
        installer.ShouldContain("VkLayer_khronos_validation.json");
        installer.ShouldContain("Start-Process @startParameters");
        execTool.ShouldContain("Tools\\Dependencies\\Install-LatestVulkanSdk.ps1");
    }

    [Test]
    public void RenderDocInstaller_InstallsCliPersistsPathAndRunsDoctor()
    {
        string installer = ReadWorkspaceFile("Tools/Dependencies/Install-RenderDoc.ps1");
        string execTool = ReadWorkspaceFile("ExecTool.bat");
        string documentation = ReadWorkspaceFile("Tools/RenderDoc/README.md");

        installer.ShouldContain("$renderDocPackageId = \"BaldurKarlsson.RenderDoc\"");
        installer.ShouldContain("[string]$RdcCliVersion = \"0.5.6\"");
        installer.ShouldContain("\"tool\", \"install\", \"--force\", \"rdc-cli==$RdcCliVersion\"");
        installer.ShouldContain("[Environment]::SetEnvironmentVariable(\"Path\", $updated, \"User\")");
        installer.ShouldContain("Invoke-Native -FilePath $rdcCommand -Arguments @(\"setup-renderdoc\")");
        installer.ShouldContain("& $rdcCommand \"doctor\"");
        execTool.ShouldContain("Tools\\Dependencies\\Install-RenderDoc.ps1");
        documentation.ShouldContain("rdc close");
    }

    [Test]
    public void VulkanRvcBenchmark_OwnsMonadoAndRenderDocLauncherPassesExplicitEnvironment()
    {
        string benchmark = ReadWorkspaceFile("Tools/Benchmarks/Invoke-VulkanPerf.ps1");
        string launcher = ReadWorkspaceFile("Tools/RenderDoc/capture_xrengine.py");
        string documentation = ReadWorkspaceFile("Tools/RenderDoc/README.md");

        benchmark.ShouldContain("[string]$_.vrMode -eq 'MonadoOpenXR'");
        benchmark.ShouldContain("Tools\\OpenXR\\Start-MonadoService.ps1");
        benchmark.ShouldContain("'XR_RUNTIME_JSON'");
        benchmark.ShouldContain("if (-not [bool]$monadoStart.OwnedByRunner)");
        benchmark.ShouldContain("-MarkerPath $monadoServiceMarker -Stop");

        launcher.ShouldContain("subprocess.list2cmdline(editor_arguments)");
        launcher.ShouldContain("rd.EnvironmentModification()");
        launcher.ShouldContain("rd.ExecuteAndInject(");
        launcher.ShouldContain("\"XRE_UNIT_TEST_WORLD_SETTINGS_PATH\"");
        launcher.ShouldContain("\"XRE_FORCE_MESH_SUBMISSION_STRATEGY\"");
        documentation.ShouldContain("python Tools/RenderDoc/capture_xrengine.py");
    }
    [Test]
    public void OpenXrSmokeRun_UsesStableExitCodesAndSummaryContract()
    {
        string program = ReadWorkspaceFile("XREngine.Editor/Program.OpenXrSmokeRunController.cs");
        string diagnostics = string.Concat(
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.SmokeDiagnostics.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXrSmokeSummary.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXrSmokeFrameLedgerEntry.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXrSmokeOcclusionViewLedgerEntry.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXrSmokeOutputLedgerEntry.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXrSmokeSwapchainSummary.cs"));
        string monadoRunner = ReadWorkspaceFile("Tools/OpenXR/Run-OpenXrMonadoSmoke.ps1");
        string phase524bValidator = ReadWorkspaceFile("Tools/Validate-VulkanPhase524b.ps1");
        string xrCalls = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs");
        string frameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string runtimeStateMachine = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.RuntimeStateMachine.cs");
        string vulkan = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding.Implementation.cs");

        program.ShouldContain("ExitStartupFailure = 21");
        program.ShouldContain("ExitFrameTimeout = 22");
        program.ShouldContain("ExitSummaryFailure = 23");
        program.ShouldContain("ExitTeardownFailure = 24");
        program.ShouldContain("--openxr-smoke-summary");
        program.ShouldContain(nameof(XREngineEnvironmentVariables.OpenXrSmokeFrames));
        program.ShouldContain(nameof(XREngineEnvironmentVariables.OpenXrSmokeWarmupFrames));
        program.ShouldContain("--smoke-warmup-frames");
        program.ShouldContain("OpenXrSmokeFrameLedgerEntry[] _frameLedger");
        program.ShouldContain("SmokeFrameCompleted += RecordSmokeFrame");
        program.ShouldContain("VulkanFrameTotalMs");
        program.ShouldContain("OcclusionTelemetry.CpuQuerySubmittedTotal");
        program.ShouldContain("CopyLastActiveCpuViewSnapshots");
        program.ShouldContain("OcclusionTelemetry.CpuActiveViewSnapshotCount");
        program.ShouldContain("OpenXrSmokeOcclusionViewLedgerEntry");
        program.ShouldContain("OutputId = key.OutputId");
        program.ShouldContain("OpenXrSmokeOutputLedgerEntry[] _outputLedger");
        program.ShouldContain("LeftAcquireDelta = leftAcquireDelta");
        program.ShouldContain("LeftPublishDelta = leftPublishDelta");
        program.ShouldContain("SubmissionRejectionCount = outputWork.SubmissionRejectionCount");
        program.ShouldContain("GlobalInFlightWaitCount = outputWork.GlobalInFlightWaitCount");
        program.ShouldContain("OutputMissedDeadlineCount = outputWork.MissedDeadlineCount");
        program.ShouldContain("AchievedRateHz = output.AchievedRateHz");
        program.ShouldContain("DeadlineMissed = output.DeadlineMissed");
        program.ShouldContain("OutputMissedDeadlineCount = observedMissedDeadlineCount");
        program.ShouldContain("LayerCount = ResolveRequiredLayerCount(target.ViewMask)");
        program.ShouldContain("RequestSmokeSessionExit");
        program.ShouldContain("CompletedOpenXrFrameCount");
        program.ShouldContain("NoLayerFrameCount");

        diagnostics.ShouldContain("SchemaVersion");
        diagnostics.ShouldContain("RuntimeManifestPath");
        diagnostics.ShouldContain("EnabledExtensions");
        diagnostics.ShouldContain("SubmittedFrameCount");
        diagnostics.ShouldContain("NoLayerFrameCount");
        diagnostics.ShouldContain("SmokeCompletedFrameCount");
        diagnostics.ShouldContain("PerEyeAcquireCounts");
        diagnostics.ShouldContain("PredictedActionPoseCacheUpdated");
        diagnostics.ShouldContain("DesktopMirrorComposed");
        diagnostics.ShouldContain("PerFrameAllocationsBytes");
        diagnostics.ShouldContain("CurrentSchemaVersion = 9");
        diagnostics.ShouldContain("OpenXrSmokeFrameLedgerEntry");
        diagnostics.ShouldContain("ProjectionLayerSubmitted");
        diagnostics.ShouldContain("SmokeFrameCompleted?.Invoke");
        diagnostics.ShouldContain("OcclusionViewLedger");
        diagnostics.ShouldContain("public ulong OutputId");
        diagnostics.ShouldContain("public int RecoveryStarts");
        diagnostics.ShouldContain("public int RecoveryCompletions");
        diagnostics.ShouldContain("public int CurrentRecoveryAgeFrames");
        diagnostics.ShouldContain("public int MaxRecoveryAgeFrames");
        diagnostics.ShouldContain("OutputLedger");
        diagnostics.ShouldContain("PerEyePublishCounts");

        monadoRunner.ShouldContain("[int]$WarmupFrames = 0");
        monadoRunner.ShouldContain("--smoke-warmup-frames");
        monadoRunner.ShouldContain("XR_API_LAYER_PROPERTIES_SIZE_X64 = 544");
        phase524bValidator.ShouldContain("[int]$RetainedFrames = 300");
        phase524bValidator.ShouldContain("XRE_VULKAN_DIAGNOSTIC_PRESET");
        phase524bValidator.ShouldContain("SyncValidation");
        phase524bValidator.ShouldContain("TrueSinglePassStereo");
        phase524bValidator.ShouldContain("Resource-plan replacement occurred");
        phase524bValidator.ShouldContain("CpuQueryAsync did not perform valid work");
        phase524bValidator.ShouldContain("Desktop POV occlusion was not independently active");
        phase524bValidator.ShouldContain("VR POV occlusion was not independently active");
        phase524bValidator.ShouldContain("MinimumObservedFramesPerSecond = 0.0");
        phase524bValidator.ShouldContain("MinimumObservedFramesPerSecond -gt 0.0");
        phase524bValidator.ShouldContain("Strict SPS attempted sequential fallback");
        phase524bValidator.ShouldContain("did not complete exactly one acquire/wait/publish/release per eye");
        phase524bValidator.ShouldContain("lacks a fresh desktop final write/present or complete true-multiview OpenXR render+submit ledger");
        phase524bValidator.ShouldContain("Desktop TSR output inventory covered");
        phase524bValidator.ShouldContain("MaximumOcclusionResultAgeFrames = 12");
        phase524bValidator.ShouldContain("exceeded result/recovery age bounds");
        phase524bValidator.ShouldContain("foveationEffectiveMode");
        phase524bValidator.ShouldContain("valid full pipeline/output/POV/coverage identity");
        phase524bValidator.ShouldContain("full occlusion keys appeared more than once");
        phase524bValidator.ShouldContain("exactly 300 retained frames");

        xrCalls.ShouldContain("RecordSmokeEndFrame");
        xrCalls.ShouldContain("RecordSmokeLocatedViews");
        xrCalls.ShouldContain("RecordSmokeSessionState");
        frameLifecycle.ShouldContain("RecordSmokeEyeAcquire");
        frameLifecycle.ShouldContain("RecordSmokeEyeWait");
        frameLifecycle.ShouldContain("RecordSmokeEyePublish");
        frameLifecycle.ShouldContain("RecordSmokeEyeRelease");
        runtimeStateMachine.ShouldContain("_runtimeState != OpenXrRuntimeState.SessionRunning");
        runtimeStateMachine.ShouldContain("state == SessionState.Ready");
        runtimeStateMachine.ShouldContain("SetRuntimeState(OpenXrRuntimeState.RecreatePending);");
        runtimeStateMachine.ShouldContain("TryEnsureOpenXrRuntimeService(\"OpenXR runtime probe\")");
        runtimeStateMachine.ShouldContain("TryEnsureOpenXrRuntimeService(\"OpenXR session creation\")");
        vulkan.ShouldContain("Failed to create Vulkan OpenXR session");
        vulkan.ShouldContain("ErrorGraphicsDeviceInvalid");
        vulkan.ShouldContain("runtime-required OpenXR Vulkan");
    }

    [Test]
    public void VulkanPhase524bValidator_RequiresMachineVerifiableCohortEvidence()
    {
        string summary = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXrSmokeSummary.cs");
        string frameLedger = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXrSmokeFrameLedgerEntry.cs");
        string outputLedger = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXrSmokeOutputLedgerEntry.cs");
        string telemetry = ReadWorkspaceFile("XREngine.Runtime.Core/Settings/Contracts/Records/FrameOutputTelemetry.cs");
        string frameOutputs = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.FrameOutputs.cs");
        string vulkanStats = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.cs");
        string frameLoop = ReadVulkanDesktopFrameLoopSources();
        string validator = ReadWorkspaceFile("Tools/Validate-VulkanPhase524b.ps1");

        summary.ShouldContain("CurrentSchemaVersion = 9");
        summary.ShouldContain("VulkanSynchronizationValidationEffective");
        summary.ShouldContain("ExternallyOwnedValidationAllowlist");
        summary.ShouldContain("RequiredCaptureStages");
        summary.ShouldContain("DesktopFinalCaptureStage");
        summary.ShouldContain("OpenXrSmokeCaptureLedgerEntry[] CaptureLedger");
        summary.ShouldContain("OpenXrSmokeTemporalScenarioDefinition[] TemporalScenarioMatrix");
        summary.ShouldContain("OpenXrSmokeCaptureLedgerEntry[] TemporalScenarioCaptureLedger");

        frameLedger.ShouldContain("LifetimeValidationPassed");
        frameLedger.ShouldContain("ResourcePlanGeneration");
        frameLedger.ShouldContain("CommandGeneration");
        frameLedger.ShouldContain("DesktopFinalWriteObserved");
        frameLedger.ShouldContain("DesktopPresentAccepted");
        outputLedger.ShouldContain("PipelineInstanceId");
        outputLedger.ShouldContain("RenderFrameId");
        outputLedger.ShouldContain("SubmitObserved");
        outputLedger.ShouldContain("FinalWriteObserved");
        outputLedger.ShouldContain("PresentResult");

        telemetry.ShouldContain("int PipelineInstanceId = 0");
        telemetry.ShouldContain("int ResourcePlanGeneration = 0");
        telemetry.ShouldContain("ulong CommandGeneration = 0UL");
        frameOutputs.ShouldContain("SubmitObserved |= telemetry.Phase == EFrameOutputPhase.Submit");
        frameOutputs.ShouldContain("PresentObserved |= telemetry.Phase == EFrameOutputPhase.Present");
        frameOutputs.ShouldContain("CopyCurrentOutputs(Span<FrameOutputEntrySnapshot> destination)");
        vulkanStats.ShouldContain("RecordVulkanPresentResult(int result, bool accepted)");
        frameLoop.ShouldContain("RecordVulkanPresentResult(");

        validator.ShouldContain("Measure-SteadyStateGauge");
        validator.ShouldContain("previousTerminalWindowAverage");
        validator.ShouldContain("$values.Count - (2 * $window)");
        validator.ShouldContain("XRE_CAPTURE_DEFAULT_PIPELINE_FBO");
        validator.ShouldContain("phase524b-filtered-log-matches.log");
        validator.ShouldContain("[Math]::Floor([double]$ExpectedSpsWidth * $TsrResolutionScale)");
        validator.ShouldContain("BloomMips=1-4");
        validator.ShouldContain("DefaultPipelineSps_Temporal_${sample}_${stage}_layer${layerIndex}.png");
        validator.ShouldContain("maximumTemporalConvergenceRmse");
        validator.ShouldContain("VUID-");
        validator.ShouldContain("SYNC-HAZARD");
        validator.ShouldContain("UNASSIGNED");
        validator.ShouldContain("Capture ledger requires exactly one");
        validator.ShouldContain("DefaultPipelineSps_");
        validator.ShouldContain("DefaultPipelineDesktop_");
        validator.ShouldContain("did not complete exactly one successful desktop present");
        validator.ShouldContain("changed workload/plan/command identity after warmup");
    }

    [Test]
    public void OpenXrRuntimeRecovery_RestartsHostServiceAndKeepsStrongestLossReason()
    {
        string program = ReadWorkspaceFile("XREngine.Editor/Program.cs");
        string settingsStore = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/UnitTestingWorldSettingsStore.cs");
        string hostServices = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServices.cs");
        string hostInterface = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderPresentationServices.cs");
        string state = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs");
        string instance = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/Instance.cs");
        string runtimeStateMachine = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.RuntimeStateMachine.cs");
        string vulkanBinding = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding.cs");
        string vulkanInstance = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.Instance.cs");
        string vulkanSyncObjects = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Synchronization.cs");

        program.ShouldContain("ConfigureOpenXrRuntimeServiceRecovery(settings)");
        program.ShouldContain("RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer");
        program.ShouldContain("UnitTestingWorldSettingsStore.TryEnsureMonadoServiceForCurrentProcess");

        settingsStore.ShouldContain("TryEnsureMonadoServiceForCurrentProcess");
        settingsStore.ShouldContain("TryEnsureMonadoService(settings, reason, eyeResolution)");
        settingsStore.ShouldContain("Reason={reason}");

        hostServices.ShouldContain("OpenXrRuntimeServiceEnsurer");
        hostServices.ShouldContain("TryEnsureOpenXrRuntimeService(string reason)");
        hostInterface.ShouldContain("bool TryEnsureOpenXrRuntimeService(string reason)");

        state.ShouldContain("_runtimeLossLock");
        runtimeStateMachine.ShouldContain("GetRuntimeLossReasonSeverity");
        runtimeStateMachine.ShouldContain("OpenXrRuntimeLossReason.InstanceLostError => 80");
        runtimeStateMachine.ShouldContain("OpenXrRuntimeLossReason.SessionLostError => 60");
        runtimeStateMachine.ShouldContain("TryEnsureOpenXrRuntimeService($\"OpenXR runtime loss: {lossReason}\")");

        instance.ShouldContain("binding.InvalidateRendererOwnedInstance(renderer, \"OpenXR runtime instance teardown\")");
        vulkanBinding.ShouldContain("InvalidateRendererOwnedInstance(AbstractRenderer renderer, string reason)");
        vulkanBinding.ShouldContain("DeviceContext.InvalidateOpenXrBootstrapInstance(reason)");
        vulkanInstance.ShouldContain("public bool InvalidateOpenXrBootstrapInstance(string reason)");
        vulkanInstance.ShouldContain("AbandonXrInstanceOnDispose(reason)");
        vulkanBinding.ShouldContain("UsesOpenXrVulkanEnable2Creation");

        vulkanSyncObjects.ShouldContain("TimelineWaitPollTimeoutNanoseconds");
        vulkanSyncObjects.ShouldContain("MarkDeviceLost(");
        vulkanSyncObjects.ShouldContain("value == ulong.MaxValue");
        vulkanSyncObjects.ShouldNotContain("TryWaitForTimelineValue(semaphore, value, ulong.MaxValue)");
    }

    [Test]
    public void UnitTestingWorld_OpenXrLaneOverridesAndMixedModeWarningAreExplicit()
    {
        string store = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/UnitTestingWorldSettingsStore.cs");
        string program = ReadWorkspaceFile("XREngine.Editor/Program.cs");
        string settings = string.Join("\n", new[]
        {
            ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Settings/UnitTestingWorldSettings.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Settings/UnitTestingVrSettings.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Settings/Enums/UnitTestingVrLaunchMode.cs"),
        });
        string bootstrapRenderSettings = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/BootstrapRenderSettings.cs");
        string editorUnitTestingWorld = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.cs");
        string editorUnitTestingPawns = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Pawns.cs");
        string engineState = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Engine/Engine.State.cs");

        store.ShouldContain("ApplyVrLaunchOverrides");
        store.ShouldContain(nameof(XREngineEnvironmentVariables.UnitTestVrMode));
        store.ShouldContain(nameof(XREngineEnvironmentVariables.UnitTestVrPawn));
        store.ShouldContain(nameof(XREngineEnvironmentVariables.UnitTestUseOpenXr));
        store.ShouldContain(nameof(XREngineEnvironmentVariables.UnitTestSceneOnlyVrPawn));
        store.ShouldContain(nameof(XREngineEnvironmentVariables.UnitTestPreviewVrStereoViews));
        store.ShouldContain(nameof(XREngineEnvironmentVariables.UnitTestRenderWindowsWhileInVr));
        store.ShouldContain(nameof(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson));
        store.ShouldContain(nameof(XREngineEnvironmentVariables.UnitTestRenderApi));
        store.ShouldContain("settings.RenderWindowsWhileInVR = renderWindowsWhileInVr");
        store.ShouldContain("MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.Rendering))");
        store.ShouldContain("NormalizeVrSettings");
        store.ShouldContain("TryAutoDetectMonadoRuntimeJson");
        store.ShouldContain("TryAutoDetectOpenXrLoader");
        store.ShouldContain("ApplyMonadoServiceStartup");
        store.ShouldContain("monado-service.exe");
        store.ShouldContain("openxr_monado-dev.json");

        store.ShouldContain("settings.VR.Mode is UnitTestingVrLaunchMode.MonadoOpenXR or UnitTestingVrLaunchMode.OpenXR");

        settings.ShouldContain("public UnitTestingVrSettings VR");
        settings.ShouldContain("MonadoOpenXR");
        settings.ShouldContain("public bool UseOpenXR = false");
        settings.ShouldContain("public bool SceneOnlyVRPawn = false");

        editorUnitTestingPawns.ShouldContain("pawnComp.CameraComponent = cameraComponent");
        editorUnitTestingPawns.ShouldContain("Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One).OnPawnCameraChanged();");
        bootstrapRenderSettings.ShouldContain("renderSettings.VrCopyEyePreviewTextures = settings.PreviewVRStereoViews");
        bootstrapRenderSettings.ShouldContain("usesRuntimeDesktopCamera");
        bootstrapRenderSettings.ShouldContain("renderSettings.RenderWindowsWhileInVR = settings.RenderWindowsWhileInVR || requiresIndependentDesktopWindow || usesRuntimeDesktopCamera;");
        bootstrapRenderSettings.ShouldContain("renderSettings.VrMirrorComposeFromEyeTextures = false");
        bootstrapRenderSettings.ShouldContain("renderSettings.VrMirrorMode = EVrMirrorMode.FullIndependentRender");
        bootstrapRenderSettings.ShouldContain("VrMirrorMode={renderSettings.VrMirrorMode}");
        bootstrapRenderSettings.ShouldContain("VrCopyEyePreviewTextures={renderSettings.VrCopyEyePreviewTextures}");
        editorUnitTestingWorld.ShouldContain("s.VrCopyEyePreviewTextures = previewVrStereoViews");
        editorUnitTestingWorld.ShouldContain("usesRuntimeDesktopCamera");
        editorUnitTestingWorld.ShouldContain("s.VrMirrorComposeFromEyeTextures = false");
        editorUnitTestingWorld.ShouldContain("s.VrMirrorMode = EVrMirrorMode.FullIndependentRender");
        editorUnitTestingWorld.ShouldContain("VrMirrorMode={s.VrMirrorMode}");
        editorUnitTestingWorld.ShouldContain("VrCopyEyePreviewTextures={s.VrCopyEyePreviewTextures}");
        engineState.ShouldContain("XRComponent? controlledPawn = existing.ControlledPawnComponent");
        engineState.ShouldContain("replacement.ControlledPawnComponent = controlledPawn");
    }

    [Test]
    public void RuntimeVrDesktopView_DoesNotReuseEyeCommandsOrEditorImGuiWhenDesktopEditingDisabled()
    {
        string vrState = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs");
        string vrDeviceTransform = ReadWorkspaceFile("XREngine.Runtime.InputIntegration/Scene/Transforms/VR/VRDeviceTransformBase.cs");
        string openXrApi = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.SceneViews.cs");
        string openXrState = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs");
        string openXrFrameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string bootstrapPawns = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/BootstrapPawnFactory.cs");
        string editorUnitTestingPawns = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Pawns.cs");
        string editorImGui = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.ImGui.cs");
        string hostServices = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/RenderingHost/Engine.RuntimeRenderingHostServices.cs");
        string frameOutputs = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.FrameOutputs.cs");

        vrState.ShouldContain("ConfigureDesktopViewportForVrWindow(window);");
        int initSinglePassIndex = vrState.IndexOf("private static void InitSinglePass", StringComparison.Ordinal);
        initSinglePassIndex.ShouldBeGreaterThanOrEqualTo(0);
        vrState.IndexOf("ConfigureDesktopViewportForVrWindow(window);", initSinglePassIndex, StringComparison.Ordinal)
            .ShouldBeGreaterThan(initSinglePassIndex);
        vrState.ShouldContain("bool shareStereoCommands = RuntimeRenderingHostServices.Presentation.VrMirrorComposeFromEyeTextures;");
        vrState.ShouldNotContain("!RuntimeRenderingHostServices.Presentation.RenderWindowsWhileInVR ||");
        vrState.ShouldContain("desktopViewport.MeshRenderCommandsOverride = null");
        vrState.ShouldContain("desktopViewport.AutomaticallyCollectVisible = true");
        vrState.ShouldContain("desktopViewport.AutomaticallySwapBuffers = true");
        vrState.ShouldContain("_sharedMeshRenderCommands.IsRenderCommandSnapshotAuthority = true;");
        vrState.ShouldNotContain("bool independentDesktopView = RuntimeRenderingHostServices.Presentation.RenderWindowsWhileInVR && !shareStereoCommands;");
        vrDeviceTransform.ShouldContain("RuntimeVrStateServices.IsOpenXRActive && this is XREngine.Scene.Transforms.VRHeadsetTransform");
        vrDeviceTransform.ShouldContain("SetRenderMatrix(renderMatrix, recalcAllChildRenderMatrices: !isOpenXrHeadset)");
        vrDeviceTransform.ShouldNotContain("PropagateOpenXrHeadsetRenderMatrixToNonEyeChildren");
        vrDeviceTransform.ShouldNotContain("child.LocalMatrix * parentRenderMatrix");
        openXrApi.ShouldContain("camera.Transform.SetRenderMatrix(localPose * rootRender, recalcAllChildRenderMatrices: false);");
        openXrState.ShouldNotContain("_openXrSharedMeshRenderCommands");
        openXrFrameLifecycle.ShouldContain("leftMeshCommands = _openXrLeftViewport.RenderPipelineInstance.MeshRenderCommands");
        openXrFrameLifecycle.ShouldContain("rightMeshCommands = _openXrRightViewport.RenderPipelineInstance.MeshRenderCommands");
        openXrFrameLifecycle.ShouldContain("_openXrLeftViewport.SwapBuffers(leftCommands");
        openXrFrameLifecycle.ShouldContain("_openXrRightViewport.SwapBuffers(rightCommands");
        openXrState.ShouldNotContain("HasIndependentDesktopVrView");
        string editorUnitTestingWorld = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.cs");
        editorUnitTestingWorld.ShouldContain("s.RenderWindowsWhileInVR = Toggles.RenderWindowsWhileInVR || requiresIndependentDesktopWindow || usesRuntimeDesktopCamera;");
        editorUnitTestingPawns.ShouldContain("firstPersonViewNode.SetTransform<Transform>();");
        editorUnitTestingPawns.ShouldNotContain("var firstPersonViewTfm = firstPersonViewNode.SetTransform<SmoothedParentConstraintTransform>();");

        bootstrapPawns.ShouldNotContain("CreateEditorUi(characterPawnModelParentNode");
        editorUnitTestingPawns.ShouldNotContain("CreateEditorUI(characterPawnModelParentNode");

        editorImGui.ShouldContain("ShouldSuppressEditorImGuiForRuntimeVrView");
        editorImGui.ShouldContain("!EditorUnitTests.Toggles.AllowEditingInVR");
        editorImGui.ShouldContain("Engine.Input.SetUIInputCaptured(false)");
        hostServices.ShouldContain("output.OutputKind == EFrameOutputKind.DesktopScene && output.RenderPhaseSceneRendered");
        hostServices.ShouldContain("if (autoSkipWhenOverBudget && ShouldHoldDesktopOutputForVrPressure(frameId, manifest))");
        hostServices.ShouldContain("if (ShouldKeepIndependentDesktopLive(mode))");
        hostServices.ShouldContain("autoSkipWhenOverBudget = false;");
        hostServices.ShouldContain("private bool ShouldKeepIndependentDesktopLive(EVrMirrorMode mode)");
        hostServices.ShouldContain("mode == EVrMirrorMode.FullIndependentRender");
        hostServices.ShouldContain("RuntimeEngine.Rendering.Settings.RenderWindowsWhileInVR");
        hostServices.ShouldNotContain("if (output.SceneRendered ||");
        frameOutputs.ShouldContain("public bool RenderPhaseSceneRendered");
        frameOutputs.ShouldContain("telemetry.Phase == EFrameOutputPhase.Render && telemetry.SceneRendered");
    }

    [Test]
    public void UnitTestingWorld_DesktopEditingCameraRemainsFlyableWhenVrPickupIsEnabled()
    {
        string editorUnitTestingPawns = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Pawns.cs");
        string bootstrapPawns = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/BootstrapPawnFactory.cs");
        string editorUnitTestingUi = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs");
        string uiPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/UserInterfaceRenderPipeline.cs");
        string bootstrapEditorBridge = string.Join("\n", new[]
        {
            ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Bridges/BootstrapEditorBridge.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Bridges/IBootstrapEditorBridge.cs"),
        });
        string bootstrapEditorHooks = ReadWorkspaceFile("XREngine.Editor/Bootstrap/BootstrapEditorHookRegistration.cs");

        AssertDesktopEditingCameraContract(
            editorUnitTestingPawns,
            "Toggles.AllowEditingInVR",
            "Toggles.AddCameraVRPickup",
            "UserInterface.CreateCameraPreviewOverlay(camComp, CameraVRPickupName)");

        AssertDesktopEditingCameraContract(
            bootstrapPawns,
            "settings.AllowEditingInVR",
            "settings.AddCameraVRPickup",
            "BootstrapEditorBridge.Current?.CreateCameraPreviewUi(camComp, CameraVRPickupName)");

        bootstrapEditorBridge.ShouldContain("void CreateCameraPreviewUi(CameraComponent camera, string label);");
        bootstrapEditorHooks.ShouldContain("EditorUnitTests.UserInterface.CreateCameraPreviewOverlay(camera, label);");

        editorUnitTestingUi.ShouldContain("public static void CreateCameraPreviewOverlay(CameraComponent camera, string label)");
        editorUnitTestingUi.ShouldContain("private const int PreviewOverlayRenderPass = (int)EDefaultRenderPass.OnTopForward;");
        editorUnitTestingUi.ShouldContain("CreateVRStereoPreviewOverlay(rootCanvasNode);");
        editorUnitTestingUi.ShouldContain("FlushPendingCameraPreviewOverlays(rootCanvasNode);");
        editorUnitTestingUi.ShouldContain("var preview = previewNode.AddComponent<UIViewportComponent>()!");
        editorUnitTestingUi.ShouldContain("preview.RenderPass = PreviewOverlayRenderPass;");
        editorUnitTestingUi.ShouldContain("preview.Viewport.AutomaticallyCollectVisible = false;");
        editorUnitTestingUi.ShouldContain("preview.Viewport.AutomaticallySwapBuffers = false;");
        editorUnitTestingUi.ShouldContain("preview.Viewport.AllowUIRender = false;");
        editorUnitTestingUi.ShouldContain("preview.Viewport.CameraComponent = camera;");
        editorUnitTestingUi.ShouldContain("previewTfm.MinAnchor = new Vector2(0.5f, 0.0f);");
        editorUnitTestingUi.ShouldContain("RenderPass = PreviewOverlayRenderPass");
        uiPipeline.ShouldContain("{ (int)EDefaultRenderPass.OnTopForward, _nearToFarSorter }");

        string createEditorUi = SliceMethod(
            editorUnitTestingUi,
            "public static UICanvasComponent CreateEditorUI",
            "private static void CreateVRStereoPreviewOverlay");
        int nativeBranchIndex = createEditorUi.IndexOf("if (Toggles.EditorType == UnitTestEditorType.Native)", StringComparison.Ordinal);
        int previewFlushIndex = createEditorUi.IndexOf("FlushPendingCameraPreviewOverlays(rootCanvasNode);", StringComparison.Ordinal);
        nativeBranchIndex.ShouldBeGreaterThanOrEqualTo(0);
        previewFlushIndex.ShouldBeGreaterThan(nativeBranchIndex);
    }

    private static void AssertDesktopEditingCameraContract(
        string source,
        string allowEditingExpression,
        string addPickupExpression,
        string cameraPreviewRegistration)
    {
        string createPlayerPawn = SliceMethod(
            source,
            "public static SceneNode? CreatePlayerPawn",
            "private static SceneNode CreateCharacterVRPawn");

        CountOccurrences(createPlayerPawn, "CreateVrDesktopEditorCamera(rootNode, setUI, isServer);").ShouldBe(2);
        CountOccurrences(createPlayerPawn, "CreateCameraVRPickup(rootNode, setUI);").ShouldBe(2);
        createPlayerPawn.ShouldNotContain($"{allowEditingExpression} || {addPickupExpression}");
        createPlayerPawn.ShouldNotContain($"{allowEditingExpression} && !{addPickupExpression}");
        createPlayerPawn.ShouldNotContain($"CreateDesktopCamera(cameraNode, isServer, {allowEditingExpression}");

        source.ShouldContain($"if (!{allowEditingExpression})");
        source.ShouldContain($"if (!{addPickupExpression})");
        source.ShouldContain("CreateDesktopCamera(cameraNode, isServer, flyable: true, addListener: false)");
        source.ShouldContain("CreateCamera(rootNode, out var camComp, null, cameraName: CameraVRPickupName)");
        source.ShouldContain("AddCameraPickupPhysicsBody(cameraNode, initialPosition, initialRotation);");
        source.ShouldContain(cameraPreviewRegistration);
        source.ShouldContain("private static DynamicRigidBodyComponent AddCameraPickupPhysicsBody(");

        string createDesktopCamera = SliceMethod(
            source,
            "private static PawnComponent? CreateDesktopCamera",
            "private static SceneNode CreateDesktopCharacterPawn");

        (createDesktopCamera.Contains("EditorFlyingCameraPawnComponent", StringComparison.Ordinal) ||
            createDesktopCamera.Contains("CreateFlyableCameraPawn(cameraNode", StringComparison.Ordinal))
            .ShouldBeTrue();
        createDesktopCamera.ShouldContain("cameraNode.AddComponent<PawnComponent>()");
        createDesktopCamera.ShouldNotContain("DynamicRigidBodyComponent");

        int plainPawnIndex = createDesktopCamera.IndexOf("pawnComp = cameraNode.AddComponent<PawnComponent>()!", StringComparison.Ordinal);
        int configureIndex = createDesktopCamera.IndexOf("ConfigureEditorViewCamera(parent, cameraNode);", StringComparison.Ordinal);
        plainPawnIndex.ShouldBeGreaterThanOrEqualTo(0);
        configureIndex.ShouldBeGreaterThan(plainPawnIndex);
    }

    [Test]
    public void EditorDepthHitAndPreviewRenderTargets_DoNotDependOnOpenXrSwapchainAlpha()
    {
        string editorPawn = ReadWorkspaceFile("XREngine.Editor/EditorFlyingCameraPawnComponent.cs");
        string uiMaterial = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/UI/Core/UIMaterialComponent.cs");
        string uiViewport = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/UI/Core/UIViewportComponent.cs");
        string editorUnitTestingUi = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs");
        string xrViewport = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRViewport.cs");
        string xrPipelineInstance = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        string cameraComponent = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Camera/CameraComponent.cs");

        string postRender = SliceMethod(
            editorPawn,
            "private void PostRender()",
            "private void ApplyInput");
        postRender.ShouldContain("GetDepthHit(vp, GetCursorInternalCoordinatePosition(vp));");
        postRender.ShouldNotContain("IsRenderingExternalSwapchainTarget");

        uiMaterial.ShouldContain("public void SetBlendModeAllDrawBuffers(BlendMode? blendMode)");
        uiMaterial.ShouldContain("_renderParameters.BlendModeAllDrawBuffers = blendMode;");
        uiMaterial.ShouldContain("RenderCommand2D.MarkDirty();");
        uiMaterial.ShouldContain("RenderCommand3D.MarkDirty();");

        uiMaterial.ShouldContain("public bool DisableBatching");
        uiMaterial.ShouldContain("return !DisableBatching &&");
        uiViewport.ShouldContain("DisableBatching = true;");
        uiViewport.ShouldContain("SetBlendModeAllDrawBuffers(BlendMode.Disabled());");
        uiViewport.ShouldContain("Viewport.AllowAutomaticInternalResolution = false;");
        uiViewport.ShouldNotContain("Viewport.UseDirectFboTargetCommandsWhenRenderingToFbo = true;");
        editorUnitTestingUi.ShouldNotContain("preview.Viewport.UseDirectFboTargetCommandsWhenRenderingToFbo = true;");
        xrViewport.ShouldContain("public bool AllowAutomaticInternalResolution");
        xrViewport.ShouldContain("_renderPipeline.InternalResolutionResized(InternalWidth, InternalHeight, this);");
        xrViewport.ShouldContain("_renderPipeline.ViewportResized(Width, Height, this);");
        xrPipelineInstance.ShouldContain("public void InternalResolutionResized(int internalWidth, int internalHeight, XRViewport? viewport)");
        xrPipelineInstance.ShouldContain("viewport ??= RenderState.WindowViewport ?? LastWindowViewport;");
        xrPipelineInstance.ShouldContain("if (viewport.AllowAutomaticInternalResolution &&");
        xrPipelineInstance.ShouldContain("!ShouldDeferResourceGenerationForInteractiveWindowResize(viewport))");
        cameraComponent.ShouldContain("if (!viewport.AllowAutomaticInternalResolution)");
        cameraComponent.ShouldContain("return;");
        editorUnitTestingUi.ShouldContain("private const int PreviewOverlayZIndex = int.MaxValue;");
        editorUnitTestingUi.ShouldContain("private const int FpsOverlayZIndex = int.MaxValue - 100;");
        editorUnitTestingUi.ShouldContain("text.RenderCommand2D.ZIndex = FpsOverlayZIndex;");
        editorUnitTestingUi.ShouldContain("left.DisableBatching = true;");
        editorUnitTestingUi.ShouldContain("right.DisableBatching = true;");
        editorUnitTestingUi.ShouldContain("left.SetBlendModeAllDrawBuffers(BlendMode.Disabled());");
        editorUnitTestingUi.ShouldContain("right.SetBlendModeAllDrawBuffers(BlendMode.Disabled());");
        editorUnitTestingUi.ShouldContain("target.DisableBatching = true;");
        editorUnitTestingUi.ShouldContain("target.SetBlendModeAllDrawBuffers(BlendMode.Disabled());");
        editorUnitTestingUi.ShouldContain("RegisterPreviewOverlayDiagnostics(\"Left Eye Preview\", left);");
        editorUnitTestingUi.ShouldContain("RegisterPreviewOverlayDiagnostics(previewNode.Name, preview);");
    }

    [Test]
    public void HeavyUploadStageLogging_IsExplicitOptIn()
    {
        string renderDiagnosticsFlags = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RenderDiagnosticsFlags.cs");

        renderDiagnosticsFlags.ShouldContain(
            "UploadStageLogging = ReadBool(XREngineEnvironmentVariables.UploadStageLogging);");
        renderDiagnosticsFlags.ShouldNotContain("Debugger.IsAttached");
    }

    [Test]
    public void UnsupportedGpuMeshBvhPicking_UsesCoarseBoundsInsteadOfExactCpuTriangleWalk()
    {
        string worldInstance = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.Picking.cs");
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/BvhRaycastDispatcher.cs");

        worldInstance.ShouldContain("using coarse bounds picking");
        dispatcher.ShouldContain("rejecting GPU raycast request");
        dispatcher.ShouldNotContain("Falling back to CPU mesh picking");

        string gpuBvhPickBranch = SliceMethod(
            worldInstance,
            "if (TryGetGpuMeshBvhPickSubMesh",
            "if (!TryIntersectRenderableMesh");

        gpuBvhPickBranch.ShouldContain("TryCreateUnsupportedGpuMeshBvhCoarsePick(");
        gpuBvhPickBranch.ShouldNotContain("TryIntersectRenderableMesh(");

        string coarsePick = SliceMethod(
            worldInstance,
            "private static bool TryCreateUnsupportedGpuMeshBvhCoarsePick",
            "private static GpuMeshBvhPickCandidate QueueGpuMeshBvhPick");

        coarsePick.ShouldContain("GpuMeshBvhPickRayIntersectsRequestBounds");
        coarsePick.ShouldContain("candidate.CompleteHit(");
        coarsePick.ShouldContain("result = candidate;");
    }

    [Test]
    [NonParallelizable]
    public void UnitTestingWorld_DesktopModeOverrideDoesNotPublishConfiguredOpenXrRuntime()
    {
        string? previousMode = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrMode);
        string? previousRuntimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        string? previousRuntimeJsonOverride = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson);
        string? previousUseOpenXr = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestUseOpenXr);
        string? previousPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path);

        try
        {
            Environment.SetEnvironmentVariable(
                XREngineEnvironmentVariables.UnitTestVrMode,
                nameof(UnitTestingVrLaunchMode.Desktop));
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, null);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson, null);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestUseOpenXr, null);

            UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
                """
                {
                  "VR": {
                    "Mode": "MonadoOpenXR",
                    "OpenXrRuntimeJson": "configured-openxr-runtime.json"
                  }
                }
                """);

            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson).ShouldBeNull();
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path).ShouldBe(previousPath);

            UnitTestingWorldSettingsStore.ApplyVrLaunchOverrides(settings);

            settings.VR.Mode.ShouldBe(UnitTestingVrLaunchMode.Desktop);
            settings.UseOpenXR.ShouldBeFalse();
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson).ShouldBeNull();
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path).ShouldBe(previousPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrMode, previousMode);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, previousRuntimeJson);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson, previousRuntimeJsonOverride);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestUseOpenXr, previousUseOpenXr);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.Path, previousPath);
        }
    }

    [Test]
    [NonParallelizable]
    public void UnitTestingWorld_VrPerfEnvOverridesCanDisableDesktopVrWindow()
    {
        string? previousRuntimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        string? previousPreview = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestPreviewVrStereoViews);
        string? previousAllowDesktopEditing = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestAllowDesktopEditingInVr);
        string? previousRenderWindows = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestRenderWindowsWhileInVr);
        string? previousPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path);

        try
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, @"C:\existing\openxr_monado.json");
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestPreviewVrStereoViews, "0");
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestAllowDesktopEditingInVr, "0");
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestRenderWindowsWhileInVr, "0");

            UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
                """
                {
                  "VR": {
                    "Mode": "MonadoOpenXR",
                    "PreviewStereoViews": true,
                    "AllowDesktopEditing": true,
                    "OpenXrRuntimeJson": null
                  },
                  "RenderWindowsWhileInVR": true
                }
                """);

            UnitTestingWorldSettingsStore.ApplyVrLaunchOverrides(settings);

            settings.VR.PreviewStereoViews.ShouldBeFalse();
            settings.VR.AllowDesktopEditing.ShouldBeFalse();
            settings.PreviewVRStereoViews.ShouldBeFalse();
            settings.AllowEditingInVR.ShouldBeFalse();
            settings.RenderWindowsWhileInVR.ShouldBeFalse();
            settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderWindowsWhileInVR)).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, previousRuntimeJson);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestPreviewVrStereoViews, previousPreview);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestAllowDesktopEditingInVr, previousAllowDesktopEditing);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestRenderWindowsWhileInVr, previousRenderWindows);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.Path, previousPath);
        }
    }

    [Test]
    [NonParallelizable]
    public void UnitTestingWorld_VrModeNormalizesToRuntimeFlags()
    {
        string? previousRuntimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        string? previousPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path);
        try
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, @"C:\existing\openxr_runtime.json");
            UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
                """
                {
                  "VR": {
                    "Mode": "MonadoOpenXR",
                    "PreviewStereoViews": true,
                    "AllowDesktopEditing": false,
                    "OpenXrRuntimeJson": null
                  }
                }
                """);

            settings.VR.Mode.ShouldBe(UnitTestingVrLaunchMode.MonadoOpenXR);
            settings.VRPawn.ShouldBeTrue();
            settings.UseOpenXR.ShouldBeTrue();
            settings.SceneOnlyVRPawn.ShouldBeFalse();
            settings.PreviewVRStereoViews.ShouldBeTrue();
            settings.AllowEditingInVR.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, previousRuntimeJson);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.Path, previousPath);
        }
    }

    [Test]
    [NonParallelizable]
    public void UnitTestingWorld_MonadoModePreservesExplicitVulkanBackend()
    {
        string? previousRuntimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        string? previousRenderApi = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestRenderApi);
        string? previousPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path);

        try
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, @"C:\existing\openxr_monado.json");
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestRenderApi, null);

            UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
                """
                {
                  "Rendering": {
                    "RenderBackend": "Vulkan"
                  },
                  "VR": {
                    "Mode": "MonadoOpenXR",
                    "OpenXrRuntimeJson": null
                  }
                }
                """);

            settings.Rendering.RenderBackend.ShouldBe(ERenderLibrary.Vulkan);

            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestRenderApi, "Vulkan");
            settings = UnitTestingWorldSettingsStore.ParseJsonc(
                """
                {
                  "Rendering": {
                    "RenderBackend": "Vulkan"
                  },
                  "VR": {
                    "Mode": "MonadoOpenXR",
                    "OpenXrRuntimeJson": null
                  }
                }
                """);

            settings.Rendering.RenderBackend.ShouldBe(ERenderLibrary.Vulkan);
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, previousRuntimeJson);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestRenderApi, previousRenderApi);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.Path, previousPath);
        }
    }

    [Test]
    [NonParallelizable]
    public void UnitTestingWorld_OpenXrVulkanStartupHonorsCpuDirectAndUsesNonDiagnosticProfile()
    {
        string? previousRuntimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        string? previousPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path);

        try
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, @"C:\existing\openxr_monado.json");

            UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
                """
                {
                  "Rendering": {
                    "RenderBackend": "Vulkan"
                  },
                  "VR": {
                    "Mode": "MonadoOpenXR",
                    "OpenXrRuntimeJson": null
                  },
                  "GPURenderDispatch": false
                }
                """);

            var startupSettings = new GameStartupSettings
            {
                DefaultUserSettings = new UserSettings(),
                GPURenderDispatch = false,
            };

            UnitTestingWorldSettingsStore.ApplyStartupOverrides(startupSettings, settings);

            startupSettings.GPURenderDispatch.ShouldBeFalse();
            startupSettings.VulkanGpuDrivenProfileOverride.HasOverride.ShouldBeTrue();
            startupSettings.VulkanGpuDrivenProfileOverride.Value.ShouldBe(EVulkanGpuDrivenProfile.DevParity);
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, previousRuntimeJson);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.Path, previousPath);
        }
    }

    [Test]
    public void MonkeyBallDefaults_DoNotForceCpuMeshSubmission()
    {
        string defaults = ReadWorkspaceFile("Samples/MonkeyBallVR/Config/engine_defaults.asset");

        defaults.ShouldNotContain("ForceMeshSubmissionStrategy: CpuDirect");
    }

    [Test]
    public void UnitTestingOpenXrVulkan_HonorsPersistedCpuDirectForceAndAllowsEnvOverride()
    {
        string effectiveSettings = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.EffectiveSettings.cs");

        effectiveSettings.ShouldNotContain("ShouldIgnorePersistedCpuDirectMeshSubmissionForceForUnitTestingOpenXrVulkan");
        effectiveSettings.ShouldNotContain("Ignoring persisted ForceMeshSubmissionStrategy=CpuDirect");
        effectiveSettings.ShouldContain("return RuntimeEngine.Rendering.Settings.ForceMeshSubmissionStrategy;");
        effectiveSettings.ShouldContain("EffectiveSettingsEnvOverrides.ForceMeshSubmissionStrategy");
    }

    [Test]
    [NonParallelizable]
    public void UnitTestingWorld_MonadoModeAutoDetectsRuntimeManifestWhenUnset()
    {
        string? previousRuntimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        string? previousMonadoRuntimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.MonadoRuntimeJson);
        string? previousMonadoInstallDir = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.MonadoInstallDir);
        string? previousPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path);
        string tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            Directory.CreateDirectory(tempRoot);
            string manifestPath = Path.Combine(tempRoot, "openxr_monado.json");
            string libraryPath = Path.Combine(tempRoot, "monado_runtime.dll");
            File.WriteAllText(libraryPath, string.Empty);
            File.WriteAllText(
                manifestPath,
                """
                {
                  "runtime": {
                    "name": "Monado",
                    "library_path": "monado_runtime.dll",
                    "api_version": "1.1"
                  }
                }
                """);

            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, null);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.MonadoRuntimeJson, manifestPath);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.MonadoInstallDir, null);

            UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
                """
                {
                  "VR": {
                    "Mode": "MonadoOpenXR",
                    "OpenXrRuntimeJson": null
                  }
                }
                """);

            settings.VR.OpenXrRuntimeJson.ShouldBe(Path.GetFullPath(manifestPath));
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson).ShouldBe(Path.GetFullPath(manifestPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, previousRuntimeJson);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.MonadoRuntimeJson, previousMonadoRuntimeJson);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.MonadoInstallDir, previousMonadoInstallDir);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.Path, previousPath);

            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    [NonParallelizable]
    public void UnitTestingWorld_MonadoModeAddsDetectedOpenXrLoaderToProcessPath()
    {
        string? previousRuntimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        string? previousMonadoRuntimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.MonadoRuntimeJson);
        string? previousMonadoInstallDir = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.MonadoInstallDir);
        string? previousPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path);
        string tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            string binDir = Path.Combine(tempRoot, "bin");
            Directory.CreateDirectory(binDir);
            string manifestPath = Path.Combine(tempRoot, "openxr_monado.json");
            string runtimeLibraryPath = Path.Combine(binDir, "openxr_monado.dll");
            string loaderPath = Path.Combine(binDir, "openxr_loader.dll");
            File.WriteAllText(runtimeLibraryPath, string.Empty);
            File.WriteAllText(loaderPath, string.Empty);
            File.WriteAllText(
                manifestPath,
                """
                {
                  "runtime": {
                    "name": "Monado",
                    "library_path": "bin/openxr_monado.dll"
                  }
                }
                """);

            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, null);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.MonadoRuntimeJson, manifestPath);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.MonadoInstallDir, tempRoot);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.Path, Environment.SystemDirectory);

            _ = UnitTestingWorldSettingsStore.ParseJsonc(
                """
                {
                  "VR": {
                    "Mode": "MonadoOpenXR",
                    "OpenXrRuntimeJson": null
                  }
                }
                """);

            string? updatedPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path);
            updatedPath.ShouldNotBeNullOrWhiteSpace();
            updatedPath!.Split(Path.PathSeparator)[0].ShouldBe(Path.GetFullPath(binDir));
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, previousRuntimeJson);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.MonadoRuntimeJson, previousMonadoRuntimeJson);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.MonadoInstallDir, previousMonadoInstallDir);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.Path, previousPath);

            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void UnitTestingWorld_LegacyVrBooleansNormalizeToGroupedMode()
    {
        UnitTestingWorldSettings settings = UnitTestingWorldSettingsStore.ParseJsonc(
            """
            {
              "VRPawn": true,
              "UseOpenXR": false,
              "SceneOnlyVRPawn": true,
              "PreviewVRStereoViews": true,
              "AllowEditingInVR": false
            }
            """);

        settings.VR.Mode.ShouldBe(UnitTestingVrLaunchMode.Emulated);
        settings.VRPawn.ShouldBeTrue();
        settings.UseOpenXR.ShouldBeFalse();
        settings.SceneOnlyVRPawn.ShouldBeTrue();
        settings.VR.PreviewStereoViews.ShouldBeTrue();
        settings.VR.AllowDesktopEditing.ShouldBeFalse();
    }

    [Test]
    public void PacingThread_ModeIsConfigurableAndSurfacesStats()
    {
        string state = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs");
        string pacingMode = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.OpenXrRenderPacingMode.cs");
        string settings = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Settings/RuntimeEngine.Rendering.EngineSettings.cs");
        string stats = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vr.cs");
        string packet = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string sender = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/Engine/Engine.ProfilerSender.cs");
        string editorSource = ReadWorkspaceFile("XREngine.Editor/EngineProfilerDataSource.cs");
        string panel = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");

        pacingMode.ShouldContain("enum OpenXrRenderPacingMode");
        pacingMode.ShouldContain("InRenderCallback");
        pacingMode.ShouldContain("PostRenderCallback");
        pacingMode.ShouldContain("DedicatedThread");
        pacingMode.ShouldContain("CollectVisibleThread");
        state.ShouldContain("OpenXrRenderPacingHandling");

        settings.ShouldContain("OpenXrRenderPacingMode");

        stats.ShouldContain("VrXrPacingThreadIdleTimeMs");
        stats.ShouldContain("VrXrPacingHandoffStalls");
        stats.ShouldContain("RecordVrXrPacingThreadIdleTime");
        stats.ShouldContain("RecordVrXrPacingHandoffStall");

        packet.ShouldContain("VrXrPacingThreadIdleTimeMs");
        packet.ShouldContain("VrXrPacingHandoffStalls");
        sender.ShouldContain("VrXrPacingThreadIdleTimeMs");
        sender.ShouldContain("VrXrPacingHandoffStalls");
        editorSource.ShouldContain("VrXrPacingThreadIdleTimeMs");
        editorSource.ShouldContain("VrXrPacingHandoffStalls");
        panel.ShouldContain("Pacing thread idle");
    }

    [Test]
    public void PacingThread_UsesEventPingPongAndShutsDownCleanly()
    {
        string pacing = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Pacing.cs");
        string frameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string xrCalls = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs");
        string runtimeStateMachine = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.RuntimeStateMachine.cs");

        // Pacing thread exists with the expected name and ping-pong primitives.
        pacing.ShouldContain("XR Pacing");
        pacing.ShouldContain("EnsureOpenXrPacingThreadStarted");
        pacing.ShouldContain("StopOpenXrPacingThread");
        pacing.ShouldContain("SignalPacingThreadFrameSubmitted");
        pacing.ShouldContain("_openXrPacingWakeEvent.Wait()");
        pacing.ShouldContain("_openXrPacingWakeEvent.Reset()");
        pacing.ShouldContain("PrepareNextFrameForPacingOwner()");
        pacing.ShouldContain("MarkOpenXrPacingThread");
        xrCalls.ShouldContain("TryBeginOpenXrCollectVisiblePrepThread");
        xrCalls.ShouldContain("_openXrCollectVisiblePrepThreadId");

        // Render thread signals after every successful EndFrame and on aborted prep.
        int submitSignals = CountOccurrences(frameLifecycle, "SignalPacingThreadFrameSubmitted()");
        submitSignals.ShouldBeGreaterThanOrEqualTo(4);
        frameLifecycle.ShouldContain("RecordVrXrPacingHandoffStall");
        frameLifecycle.ShouldContain("OpenXrRenderPacingMode.InRenderCallback");
        frameLifecycle.ShouldContain("OpenXrRenderPacingMode.DedicatedThread");
        frameLifecycle.ShouldContain("OpenXrRenderPacingMode.CollectVisibleThread");
        frameLifecycle.ShouldContain("EnsureOpenXrPacingThreadStarted()");

        // Pacing thread shut down on every session-end / teardown path.
        xrCalls.ShouldContain("StopOpenXrPacingThread();");
        runtimeStateMachine.ShouldContain("StopOpenXrPacingThread();");

        // Render-thread assert was generalized to accept the pacing thread.
        xrCalls.ShouldContain("_openXrPacingThreadId");
    }

    [Test]
    public void TrackingLoss_WarningIsStreakGatedAndDoesNotAllocatePerFrame()
    {
        string xrCalls = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs");

        // The streak flag is read+written via Interlocked, and is reset on recovery via CacheLastValidViews.
        xrCalls.ShouldContain("_trackingLossStreakLogged");
        xrCalls.ShouldContain("_freezeFallbackStreakLogged");
        xrCalls.ShouldContain("Interlocked.Exchange(ref _trackingLossStreakLogged");

        string cacheLastValid = SliceMethod(xrCalls, "private void CacheLastValidViews", "private bool TryRestoreLastValidViews");
        cacheLastValid.ShouldContain("_trackingLossStreakLogged");
        cacheLastValid.ShouldContain("_freezeFallbackStreakLogged");

        // The formatted warning must not run unconditionally inside HandleLocatedViewState.
        string handle = SliceMethod(xrCalls, "private bool HandleLocatedViewState", "private void CacheLastValidViews");
        handle.ShouldContain("_trackingLossStreakLogged");
    }

    [Test]
    public void FrustumExpansion_RecordsOnlyForPaddedFrustumPolicy()
    {
        string openGl = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.SceneViews.cs");

        string cameraUpdate = SliceMethod(
            openGl,
            "private float UpdateOpenXrEyeCameraFromView",
            "private void ApplyOpenXrEyePoseForRenderThread");

        // PaddedFrustum is the only branch that returns a non-zero padding.
        cameraUpdate.ShouldContain("OpenXrCollectVisiblePosePolicy.PaddedFrustum");
        cameraUpdate.ShouldContain("OpenXrCollectFrustumPaddingDegrees");
    }

    [Test]
    public void VulkanOpenXr_DirectEyeSwapchainsUsePerEyeDepthTargets()
    {
        string backend = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrBackend.cs");
        string resources = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrOutputResourceService.cs");
        string frameLoop = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.OpenXR.ResourcesPressure.cs");

        backend.ShouldContain("VulkanOpenXrDepthTarget[] CachedDepthTargets = new VulkanOpenXrDepthTarget[EyeResourcePlannerStateCount];");
        backend.ShouldContain("Extent2D[] CachedDepthExtents = new Extent2D[EyeResourcePlannerStateCount];");
        resources.ShouldContain("ref VulkanOpenXrDepthTarget cached = ref backend.CachedDepthTargets[targetIndex];");
        resources.ShouldContain("ref Extent2D cachedExtent = ref backend.CachedDepthExtents[targetIndex];");
        frameLoop.ShouldContain("ResolveOpenXrEyeUploadPublicationBufferIndex(openXrViewIndex)");
        frameLoop.ShouldContain(".GetOrCreateDepthTarget(targetIndex, extent);");

        resources.ShouldContain("for (int index = 0; index < _backend.CachedDepthTargets.Length; index++)");
        resources.ShouldContain("RetireDepthTarget(_backend.CachedDepthTargets[index]);");
        resources.ShouldContain("_backend.CachedDepthExtents[index] = default;");
        resources.ShouldNotContain("VulkanOpenXrDepthTarget _cachedDepthTarget;");
    }

    [Test]
    public void VulkanOpenXr_RetiredResourceDrainCleansCompletedSlotsIncludingImages()
    {
        string vulkanOpenXr = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "DrainRetiredResourcesFromCompletedSubmittedFrameSlots");

        vulkanOpenXr.ShouldContain("DrainRetiredResourcesFromCompletedSubmittedFrameSlots");
        vulkanOpenXr.ShouldNotContain("DrainRetiredResourcesIfSubmittedFrameSlotsCompleted");
        vulkanOpenXr.ShouldNotContain("ForceFlushCompletedNonImageRetiredResources();");
        CountOccurrences(vulkanOpenXr, "DrainRetiredResourcesFromCompletedSubmittedFrameSlots();")
            .ShouldBeGreaterThanOrEqualTo(8);

        string resourcePressure = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.OpenXR.ResourcesPressure.cs");
        string drainMethod = SliceMethod(
            resourcePressure,
            "private void DrainRetiredResourcesFromCompletedSubmittedFrameSlots",
            "private bool TryPrepareOpenXrFrameDataSlot");

        drainMethod.ShouldContain("using VulkanDesktopFrameRetirementScope retirement =");
        drainMethod.ShouldContain("new(_commandRuntime, RetirementGate);");
        drainMethod.ShouldContain("int frameSlotCount = Math.Min(");
        drainMethod.ShouldContain("timelineValues.Length");
        drainMethod.ShouldContain("FrameSlotCount");
        drainMethod.ShouldContain("DesktopFrameActivitySnapshot desktopActivity =");
        drainMethod.ShouldContain("CaptureDesktopFrameActivity();");
        drainMethod.ShouldContain("desktopActivity.IsActive &&");
        drainMethod.ShouldContain("i == desktopActivity.FrameSlot");
        drainMethod.ShouldContain("desktopActivity.FrameNumber");
        drainMethod.ShouldContain("skipped retired-resource drain for active desktop frame slot");
        drainMethod.ShouldNotContain("_windowRenderCallbackInProgress");
        drainMethod.ShouldNotContain("_desktopFrameSlot");
        drainMethod.ShouldContain("const int retirementBudgetPerType = 32;");
        drainMethod.ShouldContain("DrainRetiredPipelines(i, retirementBudgetPerType);");
        drainMethod.ShouldContain("DrainRetiredBuffers(i, retirementBudgetPerType);");
        drainMethod.ShouldContain("DrainRetiredFramebuffers(i, retirementBudgetPerType);");
        drainMethod.ShouldContain("ResourceRuntime.DrainRetiredImages(");
        drainMethod.ShouldContain("retirementBudgetPerType);");
        drainMethod.ShouldNotContain("deferred completed-slot retired-resource drain because desktop frame");
        drainMethod.ShouldNotContain("int savedFrameSlot = currentFrame;");
        drainMethod.ShouldNotContain("currentFrame = i;");

        string pendingSlotBranch = SliceMethod(
            drainMethod,
            "if (value != 0 &&",
            "drainableSlots[i] = true;");

        pendingSlotBranch.ShouldContain("continue;");
        pendingSlotBranch.ShouldNotContain("return;");
    }

    private static int CountOccurrences(string source, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);

        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);

        return source[start..end];
    }

    private static string ReadVulkanDesktopFrameLoopSources()
        => string.Concat(
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Preflight.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Preflight.Policy.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.SwapchainPolicy.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.FrameSlots.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Acquire.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Recording.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Recovery.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Submission.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Presentation.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Telemetry.cs"));

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);
}
