using System;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Source-level guard rails for Vulkan P1 architecture work that can be
/// validated without requiring a Vulkan device in CI.
/// </summary>
[TestFixture]
public sealed class VulkanP1ValidationTests
{
    [Test]
    public void DescriptorRobustnessDiagnostics_AreProfilerVisible()
    {
        string statsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string materialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string packetSource = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string senderSource = ReadWorkspaceFile("XREngine.Runtime.Host/Engine/Engine.ProfilerSender.cs");
        string editorSource = ReadWorkspaceFile("XREngine.Editor/EngineProfilerDataSource.cs");
        string profilerUiSource = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");

        statsSource.ShouldContain("RecordVulkanDescriptorFallback");
        statsSource.ShouldContain("RecordVulkanDescriptorBindingFailure");
        statsSource.ShouldContain("VulkanDescriptorFallbackSummary");
        statsSource.ShouldContain("VulkanDescriptorFailureSummary");
        statsSource.ShouldContain("VulkanDescriptorSkippedDraws");
        statsSource.ShouldContain("VulkanDescriptorSkippedDispatches");

        meshSource.ShouldContain("RecordDescriptorFallback");
        meshSource.ShouldContain("RecordDescriptorFailure");
        materialSource.ShouldContain("RecordDescriptorFallback");
        materialSource.ShouldContain("RecordDescriptorFailure");
        programSource.ShouldContain("RecordComputeDescriptorFallback");
        programSource.ShouldContain("RecordComputeDescriptorFailure");

        packetSource.ShouldContain("VulkanDescriptorFallbackSampledImages");
        packetSource.ShouldContain("VulkanDescriptorBindingFailures");
        senderSource.ShouldContain("VulkanDescriptorFallbackSampledImages = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackSampledImages");
        editorSource.ShouldContain("VulkanDescriptorFallbackSampledImages = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackSampledImages");
        profilerUiSource.ShouldContain("Descriptor Fallbacks:");
        profilerUiSource.ShouldContain("Descriptor Failures:");
    }

    [Test]
    public void DescriptorUpdateTemplates_AreBackendGatedAcrossDescriptorPaths()
    {
        string templateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs");
        string cacheSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorManager.cs");
        string lifecycleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorWrites.cs");
        string materialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Compute.cs");

        templateSource.ShouldContain("TryUpdateDescriptorSetWithTemplate");
        cacheSource.ShouldContain("_descriptorUpdateTemplateCache");
        templateSource.ShouldContain("TryGetOrCreateUpdateTemplate");
        templateSource.ShouldContain("ComputeTemplateHash");
        templateSource.ShouldContain("DescriptorUpdateTemplateCreateInfo");
        templateSource.ShouldContain("CreateDescriptorUpdateTemplate");
        templateSource.ShouldContain("UpdateDescriptorSetWithTemplate");
        templateSource.ShouldContain("DestroyUpdateTemplateCache");
        lifecycleSource.ShouldContain("_resourceRuntime.DescriptorLifetime.DestroyUpdateTemplateCache();");

        meshSource.ShouldContain("DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template");
        meshSource.ShouldContain("TryUpdateDescriptorSetWithTemplate");
        meshSource.ShouldContain("BackendContext.Resources.DescriptorLifetime.TryUpdateDescriptorSets(");
        materialSource.ShouldContain("DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template");
        materialSource.ShouldContain("TryUpdateDescriptorSetWithTemplate");
        materialSource.ShouldContain("BackendContext.Resources.DescriptorLifetime.TryUpdateDescriptorSets(");
        programSource.ShouldContain("DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template");
        programSource.ShouldContain("TryUpdateDescriptorSetWithTemplate");
        programSource.ShouldContain("BackendContext.Resources.DescriptorLifetime.TryUpdateDescriptorSets(");
        templateSource.ShouldContain("device.Api.UpdateDescriptorSets(");
    }

    [Test]
    public void CanonicalImmutableSamplers_AreCreatedDestroyedAndAppliedToSamplerLayouts()
    {
        string samplerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanCanonicalImmutableSamplerService.cs");
        string lifecycleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs");
        string layoutCacheSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanDescriptorLayoutCache.cs");

        samplerSource.ShouldContain("VulkanCanonicalSampler");
        samplerSource.ShouldContain("LinearClamp");
        samplerSource.ShouldContain("NearestClamp");
        samplerSource.ShouldContain("LinearRepeat");
        samplerSource.ShouldContain("Anisotropic");
        samplerSource.ShouldContain("ShadowComparison");
        samplerSource.ShouldContain("internal static void Initialize(");
        samplerSource.ShouldContain("internal static void Destroy(");

        lifecycleSource.ShouldContain("VulkanCanonicalImmutableSamplerService.Initialize(_resourceRuntime, Api, _deviceContext);");
        lifecycleSource.ShouldContain("VulkanCanonicalImmutableSamplerService.Destroy(_resourceRuntime, Api, _deviceContext.Device);");
        layoutCacheSource.ShouldContain("DescriptorType.Sampler");
        layoutCacheSource.ShouldContain("TryGetCanonicalImmutableSampler(VulkanCanonicalSampler.LinearClamp");
        layoutCacheSource.ShouldContain("PImmutableSamplers");
        layoutCacheSource.ShouldContain("NativeMemory.Alloc");
        layoutCacheSource.ShouldContain("NativeMemory.Free");
    }

    [Test]
    public void PushConstants_CoverGraphicsComputeAndImGuiPaths()
    {
        string commandBufferSource =
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string renderProgramSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string renderProgramPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgramPipeline.cs");

        commandBufferSource.ShouldContain("CommonPushConstantSize = 16");
        commandBufferSource.ShouldContain("ShaderStageFlags.VertexBit |");
        commandBufferSource.ShouldContain("ShaderStageFlags.FragmentBit |");
        commandBufferSource.ShouldContain("ShaderStageFlags.ComputeBit");
        commandBufferSource.ShouldContain("PushConstantsTracked");
        commandBufferSource.ShouldContain("RecordVulkanBindChurn(pushConstantWrites: 1)");
        commandBufferSource.ShouldContain("ComputeDispatchPushConstants");
        commandBufferSource.ShouldContain("Api!.CmdPushConstants");
        meshSource.ShouldContain("MeshDrawPushConstants");
        meshSource.ShouldContain("PushPerDrawConstants");
        meshSource.ShouldContain("CommandOperations.PushConstantsTracked");
        renderProgramSource.ShouldContain("CreateCommonPushConstantRange");
        renderProgramSource.ShouldContain("StageFlags = CommonPushConstantStageFlags");
        renderProgramPipelineSource.ShouldContain("CreateCommonPushConstantRange");
        renderProgramPipelineSource.ShouldContain("StageFlags = CommonPushConstantStageFlags");
        string imguiRecorder = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiOverlayCommandRecorder.cs");
        imguiRecorder.ShouldContain("encoder.PushConstants(input.OverlayCommandBuffer, input.Resources.PipelineLayout");
    }

    [Test]
    public void MappedFrameArena_IsInstrumentedForProfiling()
    {
        string arenaSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanMappedFrameArena.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanRenderer.MappedFrameArena.cs");
        string allocationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs");
        string statsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.cs");
        string packetSource = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string profilerUiSource = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");

        arenaSource.ShouldContain("RecordVulkanDynamicUniformAllocation");
        allocationSource.ShouldContain("RecordVulkanDynamicUniformExhaustion");
        statsSource.ShouldContain("VulkanDynamicUniformAllocations");
        statsSource.ShouldContain("VulkanDynamicUniformAllocatedBytes");
        statsSource.ShouldContain("VulkanDynamicUniformExhaustions");
        packetSource.ShouldContain("VulkanDynamicUniformAllocatedBytes");
        profilerUiSource.ShouldContain("Dynamic UBO Ring:");
    }

    [Test]
    public void ResourcePlanReplacements_AreFenceRetiredAndObservable()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string resourceRegistrationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/VulkanRenderer.ResourceRegistration.cs");
        string plannerUpdate = SliceBetween(
            stateSource,
            "private void UpdateResourcePlannerFromContext",
            "private static ulong ComputeResourcePlannerSignature");

        plannerUpdate.ShouldContain("RecordVulkanRetiredResourcePlanReplacement");
        plannerUpdate.ShouldContain("oldAllocator.TryRetirePhysicalResources");
        plannerUpdate.ShouldContain("LogDeferredResourcePlanReplacementRetirement");
        stateSource.ShouldContain("Deferring replaced physical resource plan retirement through frame-slot/timeline completion");
        stateSource.ShouldContain("ShouldSkipAutoExposureHistoryPreserve");
        stateSource.ShouldContain("ActiveResourcePlannerRevision == 0");
        stateSource.ShouldContain("RuntimeRenderingHostServices.Presentation.IsInVR");
        plannerUpdate.ShouldNotContain("WaitForAllInFlightWork()");
        plannerUpdate.ShouldNotContain("DeviceWaitIdle()");
        plannerUpdate.ShouldNotContain("ForceFlushAllRetiredResourcesAfterWaiting(\"ResourcePlanReplacement\")");
        plannerUpdate.ShouldNotContain("TransitionNewPhysicalImagesToInitialLayout");
        resourceRegistrationSource.ShouldNotContain("TransitionNewPhysicalImagesToInitialLayout");
        resourceRegistrationSource.ShouldContain("RetireBuffer(buffer, memory)");

