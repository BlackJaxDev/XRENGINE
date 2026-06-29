using NUnit.Framework;
using Shouldly;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OpenXrTimingPipelineContractTests
{
    [Test]
    public void FrameTiming_UsesDedicatedPacingThreadByDefault()
    {
        string frameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string runtimeDefaults = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServiceDefaults.cs");
        string runtimeSettings = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderSettings.cs");
        string engineSettings = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs");
        string editorProgram = ReadWorkspaceFile("XREngine.Editor/Program.cs");
        string environmentVariables = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string vulkanOpenXr = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs");
        string vrState = ReadWorkspaceFile("XRENGINE/Engine/Engine.VRState.cs");

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
        vulkanOpenXr.ShouldContain("Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanMirrorFbo)");
        vulkanOpenXr.ShouldContain("\"1\"");
        vulkanOpenXr.ShouldContain("leave it unset for direct swapchain rendering");
        vulkanOpenXr.ShouldContain("ShouldPrewarmVulkanEyeResources");
        vulkanOpenXr.ShouldContain("MarkVulkanEyeResourceWarmupComplete");
        editorProgram.ShouldContain("ApplyOpenXrRenderPacingOverride");
        editorProgram.ShouldContain("XREngineEnvironmentVariables.OpenXrRenderPacingMode");
        editorProgram.ShouldContain("IsVulkanOpenXrUnitTestingLaunch");

        vrState.ShouldContain("PostRenderViewportsCallback += PostRender");
        vrState.ShouldContain("OpenXRApi?.EnginePostRenderTick()");
    }

    [Test]
    public void VulkanOpenXr_EyeSubmitRecordsBothEyesBeforeOneFenceWait()
    {
        string frameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string vulkanOpenXrApi = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs");
        string vulkanRendererOpenXr = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string vulkanCommandBufferState = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferState.cs");
        string vulkanCommandChainLowering = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandChainLowering.cs");
        string vkDataBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        string vulkanComputeDescriptors = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.ComputeDescriptors.cs");
        string vulkanDynamicUniformRingBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanDynamicUniformRingBuffer.cs");
        string vulkanFrameLoop = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string vulkanInitialization = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string vulkanSwapchain = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");
        string unitTestUi = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs");
        string defaultPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs");
        string defaultPipeline2 = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs");

        frameLifecycle.ShouldContain("StartProfileScope(\"OpenXR.RenderFrame.TryRenderVulkanEyesBatch\")");
        frameLifecycle.ShouldContain("TryRenderVulkanEyesBatch(projectionViews, out vulkanBatchHandled)");

        vulkanOpenXrApi.ShouldContain("OpenXrVulkanSerialEyeSubmit");
        vulkanOpenXrApi.ShouldContain("AcquireAndWaitOpenXrEyeImage(0");
        vulkanOpenXrApi.ShouldContain("AcquireAndWaitOpenXrEyeImage(1");
        vulkanOpenXrApi.ShouldContain("TryRenderVulkanEyeBatchToSwapchains");
        vulkanOpenXrApi.ShouldContain("EnsureVulkanEyeMirrorTargets(renderer, width, height)");
        vulkanOpenXrApi.ShouldContain("OpenXrEyeMirrorRenderRequest");
        vulkanOpenXrApi.ShouldContain("renderer.TryRenderAndPublishOpenXrEyeMirrorFrameBuffers(");
        vulkanOpenXrApi.ShouldContain("renderer.TryRenderOpenXrEyeSwapchains(leftRequest, rightRequest)");
        vulkanOpenXrApi.ShouldContain("ReleaseOpenXrEyeImageIfAcquired(1");
        vulkanOpenXrApi.ShouldContain("ReleaseOpenXrEyeImageIfAcquired(0");
        vulkanOpenXrApi.ShouldContain("previewFlippedY=False");
        vulkanOpenXrApi.ShouldContain("ShouldCopyDirectVulkanEyeSwapchainPreview");
        vulkanOpenXrApi.ShouldContain("bool copiedPreview = shouldCopyPreview &&");
        vulkanOpenXrApi.ShouldContain("VulkanCaptureEyeOutputs");
        vulkanOpenXrApi.ShouldContain("RuntimeRenderingHostServices.Current.VrCopyEyePreviewTextures");
        vulkanOpenXrApi.ShouldContain("RuntimeRenderingHostServices.Current.VrMirrorComposeFromEyeTextures");

        vulkanRendererOpenXr.ShouldContain("OpenXrEyeMirrorRenderRequest");
        vulkanRendererOpenXr.ShouldContain("TryRenderOpenXrEyeMirrorFrameBuffers");
        vulkanRendererOpenXr.ShouldContain("TryRenderAndPublishOpenXrEyeMirrorFrameBuffers");
        vulkanRendererOpenXr.ShouldContain("TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer");
        vulkanRendererOpenXr.ShouldContain("TryReuseOpenXrMirrorPrimaryCommandBuffer");
        vulkanRendererOpenXr.ShouldContain("RecordOpenXrMirrorPrimaryCommandBuffer");
        vulkanRendererOpenXr.ShouldContain("BuildOpenXrMirrorPrimaryCommandBufferCacheKey");
        vulkanRendererOpenXr.ShouldContain("OpenXR.Vulkan.MirrorPrimary.RefreshFrameData");
        vulkanRendererOpenXr.ShouldContain("OwnedByOpenXrPrimaryCache: true");
        SliceMethod(
            vulkanRendererOpenXr,
            "private bool TryReuseOpenXrMirrorPrimaryCommandBuffer",
            "private CommandBuffer RecordOpenXrMirrorPrimaryCommandBuffer")
            .ShouldContain("if (!OpenXrVulkanPrimaryReuseEnabled)");
        vulkanRendererOpenXr.ShouldContain("TryRenderOpenXrEyeSwapchains");
        vulkanRendererOpenXr.ShouldContain("OpenXrVulkanPrimaryReuseEnabled");
        vulkanCommandBufferState.ShouldContain("OpenXrVulkanPrimaryReuseEnabled");
        vulkanCommandBufferState.ShouldContain("XREngineEnvironmentVariables.OpenXrVulkanPrimaryReuse), \"1\"");
        vulkanRendererOpenXr.ShouldContain("OpenXrEyePreviewCopyRequest");
        vulkanRendererOpenXr.ShouldContain("RecordOpenXrEyeSwapchainPreviewCopy(scope.CommandBuffer, in plan)");
        vulkanRendererOpenXr.ShouldContain("TryRecordOpenXrEyeSwapchainCommandBuffer(firstEye");
        vulkanRendererOpenXr.ShouldContain("TryRecordOpenXrEyeSwapchainCommandBuffer(secondEye");
        vulkanRendererOpenXr.ShouldContain("CaptureFrameOpsExcludingTextureUploads(request.EmitFrameOps, out _)");
        vulkanRendererOpenXr.ShouldContain("CaptureFrameOpsExcludingTextureUploads(emitFrameOps, out _)");
        vulkanRendererOpenXr.ShouldContain("TryReuseOpenXrPrimaryCommandBuffer");
        vulkanRendererOpenXr.ShouldContain("OpenXrExternalSwapchainTargetImageIndex");
        vulkanRendererOpenXr.ShouldContain("imageIndex: OpenXrExternalSwapchainTargetImageIndex");
        vulkanRendererOpenXr.ShouldContain("frameDataImageIndexOverride: recordImageIndex");
        vulkanRendererOpenXr.ShouldContain("ResetDynamicUniformRingBuffer(recordImageIndex)");
        vulkanRendererOpenXr.ShouldContain("private void WaitForOpenXrFrameDataSlot");
        vulkanRendererOpenXr.ShouldContain("ResolveOpenXrFrameDataSlotCount");
        vulkanRendererOpenXr.ShouldContain("ResolveOpenXrDesktopFrameDataSlotCount");
        vulkanRendererOpenXr.ShouldContain("desktopFrameDataSlotCount + eyeIndex");
        vulkanRendererOpenXr.ShouldContain("private void ReserveOpenXrFrameDataSlotsIfRequired");
        vulkanRendererOpenXr.ShouldContain("RuntimeEngine.GameSettings?.VRRuntime == EVRRuntime.OpenXR");
        vulkanRendererOpenXr.ShouldContain("private void MarkOpenXrPrimaryCommandBufferVariantsDirty");
        vulkanRendererOpenXr.ShouldContain("EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);");
        vulkanRendererOpenXr.ShouldContain("EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);");
        vulkanRendererOpenXr.ShouldContain("EnsureCommandBufferFrameDataSlotCapacity(frameDataSlotCount);");
        vulkanInitialization.ShouldContain("ReserveOpenXrFrameDataSlotsIfRequired(\"initialization\");");
        vulkanSwapchain.ShouldContain("ReserveOpenXrFrameDataSlotsIfRequired(\"swapchain recreation\");");
        vulkanRendererOpenXr.ShouldContain("WaitForTimelineValue(_graphicsTimelineSemaphore, value);");
        vulkanRendererOpenXr.ShouldContain("WaitForOpenXrFrameDataSlot(recordImageIndex, \"eye swapchain render\");");
        vulkanRendererOpenXr.ShouldContain("WaitForOpenXrFrameDataSlot(recordImageIndex, \"eye mirror render\");");
        vulkanRendererOpenXr.ShouldContain("ComputeOpenXrPrimaryCommandBufferGroupHandleSignature");
        vulkanRendererOpenXr.ShouldContain("TryComputeOpenXrPrimaryCommandBufferGroupSignature");
        vulkanRendererOpenXr.ShouldContain("OpenXrPrimaryCommandChainScheduleIsReusable");
        vulkanRendererOpenXr.ShouldContain("chain.State is not (CommandChainState.Reused or CommandChainState.FrameDataRefreshed)");
        vulkanRendererOpenXr.ShouldContain("chain.FrameDataRefreshTouchedDescriptors");
        string openXrPrimarySignature = SliceMethod(
            vulkanRendererOpenXr,
            "private static ulong ComputeOpenXrPrimaryCommandBufferGroupHandleSignature",
            "private void FreeOpenXrRecordedEyeCommandBuffer");
        openXrPrimarySignature.ShouldContain("hash.Add(chain.SecondaryCommandBufferGeneration);");
        openXrPrimarySignature.ShouldNotContain("DescriptorGeneration");
        openXrPrimarySignature.ShouldNotContain("DescriptorSetSignature");
        openXrPrimarySignature.ShouldNotContain("FrameDataSignature");
        openXrPrimarySignature.ShouldNotContain("DirtyReason");
        string directPrimaryReuse = SliceMethod(
            vulkanRendererOpenXr,
            "private bool TryReuseOpenXrPrimaryCommandBuffer",
            "private CommandBuffer RecordOpenXrPrimaryCommandBuffer");
        directPrimaryReuse.ShouldContain("bool requiresExactFrameOps = !usingCommandChains || HasTextureUploadFrameOps(ops);");
        directPrimaryReuse.ShouldContain("(requiresExactFrameOps && variant.FrameOpsSignature != frameOpsSignature)");
        directPrimaryReuse.ShouldContain("(!usingCommandChains && variant.PlannerRevision != plannerRevision)");
        string mirrorPrimaryReuse = SliceMethod(
            vulkanRendererOpenXr,
            "private bool TryReuseOpenXrMirrorPrimaryCommandBuffer",
            "private CommandBuffer RecordOpenXrMirrorPrimaryCommandBuffer");
        mirrorPrimaryReuse.ShouldContain("bool requiresExactFrameOps = !usingCommandChains || HasTextureUploadFrameOps(ops);");
        mirrorPrimaryReuse.ShouldContain("(requiresExactFrameOps && variant.FrameOpsSignature != frameOpsSignature)");
        mirrorPrimaryReuse.ShouldContain("(!usingCommandChains && variant.PlannerRevision != plannerRevision)");
        vulkanRendererOpenXr.ShouldContain("SubmitAndWaitOpenXrCommandBuffers(");
        vulkanRendererOpenXr.ShouldContain("commandBuffers[0] = firstCommandBuffer");
        vulkanRendererOpenXr.ShouldContain("commandBuffers[1] = secondCommandBuffer");
        vulkanRendererOpenXr.ShouldContain("commandBuffers[2] = publishCommandBuffer");
        vulkanRendererOpenXr.ShouldContain("fenceWaitMs");
        vulkanCommandBufferState.ShouldContain("private void EnsureCommandBufferFrameDataSlotCapacity");
        vulkanCommandBufferState.ShouldContain("private bool EnsureDescriptorFrameSlotFrameCountFloor");
        vulkanCommandBufferState.ShouldContain("Interlocked.CompareExchange(ref _descriptorFrameSlotFrameCountOverride");
        vulkanCommandBufferState.ShouldContain("MarkCommandBuffersDirty();");
        vulkanCommandBufferState.ShouldContain("MarkOpenXrPrimaryCommandBufferVariantsDirty();");
        vulkanCommandBufferState.ShouldContain("Array.Resize(ref _computeTransientResources, frameDataSlotCount);");
        vulkanCommandBufferState.ShouldContain("Array.Resize(ref _deferredSecondaryCommandBuffers, frameDataSlotCount);");
        vulkanCommandBufferState.ShouldContain("EnsureDynamicUniformRingBufferCapacity(frameDataSlotCount);");
        vulkanCommandBufferState.ShouldContain("private readonly Dictionary<ulong, OwnedCommandChainSecondaryPool> _ownedCommandChainSecondaryPools = new();");
        vulkanCommandBufferState.ShouldContain("DestroyTrackedCommandChainSecondaryPools();");
        vulkanCommandBufferState.ShouldContain("DiscardDeferredSecondaryCommandBuffersForPool(pool);");
        vulkanCommandBufferState.ShouldContain("UntrackOwnedCommandChainSecondaryCommandBuffer(entry.Pool, entry.CommandBuffer);");
        string trackedSecondaryPoolTeardown = SliceMethod(
            vulkanCommandBufferState,
            "private void DestroyTrackedCommandChainSecondaryPools",
            "private void DiscardDeferredSecondaryCommandBuffersForPool");
        trackedSecondaryPoolTeardown.ShouldContain("Api!.DestroyCommandPool(device, pool, null);");
        trackedSecondaryPoolTeardown.ShouldNotContain("!_deviceLost && pool.Handle != 0");
        string commandChainCache = SliceMethod(
            vulkanCommandChainLowering,
            "private Dictionary<CommandChainKey, CommandChain> GetCommandChainCache",
            "private int InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease");
        commandChainCache.ShouldContain("DestroyCommandChainCaches();");
        commandChainCache.ShouldContain("MarkOpenXrPrimaryCommandBufferVariantsDirty();");
        vulkanCommandChainLowering.ShouldContain("TrackOwnedCommandChainSecondaryCommandBuffer(pool, secondary);");
        vulkanCommandChainLowering.ShouldContain("TrackOwnedCommandChainSecondaryCommandBuffer(pool, replacement);");
        vulkanCommandChainLowering.ShouldContain("UntrackOwnedCommandChainSecondaryPool(pool);");
        string commandChainSecondaryTeardown = SliceMethod(
            vulkanCommandChainLowering,
            "private void DestroyCommandChainSecondaryCommandBuffer",
            "internal static CommandChainDirtyReason EvaluateCommandChainDirtyReason");
        commandChainSecondaryTeardown.ShouldContain("Api!.DestroyCommandPool(device, pool, null);");
        commandChainSecondaryTeardown.ShouldNotContain("ownsPool && pool.Handle != 0 && !_deviceLost");
        string commandChainWorkerPoolTeardown = SliceMethod(
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandChainWorkers.cs"),
            "private void DestroyWorkerCommandPool",
            "}");
        commandChainWorkerPoolTeardown.ShouldContain("Api!.DestroyCommandPool(device, pool, null);");
        commandChainWorkerPoolTeardown.ShouldNotContain("!_deviceLost");
        vkDataBuffer.ShouldContain("Renderer.MarkCommandBuffersDirty(\"VkDataBufferRecreated\");");
        vkDataBuffer.ShouldContain("Renderer.MarkOpenXrPrimaryCommandBufferVariantsDirty();");
        vulkanFrameLoop.ShouldContain("Command buffer for image {0} was dirtied after recording and before submit");
        vulkanFrameLoop.ShouldContain("RecreateSwapchainImmediately(\"Command buffer dirtied before submit - recovering timeline/present state\")");
        vulkanComputeDescriptors.ShouldContain("private void EnsureComputeDescriptorCacheCapacity");
        vulkanComputeDescriptors.ShouldContain("Array.Resize(ref _computeDescriptorCaches, imageCount);");
        vulkanDynamicUniformRingBuffer.ShouldContain("private void EnsureDynamicUniformRingBufferCapacity");
        vulkanDynamicUniformRingBuffer.ShouldContain("Array.Resize(ref _dynamicUniformRingBuffers, count);");

        defaultPipeline.ShouldContain("State.WindowViewport is not null\n            && RuntimeEngine.Rendering.State.RenderingTargetOutputFBO is null");
        defaultPipeline2.ShouldContain("State.WindowViewport is not null\n            && RuntimeEngine.Rendering.State.RenderingTargetOutputFBO is null");

        unitTestUi.ShouldContain("ShouldFlipOpenXrVulkanStereoPreviewUv");
        unitTestUi.ShouldContain("Engine.VRState.IsOpenXRActive");
        unitTestUi.ShouldContain("RuntimeRenderingHostServices.Current.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan");
        unitTestUi.ShouldContain("target.FlipVerticalUVCoord = flipVerticalUVCoord;");

        string directEyeRecord = SliceMethod(
            vulkanRendererOpenXr,
            "private bool TryRecordOpenXrEyeSwapchainCommandBuffer",
            "private void EnsureOpenXrSingleSwapchainSlotCapacity");
        directEyeRecord.IndexOf("WaitForOpenXrFrameDataSlot(recordImageIndex, \"eye swapchain render\");", StringComparison.Ordinal)
            .ShouldBeLessThan(directEyeRecord.IndexOf("ResetDynamicUniformRingBuffer(recordImageIndex);", StringComparison.Ordinal));

        string mirrorEyeRecord = SliceMethod(
            vulkanRendererOpenXr,
            "private bool TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer",
            "private bool TryReuseOpenXrMirrorPrimaryCommandBuffer");
        mirrorEyeRecord.IndexOf("WaitForOpenXrFrameDataSlot(recordImageIndex, \"eye mirror render\");", StringComparison.Ordinal)
            .ShouldBeLessThan(mirrorEyeRecord.IndexOf("CaptureFrameOpsExcludingTextureUploads(request.EmitFrameOps, out _);", StringComparison.Ordinal));

        string mirrorPrimaryRecord = SliceMethod(
            vulkanRendererOpenXr,
            "private CommandBuffer RecordOpenXrMirrorPrimaryCommandBuffer",
            "private static int ResolveOpenXrFrameDataSlotCount");
        mirrorPrimaryRecord.ShouldContain("OpenXrExternalSwapchainTargetImageIndex");
        mirrorPrimaryRecord.ShouldContain("frameDataImageIndexOverride: recordImageIndex");
    }

    [Test]
    public void VulkanImageViews_AreTrackedAndSweptBeforeLogicalDeviceDestroy()
    {
        string imageViewLifetime = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Textures/VulkanRenderer.ImageViewLifetime.cs");
        string pipelineLayoutLifetime = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Pipelines/VulkanRenderer.PipelineLayoutLifetime.cs");
        string resourceRetirement = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");
        string initialization = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string openXr = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string imageBackedTexture = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string textureView = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");
        string renderProgram = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string renderProgramPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgramPipeline.cs");
        string imgui = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");
        string renderBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkRenderBuffer.cs");
        string swapchainViews = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Textures/VulkanRenderer.SwapchainImageViews.cs");

        imageViewLifetime.ShouldContain("private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> _liveImageViewHandles = new();");
        imageViewLifetime.ShouldContain("internal void TrackLiveImageView(ImageView imageView, string owner = \"unknown\")");
        imageViewLifetime.ShouldContain("internal bool TryBeginDestroyImageView(ImageView imageView, string owner)");
        imageViewLifetime.ShouldContain("private void DestroyRemainingTrackedImageViews()");

        pipelineLayoutLifetime.ShouldContain("private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> _livePipelineLayoutHandles = new();");
        pipelineLayoutLifetime.ShouldContain("internal void TrackLivePipelineLayout(PipelineLayout pipelineLayout, string owner = \"unknown\")");
        pipelineLayoutLifetime.ShouldContain("internal bool TryBeginDestroyPipelineLayout(PipelineLayout pipelineLayout, string owner)");
        pipelineLayoutLifetime.ShouldContain("private void DestroyRemainingTrackedPipelineLayouts()");

        resourceRetirement.ShouldContain("TryBeginDestroyImageView(r.PrimaryView, \"DrainRetiredImages.PrimaryView\")");
        resourceRetirement.ShouldContain("TryBeginDestroyImageView(v, \"DrainRetiredImages.AttachmentView\")");
        initialization.ShouldContain("ForceFlushAllRetiredResources();\n            DestroyRemainingTrackedImageViews();\n            DestroyRemainingTrackedPipelineLayouts();\n            DestroyRemainingTrackedBufferAllocations();");
        initialization.ShouldContain("ForceFlushAllRetiredResources();\n            DestroyRemainingTrackedImageViews();\n            DestroyRemainingTrackedPipelineLayouts();\n            DestroySharedGraphicsPipelineLibraries();");

        openXr.ShouldContain("TrackLiveImageView(imageView, \"OpenXR.SwapchainImageView\");");
        openXr.ShouldContain("TrackLiveImageView(depthView, \"OpenXR.DepthTarget\");");
        imageBackedTexture.ShouldContain("Renderer.TrackLiveImageView(created, \"VkImageBackedTexture.View\");");
        textureView.ShouldContain("Renderer.TrackLiveImageView(_view, \"VkTextureView.View\");");
        textureView.ShouldContain("Renderer.TrackLiveImageView(_depthOnlyView, \"VkTextureView.DepthOnlyDescriptor\");");
        textureView.ShouldContain("private readonly object _viewLifetimeLock = new();");
        renderProgram.ShouldContain("Renderer.TrackLivePipelineLayout(_pipelineLayout, \"VkRenderProgram.PipelineLayout\");");
        renderProgram.ShouldContain("Renderer.TryBeginDestroyPipelineLayout(pipelineLayout, owner)");
        renderProgramPipeline.ShouldContain("Renderer.TrackLivePipelineLayout(_pipelineLayout, \"VkRenderProgramPipeline.PipelineLayout\");");
        renderProgramPipeline.ShouldContain("Renderer.TryBeginDestroyPipelineLayout(pipelineLayout, owner)");
        imgui.ShouldContain("TrackLivePipelineLayout(_imguiPipelineLayout, \"ImGui.PipelineLayout\");");
        imgui.ShouldContain("TryBeginDestroyPipelineLayout(pipelineLayout, \"ImGui.DestroyPipelineResources\")");
        renderBuffer.ShouldContain("Renderer.TrackLiveImageView(_view, \"VkRenderBuffer.View\");");
        swapchainViews.ShouldContain("TrackLiveImageView(swapChainImageViews[i], \"Swapchain.Color\");");

        string textureViewCreate = SliceMethod(
            textureView,
            "protected override uint CreateObjectInternal()",
            "protected override void DeleteObjectInternal()");
        textureViewCreate.ShouldContain("if (Renderer.IsDeviceLost)\n                    return InvalidBindingId;");

        string textureViewRefresh = SliceMethod(
            textureView,
            "private void RefreshFromViewedTextureIfStale()",
            "private ImageSubresourceRange ResolveViewSubresourceRange");
        textureViewRefresh.ShouldContain("if (Renderer.IsDeviceLost)\n                    return;");
        textureViewRefresh.ShouldContain("lock (_viewLifetimeLock)");
    }

    [Test]
    public void VulkanCommandChains_DoNotBroadDirtyForRepeatedSkippedMeshPreparation()
    {
        string dirtyReasons = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferDirtyReasons.cs");
        string meshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");

        dirtyReasons.ShouldContain("if (CommandChainsEnabledForCurrentRecording)\n            return;");

        string onRenderRequested = SliceMethod(
            meshRenderer,
            "private void OnRenderRequested",
            "RenderingParameters? matOpts");

        onRenderRequested.ShouldContain("Renderer.MarkCommandBuffersDirtyForLegacyMeshState();");
        onRenderRequested.ShouldNotContain("Renderer.MarkCommandBuffersDirty();");
    }

    [Test]
    public void VulkanTextureUploads_AutoGeneratedMipmapsUploadOnlyBaseLevelAndValidateCopyBounds()
    {
        string[] textureUploadFiles =
        [
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture1D.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture1DArray.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture2D.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture2DArray.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture3D.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureCube.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureCubeArray.cs",
        ];

        foreach (string file in textureUploadFiles)
        {
            string source = ReadWorkspaceFile(file);

            source.ShouldContain("uint levelCount = Data.AutoGenerateMipmaps");
            source.ShouldContain("? 1u");
            source.ShouldContain(": Math.Min((uint)mipmaps.Length, ResolvedMipLevels);");
            source.ShouldContain("RecreateImageForFullTextureDataUpload(");
        }

        string imageBackedTexture = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");

        imageBackedTexture.ShouldContain("if (!ValidateCopyBufferToImageRegion(mipLevel, baseArrayLayer, layerCount, extent))");
        imageBackedTexture.ShouldContain("layerCount > arrayLayerCount - baseArrayLayer");
        imageBackedTexture.ShouldContain("extent.Width == 0 || extent.Height == 0 || extent.Depth == 0");
        imageBackedTexture.ShouldContain("private static Extent3D ResolveMipExtent");
        imageBackedTexture.ShouldContain("protected void RecreateImageForFullTextureDataUpload(string reason)");
        imageBackedTexture.ShouldContain("WaitForInFlightWorkBeforeImportedTextureReplacement(reason);");
    }

    [Test]
    public void VulkanCommandChains_DescriptorReuseTracksConcreteImageIdentityAndMutableFrameSources()
    {
        string descriptors = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string canReuse = SliceMethod(
            descriptors,
            "internal bool CanReuseRecordedDescriptorSets(\n\t\t\tXRMaterial material",
            "private string BuildDescriptorAllocationMissReason");

        canReuse.ShouldContain("ulong schemaFingerprint = ComputeDescriptorSchemaFingerprint(bindings, setCount);");
        canReuse.ShouldContain("ulong resourceFingerprint = ComputeDescriptorResourceFingerprint(material, frameCount, bindings);");
        descriptors.ShouldContain("allocation.ResourceFingerprint != resourceFingerprint");
        canReuse.ShouldContain("schemaFingerprint,\n\t\t\t\t\tresourceFingerprint,");

        string capturedReuse = SliceMethod(
            descriptors,
            "private bool TryActivateReusableDescriptorSetsForCapturedResources",
            "private bool TryActivateReusableDescriptorSetsFast");

        capturedReuse.ShouldContain("allocation.SchemaFingerprint != schemaFingerprint");
        capturedReuse.ShouldContain("allocation.ResourceFingerprint != resourceFingerprint");
        capturedReuse.ShouldContain("ComputeDescriptorResourceFingerprintDetails(material, Renderer.DescriptorFrameSlotFrameCount, currentBindings)");

        string frameSourceFingerprint = SliceMethod(
            descriptors,
            "private void AddFrameSourceSamplerDescriptorResourceFingerprint",
            "private ulong ComputeReferencedProgramBufferResourceFingerprint");

        frameSourceFingerprint.ShouldContain("hash.Add(FrameSourceMutableDescriptorSignature);");
        frameSourceFingerprint.ShouldNotContain("texture?.GetHashCode()");
        frameSourceFingerprint.ShouldNotContain("DescriptorViewType");
        frameSourceFingerprint.ShouldNotContain("DescriptorGeneration");
        frameSourceFingerprint.ShouldNotContain("DescriptorImage.Handle");
        frameSourceFingerprint.ShouldNotContain("ResourceAllocatorIdentity");
        descriptors.ShouldContain("private bool TryRefreshFrameSourceDescriptorSetsForDraw");
        descriptors.ShouldContain("Api!.UpdateDescriptorSets(Device, 1, &write, 0, null);");
        descriptors.ShouldContain("FrameSourceDescriptorWriteMatches(allocation, descriptorSlotIndex, binding, descriptorCount, resolvedImageInfos)");
        descriptors.ShouldContain("RecordFrameSourceDescriptorWriteSignature(allocation, descriptorSlotIndex, binding, descriptorCount, resolvedImageInfos);");
        descriptors.ShouldContain("ComputeDescriptorImageInfoSignature(binding.DescriptorType, imageInfos)");

        string meshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string snapshotHashing = SliceMethod(
            meshRenderer,
            "private static ulong HashSamplerUnitBindings",
            "private static ulong HashBufferBindings");

        meshRenderer.ShouldContain("HashSamplerUnitBindings(snapshot.Samplers, snapshot.SamplerNamesByUnit, includeMutableFrameSourceDescriptors)");
        meshRenderer.ShouldContain("private static ulong ComputeCommandBufferDataBufferSignature(VkDataBuffer? buffer)");
        meshRenderer.ShouldContain("buffer.BufferHandle?.Handle ?? 0UL");
        meshRenderer.ShouldContain("buffer.UploadedByteCount");
        meshRenderer.ShouldContain("hash.Add(ComputeCommandBufferDataBufferSignature(indirect.IndirectBuffer));");
        meshRenderer.ShouldContain("hash.Add(ComputeCommandBufferDataBufferSignature(meshTaskDispatch.CountBuffer));");
        snapshotHashing.ShouldContain("samplerNamesByUnit.TryGetValue(pair.Key, out string? samplerName)");
        snapshotHashing.ShouldContain("IsFrameSourceSamplerName(samplerName)");
        snapshotHashing.ShouldContain("IsFrameSourceSamplerName(pair.Key)");
        snapshotHashing.ShouldContain("!includeMutableFrameSourceDescriptors");
        snapshotHashing.ShouldContain("AddFrameSourceTextureDescriptorSignature(ref item, pair.Value);");
        snapshotHashing.ShouldContain("hash.Add(FrameSourceMutableDescriptorSignature);");
        snapshotHashing.ShouldContain("AddTextureDescriptorSignature(ref item, pair.Value);");
        snapshotHashing.ShouldContain("AddTextureDescriptorSignature(ref item, binding.Texture);");
        snapshotHashing.ShouldContain("source.DescriptorGeneration");
        snapshotHashing.ShouldContain("source.DescriptorImage.Handle");
        snapshotHashing.ShouldContain("source.DescriptorView.Handle");

        string drawing = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        drawing.ShouldContain("TryRefreshFrameSourceDescriptorSetsForDraw(imageIndex, drawUniformSlot, material, draw.ProgramBindingSnapshot");
        drawing.ShouldContain("TryRefreshFrameSourceDescriptorSetsForDraw(frameIndex, drawUniformSlot, material, draw.ProgramBindingSnapshot");

        string frameOpSignatures = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.FrameOpSignatures.cs");
        frameOpSignatures.ShouldContain("hash.Add(ComputeTextureDescriptorSignature(pair.Value));");
        frameOpSignatures.ShouldContain("hash.Add(ComputeTextureDescriptorSignature(binding.Texture));");
        frameOpSignatures.ShouldContain("ComputeCommandBufferDataBufferSignature(indirect.IndirectBuffer)");
        frameOpSignatures.ShouldContain("indirectBuffer=0x{indirect.IndirectBuffer.BufferHandle?.Handle");

        string commandChainLowering = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandChainLowering.cs");
        commandChainLowering.ShouldContain("HashProgramBindingSnapshot(ref hash, snapshot, includeMutableFrameSourceDescriptors: true);");
        commandChainLowering.ShouldContain("ComputeCommandBufferDataBufferSignature(indirect.IndirectBuffer)");
        commandChainLowering.ShouldContain("ComputeCommandBufferDataBufferSignature(meshTask.CountBuffer)");

        string program = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string programSamplerFingerprint = SliceMethod(
            program,
            "private ulong ComputeSamplerResourceFingerprintItem",
            "private ulong ComputeBoundBufferResourceFingerprintItem");

        programSamplerFingerprint.ShouldContain("source.DescriptorGeneration");
        programSamplerFingerprint.ShouldContain("source.DescriptorImage.Handle");
        programSamplerFingerprint.ShouldContain("source.DescriptorView.Handle");
    }

    [Test]
    public void PoseThreading_UsesLockedCachesAndExplicitRecalcTiming()
    {
        string openGl = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.OpenGL.cs");
        string state = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs");
        string frameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string runtimeVrState = ReadWorkspaceFile("XREngine.Runtime.Core/Input/RuntimeVrStateServices.cs");
        string engineVrState = ReadWorkspaceFile("XRENGINE/Engine/Engine.VRState.cs");

        string collectCameraUpdate = SliceMethod(
            openGl,
            "private float UpdateOpenXrEyeCameraFromView",
            "private void ApplyOpenXrEyePoseForRenderThread");

        collectCameraUpdate.ShouldContain("lock (_openXrPoseLock)");
        collectCameraUpdate.ShouldContain("_openXrPredLeftEyeFov");
        collectCameraUpdate.ShouldContain("_openXrPredRightEyeFov");
        state.ShouldContain("TryGetEyeLocalPose(OpenXrPoseTiming.Predicted");
        state.ShouldContain("_openXrPredLeftEyeLocalPose");
        state.ShouldContain("_openXrPredRightEyeLocalPose");
        collectCameraUpdate.ShouldNotContain("_views[");

        frameLifecycle.ShouldContain("InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Predicted)");
        frameLifecycle.ShouldContain("InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Late)");
        runtimeVrState.ShouldContain("Action<RuntimeVrPoseTiming>?");
        engineVrState.ShouldNotContain("PoseTimingForRecalc");
    }

    [Test]
    public void TimingStats_AreRecordedAndSurfacedThroughProfiler()
    {
        string xrCalls = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs");
        string stats = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.Vr.cs");
        string packet = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string sender = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfilerSender.cs");
        string editorSource = ReadWorkspaceFile("XREngine.Editor/EngineProfilerDataSource.cs");
        string panel = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");

        xrCalls.ShouldContain("ConvertWin32PerformanceCounterToTime");
        xrCalls.ShouldContain("RecordDeadlineStatus");
        xrCalls.ShouldContain("RecordVrXrWaitFrameBlockTime");
        xrCalls.ShouldContain("RecordVrXrEndFrameSubmitTime");

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
    public void PoseAndInputPolicies_AreConfigurable()
    {
        string state = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs");
        string settings = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs");
        string input = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Input.cs");
        string xrCalls = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs");

        state.ShouldContain("OpenXrCollectVisiblePosePolicy");
        state.ShouldContain("RelocatePredicted");
        state.ShouldContain("PaddedFrustum");
        state.ShouldContain("OpenXrTrackingLossPolicy");
        state.ShouldContain("OpenXrActionSyncPolicy");

        settings.ShouldContain("OpenXrCollectVisibleFrustumPaddingDegrees");
        settings.ShouldContain("OpenXrTrackingLossPolicy");
        settings.ShouldContain("OpenXrActionSyncPolicy");

        input.ShouldContain("OpenXrActionSyncHandling == OpenXrActionSyncPolicy.PredictedAndLate");
        input.ShouldContain("_openXrActionsSyncedFrameNumber");
        input.ShouldContain("Result.ErrorPathUnsupported");
        input.ShouldContain("optional Vive tracker role paths are not supported");
        xrCalls.ShouldContain("ViewStateFlags.PositionValidBit");
        xrCalls.ShouldContain("RecordVrXrTrackingLossFrame");
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

        installer.ShouldContain("https://gitlab.freedesktop.org/monado/monado.git");
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

        runner.ShouldContain("xrEnumerateApiLayerProperties");
        runner.ShouldContain("xrEnumerateInstanceExtensionProperties");
        runner.ShouldContain("XR_KHR_opengl_enable");
        runner.ShouldContain(XREngineEnvironmentVariables.XrRuntimeJson);
        runner.ShouldContain("--smoke-frames");
        runner.ShouldContain(XREngineEnvironmentVariables.UnitTestVrMode);
        runner.ShouldContain("MonadoOpenXR");
        runner.ShouldContain("Build\\_AgentValidation");
        runner.ShouldContain("-FailOnOpenXrHotPathAllocations");

        tasks.ShouldContain("Start-Editor-UnitTesting-OpenXR-Monado-NoDebug");
        tasks.ShouldContain("Install-Monado");
        tasks.ShouldContain("Test-OpenXR-Monado-Smoke");
        tasks.ShouldContain("Test-OpenXR-SceneOnlyVR-Smoke");
    }

    [Test]
    public void OpenXrSmokeRun_UsesStableExitCodesAndSummaryContract()
    {
        string program = ReadWorkspaceFile("XREngine.Editor/Program.cs");
        string diagnostics = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.SmokeDiagnostics.cs");
        string xrCalls = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs");
        string frameLifecycle = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs");
        string runtimeStateMachine = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.RuntimeStateMachine.cs");
        string vulkan = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs");

        program.ShouldContain("ExitStartupFailure = 21");
        program.ShouldContain("ExitFrameTimeout = 22");
        program.ShouldContain("ExitSummaryFailure = 23");
        program.ShouldContain("ExitTeardownFailure = 24");
        program.ShouldContain("--openxr-smoke-summary");
        program.ShouldContain(nameof(XREngineEnvironmentVariables.OpenXrSmokeFrames));
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

        xrCalls.ShouldContain("RecordSmokeEndFrame");
        xrCalls.ShouldContain("RecordSmokeLocatedViews");
        xrCalls.ShouldContain("RecordSmokeSessionState");
        frameLifecycle.ShouldContain("RecordSmokeEyeAcquire");
        frameLifecycle.ShouldContain("RecordSmokeEyeWait");
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
    public void OpenXrRuntimeRecovery_RestartsHostServiceAndKeepsStrongestLossReason()
    {
        string program = ReadWorkspaceFile("XREngine.Editor/Program.cs");
        string settingsStore = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/UnitTestingWorldSettingsStore.cs");
        string hostServices = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServices.cs");
        string hostInterface = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderingHostServices.cs");
        string state = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs");
        string instance = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/Instance.cs");
        string runtimeStateMachine = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.RuntimeStateMachine.cs");
        string vulkanInstance = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Instance.cs");
        string vulkanSyncObjects = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SyncObjects.cs");

        program.ShouldContain("ConfigureOpenXrRuntimeServiceRecovery(settings)");
        program.ShouldContain("RuntimeRenderingHostServices.OpenXrRuntimeServiceEnsurer");
        program.ShouldContain("UnitTestingWorldSettingsStore.TryEnsureMonadoServiceForCurrentProcess");

        settingsStore.ShouldContain("TryEnsureMonadoServiceForCurrentProcess");
        settingsStore.ShouldContain("TryEnsureMonadoService(settings, reason)");
        settingsStore.ShouldContain("Reason={reason}");

        hostServices.ShouldContain("OpenXrRuntimeServiceEnsurer");
        hostServices.ShouldContain("TryEnsureOpenXrRuntimeService(string reason)");
        hostInterface.ShouldContain("bool TryEnsureOpenXrRuntimeService(string reason)");

        state.ShouldContain("_runtimeLossLock");
        runtimeStateMachine.ShouldContain("GetRuntimeLossReasonSeverity");
        runtimeStateMachine.ShouldContain("OpenXrRuntimeLossReason.InstanceLostError => 80");
        runtimeStateMachine.ShouldContain("OpenXrRuntimeLossReason.SessionLostError => 60");
        runtimeStateMachine.ShouldContain("TryEnsureOpenXrRuntimeService($\"OpenXR runtime loss: {lossReason}\")");

        instance.ShouldContain("InvalidateOpenXrVulkanEnable2BootstrapInstance(\"OpenXR runtime instance teardown\")");
        vulkanInstance.ShouldContain("internal bool InvalidateOpenXrVulkanEnable2BootstrapInstance(string reason)");
        vulkanInstance.ShouldContain("AbandonXrInstanceOnDispose(reason)");
        vulkanInstance.ShouldContain("UsesOpenXrVulkanEnable2Creation");
        vulkanInstance.ShouldContain("MarkDeviceLost(");

        vulkanSyncObjects.ShouldContain("TimelineWaitPollTimeoutNanoseconds");
        vulkanSyncObjects.ShouldContain("value == ulong.MaxValue");
        vulkanSyncObjects.ShouldNotContain("TryWaitForTimelineValue(semaphore, value, ulong.MaxValue)");
    }

    [Test]
    public void UnitTestingWorld_OpenXrLaneOverridesAndMixedModeWarningAreExplicit()
    {
        string store = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/UnitTestingWorldSettingsStore.cs");
        string program = ReadWorkspaceFile("XREngine.Editor/Program.cs");
        string settings = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/UnitTestingWorldSettings.cs");
        string bootstrapRenderSettings = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/BootstrapRenderSettings.cs");
        string editorUnitTestingWorld = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.cs");
        string editorUnitTestingPawns = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Pawns.cs");
        string engineState = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Engine.State.cs");

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

        program.ShouldContain("VR.Mode=MonadoOpenXR or OpenXR");

        settings.ShouldContain("public UnitTestingVrSettings VR");
        settings.ShouldContain("MonadoOpenXR");
        settings.ShouldContain("public bool UseOpenXR = false");
        settings.ShouldContain("public bool SceneOnlyVRPawn = false");

        editorUnitTestingPawns.ShouldContain("pawnComp.CameraComponent = cameraComponent");
        editorUnitTestingPawns.ShouldContain("Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One).OnPawnCameraChanged();");
        bootstrapRenderSettings.ShouldContain("renderSettings.VrCopyEyePreviewTextures = settings.PreviewVRStereoViews");
        bootstrapRenderSettings.ShouldContain("VrCopyEyePreviewTextures={renderSettings.VrCopyEyePreviewTextures}");
        editorUnitTestingWorld.ShouldContain("s.VrCopyEyePreviewTextures = previewVrStereoViews");
        editorUnitTestingWorld.ShouldContain("VrCopyEyePreviewTextures={s.VrCopyEyePreviewTextures}");
        engineState.ShouldContain("XRComponent? controlledPawn = existing.ControlledPawnComponent");
        engineState.ShouldContain("replacement.ControlledPawnComponent = controlledPawn");
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
    public void UnitTestingWorld_OpenXrVulkanStartupRequiresGpuRenderDispatch()
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

            startupSettings.GPURenderDispatch.ShouldBeTrue();
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
    public void UnitTestingOpenXrVulkan_IgnoresPersistedCpuDirectForceButAllowsEnvOverride()
    {
        string effectiveSettings = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Engine.EffectiveSettings.cs");

        effectiveSettings.ShouldContain("ShouldIgnorePersistedCpuDirectMeshSubmissionForceForUnitTestingOpenXrVulkan");
        effectiveSettings.ShouldContain("XRE_FORCE_MESH_SUBMISSION_STRATEGY=CpuDirect");
        effectiveSettings.ShouldContain("persistedForce == EMeshSubmissionStrategy.CpuDirect");
        effectiveSettings.ShouldContain("IsUnitTestingOpenXrLaunch");
        effectiveSettings.ShouldContain("XREngineEnvironmentVariables.UnitTestVrMode");
        effectiveSettings.ShouldContain("MonadoOpenXR");
        effectiveSettings.ShouldContain("PreferredRenderBackend == ERenderLibrary.Vulkan");
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
        string settings = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs");
        string stats = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.Vr.cs");
        string packet = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string sender = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfilerSender.cs");
        string editorSource = ReadWorkspaceFile("XREngine.Editor/EngineProfilerDataSource.cs");
        string panel = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");

        state.ShouldContain("enum OpenXrRenderPacingMode");
        state.ShouldContain("InRenderCallback");
        state.ShouldContain("PostRenderCallback");
        state.ShouldContain("DedicatedThread");
        state.ShouldContain("CollectVisibleThread");
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
        string openGl = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.OpenGL.cs");

        string cameraUpdate = SliceMethod(
            openGl,
            "private float UpdateOpenXrEyeCameraFromView",
            "private void ApplyOpenXrEyePoseForRenderThread");

        // PaddedFrustum is the only branch that returns a non-zero padding.
        cameraUpdate.ShouldContain("OpenXrCollectVisiblePosePolicy.PaddedFrustum");
        cameraUpdate.ShouldContain("OpenXrCollectFrustumPaddingDegrees");
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

    private static string ReadWorkspaceFile(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        string platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);

        while (dir is not null)
        {
            string fullPath = Path.Combine(dir.FullName, platformPath);
            if (File.Exists(fullPath))
                return File.ReadAllText(fullPath).Replace("\r\n", "\n");

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}