        string statsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.cs");
        string packetSource = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string profilerUiSource = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");

        statsSource.ShouldContain("RecordVulkanRetiredResourcePlanReplacement");
        packetSource.ShouldContain("VulkanRetiredResourcePlanReplacements");
        profilerUiSource.ShouldContain("Retired Plan Resources:");
    }

    [Test]
    public void AutoExposureHistoryCopy_HandlesUndefinedNewPhysicalTarget()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string preserveSource = SliceBetween(
            stateSource,
            "private bool TryCopyAutoExposureHistory",
            "private VulkanPhysicalImageGroup RetainAutoExposureHistory");

        preserveSource.ShouldContain("ImageLayout newCurrentLayout = newGroup.LastKnownLayout;");
        preserveSource.ShouldContain("newCurrentLayout == ImageLayout.Undefined ? AccessFlags.None : AccessFlags.ShaderWriteBit");
        preserveSource.ShouldContain("newCurrentLayout == ImageLayout.Undefined ? PipelineStageFlags.TopOfPipeBit : autoExposureStages");
        preserveSource.ShouldContain("newGroup.LastKnownLayout = newRestoreLayout;");
        preserveSource.ShouldNotContain("newGroup,\n            newLayout,\n            ImageLayout.TransferDstOptimal");
    }

    [Test]
    public void ExternalSwapchainPlannerDisplayExtent_IsAuthoritativeWhileInternalExtentRemainsScaled()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs").Replace("\r\n", "\n");
        string stateTrackingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs").Replace("\r\n", "\n");
        string initializationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs").Replace("\r\n", "\n");
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs").Replace("\r\n", "\n");
        string openXrApiSource = ReadWorkspaceFile("XREngine.Runtime.XR.OpenXR/OpenXRAPI.SceneViews.cs").Replace("\r\n", "\n");
        string openXrVulkanApiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding.Implementation.cs").Replace("\r\n", "\n");
        string openXrFrameLifecycleSource = ReadWorkspaceFile("XREngine.Runtime.XR.OpenXR/OpenXRAPI.FrameLifecycle.cs").Replace("\r\n", "\n");
        string pipelineInstanceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs").Replace("\r\n", "\n");
        string renderStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs").Replace("\r\n", "\n");
        string renderToWindowSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderToWindow.cs").Replace("\r\n", "\n");
        string temporalSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs").Replace("\r\n", "\n");
        string defaultPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        string advancedPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs").Replace("\r\n", "\n");
        string pushMainAttributes = SliceBetween(
            renderStateSource,
            "public StateObject PushMainAttributes",
            "public void PopMainAttributes");
        string initialRenderArea = SliceBetween(
            renderStateSource,
            "private bool PushInitialMainRenderArea",
            "private void PushRequiredRenderArea");
        string captureContext = SliceBetween(
            stateSource,
            "internal FrameOpContext CaptureFrameOpContext()",
            "private FrameOpContext ApplyInteractiveResizePlannerFreeze");
        string resizeFreeze = SliceBetween(
            stateSource,
            "private FrameOpContext ApplyInteractiveResizePlannerFreeze",
            "private void CaptureInteractiveResizePlannerExtents");
        string refreshContext = SliceBetween(
            stateSource,
            "private FrameOpContext RefreshPlannerExtentsFromLiveContext(\n        FrameOpContext context",
            "private static FrameOpContext SelectPrimaryPlannerContext(FrameOp[] ops)");
        string extentContext = SliceBetween(
            stateSource,
            "private VulkanResourceExtentContext BuildResourceExtentContext",
            "private RenderResourceRegistry? BuildMergedFrameOpRegistry");
        string externalScope = SliceBetween(
            openXrSource,
            "internal IDisposable EnterOpenXrExternalSwapchainRenderScope",
            "internal bool TryRenderOpenXrEyeSwapchain");
        string fillProjectionView = SliceBetween(
            openXrFrameLifecycleSource,
            "private void FillProjectionView",
            "private void ValidateProjectionViewSubImage");
        string validateProjectionView = SliceBetween(
            openXrFrameLifecycleSource,
            "private void ValidateProjectionViewSubImage",
            "private void TraceProjectionViewSubImage");

        stateSource.ShouldContain("private bool TryResolveExternalSwapchainTargetExtent(out Extent2D extent)");
        captureContext.ShouldContain("if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))");
        captureContext.ShouldContain("ResolveExternalFrameOpResourceDimensions(");
        captureContext.ShouldContain("displayWidth = dimensions.DisplayWidth;");
        captureContext.ShouldContain("internalHeight = dimensions.InternalHeight;");
        resizeFreeze.ShouldContain("if (TryResolveExternalSwapchainTargetExtent(out _))\n            return context;");
        refreshContext.ShouldContain("Refreshing external swapchain frame-op planner extents");
        refreshContext.ShouldContain("DisplayWidth = DisplayWidth");
        refreshContext.ShouldContain("InternalHeight = InternalHeight");
        extentContext.ShouldContain("if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))");
        extentContext.ShouldContain("ResolveExternalFrameOpResourceDimensions(");
        extentContext.ShouldContain("return new VulkanResourceExtentContext(\n                DisplayWidth,\n                DisplayHeight,\n                InternalWidth,\n                InternalHeight);");
        stateSource.ShouldContain("OpenXR external swapchain rendering is active, but no valid external target extent is bound.");
        stateTrackingSource.ShouldContain("if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))\n            return externalExtent;");
        initializationSource.ShouldContain("if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))\n                    ActiveState.SetCurrentTargetExtent(externalExtent);");
        externalScope.ShouldContain("if (width == 0 || height == 0)");
        externalScope.ShouldContain("exceeds supported render-region dimensions");
        externalScope.ShouldNotContain("Math.Min");
        openXrApiSource.ShouldContain("_openXrLeftViewport ??= CreateOpenXrViewport()");
        openXrApiSource.ShouldContain("_openXrRightViewport ??= CreateOpenXrViewport()");
        openXrApiSource.ShouldContain("viewport.Window = null;");
        openXrVulkanApiSource.ShouldContain("ValidateOpenXrEyeViewportExtent");
        pipelineInstanceSource.ShouldContain("EnsureExternalSwapchainResourceGenerationForCurrentFrame");
        pipelineInstanceSource.ShouldContain("ExternalSwapchainFramePrepare");
        pushMainAttributes.ShouldContain("bool applyRenderArea = true");
        initialRenderArea.ShouldContain("renderer?.TryGetExternalSwapchainTargetRegion(out BoundingRectangle externalRegion) == true");
        initialRenderArea.ShouldContain("PushRequiredRenderArea(externalRegion, \"OpenXR external swapchain target\");");
        initialRenderArea.ShouldContain("viewport?.RendersToExternalSwapchainTarget == true");
        initialRenderArea.ShouldContain("PushRequiredRenderArea(externalViewportRegion, \"OpenXR external swapchain viewport\");");
        openXrFrameLifecycleSource.ShouldContain("catch (InvalidOperationException)\n        {\n            throw;\n        }");
        openXrVulkanApiSource.ShouldContain("catch (InvalidOperationException)\n        {\n            throw;\n        }");
        fillProjectionView.ShouldContain("uint expectedWidth = GetOpenXrSwapchainWidth(viewIndex);");
        fillProjectionView.ShouldContain("uint expectedHeight = GetOpenXrSwapchainHeight(viewIndex);");
        fillProjectionView.ShouldContain("ValidateProjectionViewSubImage(viewIndex, in projectionViews[viewIndex], expectedWidth, expectedHeight);");
        validateProjectionView.ShouldContain("projectionView.SubImage.Swapchain.Handle != _swapchains[viewIndex].Handle");
        validateProjectionView.ShouldContain("OpenXR projection view {viewIndex} sub-image does not cover the full eye swapchain");
        validateProjectionView.ShouldContain("Expected=(0,0,{expectedWidth}x{expectedHeight});");
        openXrSource.ShouldContain("ValidateOpenXrExternalFrameOpContexts");
        openXrSource.ShouldContain("ValidateOpenXrExternalSwapchainWriterDrawState");
        openXrSource.ShouldContain("ExpectedViewportScissorCount=1");
        openXrSource.ShouldContain("captured a swapchain writer that does not cover the full eye target");
        renderToWindowSource.ShouldContain("renderer.TryGetExternalSwapchainTargetRegion(out BoundingRectangle externalRegion)\n            ? externalRegion\n            : useBoundOutputFbo");
        temporalSource.ShouldContain("RuntimeRenderingHostServices.FrameTiming.CurrentRenderer as AbstractRenderer\n            ?? AbstractRenderer.Current");
        defaultPipelineSource.ShouldContain("RuntimeRenderingHostServices.FrameTiming.CurrentRenderer as AbstractRenderer\n            ?? AbstractRenderer.Current");
        advancedPipelineSource.ShouldContain("RuntimeRenderingHostServices.FrameTiming.CurrentRenderer as AbstractRenderer\n            ?? AbstractRenderer.Current");
    }

    [Test]
    public void VisibilityCollection_DoesNotMutateVulkanRenderAreaOrUseAmbientTemporalState()
    {
        string viewportSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRViewport.cs").Replace("\r\n", "\n");
        string renderStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs").Replace("\r\n", "\n");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs").Replace("\r\n", "\n");

        viewportSource.ShouldContain("meshRenderCommands: commandCollection,\n                applyRenderArea: false);");
        renderStateSource.ShouldContain("FrameViewSet = null;");
        renderStateSource.ShouldContain("WorldSnapshot = null;");
        meshSource.ShouldContain("VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(currentPipeline, out var temporalData)");
        meshSource.ShouldNotContain("VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData)");
    }

    [Test]
    public void VulkanThreadRenderStateScope_IsolatesFramebufferBindings()
    {
        string stateTrackingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs").Replace("\r\n", "\n");
        string initializationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs").Replace("\r\n", "\n");
        string readbackSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Readback.cs").Replace("\r\n", "\n");
        string plannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs").Replace("\r\n", "\n");

        stateTrackingSource.ShouldContain("private static VulkanRenderer? _threadFramebufferBindingOwner;");
        stateTrackingSource.ShouldContain("private static XRFrameBuffer? _threadBoundDrawFrameBuffer;");
        stateTrackingSource.ShouldContain("private static XRFrameBuffer? _threadBoundReadFrameBuffer;");
        stateTrackingSource.ShouldContain("private static EReadBufferMode _threadReadBufferMode;");
        stateTrackingSource.ShouldContain("private XRFrameBuffer? ActiveBoundDrawFrameBuffer");
        stateTrackingSource.ShouldContain("private XRFrameBuffer? ActiveBoundReadFrameBuffer");
        stateTrackingSource.ShouldContain("private EReadBufferMode ActiveReadBufferMode");

        string renderScope = SliceBetween(
            stateTrackingSource,
            "private readonly struct ThreadRenderStateScope : IDisposable",
            "private ThreadRenderStateScope EnterThreadRenderStateScope");
        renderScope.ShouldContain("_previousFramebufferBindingOwner = _threadFramebufferBindingOwner;");
        renderScope.ShouldContain("_threadFramebufferBindingOwner = renderer;");
        renderScope.ShouldContain("_threadBoundDrawFrameBuffer = null;");
        renderScope.ShouldContain("_threadReadBufferMode = EReadBufferMode.ColorAttachment0;");
        renderScope.ShouldContain("_threadFramebufferBindingOwner = _previousFramebufferBindingOwner;");

        stateTrackingSource.ShouldContain("return XRFrameBuffer.BoundForWriting ?? ActiveBoundDrawFrameBuffer;");
        stateTrackingSource.ShouldContain("=> ActiveBoundReadFrameBuffer;");
        stateTrackingSource.ShouldContain("=> ActiveReadBufferMode;");
        initializationSource.ShouldContain("ActiveReadBufferMode = mode;");
        initializationSource.ShouldContain("ActiveBoundReadFrameBuffer = fbo;");
        initializationSource.ShouldContain("ActiveBoundDrawFrameBuffer = fbo;");
        initializationSource.ShouldContain("XRFrameBuffer? boundDrawFrameBuffer = ActiveBoundDrawFrameBuffer;");
        readbackSource.ShouldContain("XRFrameBuffer? boundReadFrameBuffer = ActiveBoundReadFrameBuffer;");
        readbackSource.ShouldContain("ActiveReadBufferMode");
        plannerSource.ShouldContain("if (ActiveBoundDrawFrameBuffer is null)\n            ActiveState.SetCurrentTargetExtent(extent);");
    }

    [Test]
    public void RenderAreaStackClearsVulkanExplicitViewportWhenEmpty()
    {
        string abstractRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs")
            .Replace("\r\n", "\n");
        string renderingStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs")
            .Replace("\r\n", "\n");
        string vulkanRenderStateApiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.RenderStateApi.cs")
            .Replace("\r\n", "\n");
        string vulkanRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.cs");
        string vulkanStateMutationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.RenderStateMutation.cs")
            .Replace("\r\n", "\n");

        abstractRendererSource.ShouldContain("public virtual void ClearRenderArea()");
        renderingStateSource.ShouldContain("else\n                AbstractRenderer.Current?.ClearRenderArea();");
        vulkanRendererSource.ShouldContain("public override void ClearRenderArea() => _commandRuntime.ClearViewport();");
        vulkanRenderStateApiSource.ShouldContain("ActiveState.ClearViewport();");
        vulkanStateMutationSource.ShouldContain("public bool ClearViewport()");
        vulkanStateMutationSource.ShouldContain("_viewportExplicitlySet = false;");
    }

    [Test]
    public void VulkanFrameLoop_GenericRendererApisHaveFocusedOwners()
    {
        string renderer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.cs");
        string frameLoop = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.LegacyCommandApi.cs");
        string frameOperations = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.FrameOperationApi.cs");

        renderer.ShouldContain("public override void MemoryBarrier(EMemoryBarrierMask mask) => _frameLoop.EnqueueMemoryBarrier(mask);");
        renderer.ShouldContain("public override void PublishFrameBufferAttachmentsForSampling(XRFrameBuffer frameBuffer) => _frameLoop.PublishFrameBufferAttachmentsForSampling(frameBuffer);");
        renderer.ShouldContain("public override void ColorMask(bool red, bool green, bool blue, bool alpha) => _commandRuntime.SetColorMask(red, green, blue, alpha);");
        renderer.ShouldContain("public override void ClearRenderArea() => _commandRuntime.ClearViewport();");
        renderer.ShouldContain("protected override AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject) => _resourceRuntime.CreateAPIRenderObject(renderObject);");
        frameLoop.ShouldContain("VulkanCommandRuntime.CreatePublishFramebufferOperation(");
        frameOperations.ShouldContain("=> new(passIndex, frameBuffer, context);");
    }

    [Test]
    public void OpenXrFrameOpCapture_RespectsExplicitNullCameraAndScopedRenderer()
    {
        string renderStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs").Replace("\r\n", "\n");
        string meshRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs").Replace("\r\n", "\n");
        string dirtyReasonsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferDirtyReasons.cs").Replace("\r\n", "\n");
        string stateTrackingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs").Replace("\r\n", "\n");

        renderStateSource.ShouldContain("public bool HasRenderingCameraScope => _renderingCameras.Count > 0;");
        meshRendererSource.ShouldContain("bool explicitCameraScope = RuntimeEngine.Rendering.State.RenderingPipelineState?.HasRenderingCameraScope == true;");
        meshRendererSource.ShouldContain("XRCamera? snapshotCamera = explicitCameraScope\n                ? RuntimeEngine.Rendering.State.RenderingCamera\n                : RuntimeEngine.Rendering.State.RenderingCamera");
        meshRendererSource.ShouldContain("XRCamera? snapshotRightEyeCamera = snapshotCamera is null\n                ? null\n                : RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera");
        dirtyReasonsSource.ShouldContain("if (VulkanPrimaryCommandBufferReuseEnabled || CommandChainsEnabledForCurrentRecording || t_frameOpCapture is not null)\n            return;");
        stateTrackingSource.ShouldContain("private readonly IDisposable _currentRendererScope;");
        stateTrackingSource.ShouldContain("_currentRendererScope = AbstractRenderer.PushThreadCurrent(renderer);");
        stateTrackingSource.ShouldContain("_currentRendererScope.Dispose();");
    }

    [Test]
    public void OpenXrDirectEyeSwapchain_UsesTrackedExternalTargetInitialLayout()
    {
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs").Replace("\r\n", "\n");
        string recordingTarget = SliceBetween(
            commandBufferSource,
            "private readonly record struct SwapchainRecordingTarget",
            "private bool TryRecordCommandBuffer");

        recordingTarget.ShouldContain("ImageLayout InitialColorLayout");
        recordingTarget.ShouldContain("private ImageLayout ResolveTrackedSwapchainTargetColorLayout(Image image)");
        recordingTarget.ShouldContain("ResolveTrackedSwapchainTargetColorLayout(openXrTarget.Image)");
        recordingTarget.ShouldContain("ResolveTrackedSwapchainTargetColorLayout(swapchainImage)");

        string recordSource = SliceBetween(
            commandBufferSource,
            "private bool TryRecordCommandBuffer",
            "private void RecordClearOp");

        recordSource.ShouldContain("ImageLayout initialSwapchainColorLayout = swapchainTarget.IsValid");
        recordSource.ShouldContain("ImageLayout swapchainFinalLayout = initialSwapchainColorLayout;");
        recordSource.ShouldContain("ImageLayout oldLayout = ResolveCurrentSwapchainColorLayout();");
        recordSource.ShouldContain("OldLayout = oldLayout");
        recordSource.ShouldNotContain("ImageLayout swapchainFinalLayout = imageWasEverPresentedAtRecordStart");
    }

    [Test]
    public void ParallelOpenXrPreparedEyes_OwnFrameOpArrays()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs")
            .Replace("\r\n", "\n");
        string prepareMethod = SliceBetween(
            openXrSource,
            "private bool TryPrepareOpenXrEyeSwapchainCommandBuffer",
            "private bool TryRecordPreparedOpenXrEyeSwapchainCommandBuffer");

        prepareMethod.ShouldContain("CloneFrameOpsForPreparedOpenXrEye(ops)");
        openXrSource.ShouldContain("private static FrameOp[] CloneFrameOpsForPreparedOpenXrEye(FrameOp[] ops)");
        openXrSource.ShouldContain("return ops.Length == 0 ? ops : (FrameOp[])ops.Clone();");
    }

    [Test]
    public void MonadoPreviewWindow_IsResizableAndReportsEyeResolutionInTitle()
    {
        string windowSource = ReadWorkspaceFile("Build/Submodules/monado/src/xrt/compositor/main/comp_window_mswin.c").Replace("\r\n", "\n");
        string previewSource = ReadWorkspaceFile("Build/Submodules/monado/src/xrt/compositor/main/comp_window_peek.c").Replace("\r\n", "\n");

        windowSource.ShouldContain("CreateWindowExW(0, szWindowClass, L\"Monado (Windowed)\", WS_OVERLAPPEDWINDOW");
        windowSource.ShouldNotContain("COMP_WINDOW_MSWIN_RESTORE_PREFERRED_CLIENT_SIZE");
        windowSource.ShouldNotContain("restoring preferred simulated HMD extent");
        windowSource.ShouldContain("set_window_title_utf8(cwm->window, cwm->current_title);");
        previewSource.ShouldContain("uint32_t title_eye_width");
        previewSource.ShouldContain("\"Monado preview %s | preset %s @ %sx | internal eye %ux%u | window %ux%u | preview eye %ux%u %.2fx\"");
    }

    [Test]
    public void EditorImGuiStyling_IsInitializedPerContext()
    {
        string source = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.ImGui.cs").Replace("\r\n", "\n");

        source.ShouldContain("private static IntPtr _imguiStyledContext;");
        source.ShouldContain("private static IntPtr _dockingIniReloadedContext;");
        source.ShouldContain("IntPtr currentContext = ImGui.GetCurrentContext();");
        source.ShouldContain("if (IsProfessionalImGuiStylingCurrent(currentContext))");
        source.ShouldContain("if (!_imguiStyleInitialized || _imguiStyledContext != currentContext || currentContext == IntPtr.Zero)");
        source.ShouldContain("_imguiStyledContext = currentContext;");
        source.ShouldContain("if (_dockingIniReloadedContext != currentContext)");
        source.ShouldContain("_dockingIniReloadedContext = currentContext;");
    }

    [Test]
    public void ResourcePlannerMergedRegistry_ReusesPrimaryWhenOtherContextsAreCovered()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string mergeSource = SliceBetween(
            stateSource,
            "private RenderResourceRegistry? BuildMergedFrameOpRegistry",
            "internal static void AddRegistryDescriptors");

        mergeSource.ShouldContain("RegistriesCoveredByPrimary(registries, primaryRegistry)");
        mergeSource.ShouldContain("return primaryRegistry;");
        mergeSource.ShouldContain("FrameBufferDescriptorsEquivalent");
        mergeSource.ShouldContain("TryGetCachedMergedFrameOpRegistry");
        mergeSource.ShouldContain("RememberMergedFrameOpRegistry");
        mergeSource.ShouldContain("DescriptorSignature");
        mergeSource.ShouldNotContain("ResourceGenerationStamp");
    }

    [Test]
    public void ResourcePlanner_SplitsPhysicalAllocationSignatureFromGraphSignature()
    {
        string stateSource =
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string resourcePlannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string plannerUpdate = SliceBetween(
            resourcePlannerSource,
            "private void UpdateResourcePlannerFromContext",
            "private IReadOnlyCollection<RenderPassMetadata>? FilterActivePassMetadata");

        stateSource.ShouldContain("_resourceAllocationSignature");
        stateSource.ShouldContain("_resourcePlannerFastPathKey");
        stateSource.ShouldContain("_barrierPlanFastPathKey");
        resourcePlannerSource.ShouldContain("ComputeResourceAllocationSignature");
        plannerUpdate.ShouldContain("PrepareResourcePlanningInputs");
        plannerUpdate.ShouldContain("CanReuseResourcePlannerFastPath");
        plannerUpdate.ShouldContain("BuildResourceDescriptorPlan");
        plannerUpdate.ShouldContain("BuildPhysicalAllocationPlan");
        plannerUpdate.ShouldContain("TryBuildPhysicalAllocator");
        plannerUpdate.ShouldContain("CommitPhysicalAllocatorPlan");
        plannerUpdate.ShouldContain("RebuildRenderGraphAndBarriers");
        plannerUpdate.ShouldContain("Reusing physical resource plan for metadata-only graph change");
        plannerUpdate.ShouldContain("ActiveResourceAllocationSignature = allocationPlan.Signature;");
        plannerUpdate.ShouldContain("RememberResourcePlannerFastPath");
        stateSource.ShouldContain("BarrierPlanFastPathKey");
        resourcePlannerSource.ShouldContain("barrierKey.Matches(ActiveBarrierPlanFastPathKey)");

        string allocatorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/VulkanResourceAllocator.cs");
        allocatorSource.ShouldContain("ComputePhysicalPlanUsageSignature");
        allocatorSource.ShouldContain("Physical allocations are descriptor-driven");
        allocatorSource.ShouldContain("planner.FrameBufferDescriptors.OrderBy");
        allocatorSource.ShouldNotContain("BuildUsageProfiles(passMetadata, planner)");
        allocatorSource.ShouldContain("usage |= ImageUsageFlags.SampledBit;");
        allocatorSource.ShouldContain("BufferUsageFlags.UniformBufferBit");
    }

    [Test]
    public void ResourcePlanner_CachesActivePassMetadataAndCompiledGraphs()
    {
        string stateSource =
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");

        stateSource.ShouldContain("_lastActiveFilterSourcePassMetadata");
        stateSource.ShouldContain("_lastActiveFilterPassSetSignature");
        stateSource.ShouldContain("ComputeActivePassSetSignature");
        stateSource.ShouldContain("ComputePassMetadataRevisionStamp");
        stateSource.ShouldContain("return _lastActiveFilterResult;");
        stateSource.ShouldContain("ChangedFields=[{6}]");
        stateSource.ShouldContain("DescribeDelta");
        stateSource.ShouldNotContain("foreach (int passIndex in activePassIndices.OrderBy");
        stateSource.ShouldNotContain("foreach (RenderPassResourceUsage usage in pass.ResourceUsages\r\n                .OrderBy");
        compilerSource.ShouldContain("CompiledGraphCache");
        compilerSource.ShouldContain("CompiledGraphCacheEntry");
        compilerSource.ShouldContain("BuildCompiledGraph(metadata)");
        compilerSource.ShouldContain("_secondaryRecordingBucketScratch");
        compilerSource.ShouldContain("buckets.Clear();");
        compilerSource.ShouldNotContain("List<SecondaryRecordingBucket> buckets = [];");
    }

    [Test]
    public void ResourcePlanner_ReusesExactCleanFramePlanWithoutSkippingMutableSafetyChecks()
    {
        string trackingSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string plannerSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string commandBufferSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        trackingSource.ShouldContain("PreparedFrameOpsSignature");
        trackingSource.ShouldContain("PreparedPlanRevision");
        trackingSource.ShouldContain("HasPreparedPlan");

        plannerSource.ShouldContain("TryReusePreparedFrameOpResourcePlannerStates");
        plannerSource.ShouldContain("switchingState.PreparedFrameOpsSignature != frameOpsSignature");
        plannerSource.ShouldContain("currentRegistryRevision != fastPathKey.RegistryDescriptorRevision");
        plannerSource.ShouldContain("currentPassMetadataRevision != fastPathKey.ActivePassMetadataRevision");
        plannerSource.ShouldContain("BuildQueueOwnershipConfig(fastPathKey.ActivePassMetadata)");
        plannerSource.ShouldContain("!state.ResourceAllocator.IsRetired");
        plannerSource.ShouldContain("RememberPreparedFrameOpResourcePlannerStates");
        plannerSource.ShouldContain("InvalidatePreparedFrameOpResourcePlan");

        int cleanFrameReuse = commandBufferSource.IndexOf(
            "TryReusePreparedFrameOpResourcePlannerStates(",
            StringComparison.Ordinal);
        int fullPreparation = commandBufferSource.IndexOf(
            "PrepareResourcePlannerForFrameOps(ops, frameOpsSignature)",
            StringComparison.Ordinal);
        int rememberPreparedPlan = commandBufferSource.IndexOf(
            "RememberPreparedFrameOpResourcePlannerStates(",
            StringComparison.Ordinal);

        cleanFrameReuse.ShouldBeGreaterThanOrEqualTo(0);
        fullPreparation.ShouldBeGreaterThan(cleanFrameReuse);
        rememberPreparedPlan.ShouldBeGreaterThan(fullPreparation);
    }

    [Test]
    public void DefaultPipeline_CleanFrameSettingsAndProbeScansAvoidEnumeratorAllocations()
    {
        string postProcessState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/PostProcessing/CameraPostProcessStateCollection.cs");
        string defaultPipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        postProcessState.ShouldContain("Volatile.Read(ref _publishedPipelines)");
        postProcessState.ShouldContain("PublishPipelinesNoLock()");
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string dataBuffer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

        postProcessState.ShouldNotContain("_stages.Values.FirstOrDefault");
        postProcessState.ShouldContain("foreach (PostProcessStageState stage in _stages.Values)");
        defaultPipeline.ShouldContain("for (int i = 0; i < probes.Count; i++)");
        defaultPipeline.ShouldContain("UnitTestEnvironmentRequestsOpenXr");
        planner.ShouldContain("Enum.IsDefined<EDefaultRenderPass>");
        planner.ShouldContain("PassMetadataContainsPassIndex(passMetadata!, passIndex)");
        planner.ShouldNotContain("passMetadata!.Any(m => m.PassIndex");
        dataBuffer.ShouldNotContain("foreach (VulkanMemoryAllocation candidate in _bufferAllocations.Values)");
    }

    [Test]
    public void DefaultPipeline_DirectionalLightUsesCachedNamesAndOneVulkanArrayUploadPath()
    {
        string directionalLight = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.cs");
        string uniformNames = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.UniformNames.cs");

        directionalLight.ShouldContain("DirectionalLightUniformNames names = ResolveUniformNames(targetStructName);");
        directionalLight.ShouldContain("bool useVulkanBulkArrays = IsVulkanDirectionalShadowBackend();");
        directionalLight.ShouldContain("if (!useVulkanBulkArrays)");
        directionalLight.ShouldNotContain("program.Uniform($\"{flatPrefix}");
        uniformNames.ShouldContain("private static readonly DirectionalLightUniformNames DefaultUniformNames");
        uniformNames.ShouldContain("UniformNamesByPrefix.GetOrAdd");
        uniformNames.ShouldContain("IndexedRenderedCascadeStaleAge = CreateIndexedNames(RenderedCascadeStaleAge);");
    }

    [Test]
    public void ImGuiOverlaySnapshots_ReuseGrowthOnlyStorageAndRecycleAfterRecording()
    {
        string imgui = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");
        string frameRecording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Recording.cs");

        imgui.ShouldContain("private ImGuiFrameSnapshot? _recycledSnapshot;");
        imgui.ShouldContain("snapshot.Capture(drawData);");
        imgui.ShouldContain("EnsureCapacity(ref _vertices, VertexCount);");
        imgui.ShouldContain("for (int listIndex = 0; listIndex < drawData.CommandListCount; listIndex++)");
        imgui.ShouldContain("for (int cmdIndex = 0; cmdIndex < cmdList.CommandCount; cmdIndex++)");
        imgui.ShouldNotContain("new ImDrawVert[cmdList.VtxBuffer.Size]");
        imgui.ShouldNotContain("new ushort[cmdList.IdxBuffer.Size]");
        imgui.ShouldNotContain("new ImGuiCommandSnapshot[cmdList.CmdBuffer.Size]");
        frameRecording.ShouldContain("_imguiDrawData.Recycle(imguiOverlaySnapshot);");
    }

    [Test]
    public void ResourcePlanner_SwitchesPerFrameOpContextDuringPrimaryRecording()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs").Replace("\r\n", "\n");
        string plannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs").Replace("\r\n", "\n");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs").Replace("\r\n", "\n");
        string loweringSource = SourceContractWorkspace.ReadVulkanSourcesContaining("FrameOpResourcePlannerSwitchingState frameOpSwitchingState = ActiveFrameOpResourcePlannerSwitchingState;").Replace("\r\n", "\n");
        string initializationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs").Replace("\r\n", "\n");

        stateSource.ShouldContain("Dictionary<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> States");
        stateSource.ShouldContain("private readonly struct FrameOpResourcePlannerPreparationScope : IDisposable");
        stateSource.ShouldContain("private readonly struct FrameOpResourcePlannerRecordingScope : IDisposable");
        stateSource.ShouldContain("switchingState.RecordingScopeActive = true;");

        plannerSource.ShouldContain("private ulong PrepareFrameOpResourcePlannerStatesForFrameOps(FrameOp[] ops, ulong frameOpsSignature = 0)");
        plannerSource.ShouldContain("private FrameOpContext PrepareResourcePlannerForFrameOps(");
        plannerSource.ShouldContain("in VulkanFrameOpPlannerStateKey key,");
        plannerSource.ShouldContain("private bool TryActivateFrameOpResourcePlannerState(in FrameOpContext context)");
        plannerSource.ShouldContain("private void SaveActiveFrameOpResourcePlannerState()");
        plannerSource.ShouldContain("SelectPrimaryPlannerContext(ops, key)");
        plannerSource.ShouldContain("filterByPlannerKey: true, plannerKey: key");

        commandBufferSource.ShouldContain("PrepareFrameOpResourcePlannerStatesForFrameOps(ops, frameOpsSignature)");
        commandBufferSource.ShouldContain("using FrameOpResourcePlannerRecordingScope frameOpResourcePlannerRecordingScope = EnterFrameOpResourcePlannerRecordingScope();");
        commandBufferSource.ShouldContain("_ = TryActivateFrameOpResourcePlannerState(initialContext);");
        commandBufferSource.ShouldContain("if (TryActivateFrameOpResourcePlannerState(activeContext))");
        commandBufferSource.ShouldContain("VulkanFrameOpPlannerStateKey packetPlannerKey = BuildFrameOpPlannerStateKey(packetContext);");
        commandBufferSource.ShouldContain("BuildFrameOpPlannerStateKey(ops[packetEnd].Context) == packetPlannerKey");

        loweringSource.ShouldContain("FrameOpResourcePlannerSwitchingState frameOpSwitchingState = ActiveFrameOpResourcePlannerSwitchingState;");
        initializationSource.ShouldContain("DestroyFrameOpResourcePlannerStates();");
    }

    [Test]
    public void FrameOpFrameBuffers_AreDeclaredInPlannerOverlayWithoutMutatingGenerationRegistry()
    {
        string frameOpApiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOpApi.cs")
            .Replace("\r\n", "\n");
        string plannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs")
            .Replace("\r\n", "\n");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs")
            .Replace("\r\n", "\n");
        string blitSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs")
            .Replace("\r\n", "\n");
        string initializationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs")
            .Replace("\r\n", "\n");
        string registrationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/VulkanRenderer.ResourceRegistration.cs")
            .Replace("\r\n", "\n");

        frameOpApiSource.ShouldContain("public override void PublishFrameBufferAttachmentsForSampling");
        frameOpApiSource.ShouldContain("EnqueueFrameOp(new PublishFramebufferForSamplingOp(passIndex, frameBuffer, context));");
        frameOpApiSource.ShouldNotContain("EnsureFrameBufferRegistered");
        frameOpApiSource.ShouldNotContain("EnsureFrameBufferAttachmentsRegistered");

        string recordPublish = SliceBetween(
            commandBufferSource,
            "private void RecordPublishFramebufferForSamplingOp",
            "private static ImageLayout ResolvePublishedSampledLayout");
        recordPublish.ShouldNotContain("EnsureFrameBufferRegistered");
        recordPublish.ShouldNotContain("EnsureFrameBufferAttachmentsRegistered");

        plannerSource.ShouldContain("AddFrameOpFrameBufferDescriptors(merged, ops);");
        plannerSource.ShouldContain("RegisterTextureDescriptor(EnrichTextureDescriptorForFrameBufferAttachment");
        plannerSource.ShouldContain("RegisterFrameBufferDescriptor(RenderResourceDescriptorFactory.FromFrameBuffer");
        plannerSource.ShouldContain("RenderResourceLifetime.External");
        plannerSource.ShouldContain("int frameBufferDescriptorSignature = ComputeFrameOpFrameBufferDescriptorSignature(ops);");
        plannerSource.ShouldNotContain("RegisterFrameOpOutputFrameBuffer");
        blitSource.ShouldNotContain("EnsureFrameBufferRegistered");
        blitSource.ShouldNotContain("EnsureFrameBufferAttachmentsRegistered");
        initializationSource.ShouldNotContain("EnsureFrameBufferRegistered(fbo);");
        initializationSource.ShouldNotContain("EnsureFrameBufferAttachmentsRegistered(fbo);");
        registrationSource.ShouldNotContain("registry.BindFrameBuffer(");
        registrationSource.ShouldNotContain("registry.BindTexture(");
        registrationSource.ShouldNotContain("registry.BindBuffer(");
    }

    [Test]
    public void CommandChainResourcePlanFreeze_PreventsPlannerMutationDuringLowering()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string plannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string loweringSource = SourceContractWorkspace.ReadVulkanSourcesContaining("BeginCommandChainResourcePlanReadScope(resourcePlanRevision)");

        stateSource.ShouldContain("_commandChainFrozenPlanReaders");
        stateSource.ShouldContain("_commandChainFrozenResourcePlanRevision");
        stateSource.ShouldContain("private bool IsCommandChainResourcePlanFrozen => Volatile.Read(ref _commandChainFrozenPlanReaders) > 0;");
        stateSource.ShouldContain("private readonly struct CommandChainResourcePlanReadScope : IDisposable");
        plannerSource.ShouldContain("Refusing lazy physical-image plan rebuild");
        plannerSource.ShouldContain("Resource planner cannot be replaced while command-chain readers are using frozen plan revision");
        loweringSource.ShouldContain("BeginCommandChainResourcePlanReadScope(resourcePlanRevision)");
        loweringSource.ShouldContain("using CommandChainResourcePlanReadScope resourcePlanReadScope");

        string ensurePhysicalImageSource = SliceBetween(
            plannerSource,
            "internal bool TryEnsurePhysicalImageForTextureResource",
            "private FrameOpContext PrepareResourcePlannerForFrameOps");
        int frozenGuardIndex = ensurePhysicalImageSource.IndexOf("if (IsCommandChainResourcePlanFrozen)", StringComparison.Ordinal);
        int updatePlannerIndex = ensurePhysicalImageSource.IndexOf("UpdateResourcePlannerFromContext(context);", StringComparison.Ordinal);
        frozenGuardIndex.ShouldBeGreaterThanOrEqualTo(0);
        updatePlannerIndex.ShouldBeGreaterThan(frozenGuardIndex);

        string plannerUpdateSource = SliceBetween(
            plannerSource,
            "private void UpdateResourcePlannerFromContext",
            "private ResourcePlanningInputs PrepareResourcePlanningInputs");
        plannerUpdateSource.ShouldContain("if (IsCommandChainResourcePlanFrozen)");
        plannerUpdateSource.IndexOf("if (IsCommandChainResourcePlanFrozen)", StringComparison.Ordinal)
            .ShouldBeLessThan(plannerUpdateSource.IndexOf("PrepareResourcePlanningInputs", StringComparison.Ordinal));
    }

    [Test]
    public void SwapchainCommandChainSecondaryRuns_CountAsActualWrites()
    {
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs")
            .Replace("\r\n", "\n");

        string meshSecondaryBlock = SliceBetween(
            commandBufferSource,
            "if (TryExecuteScheduledMeshCommandChainSecondaryRun(opIndex, meshCommandChainRunCount, opPassIndex, drawOp) ||",
            "case IndirectDrawOp indirectOp:");

        meshSecondaryBlock.ShouldContain("if (drawOp.Target is null)\n                                actualSwapchainWriteCount += meshCommandChainRunCount;");
        meshSecondaryBlock.ShouldContain("opIndex = opIndex + meshCommandChainRunCount - 1;");
    }

    [Test]
    public void DescriptorPoolRetirement_IsFrameSlotAndTimelineBased()
    {
        string queueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Retirement/VulkanResourceRetirementQueue.cs");
        string descriptorLifetimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs");
        string drainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");
        string meshCleanupSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Cleanup.cs");
        string materialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");

        queueSource.ShouldContain("List<RetiredDescriptorPool>[] DescriptorPools");
        queueSource.ShouldContain("HashSet<ulong>[] DescriptorPoolHandles");
        descriptorLifetimeSource.ShouldContain("CaptureDescriptorPoolRetirementTicket(");
        descriptorLifetimeSource.ShouldContain("int frameSlot = _resources.FramebufferRetirementFrameSlot;");
        descriptorLifetimeSource.ShouldContain("_lifetime.Retirement.DescriptorPools[frameSlot].Add(");
        descriptorLifetimeSource.ShouldContain("new RetiredDescriptorPool(descriptorPool, ticket)");
        drainSource.ShouldContain("Lifetime.Tracker.IsRetirementReady(candidate.Ticket)");
        drainSource.ShouldContain("api.DestroyDescriptorPool(device, pool, null);");
        drainSource.ShouldContain("descriptorPools: destroyed");

        string frameSlotWaitSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.FrameSlots.Retirement.cs");
        int waitIndex = frameSlotWaitSource.IndexOf("WaitForTimelineValue(_commandRuntime.Synchronization._graphicsTimelineSemaphore, slotWaitValue);", StringComparison.Ordinal);
        int drainIndex = frameSlotWaitSource.IndexOf("ResourceRuntime.DrainRetiredDescriptorPools(", StringComparison.Ordinal);
        waitIndex.ShouldBeGreaterThanOrEqualTo(0);
        drainIndex.ShouldBeGreaterThan(waitIndex);

        meshCleanupSource.ShouldContain("BackendContext.Resources.DescriptorLifetime.RetireDescriptorPool(descriptorPool);");
        materialSource.ShouldContain("BackendContext.Resources.DescriptorLifetime.RetireDescriptorPool(state.DescriptorPool);");
    }

    [Test]
    public void DesktopWindowRenderCallback_IsNonReentrantAndUsesCapturedFrameNumber()
    {
        string drawingSource = ReadVulkanDesktopFrameLoopSources();
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.cs");

        stateSource.ShouldContain("DesktopFrameActivityState _activity");
        stateSource.ShouldContain("internal bool TryEnter(out DesktopFrameIdentity identity)");
        stateSource.ShouldContain("internal void Exit(in DesktopFrameIdentity identity)");
        drawingSource.ShouldContain("out DesktopFrameIdentity desktopFrameIdentity");
        drawingSource.ShouldContain("Skipping reentrant desktop window render callback");
        drawingSource.ShouldContain("VulkanFrameAttempt attempt = new(in identity);");
        drawingSource.ShouldContain("RunDesktopFramePreflight(ref attempt)");
        drawingSource.ShouldContain("PrepareDesktopFrameSlot(ref attempt)");
        drawingSource.ShouldContain("AcquireDesktopSwapchainImageCore(");
        drawingSource.ShouldContain("RecordDesktopFrame(ref attempt)");
        drawingSource.ShouldContain("SubmitDesktopFrame(ref attempt)");
        drawingSource.ShouldContain("PresentSubmittedDesktopFrame(ref attempt)");
        drawingSource.ShouldContain("Exit(in desktopFrameIdentity);");
        drawingSource.ShouldNotContain("_windowRenderCallbackInProgress");
        string lifecycleSources = ReadVulkanDesktopFrameLoopSources();
        lifecycleSources.ShouldContain("[Vulkan] Frame={0} WindowFB={1}x{2} Swapchain={3}x{4}");
        lifecycleSources.ShouldContain("[Vulkan] Frame={0} InFlightSlot={1} AcquiredImage={2} LastPresented={3}");
        lifecycleSources.ShouldContain("[Vulkan] Frame={0} SubmittedImage={1}");
    }

    [Test]
    public void CommandRecording_ReusesPerFrameScratchCollections()
    {
        string commandBufferSource =
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecordingScratch.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOpDiagnostics.cs");
        string frameOpSource =
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/FrameOpSignatureHasher.cs");
        string recordSource = SliceBetween(
            commandBufferSource,
            "private bool TryRecordCommandBuffer",
            "private void RecordClearOp");
        string drainSource = SliceBetween(
            frameOpSource,
            "internal FrameOp[] DrainFrameOps(out ulong signature)",
            "private static ulong ComputeFrameOpsSignature");

        commandBufferSource.ShouldContain("SecondaryBucketByStart { get; }");
        commandBufferSource.ShouldContain("SwapchainWritesByPipeline { get; }");
        commandBufferSource.ShouldContain("SwapchainWriterOpByPipeline { get; }");
        commandBufferSource.ShouldContain("SwapchainWriterDynamicUiDrawCountByPipeline { get; }");
        commandBufferSource.ShouldContain("MeshDrawSlotsByRenderer { get; }");
        commandBufferSource.ShouldContain("ReusableMeshDrawSlotsByRendererFamily { get; }");
        commandBufferSource.ShouldContain("DynamicUiMeshDrawSlotsByRenderer { get; }");
        commandBufferSource.ShouldContain("FboLayoutTracking { get; }");
        commandBufferSource.ShouldContain("SwapchainWriterCountSort { get; }");
        commandBufferSource.ShouldContain("SwapchainWriterSummaryBuilder { get; }");
        commandBufferSource.ShouldContain("RecordSwapchainWriterCapacityHint { get; set; }");
        commandBufferSource.ShouldContain("RecordFboLayoutCapacityHint { get; set; }");
        commandBufferSource.ShouldNotContain("VulkanDisableParallelSecondaryRecording");
        commandBufferSource.ShouldNotContain("IsParallelSecondaryCommandBufferRecordingDisabled");
        recordSource.ShouldContain("secondaryBucketByStart = recordingScratch.SecondaryBucketByStart;");
        recordSource.ShouldContain("secondaryBucketByStart.Clear();");
        recordSource.ShouldContain("secondaryBucketByStart.EnsureCapacity(Math.Max(recordingScratch.SecondaryBucketByStartCapacityHint, secondaryBuckets.Count));");
        recordSource.ShouldContain("swapchainWritesByPipeline.Clear();");
        recordSource.ShouldContain("swapchainWriterOpByPipeline.Clear();");
        recordSource.ShouldContain("meshDrawSlotsByRenderer.Clear();");
        recordSource.ShouldContain("fboLayoutTracking.Clear();");
        recordSource.ShouldContain("fboLayoutTracking.EnsureCapacity(Math.Max(1, recordingScratch.RecordFboLayoutCapacityHint));");
        recordSource.ShouldNotContain("new Dictionary<int, VulkanRenderGraphCompiler.SecondaryRecordingBucket>");
        recordSource.ShouldNotContain("Dictionary<int, int> swapchainWritesByPipeline = [];");
        recordSource.ShouldNotContain("Dictionary<XRFrameBuffer, ImageLayout[]> fboLayoutTracking = [];");
        recordSource.ShouldNotContain("BuildSwapchainWriterDetail(clear)");
        recordSource.ShouldNotContain("BuildSwapchainWriterDetail(meshDraw)");
        recordSource.ShouldNotContain("BuildSwapchainWriterDetail(indirectDraw)");
        recordSource.ShouldNotContain("BuildSwapchainWriterDetail(meshTaskDispatch)");
        recordSource.ShouldNotContain("BuildSwapchainWriterDetail(blit)");
        commandBufferSource.ShouldContain("Debug.ShouldLogEvery(summaryKey, logInterval)");
        commandBufferSource.ShouldContain("Debug.ShouldLogEvery($\"Vulkan.OnScreenDiagnostic.{GetHashCode()}\"");
        commandBufferSource.ShouldContain("AppendSwapchainWriterSummary");
        commandBufferSource.ShouldContain("AppendSwapchainWriterDetails");
        commandBufferSource.ShouldContain("AppendSwapchainWriterDetail");
        frameOpSource.ShouldContain("_drainedFrameOpsBuffer");
        frameOpSource.ShouldContain("FrameOpKindClear");
        frameOpSource.ShouldContain("GetFrameOpKindId(op)");
        frameOpSource.ShouldContain("FrameOpSignatureHasher hash = new();");
        frameOpSource.ShouldContain("internal struct FrameOpSignatureHasher");
        frameOpSource.ShouldNotContain("hash.Add(op.GetType().Name, StringComparer.Ordinal);");
        drainSource.ShouldContain("_frameOps.CopyTo(_drainedFrameOpsBuffer);");
        drainSource.ShouldNotContain("_frameOps.ToArray()");

        string drawingSource = ReadVulkanDesktopFrameLoopSources();
        string statsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.cs");
        string profileCaptureSource = ReadWorkspaceFile("XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs");
        string measureSource = ReadWorkspaceFile("Tools/Measure-GameLoopRenderPipeline.ps1");
        drawingSource.ShouldContain("GC.GetAllocatedBytesForCurrentThread()");
        drawingSource.ShouldContain("RecordVulkanRecordCommandBufferAllocation");
        statsSource.ShouldContain("VulkanRecordCommandBufferAllocatedBytes");
        profileCaptureSource.ShouldContain("vulkan_record_command_buffer_allocated_bytes");
        measureSource.ShouldContain("FailOnSteadyStateCommandBufferAllocations");
    }

    [Test]
    public void SwapchainPrimaryCommandBufferReuse_IsExplicitOptInForFrameOps()
    {
        string envSource = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        string recordingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string allocationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferAllocation.cs");

        envSource.ShouldContain("VulkanPrimaryCommandBufferReuse = \"XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE\"");
        stateSource.ShouldContain("VulkanPrimaryCommandBufferReuseEnabled");
        recordingSource.ShouldContain("VulkanPrimaryCommandBufferReuseEnabled &&");
        recordingSource.ShouldContain("bool frameOpsRequireFreshPrimary =");
        recordingSource.ShouldContain("hasStaticFrameOps && !VulkanPrimaryCommandBufferReuseEnabled;");
        recordingSource.ShouldContain("usingCommandChains && variant.FrameOpsSignature != frameOpsSignature");
        allocationSource.ShouldContain("variant.FrameOpsSignature == frameOpsSignature");
        allocationSource.ShouldContain("variant.DynamicUiSignature == dynamicUiBatchTextSignature");
        recordingSource.ShouldContain("primaryFrameStateDirty = true;");
        recordingSource.ShouldContain("primary-frame-state old=cached");
    }

    [Test]
    public void SwapchainResizeAndPresentation_HaveRecoveryAndPresentTransitionDiagnostics()
    {
        string drawingSource = ReadVulkanDesktopFrameLoopSources();
        string syncSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SyncObjects.cs");
        string win32ResizeSource = ReadWorkspaceFile("XREngine.Runtime.Platform.Desktop/Windowing/DesktopWin32ModalResizeHook.cs");
        string desktopBackendSource = ReadWorkspaceFile("XREngine.Runtime.Platform.Desktop/Windowing/DesktopSilkWindowBackend.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string resizeResourceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/RenderPipelineAntiAliasingResources.cs");

        drawingSource.ShouldContain("AcquireNextImage");
        drawingSource.ShouldContain("Result.ErrorOutOfDateKhr");
        drawingSource.ShouldContain("Result.SuboptimalKhr");
        drawingSource.ShouldContain("Result.NotReady");
        drawingSource.ShouldContain("MaxConsecutiveNotReadyBeforeRecreate");
        drawingSource.ShouldContain("ScheduleSwapchainRecreate");
        drawingSource.ShouldContain("TryRecreateSwapchainNow");
        drawingSource.ShouldContain("returned ErrorOutOfDateKhr");
        drawingSource.ShouldContain("returned SuboptimalKhr");
        drawingSource.ShouldContain("InteractiveResizeAcquireTimeoutNanoseconds");
        drawingSource.ShouldContain("acquireTimeoutNanoseconds = attempt.InteractiveResize");
        drawingSource.ShouldContain("case Result.NotReady:");
        drawingSource.ShouldContain("case Result.Timeout:");
        drawingSource.ShouldContain("AcquireNextImage returned {0} during interactive resize; skipping this repaint tick.");
        drawingSource.ShouldContain("pendingMatchesLive &&");
        drawingSource.ShouldContain("ShouldRunInteractiveSwapchainRecreate()");
        drawingSource.ShouldContain("InteractiveSwapchainRecreateMinInterval =");
        drawingSource.ShouldContain("TimeSpan.FromMilliseconds(16)");
        drawingSource.ShouldContain("HasTimelineValueCompleted(_graphicsTimelineSemaphore, slotWaitValue)");
        drawingSource.ShouldContain("VulkanResizeResourceMismatch");
        drawingSource.ShouldContain("SkippedResizeCatchUpThisFrame");
        drawingSource.ShouldContain("skipped command-chain execution this frame while resize resources catch up");
        drawingSource.ShouldNotContain("TryPreparePendingGenerationForResizeCatchUp");
        drawingSource.ShouldNotContain("FramePrepareResizeCatchUp");
        string pipelineInstanceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        pipelineInstanceSource.ShouldContain("FramePrepareResizeCatchUp");
        pipelineInstanceSource.ShouldContain("FrameSkippedForResizeCatchUp");
        pipelineInstanceSource.ShouldContain("_resizeCatchUpSkippedFrameId = RuntimeEngine.Rendering.State.RenderFrameId");
        pipelineInstanceSource.ShouldContain("return PendingGeneration is null && !_requiresManagedResourceGeneration;");
        pipelineInstanceSource.ShouldContain("legacy");
        syncSource.ShouldContain("GetSemaphoreCounterValue");
        win32ResizeSource.ShouldContain("case WmPaint when _inSizeMove:");
        win32ResizeSource.ShouldContain("case WmSizing:");
        win32ResizeSource.ShouldContain("case WmTimer when wParam == TimerId:");
        win32ResizeSource.ShouldContain("_window?.UpdateNativeResize();");
        win32ResizeSource.ShouldContain("RequestPaint();");
        win32ResizeSource.ShouldContain("_window?.EndNativeResize();");
        desktopBackendSource.ShouldContain("internal void UpdateNativeResize()");
        desktopBackendSource.ShouldContain("AssertOwnerThread();");
        desktopBackendSource.ShouldContain("_sink?.RepaintRequested();");
        win32ResizeSource.ShouldNotContain("RenderInteractiveResizeFrame");
        win32ResizeSource.ShouldNotContain("VulkanActiveSizingRenderHz");
        win32ResizeSource.ShouldNotContain("if (ApplyCoalescedClientPresentationResize(\"win32-timer\"))");
        pipelineInstanceSource.ShouldContain(
            "!ShouldDeferResourceGenerationForInteractiveWindowResize(viewport)");
        resizeResourceSource.ShouldContain("InvalidateAntiAliasingResources(instance, \"ViewportResized\")");
        resizeResourceSource.ShouldNotContain("RemoveTextureResource");
        resizeResourceSource.ShouldNotContain("RemoveFrameBufferResource");

        commandBufferSource.ShouldContain("swapchainPresentTransitions");
        commandBufferSource.ShouldContain("usedSwapchainDynamicRendering");
        commandBufferSource.ShouldContain("presentTransitions=");
        commandBufferSource.ShouldContain("expectedPresentTransitions");
        commandBufferSource.ShouldContain("expected {1}");
        commandBufferSource.ShouldContain("ImageLayout.PresentSrcKhr");
    }

    [Test]
    public void VulkanSourceContracts_AreIncludedInWindowsCiUnitTestRun()
    {
        string workflowSource = ReadWorkspaceFile(".github/workflows/windows-ci.yml");
        string projectSource = ReadWorkspaceFile("XREngine.UnitTests/XREngine.UnitTests.csproj");
        int testStepStart = workflowSource.IndexOf("- name: Run unit tests", StringComparison.Ordinal);
        testStepStart.ShouldBeGreaterThanOrEqualTo(0);
        int nextStepStart = workflowSource.IndexOf("- name: Upload test results", testStepStart, StringComparison.Ordinal);
        nextStepStart.ShouldBeGreaterThan(testStepStart);

        string testStep = workflowSource[testStepStart..nextStepStart];
        testStep.ShouldContain("dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj");
        testStep.ShouldNotContain("--filter");
        projectSource.ShouldNotContain("<Compile Remove=\"Rendering\\Vulkan");
        SourceContractWorkspace.ResolveCanonicalFile("XREngine.UnitTests/Rendering/VulkanP1ValidationTests.cs");
        SourceContractWorkspace.ResolveCanonicalFile("XREngine.UnitTests/Rendering/VulkanP0ValidationTests.cs");
        SourceContractWorkspace.ResolveCanonicalFile("XREngine.UnitTests/Rendering/VulkanTodoP2ValidationTests.cs");
    }

    [Test]
    public void OpenXrExternalIndirectDrawSnapshots_PrepareDescriptorsWhenPrewarmMisses()
    {
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs")
            .Replace("\r\n", "\n");
        string method = SliceBetween(
            meshSource,
            "internal bool TryCreatePreparedIndirectDrawSnapshot",
            "XRFrameBuffer? effectiveTarget");
        string externalTargetBranch = SliceBetween(
            method,
            "else if (Renderer.IsRenderingExternalSwapchainTarget)",
            "else\n            {");

        externalTargetBranch.ShouldContain("Renderer.BlockSynchronousResourceUploads(\"IndirectDrawSnapshot\")");
        externalTargetBranch.ShouldContain("TryReuseCapturedProgramForIndirectDrawSnapshot");
        externalTargetBranch.ShouldContain("if (!preparedForIndirect)\n                        preparedForIndirect = TryPrepareCapturedProgramForRecording");
    }

    [Test]
    public void OpenXrExternalEyes_UseIndependentPipelineCommandChains()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.XR.OpenXR/OpenXRAPI.State.cs")
            .Replace("\r\n", "\n");
        string lifecycleSource = ReadWorkspaceFile("XREngine.Runtime.XR.OpenXR/OpenXRAPI.FrameLifecycle.cs")
            .Replace("\r\n", "\n");

        stateSource.ShouldContain("private RenderPipeline? _openXrLeftRenderPipeline;");
        stateSource.ShouldContain("private RenderPipeline? _openXrRightRenderPipeline;");
        stateSource.ShouldNotContain("private RenderPipeline? _openXrRenderPipeline;");
        stateSource.ShouldContain("GetOrCreateOpenXrPipelineInSlot(sourcePipeline, stereo: false, ref _openXrRightRenderPipeline)");
        stateSource.ShouldContain("GetOrCreateOpenXrPipelineInSlot(sourcePipeline, stereo: false, ref _openXrLeftRenderPipeline)");

        lifecycleSource.ShouldContain("leftEyePipeline = GetOrCreateOpenXrPipeline(sourcePipeline, eyeIndex: 0);");
        lifecycleSource.ShouldContain("rightEyePipeline = GetOrCreateOpenXrPipeline(sourcePipeline, eyeIndex: 1);");
        lifecycleSource.ShouldContain("_openXrLeftViewport.RenderPipeline = leftEyePipeline;");
        lifecycleSource.ShouldContain("_openXrRightViewport.RenderPipeline = rightEyePipeline;");
        lifecycleSource.ShouldContain("_openXrLeftEyeCamera!.RenderPipeline = leftEyePipeline;");
        lifecycleSource.ShouldContain("_openXrRightEyeCamera!.RenderPipeline = rightEyePipeline;");
        lifecycleSource.ShouldNotContain("RenderPipeline desiredPipeline = GetOrCreateOpenXrPipeline(sourcePipeline);");
        lifecycleSource.ShouldNotContain("_openXrRightViewport.RenderPipeline = desiredPipeline;");
    }

    [Test]
    public void OpenXrSharedEyeCommands_AreSnapshotAuthority()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs")
            .Replace("\r\n", "\n");

        stateSource.ShouldContain("public bool IsRenderCommandSnapshotAuthority { get; set; } = true;");
        stateSource.ShouldContain("if (IsRenderCommandSnapshotAuthority)");
    }

    [Test]
    public void ExternalOpenXrSharedVisibility_SkipsPerEyeOcclusionRefine()
    {
        string occlusionSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Occlusion.cs")
            .Replace("\r\n", "\n");
        string telemetrySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Occlusion/OcclusionTelemetry.cs")
            .Replace("\r\n", "\n");

        string applyOcclusion = SliceBetween(
            occlusionSource,
            "private void ApplyOcclusionCulling",
            "private void LogOcclusionModeActivation");

        applyOcclusion.ShouldContain("ShouldUseExternalVrSharedVisibilityPassFilter(camera)");
        applyOcclusion.ShouldContain("Exit.ExternalVrSharedVisibility");
        applyOcclusion.ShouldContain("RecordOcclusionFrameStats(candidates, 0u, 0u, 0u);");
        applyOcclusion.ShouldContain("EGpuHiZSkipReason.ExternalVrSharedVisibility");
        telemetrySource.ShouldContain("ExternalVrSharedVisibility");
    }

    private static string SliceBetween(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Expected to find start token '{startToken}'.");

        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Expected to find end token '{endToken}' after '{startToken}'.");

        return source[start..end];
    }

    [Test]
    public void DesktopFrameLoop_PreservesQueueLabelsAndSettlesEveryDispatchBoundary()
    {
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.State.cs");
        string submission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Submission.cs");
        string recovery = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Recovery.cs");
        string recoveryBridge = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Recovery.SubmissionBridge.cs");
        string presentation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Presentation.cs");
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");

        submission.ShouldContain(
            "caller: nameof(WindowRenderCallback)");
        presentation.ShouldContain(
            "caller: nameof(WindowRenderCallback)");
        recoveryBridge.ShouldContain(
            "caller: nameof(SubmitAcquireSemaphoreBridge)");
        submission.ShouldContain(
            "ConsumedBySubmissionImagePendingPresent");
        submission.ShouldContain(
            "SubmittedDeferredFree");
        recovery.ShouldContain(
            "ConsumedBySubmissionImagePendingPresent");
        recovery.ShouldContain(
            "ConsumedByRecoveryImagePendingPresent");
        recovery.ShouldContain(
            "PresentSubmittedDesktopFrame(ref attempt)");
        presentation.ShouldContain("if (!dispatch.Dispatched");
        presentation.ShouldContain(
            "ResolveDesktopAcquireBySwapchainRecreation(");
        state.ShouldContain(
            "lock (_desktopFrameRetirementGate)");
        openXr.ShouldContain(
            "lock (_desktopFrameRetirementGate)");
    }

    private static string ReadVulkanDesktopFrameLoopSources()
        => string.Join("\n", SourceContractWorkspace.GetVulkanSourceFiles()
            .Where(file => file.RelativePath.Contains("/Frame/Loop/", StringComparison.Ordinal) &&
                file.Source.Contains("partial class VulkanFrameLoop", StringComparison.Ordinal) &&
                !file.RelativePath.Contains("OpenXR", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Source));

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);
}
