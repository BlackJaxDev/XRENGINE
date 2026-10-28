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
        string meshSource = SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string materialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string programSource = SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
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
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/State/VulkanRenderer.CommandBufferState.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Recording/VulkanRenderer.DrawAndComputeRecording.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string renderProgramSource = SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string conventionsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanMeshRenderingConventions.cs");
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
        // Graphics and compute programs share one push-constant stage-mask policy.
        conventionsSource.ShouldContain("internal const ShaderStageFlags CommonPushConstantStageFlags =");
        conventionsSource.ShouldContain("return CommonPushConstantStageFlags |");
        renderProgramSource.ShouldContain("CreateCommonPushConstantRange");
        renderProgramSource.ShouldContain("StageFlags = VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(");
        renderProgramPipelineSource.ShouldContain("CreateCommonPushConstantRange");
        renderProgramPipelineSource.ShouldContain("StageFlags = VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(");
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
        string updateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.ResourcePlanningUpdate.cs");
        string physicalPlanningSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.PhysicalResourcePlanning.cs");
        string allocatorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/VulkanResourceAllocator.cs");
        string bufferGroupSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/VulkanPhysicalBufferGroup.cs");
        string plannerUpdate = SliceBetween(
            updateSource,
            "private void UpdateResourcePlannerFromContextCore(",
            "private bool TryFreezeResourcePlannerRenderGraphPlanWithBindingRetries(");
        string commit = SliceBetween(
            physicalPlanningSource,
            "private void CommitPhysicalAllocatorPlan(",
            "private void EvictFrameOpResourcePlannerStatesReferencingAllocator(");
        string preserve = SliceBetween(
            physicalPlanningSource,
            "internal VulkanPhysicalImageGroup? PreserveAutoExposureHistory(",
            "internal static void DestroyRetainedAutoExposureHistory(");

        plannerUpdate.ShouldContain("result = _framePlanner.ApplyPhysicalResourcePlan(ref state, in request);");
        plannerUpdate.ShouldContain("RecordVulkanRetiredResourcePlanReplacement(\n                result.RetiredImageCount,\n                result.RetiredBufferCount);");
        physicalPlanningSource.ShouldContain("RetiredImageCount: retiredImageCount,");
        commit.ShouldContain("oldAllocator.TryRetirePhysicalResources");
        commit.ShouldContain("Deferring replaced physical resource plan retirement through frame-slot/timeline completion");
        // Auto-exposure history is not preserved in VR, in OpenXR, or after device loss.
        commit.ShouldContain("request.IsOpenXrOrVr);");
        preserve.ShouldContain("if (skipPreservation)\n            return null;");
        plannerUpdate.ShouldContain("RuntimeRenderingHostServices.Presentation.IsOpenXRActive || RuntimeRenderingHostServices.Presentation.IsInVR,");
        updateSource.ShouldContain("IsDeviceLost || RuntimeRenderingHostServices.Presentation.IsInVR");
        foreach (string source in new[] { plannerUpdate, commit })
        {
            source.ShouldNotContain("WaitForAllInFlightWork()");
            source.ShouldNotContain("DeviceWaitIdle()");
            source.ShouldNotContain("ForceFlushAllRetiredResourcesAfterWaiting(\"ResourcePlanReplacement\")");
            source.ShouldNotContain("TransitionNewPhysicalImagesToInitialLayout");
        }
        allocatorSource.ShouldNotContain("TransitionNewPhysicalImagesToInitialLayout");
        bufferGroupSource.ShouldContain("context.Resources.Buffers.Retire(_buffer, _memory, $\"ResourcePlanner.{Key}\");");

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
        string capabilitySource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanAutoExposureHistoryCommandCapability.cs");
        string preserveSource = SliceBetween(
            capabilitySource,
            "internal bool TryCopy(",
            "private void Transition(");

        preserveSource.ShouldContain("ImageLayout newCurrentLayout = newGroup.LastKnownLayout;");
        preserveSource.ShouldContain("newCurrentLayout == ImageLayout.Undefined ? AccessFlags.None : AccessFlags.ShaderWriteBit");
        preserveSource.ShouldContain("newCurrentLayout == ImageLayout.Undefined ? PipelineStageFlags.TopOfPipeBit : autoExposureStages");
        preserveSource.ShouldContain("newGroup.LastKnownLayout = newRestoreLayout;");
        preserveSource.ShouldContain("newGroup,\n            newCurrentLayout,\n            ImageLayout.TransferDstOptimal,");
        preserveSource.ShouldNotContain("newGroup,\n            newLayout,\n            ImageLayout.TransferDstOptimal");
    }

    [Test]
    public void ExternalSwapchainPlannerDisplayExtent_IsAuthoritativeWhileInternalExtentRemainsScaled()
    {
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string stateSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlannerContext.cs");
        string switchingSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlannerSwitching.cs");
        string plannerContextSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.PlannerContext.cs");
        string stateTrackingSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.RendererApi.cs");
        string openXrSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.cs");
        string openXrValidationSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.PrewarmValidation.cs");
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
            "internal FrameOpContext CaptureFrameOpContextForCurrentPipelineScope()");
        string resizeFreeze = SliceBetween(
            stateSource,
            "private FrameOpContext ApplyInteractiveResizePlannerFreeze",
            "private void CaptureInteractiveResizePlannerExtents");
        string extentContext = SliceBetween(
            plannerContextSource,
            "private VulkanResourceExtentContext BuildResourceExtentContext",
            "private ExternalResourcePlannerReadbackScope EnterFrameOpResourcePlannerReadbackScope");
        string externalScope = SliceBetween(
            openXrSource,
            "internal IDisposable EnterOpenXrExternalSwapchainRenderScope",
            "internal VulkanOpenXrDiagnosticsSnapshot CaptureOpenXrDiagnostics()");
        string fillProjectionView = SliceBetween(
            openXrFrameLifecycleSource,
            "private void FillProjectionView",
            "private void ValidateProjectionViewSubImage");
        string validateProjectionView = SliceBetween(
            openXrFrameLifecycleSource,
            "private void ValidateProjectionViewSubImage",
            "private void TraceProjectionViewSubImage");

        switchingSource.ShouldContain("internal bool TryResolveExternalSwapchainTargetExtent(out Extent2D extent)");
        captureContext.ShouldContain("if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))");
        captureContext.ShouldContain("ResolveExternalFrameOpResourceDimensions(");
        captureContext.ShouldContain("displayWidth = dimensions.DisplayWidth;");
        captureContext.ShouldContain("internalHeight = dimensions.InternalHeight;");
        // The interactive-resize freeze never replaces the external target extent.
        resizeFreeze.ShouldContain("if (TryResolveExternalSwapchainTargetExtent(out _) ||");
        resizeFreeze.ShouldContain("return context;");
        // The planner extent context reads the live external target region, and
        // keeps the scaled internal extent of the frame-op context as a fallback.
        extentContext.ShouldContain("if (TryGetExternalSwapchainTargetRegion(out var region) && region.Width > 0 && region.Height > 0)");
        extentContext.ShouldContain("ResolveExternalFrameOpResourceDimensions(");
        extentContext.ShouldContain("context.InternalWidth,\n                context.InternalHeight);");
        extentContext.ShouldContain("return new VulkanResourceExtentContext(\n                dimensions.DisplayWidth,\n                dimensions.DisplayHeight,\n                dimensions.InternalWidth,\n                dimensions.InternalHeight);");
        switchingSource.ShouldContain("OpenXR external swapchain rendering is active, but no valid external target extent is bound.");
        switchingSource.ShouldContain("=> TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent)\n            ? externalExtent\n            : OutputRuntime.Desktop.Extent;");
        stateTrackingSource.ShouldContain("if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))\n            return externalExtent;");
        stateTrackingSource.ShouldContain("_commandRuntime.ActiveState.SetCurrentTargetExtent(\n                ResolveUnboundDrawTargetExtent());");
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
        openXrValidationSource.ShouldContain("ValidateOpenXrExternalFrameOpContexts");
        openXrValidationSource.ShouldContain("ValidateOpenXrExternalSwapchainWriterDrawState");
        openXrValidationSource.ShouldContain("ExpectedViewportScissorCount=1");
        openXrValidationSource.ShouldContain("captured a swapchain writer that does not cover the full eye target");
        renderToWindowSource.ShouldContain("isExternalSwapchainTarget &&\n                  renderer.TryGetExternalSwapchainTargetRegion(out BoundingRectangle externalRegion)\n                    ? externalRegion\n                    : ResolveTargetRegion(instance, targetWindow!);");
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

        viewportSource.ShouldContain("meshRenderCommands: commandCollection,\n                applyRenderArea: false,\n                viewHistorySequenceId: visibilityHistorySequence,");
        renderStateSource.ShouldContain("FrameViewSet = null;");
        renderStateSource.ShouldContain("WorldSnapshot = null;");
        string viewSnapshotSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanMeshDrawViewSnapshot.cs");

        // The mesh draw view snapshot reads temporal data from its explicit
        // pipeline, never from ambient render state.
        meshSource.ShouldContain("VulkanMeshDrawViewSnapshot.Capture(");
        viewSnapshotSource.ShouldContain("VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(\n                pipeline,\n                out var temporalData)");
        meshSource.ShouldNotContain("VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData)");
        viewSnapshotSource.ShouldNotContain("VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData)");
    }

    [Test]
    public void VulkanThreadRenderStateScope_IsolatesFramebufferBindings()
    {
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string threadContextSource = ReadWorkspaceFile(vulkanRoot + "Commands/Authority/VulkanCommandThreadContext.cs");
        string threadWorkspaceSource = ReadWorkspaceFile(vulkanRoot + "Commands/Authority/VulkanCommandThreadWorkspace.cs");
        string stateTrackingSource = ReadWorkspaceFile(vulkanRoot + "Commands/Authority/VulkanCommandRuntime.FramePlanningState.cs");
        string renderScope = ReadWorkspaceFile(vulkanRoot + "Commands/Authority/VulkanOpenXrThreadRenderStateScope.cs");
        string rendererApiSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.RendererApi.cs");
        string readbackSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.Readback.cs");
        string plannerSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlannerContext.cs");

        // Each thread has its own command context. Framebuffer bindings live in
        // that context while a scope makes the command runtime their owner.
        threadWorkspaceSource.ShouldContain("private readonly ThreadLocal<VulkanCommandThreadContext> _current;");
        threadContextSource.ShouldContain("public VulkanCommandRuntime? FramebufferBindingOwner;");
        threadContextSource.ShouldContain("public XRFrameBuffer? BoundDrawFrameBuffer;");
        threadContextSource.ShouldContain("public XRFrameBuffer? BoundReadFrameBuffer;");
        threadContextSource.ShouldContain("public EReadBufferMode ReadBufferMode;");
        stateTrackingSource.ShouldContain("internal XRFrameBuffer? ActiveBoundDrawFrameBuffer");
        stateTrackingSource.ShouldContain("internal XRFrameBuffer? ActiveBoundReadFrameBuffer");
        stateTrackingSource.ShouldContain("internal EReadBufferMode ActiveReadBufferMode");
        stateTrackingSource.ShouldContain(
            "return ReferenceEquals(context.FramebufferBindingOwner, this)\n" +
            "                ? context.BoundDrawFrameBuffer\n" +
            "                : CommandBuffers.BoundDrawFrameBuffer;");
        stateTrackingSource.ShouldContain(
            "return ReferenceEquals(context.FramebufferBindingOwner, this)\n" +
            "                ? context.BoundReadFrameBuffer\n" +
            "                : CommandBuffers.BoundReadFrameBuffer;");
        stateTrackingSource.ShouldContain(
            "return ReferenceEquals(context.FramebufferBindingOwner, this)\n" +
            "                ? context.ReadBufferMode\n" +
            "                : CommandBuffers.ReadBufferMode;");

        renderScope.ShouldContain("internal readonly struct VulkanOpenXrThreadRenderStateScope : IDisposable");
        renderScope.ShouldContain("_previousFramebufferOwner = data.ThreadContext.FramebufferBindingOwner;");
        renderScope.ShouldContain("data.ThreadContext.FramebufferBindingOwner = data.Owner;");
        renderScope.ShouldContain("data.ThreadContext.BoundDrawFrameBuffer = null;");
        renderScope.ShouldContain("data.ThreadContext.ReadBufferMode = EReadBufferMode.ColorAttachment0;");
        renderScope.ShouldContain("_data.ThreadContext.FramebufferBindingOwner = _previousFramebufferOwner;");

        stateTrackingSource.ShouldContain("if (XRFrameBuffer.BoundForWriting is { } directlyBoundTarget)\n            return directlyBoundTarget;");
        stateTrackingSource.ShouldContain(": ActiveBoundDrawFrameBuffer;");
        rendererApiSource.ShouldContain("_commandRuntime.ActiveReadBufferMode = mode;");
        rendererApiSource.ShouldContain("_commandRuntime.ActiveBoundReadFrameBuffer = frameBuffer;");
        rendererApiSource.ShouldContain("_commandRuntime.ActiveBoundDrawFrameBuffer = frameBuffer;");
        rendererApiSource.ShouldContain("XRFrameBuffer? drawFrameBuffer = _commandRuntime.ActiveBoundDrawFrameBuffer;");
        readbackSource.ShouldContain("XRFrameBuffer? boundReadFrameBuffer = _commandRuntime.ActiveBoundReadFrameBuffer;");
        readbackSource.ShouldContain("_commandRuntime.ActiveReadBufferMode");
        plannerSource.ShouldContain("if (_commandRuntime.ActiveBoundDrawFrameBuffer is null)\n            _commandRuntime.ActiveState.SetCurrentTargetExtent(extent);");
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
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string renderStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs").Replace("\r\n", "\n");
        string meshRendererSource = ReadWorkspaceFile(vulkanRoot + "BackendObjects/MeshRendering/VkMeshRenderer.cs").Replace("\r\n", "\n");
        string dirtyReasonsSource = ReadWorkspaceFile(vulkanRoot + "Commands/CommandBuffers/Reuse/Invalidation/VulkanRenderer.CommandBufferDirtyReasons.cs").Replace("\r\n", "\n");
        string stateTrackingSource = ReadWorkspaceFile(vulkanRoot + "OpenXR/VulkanOpenXrRenderOperation.cs").Replace("\r\n", "\n");

        renderStateSource.ShouldContain("public bool HasRenderingCameraScope => _renderingCameras.Count > 0;");
        meshRendererSource.ShouldContain("bool explicitCameraScope = RuntimeEngine.Rendering.State.RenderingPipelineState?.HasRenderingCameraScope == true;");
        meshRendererSource.ShouldContain("XRCamera? snapshotCamera = explicitCameraScope\n            ? RuntimeEngine.Rendering.State.RenderingCamera\n            : RuntimeEngine.Rendering.State.RenderingCamera");
        meshRendererSource.ShouldContain("XRCamera? snapshotRightEyeCamera = snapshotCamera is null\n            ? null\n            : RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera");
        // Legacy mesh-state changes do not dirty reusable primaries or command chains.
        dirtyReasonsSource.ShouldContain("if (VulkanPrimaryCommandBufferReuseEnabled || CommandChainsEnabledForCurrentRecording)\n            return;");
        stateTrackingSource.ShouldContain("private readonly IDisposable _currentRendererScope;");
        stateTrackingSource.ShouldContain("_currentRendererScope = AbstractRenderer.PushThreadCurrent(renderer);");
        stateTrackingSource.ShouldContain("_currentRendererScope.Dispose();");
    }

    [Test]
    public void OpenXrDirectEyeSwapchain_UsesTrackedExternalTargetInitialLayout()
    {
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string recordingTarget = ReadWorkspaceFile(vulkanRoot + "Commands/CommandBuffers/Recording/Targets/VulkanRenderer.SwapchainRecordingTarget.cs").Replace("\r\n", "\n");
        string eyeSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.EyeRendering.cs").Replace("\r\n", "\n");
        string preparationSource = ReadWorkspaceFile(vulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs").Replace("\r\n", "\n");
        string swapchainSource = ReadWorkspaceFile(vulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Swapchain.cs").Replace("\r\n", "\n");
        string eyeRecord = SliceBetween(
            eyeSource,
            "private bool TryRecordPreparedOpenXrEyeSwapchainCommandBuffer",
            "VulkanPreparedPrimaryCommandInput commandInput = new(");

        recordingTarget.ShouldContain("ImageLayout InitialColorLayout");
        // The direct eye target starts from the layout that the submission
        // tracker holds for the external OpenXR image.
        eyeRecord.ShouldContain("_commandRuntime.Synchronization.TryGetSubmittedImageLayout(\n            targetContext.Image,\n            in colorRange,\n            out ImageLayout trackedTargetLayout);");
        eyeRecord.ShouldContain("targetContext.DepthAspect,\n            trackedTargetLayout,\n            ImageEverPresentedAtRecordStart: false);");

        preparationSource.ShouldContain("recordingState.InitialSwapchainColorLayout = recordingState.SwapchainTarget.IsValid\n                ? recordingState.SwapchainTarget.InitialColorLayout");
        preparationSource.ShouldContain("recordingState.SwapchainFinalLayout = recordingState.InitialSwapchainColorLayout;");
        swapchainSource.ShouldContain("ImageLayout oldLayout = ResolveCurrentSwapchainColorLayout(ref recordingState);");
        swapchainSource.ShouldContain("OldLayout = oldLayout");
        preparationSource.ShouldNotContain("SwapchainFinalLayout = recordingState.ImageWasEverPresentedAtRecordStart");
        preparationSource.ShouldNotContain("ImageLayout swapchainFinalLayout = imageWasEverPresentedAtRecordStart");
    }

    [Test]
    public void ParallelOpenXrPreparedEyes_OwnFrameOpArrays()
    {
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string eyeSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.EyeRendering.cs")
            .Replace("\r\n", "\n");
        string openXrSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.PrewarmValidation.cs")
            .Replace("\r\n", "\n");
        string prepareMethod = SliceBetween(
            eyeSource,
            "private bool TryPrepareOpenXrEyeSwapchainCommandBuffer",
            "private bool TryRecordPreparedOpenXrEyeSwapchainCommandBuffer");
        string cloneMethod = SliceBetween(
            openXrSource,
            "private static FrameOp[] CloneFrameOpsForPreparedOpenXrEye(FrameOp[] ops)",
            "catch");

        prepareMethod.ShouldContain("ops = CloneFrameOpsForPreparedOpenXrEye(");
        // Each prepared eye owns a new array of sealed operation copies.
        cloneMethod.ShouldContain("if (ops.Length == 0)\n            return ops;");
        cloneMethod.ShouldContain("FrameOp[] frozen = new FrameOp[ops.Length];");
        cloneMethod.ShouldContain("frozen[index] = ops[index].CreateSealedAuthoringCopy();");
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
        string stateSource = ReadExactWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourceRegistryPlanning.cs");
        string mergeSource = SliceBetween(
            stateSource,
            "internal RenderResourceRegistry? BuildMergedFrameOpRegistry",
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
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string stateSource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/Authority/ResourcePlannerRuntimeState.cs");
        string updateSource = ReadExactWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlanningUpdate.cs");
        string physicalSource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/VulkanRenderer.PhysicalResourcePlanning.cs");
        string signatureSource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/VulkanRenderer.RenderGraphRebuild.cs");
        string plannerUpdate = SliceBetween(
            updateSource,
            "private void UpdateResourcePlannerFromContextCore(",
            "private bool TryFreezeResourcePlannerRenderGraphPlanWithBindingRetries(");
        string applyPlan = SliceBetween(
            physicalSource,
            "internal VulkanPhysicalPlanningResult ApplyPhysicalResourcePlan(",
            "return new VulkanPhysicalPlanningResult(");
        string commitPlan = SliceBetween(
            physicalSource,
            "private void CommitPhysicalAllocatorPlan(",
            "if (retiredImageCount > 0 || retiredBufferCount > 0)");

        stateSource.ShouldContain("public ulong ResourcePlannerSignature;");
        stateSource.ShouldContain("public ulong ResourceAllocationSignature;");
        stateSource.ShouldContain("public ResourcePlannerFastPathKey ResourcePlannerFastPathKey;");
        stateSource.ShouldContain("public BarrierPlanFastPathKey BarrierPlanFastPathKey;");
        signatureSource.ShouldContain("internal static ulong ComputeResourceAllocationSignature(");

        plannerUpdate.ShouldContain("ResourcePlanningInputs planningInputs = PrepareResourcePlanningInputs(");
        plannerUpdate.ShouldContain("if (CanReuseResourcePlannerFastPath(planningInputs.FastPathKey))");
        plannerUpdate.ShouldContain("ulong plannerSignature = VulkanFramePlanner.ComputeResourcePlannerSignature(");
        plannerUpdate.ShouldContain("ulong allocationSignature = VulkanFramePlanner.ComputeResourceAllocationSignature(");
        plannerUpdate.ShouldContain("RememberResourcePlannerFastPath(planningInputs.FastPathKey);");
        plannerUpdate.ShouldContain("result = _framePlanner.ApplyPhysicalResourcePlan(ref state, in request);");

        // A graph-only change keeps the physical allocator. Only an allocation
        // signature change builds a new physical allocator.
        applyPlan.ShouldContain("bool allocationChanged = request.AllocationSignature != state.ResourceAllocationSignature;");
        applyPlan.ShouldContain("if (allocationChanged)");
        applyPlan.ShouldContain("if (!TryBuildPhysicalAllocator(");
        applyPlan.ShouldContain("CommitPhysicalAllocatorPlan(");
        applyPlan.ShouldContain("state.ResourceAllocationSignature = request.AllocationSignature;");
        applyPlan.ShouldContain("state.BarrierPlanner.Rebuild(");
        applyPlan.ShouldContain("!barrierKey.Matches(state.BarrierPlanFastPathKey)");
        commitPlan.ShouldContain("if (!physicalPlanChanged)\n            return;");

        string allocatorSource = ReadExactWorkspaceFile(vulkanRoot + "Resources/VulkanResourceAllocator.cs");
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
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string filterSource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/VulkanRenderer.RenderGraphRebuild.cs");
        string mutableStateSource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/Authority/VulkanFramePlannerMutableState.cs");
        string breakdownSource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/Authority/ResourcePlannerSignatureBreakdown.cs");
        string stateSource = filterSource + mutableStateSource;
        string compilerSource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/VulkanRenderGraphCompiler.cs");
        string schedulerSource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/VulkanFrameOperationScheduler.cs");
        string recordingPreparationSource = ReadExactWorkspaceFile(
            vulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs");
        string bucketBuilder = SliceBetween(
            schedulerSource,
            "public void BuildSecondaryRecordingBuckets(",
            "for (int i = 0; i < ops.Length; i++)");

        stateSource.ShouldContain("LastActiveFilterSourcePassMetadata");
        stateSource.ShouldContain("LastActiveFilterPassSetSignature");
        stateSource.ShouldContain("ComputeActivePassSetSignature");
        stateSource.ShouldContain("ComputePassMetadataRevisionStamp");
        filterSource.ShouldContain("return MutableState.LastActiveFilterResult;");
        filterSource.ShouldContain("MutableState.LastActiveFilterResult = result;");
        breakdownSource.ShouldContain("public string DescribeDelta(in ResourcePlannerSignatureBreakdown previous)");
        stateSource.ShouldNotContain("foreach (int passIndex in activePassIndices.OrderBy");
        stateSource.ShouldNotContain("foreach (RenderPassResourceUsage usage in pass.ResourceUsages\r\n                .OrderBy");
        stateSource.ShouldNotContain("foreach (RenderPassResourceUsage usage in pass.ResourceUsages\n                .OrderBy");
        compilerSource.ShouldContain("CompiledGraphCache");
        compilerSource.ShouldContain("CompiledGraphCacheEntry");
        compilerSource.ShouldContain("static _ => new CompiledGraphCacheEntry()).GetOrCompile(passMetadata);");

        // Secondary recording buckets use caller-owned scratch storage.
        bucketBuilder.ShouldContain("List<VulkanSecondaryRecordingBucket> destination)");
        bucketBuilder.ShouldContain("destination.Clear();");
        bucketBuilder.ShouldContain("List<VulkanSecondaryRecordingBucket> buckets = destination;");
        schedulerSource.ShouldNotContain("List<VulkanSecondaryRecordingBucket> buckets = [];");
        recordingPreparationSource.ShouldContain("recordingState.SecondaryBuckets =\n                recordingState.RecordingScratch.SecondaryRecordingBuckets;");
    }

    [Test]
    public void ResourcePlanner_ReusesExactCleanFramePlanWithoutSkippingMutableSafetyChecks()
    {
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string keySource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/Authority/ResourcePlannerFastPathKey.cs");
        string stateSource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/Authority/ResourcePlannerRuntimeState.cs");
        string framePlanSource = ReadExactWorkspaceFile(vulkanRoot + "Commands/FrameOps/FramePlan.RecordingResources.cs");
        string updateSource = ReadExactWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlanningUpdate.cs");
        string plannerUpdate = SliceBetween(
            updateSource,
            "private void UpdateResourcePlannerFromContextCore(",
            "private bool TryFreezeResourcePlannerRenderGraphPlanWithBindingRetries(");
        string planningInputs = SliceBetween(
            updateSource,
            "private ResourcePlanningInputs PrepareResourcePlanningInputs(",
            "private bool CanReuseResourcePlannerFastPath(");

        // The reuse key holds every mutable input. It is rebuilt from the live
        // registry, pass metadata, and queue ownership before each reuse check.
        keySource.ShouldContain("RegistryDescriptorRevision == other.RegistryDescriptorRevision &&");
        keySource.ShouldContain("RegistryInstanceRevision == other.RegistryInstanceRevision &&");
        keySource.ShouldContain("ActivePassMetadataRevision == other.ActivePassMetadataRevision &&");
        keySource.ShouldContain("QueueOwnership.Equals(other.QueueOwnership) &&");
        planningInputs.ShouldContain("context.ResourceRegistry?.DescriptorRevision ?? 0,");
        planningInputs.ShouldContain("VulkanFramePlanner.ComputePassMetadataRevisionStamp(activePassMetadata),");
        planningInputs.ShouldContain("VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership = _framePlanner.BuildQueueOwnershipConfig(");
        updateSource.ShouldContain("ActiveResourcePlannerSignature != ulong.MaxValue &&\n           key.Matches(ActiveResourcePlannerFastPathKey);");

        // Recording uses a planner generation only while it still owns a live
        // allocator.
        stateSource.ShouldContain("!ResourceAllocator.IsRetired &&");
        framePlanSource.ShouldContain("if (!state.HasLiveAllocatorOwnership)\n                return false;");

        int cleanFrameReuse = plannerUpdate.IndexOf(
            "if (CanReuseResourcePlannerFastPath(planningInputs.FastPathKey))",
            StringComparison.Ordinal);
        int fullPreparation = plannerUpdate.IndexOf(
            "result = _framePlanner.ApplyPhysicalResourcePlan(ref state, in request);",
            StringComparison.Ordinal);
        int rememberPreparedPlan = plannerUpdate.IndexOf(
            "state.ResourcePlannerFastPathKey = planningInputs.FastPathKey;",
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
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.RenderGraphRebuild.cs");
        string dataBuffer = SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

        postProcessState.ShouldNotContain("_stages.Values.FirstOrDefault");
        postProcessState.ShouldContain("foreach (PostProcessStageState stage in _stages.Values)");
        defaultPipeline.ShouldContain("for (int sourceIndex = 0; sourceIndex < probes.Count; sourceIndex++)");
        defaultPipeline.ShouldContain("for (int index = 0; index < probes.Count; index++)");
        defaultPipeline.ShouldNotContain("foreach (LightProbeComponent probe in probes)");
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

        string setUniforms = SliceBetween(
            directionalLight,
            "public override void SetUniforms(XRRenderProgram program, string? targetStructName = null)",
            "protected override void SetShadowMapUniforms(");
        string setShadowMapUniforms = SliceBetween(
            directionalLight,
            "protected override void SetShadowMapUniforms(",
            "lock (_cascadeDataLock)");

        setUniforms.ShouldContain("DirectionalLightUniformNames names = ResolveUniformNames(targetStructName);");
        // Cascade arrays are not uploaded as uniforms. The consuming pass
        // publishes them as immutable shadow records, so only the count remains.
        setUniforms.ShouldContain("Cascade payloads are published by the consuming pass as immutable");
        setUniforms.ShouldContain("names.CascadeCount,");
        setUniforms.ShouldNotContain("program.Uniform($\"");
        directionalLight.ShouldNotContain("program.Uniform($\"{flatPrefix}");
        setShadowMapUniforms.ShouldContain("if (IsVulkanDirectionalShadowBackend())\n            {\n                program.Uniform(\"CascadeLayerCount\", 0);");
        uniformNames.ShouldContain("private static readonly DirectionalLightUniformNames DefaultUniformNames");
        uniformNames.ShouldContain("UniformNamesByPrefix.GetOrAdd");
        uniformNames.ShouldContain("CascadeCount = flatPrefix + \"CascadeCount\";");
    }

    [Test]
    public void ImGuiOverlaySnapshots_ReuseGrowthOnlyStorageAndRecycleAfterRecording()
    {
        const string uiRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/";
        string cache = ReadExactWorkspaceFile(uiRoot + "VulkanImGuiDrawDataCache.cs");
        string frameSnapshot = ReadExactWorkspaceFile(uiRoot + "VulkanImGuiFrameSnapshot.cs");
        string listSnapshot = ReadExactWorkspaceFile(uiRoot + "VulkanImGuiCommandListSnapshot.cs");
        string recorder = ReadExactWorkspaceFile(uiRoot + "VulkanImGuiOverlayCommandRecorder.cs");
        string imgui = cache + frameSnapshot + listSnapshot;
        string frameRecording = ReadExactWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Recording.cs");

        cache.ShouldContain("private VulkanImGuiFrameSnapshot? _recycledSnapshot;");
        cache.ShouldContain("_recycledSnapshot ??\n                new VulkanImGuiFrameSnapshot();");
        cache.ShouldContain("snapshot.Capture(\n                drawData,");
        cache.ShouldContain("public void Recycle(VulkanImGuiFrameSnapshot? snapshot)");
        frameSnapshot.ShouldContain("for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)");
        frameSnapshot.ShouldContain("if (listIndex == CommandLists.Count)\n                CommandLists.Add(new VulkanImGuiCommandListSnapshot());");
        listSnapshot.ShouldContain("EnsureCapacity(ref _vertices, VertexCount);");
        listSnapshot.ShouldContain("EnsureCapacity(ref _indices, IndexCount);");
        listSnapshot.ShouldContain("EnsureCapacity(ref _commands, CommandCount);");
        listSnapshot.ShouldContain("if (buffer.Length >= requiredCount)\n            return;");
        recorder.ShouldContain("for (int listIndex = 0; listIndex < input.Snapshot.CommandListCount; listIndex++)");
        recorder.ShouldContain("for (int commandIndex = 0; commandIndex < list.CommandCount; commandIndex++)");
        imgui.ShouldNotContain("new ImDrawVert[cmdList.VtxBuffer.Size]");
        imgui.ShouldNotContain("new ushort[cmdList.IdxBuffer.Size]");
        imgui.ShouldNotContain("new VulkanImGuiCommandSnapshot[cmdList.CmdBuffer.Size]");
        imgui.ShouldNotContain("new ImGuiCommandSnapshot[cmdList.CmdBuffer.Size]");
        frameRecording.ShouldContain("_outputRuntime._imguiDrawData.Recycle(imguiOverlaySnapshot);");
    }

    [Test]
    public void ResourcePlanner_SwitchesPerFrameOpContextDuringPrimaryRecording()
    {
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string stateSource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/Authority/FrameOpResourcePlannerSwitchingState.cs").Replace("\r\n", "\n");
        string framePlanSource = ReadExactWorkspaceFile(vulkanRoot + "Commands/FrameOps/FramePlan.RecordingResources.cs").Replace("\r\n", "\n");
        string scopeSource = ReadExactWorkspaceFile(vulkanRoot + "Commands/Authority/VulkanPreparedResourcePlannerThreadScope.cs").Replace("\r\n", "\n");
        string plannerSource = ReadExactWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlanningUpdate.cs").Replace("\r\n", "\n");
        string keySource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/Authority/VulkanFramePlanner.ContextSignatures.cs").Replace("\r\n", "\n");
        string preparationSource = ReadExactWorkspaceFile(vulkanRoot + "Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs").Replace("\r\n", "\n");
        string commandBufferSource = ReadExactWorkspaceFile(vulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs").Replace("\r\n", "\n");
        string frameDataSource = ReadExactWorkspaceFile(vulkanRoot + "Commands/CommandBuffers/FrameData/VulkanRenderer.CommandBufferReuse.FrameData.cs").Replace("\r\n", "\n");
        string switchingSource = ReadExactWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlannerSwitching.cs").Replace("\r\n", "\n");
        string lifecycleSource = ReadExactWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs").Replace("\r\n", "\n");

        // Planner states are kept per frame-operation planner key.
        stateSource.ShouldContain("public Dictionary<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> States { get; }");
        stateSource.ShouldContain("public bool RecordingScopeActive;");
        keySource.ShouldContain("internal static VulkanFrameOpPlannerStateKey BuildFrameOpPlannerStateKey(in FrameOpContext context)");
        keySource.ShouldContain("internal static FrameOpContext SelectPrimaryPlannerContext(FrameOp[] ops)");

        plannerSource.ShouldContain("private FrameOpContext PrepareResourcePlannerForFrameOps(");
        plannerSource.ShouldContain("FrameOpContext context = VulkanFramePlanner.SelectPrimaryPlannerContext(operations);");

        // The sealed frame plan resolves one frozen planner generation for each
        // planner key before recording starts.
        preparationSource.ShouldContain("framePlan.PrepareRecordingPlannerGenerations(in plannerState);");
        framePlanSource.ShouldContain("internal void PrepareRecordingPlannerGenerations(in ResourcePlannerRuntimeState root)");
        framePlanSource.ShouldContain("firstSwitching.States.TryGetValue(key, out firstState);");
        framePlanSource.ShouldContain("_computePlannerGenerations[index] = new ResourcePlannerRuntimeGeneration(state);");
        framePlanSource.ShouldContain("VulkanFrameOpPlannerStateKey key = VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(context);");
        framePlanSource.ShouldContain("if (_computePlannerKeys[index] == key && _computePlannerGenerations[index] is { } found)");

        // Primary recording switches the thread planner scope each time the
        // active operation context changes.
        commandBufferSource.ShouldContain("if (!UpdatePrimaryResourcePlannerContext(ref state)) return false;");
        commandBufferSource.ShouldContain("state.ActiveResourcePlannerScope.Dispose();");
        commandBufferSource.ShouldContain("if (!state.FramePlan.TryGetRecordingPlannerGeneration(in state.ActiveContext, out ResourcePlannerRuntimeGeneration generation))");
        commandBufferSource.ShouldContain("state.ActiveResourcePlannerScope = new(ThreadWorkspace.Current, this, generation);");
        scopeSource.ShouldContain("context.ResourcePlannerRuntimeGeneration = preparedGeneration;");
        scopeSource.ShouldContain("context.FrameOpResourcePlannerSwitchingState =\n            scopedState.FrameOpResourcePlannerSwitchingState;");
        scopeSource.ShouldContain("_context.ResourcePlannerRuntimeGeneration = _previousPlannerGeneration;");

        frameDataSource.ShouldContain("VulkanFrameOpPlannerStateKey packetPlannerKey =\n                    packetRequest.PlannerKey;");
        frameDataSource.ShouldContain("activeRequests[packetEnd].PlannerKey == packetPlannerKey");

        switchingSource.ShouldContain("FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;");
        lifecycleSource.ShouldContain("RunCleanupStep(\"render-graph planner states\", DestroyFrameOpResourcePlannerStates, failures);");
    }

    [Test]
    public void FrameOpFrameBuffers_AreDeclaredInPlannerOverlayWithoutMutatingGenerationRegistry()
    {
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string frameOpApiSource = ReadExactWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.LegacyCommandApi.cs")
            .Replace("\r\n", "\n");
        string frameOpFactorySource = ReadExactWorkspaceFile(vulkanRoot + "Commands/Authority/VulkanCommandRuntime.FrameOperationApi.cs")
            .Replace("\r\n", "\n");
        string plannerSource = ReadExactWorkspaceFile(vulkanRoot + "RenderGraph/VulkanRenderer.ResourceRegistryPlanning.cs")
            .Replace("\r\n", "\n");
        string recordingSource = ReadExactWorkspaceFile(vulkanRoot + "Commands/Recording/VulkanRenderer.ClearAndPublishRecording.cs")
            .Replace("\r\n", "\n");
        string allVulkanSources = string.Join("\n", SourceContractWorkspace.GetVulkanSourceFiles().Select(static file => file.Source));

        frameOpApiSource.ShouldContain("internal void PublishFrameBufferAttachmentsForSampling(XRFrameBuffer frameBuffer)");
        frameOpApiSource.ShouldContain("_commandRuntime.EnqueueFrameOperation(_frameOperationQueue, VulkanCommandRuntime.CreatePublishFramebufferOperation(passIndex, frameBuffer, context), passIndex);");
        frameOpFactorySource.ShouldContain("internal static PublishFramebufferForSamplingOp CreatePublishFramebufferOperation(");
        frameOpApiSource.ShouldNotContain("EnsureFrameBufferRegistered");
        frameOpApiSource.ShouldNotContain("EnsureFrameBufferAttachmentsRegistered");

        string recordPublish = SliceBetween(
            recordingSource,
            "internal void RecordPublishFramebufferForSamplingOp",
            "private static ImageLayout ResolvePublishedSampledLayout");
        recordPublish.ShouldNotContain("EnsureFrameBufferRegistered");
        recordPublish.ShouldNotContain("EnsureFrameBufferAttachmentsRegistered");

        plannerSource.ShouldContain("AddFrameOpFrameBufferDescriptors(merged, ops);");
        plannerSource.ShouldContain("VulkanResourcePlanningCompatibility.EnrichTextureDescriptorForFrameBufferAttachment(");
        plannerSource.ShouldContain("destination.RegisterTextureDescriptor(");
        plannerSource.ShouldContain("RegisterFrameBufferDescriptor(RenderResourceDescriptorFactory.FromFrameBuffer");
        plannerSource.ShouldContain("RenderResourceLifetime.External");
        plannerSource.ShouldContain("int frameBufferDescriptorSignature = ComputeFrameOpFrameBufferDescriptorSignature(ops);");
        plannerSource.ShouldNotContain("RegisterFrameOpOutputFrameBuffer");

        // No Vulkan path may mutate the generation registry with frame-op
        // frame buffers. Blit, initialization, and registration paths included.
        allVulkanSources.ShouldNotContain("RegisterFrameOpOutputFrameBuffer");
        allVulkanSources.ShouldNotContain("EnsureFrameBufferRegistered");
        allVulkanSources.ShouldNotContain("EnsureFrameBufferAttachmentsRegistered");
        allVulkanSources.ShouldNotContain("registry.BindFrameBuffer(");
        allVulkanSources.ShouldNotContain("registry.BindTexture(");
        allVulkanSources.ShouldNotContain("registry.BindBuffer(");
    }

    [Test]
    public void CommandChainResourcePlanFreeze_PreventsPlannerMutationDuringLowering()
    {
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string stateSource = ReadWorkspaceFile(vulkanRoot + "RenderGraph/Authority/VulkanFramePlanner.cs");
        string guardSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.PlannerContext.cs");
        string switchingSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlannerSwitching.cs");
        string updateSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlanningUpdate.cs");
        string loweringSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs");
        string presentNowSource = ReadWorkspaceFile(vulkanRoot + "Frame/Loop/VulkanFrameLoop.PresentNowReadiness.cs");

        stateSource.ShouldContain("_frozenPlanReaders");
        stateSource.ShouldContain("public ulong FrozenResourcePlanRevision { get; private set; }");
        stateSource.ShouldContain("public bool IsResourcePlanFrozen => Volatile.Read(ref _frozenPlanReaders) > 0;");
        guardSource.ShouldContain("private bool IsCommandChainResourcePlanFrozen => _framePlanner.IsResourcePlanFrozen;");
        switchingSource.ShouldContain("Refusing lazy physical-image plan rebuild");
        updateSource.ShouldContain("Resource planner cannot be replaced while command-chain readers are using frozen plan revision");

        // Recording reads one immutable published planner generation for the
        // complete attempt, so planner updates cannot change the lowered plan.
        loweringSource.ShouldContain("? _framePlanner.GetPublishedResourcePlannerGeneration()");
        loweringSource.ShouldContain("? targetPreparationGeneration!.State");
        loweringSource.ShouldContain("? new VulkanFramePlanningSnapshot(\n                        plannerState.RenderGraphPlan,");
        loweringSource.ShouldContain(": acceptedPlan.FrozenPlanningSnapshot;");
        presentNowSource.ShouldContain("ResourcePlannerRuntimeGeneration plannerGeneration =\n            _framePlanner.GetPublishedResourcePlannerGeneration();");

        string ensurePhysicalImageSource = SliceBetween(
            switchingSource,
            "internal bool TryEnsurePhysicalImageForTextureResource(",
            "internal bool TryResolveExternalSwapchainTargetExtent");
        int frozenGuardIndex = ensurePhysicalImageSource.IndexOf("if (IsCommandChainResourcePlanFrozen)", StringComparison.Ordinal);
        int updatePlannerIndex = ensurePhysicalImageSource.IndexOf("UpdateResourcePlannerFromContext(context);", StringComparison.Ordinal);
        frozenGuardIndex.ShouldBeGreaterThanOrEqualTo(0);
        updatePlannerIndex.ShouldBeGreaterThan(frozenGuardIndex);

        string plannerUpdateSource = SliceBetween(
            updateSource,
            "private void UpdateResourcePlannerFromContextCore(",
            "private bool TryFreezeResourcePlannerRenderGraphPlanWithBindingRetries(");
        plannerUpdateSource.ShouldContain("if (IsCommandChainResourcePlanFrozen)");
        plannerUpdateSource.IndexOf("if (IsCommandChainResourcePlanFrozen)", StringComparison.Ordinal)
            .ShouldBeLessThan(plannerUpdateSource.IndexOf("PrepareResourcePlanningInputs", StringComparison.Ordinal));
    }
    [Test]
    public void SwapchainCommandChainSecondaryRuns_CountAsActualWrites()
    {
        string commandBufferSource = ReadExactWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs")
            .Replace("\r\n", "\n");

        string meshSecondaryBlock = SliceBetween(
            commandBufferSource,
            "TryExecuteScheduledMeshCommandChainSecondaryRun(\n                    ref state,\n                    info.OperationIndex,\n                    scheduledRunCount,",
            "if (info.BeginsRendering && !state.RenderScope.MatchesTarget(target))");

        meshSecondaryBlock.ShouldContain("if (target is null)\n                    state.ActualSwapchainWriteCount += scheduledRunCount;");
        meshSecondaryBlock.ShouldContain("return info.OperationIndex + scheduledRunCount - 1;");
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
        drawingSource.ShouldContain("VulkanDesktopFramePhaseResult recordResult = RecordDesktopFrame(\n            ref attempt);");
        drawingSource.ShouldContain("VulkanDesktopFramePhaseResult submitResult = SubmitDesktopFrame(\n            ref attempt);");
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
        const string vulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";
        string scratchSource = ReadWorkspaceFile(vulkanRoot + "Commands/CommandBuffers/Recording/Scratch/VulkanRenderer.CommandBufferRecordingScratch.cs");
        string recordSource = ReadVulkanSourcesUnder("Commands/CommandBuffers/Recording/Primary/");
        string commandBufferSource = scratchSource + recordSource;
        string diagnosticsSource = ReadWorkspaceFile(vulkanRoot + "Commands/Authority/VulkanCommandRuntime.FrameOperationDiagnostics.cs");
        string queueSource = ReadWorkspaceFile(vulkanRoot + "Commands/FrameOps/VulkanFrameOperationQueue.cs");
        string frameOpSource =
            ReadWorkspaceFile(vulkanRoot + "Commands/FrameOps/VulkanFrameOperationSemantics.cs") +
            ReadWorkspaceFile(vulkanRoot + "BackendObjects/MeshRendering/FrameOpSignatureHasher.cs");
        string signatureSource = SliceBetween(
            frameOpSource,
            "internal static ulong ComputeFrameOpsSignature(FrameOperationSequence ops)",
            "switch (header.OpCode)");
        string drainSource = SliceBetween(
            queueSource,
            "internal FrameOp[] DrainPending()",
            "internal FrameOp[] DrainForPrimary(");

        scratchSource.ShouldContain("SecondaryBucketByStart { get; }");
        scratchSource.ShouldContain("SwapchainWritesByPipeline { get; }");
        scratchSource.ShouldContain("SwapchainWriterOpIndexByPipeline { get; }");
        scratchSource.ShouldContain("SwapchainWriterDynamicUiDrawCountByPipeline { get; }");
        scratchSource.ShouldContain("MeshDrawSlotsByRenderer { get; }");
        scratchSource.ShouldContain("ReusableMeshDrawSlotsByRendererFamily { get; }");
        scratchSource.ShouldContain("DynamicUiMeshDrawSlotsByRenderer { get; }");
        scratchSource.ShouldContain("FboLayoutTracking { get; }");
        scratchSource.ShouldContain("SwapchainWriterCountSort { get; }");
        scratchSource.ShouldContain("SwapchainWriterSummaryBuilder { get; }");
        scratchSource.ShouldContain("RecordSwapchainWriterCapacityHint { get; set; }");
        scratchSource.ShouldContain("RecordFboLayoutCapacityHint { get; set; }");
        commandBufferSource.ShouldNotContain("VulkanDisableParallelSecondaryRecording");
        commandBufferSource.ShouldNotContain("IsParallelSecondaryCommandBufferRecordingDisabled");
        recordSource.ShouldContain("recordingState.SecondaryBucketByStart = recordingState.RecordingScratch.SecondaryBucketByStart;");
        recordSource.ShouldContain("recordingState.SecondaryBucketByStart.Clear();");
        recordSource.ShouldContain("recordingState.SecondaryBucketByStart.EnsureCapacity(Math.Max(recordingState.RecordingScratch.SecondaryBucketByStartCapacityHint, recordingState.SecondaryBuckets.Count));");
        recordSource.ShouldContain("recordingState.SwapchainWritesByPipeline.Clear();");
        recordSource.ShouldContain("recordingState.SwapchainWriterOpIndexByPipeline.Clear();");
        recordSource.ShouldContain("meshDrawSlotsByRenderer.Clear();");
        recordSource.ShouldContain("recordingState.FboLayoutTracking.Clear();");
        recordSource.ShouldContain("recordingState.FboLayoutTracking.EnsureCapacity(Math.Max(1, recordingState.RecordingScratch.RecordFboLayoutCapacityHint));");
        recordSource.ShouldNotContain("new Dictionary<int, VulkanSecondaryRecordingBucket>");
        recordSource.ShouldNotContain("new Dictionary<int, VulkanRenderGraphCompiler.SecondaryRecordingBucket>");
        recordSource.ShouldNotContain("Dictionary<int, int> swapchainWritesByPipeline = [];");
        recordSource.ShouldNotContain("Dictionary<XRFrameBuffer, ImageLayout[]> fboLayoutTracking = [];");
        recordSource.ShouldNotContain("BuildSwapchainWriterDetail(");
        commandBufferSource.ShouldContain("Debug.ShouldLogEvery(summaryKey, logInterval)");
        commandBufferSource.ShouldContain("Debug.ShouldLogEvery($\"Vulkan.OnScreenDiagnostic.{GetHashCode()}\"");
        commandBufferSource.ShouldContain("AppendSwapchainWriterSummary");
        commandBufferSource.ShouldContain("AppendSwapchainWriterDetails");
        diagnosticsSource.ShouldContain("AppendSwapchainWriterDetail");
        queueSource.ShouldContain("public FrameOp[] DrainedFrameOpsBuffer { get; set; } = [];");
        // Operation signatures hash the operation code, not the type name.
        signatureSource.ShouldContain("FrameOpSignatureHasher hash = new();");
        signatureSource.ShouldContain("hash.Add((int)header.OpCode);");
        frameOpSource.ShouldContain("internal struct FrameOpSignatureHasher");
        signatureSource.ShouldNotContain("hash.Add(op.GetType().Name, StringComparer.Ordinal);");
        drainSource.ShouldContain("if (DrainedFrameOpsBuffer.Length != operationCount)");
        drainSource.ShouldContain("DrainedFrameOpsBuffer[drainedIndex++] = operation;");
        drainSource.ShouldNotContain("Pending.ToArray()");

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
        const string commandBufferRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/";
        string envSource = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string stateSource = ReadExactWorkspaceFile(commandBufferRoot + "State/VulkanRenderer.CommandBufferState.cs");
        string recordingSource = ReadExactWorkspaceFile(commandBufferRoot + "Recording/VulkanCommandRuntime.PrimaryRecording.cs");
        string frameDataSource = ReadExactWorkspaceFile(commandBufferRoot + "FrameData/VulkanRenderer.CommandBufferReuse.FrameData.cs");
        string secondarySource = ReadExactWorkspaceFile(commandBufferRoot + "Recording/Secondary/VulkanRenderer.SecondaryCommandBuffers.cs");
        string dependencySource = ReadExactWorkspaceFile(commandBufferRoot + "Reuse/Dependencies/VulkanRenderer.CommandBufferReuse.Dependencies.cs");

        // The environment variable and the rendering setting select primary reuse.
        envSource.ShouldContain("VulkanPrimaryCommandBufferReuse = \"XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE\"");
        stateSource.ShouldContain("internal bool VulkanPrimaryCommandBufferReuseEnabled =>");
        stateSource.ShouldContain("(VulkanPrimaryCommandBufferReuseOverride ??\n             RuntimeRenderingHostServices.Settings.EnableVulkanPrimaryCommandBufferReuse);");

        // Frame ops reuse a cached primary only when reuse is enabled and the
        // command-chain schedule does not require a fresh primary.
        recordingSource.ShouldContain("VulkanPrimaryCommandBufferReuseEnabled &&\n            CommandChainsEnabledForCurrentRecording &&");
        recordingSource.ShouldContain("!input.CommandChainSchedule.RequiresFreshPrimary &&");
        frameDataSource.ShouldContain("if (variant.FrameOpsSignature != frameOpsSignature ||");
        frameDataSource.ShouldContain("=> variant.DynamicUiSignature != dynamicUiBatchTextSignature ||");
        secondarySource.ShouldContain("variant.DynamicUiSignature == dynamicUiBatchTextSignature");
        dependencySource.ShouldContain("if (primaryFrameStateDirty)");
        dependencySource.ShouldContain("primary-frame-state old=cached");
    }

    [Test]
    public void SwapchainResizeAndPresentation_HaveRecoveryAndPresentTransitionDiagnostics()
    {
        string drawingSource = ReadVulkanDesktopFrameLoopSources();
        string syncSource = ReadExactWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Synchronization.cs");
        string win32ResizeSource = ReadWorkspaceFile("XREngine.Runtime.Platform.Desktop/Windowing/DesktopWin32ModalResizeHook.cs");
        string desktopBackendSource = ReadWorkspaceFile("XREngine.Runtime.Platform.Desktop/Windowing/DesktopSilkWindowBackend.cs");
        string commandBufferSource = ReadVulkanSourcesUnder("Commands/CommandBuffers/Recording/Primary/");
        string acquireAvailabilitySource = ReadExactWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Desktop/Runtime/VulkanDesktopAcquireAvailabilityTracker.cs");
        string resizeResourceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/RenderPipelineAntiAliasingResources.cs");

        drawingSource.ShouldContain("AcquireNextImage");
        drawingSource.ShouldContain("Result.ErrorOutOfDateKhr");
        drawingSource.ShouldContain("Result.SuboptimalKhr");
        drawingSource.ShouldContain("Result.NotReady");
        acquireAvailabilitySource.ShouldContain("internal const int DefaultRecreateThreshold = 3;");
        drawingSource.ShouldContain("VulkanDesktopAcquireAvailabilityTracker\n                    .DefaultRecreateThreshold);");
        drawingSource.ShouldContain("AcquireNextImage returned {0} {1} consecutive times. Recreating swapchain to recover.");
        drawingSource.ShouldContain("ScheduleSwapchainRecreate");
        drawingSource.ShouldContain("TryRecreateSwapchainNow");
        drawingSource.ShouldContain("returned ErrorOutOfDateKhr");
        drawingSource.ShouldContain("returned SuboptimalKhr");
        drawingSource.ShouldContain("InteractiveResizeAcquireTimeoutNanoseconds");
        drawingSource.ShouldContain("acquireTimeoutNanoseconds = attempt.InteractiveResize");
        drawingSource.ShouldContain("case Result.NotReady:");
        drawingSource.ShouldContain("case Result.Timeout:");
        drawingSource.ShouldContain("AcquireNextImage returned {0} during {1}; deferring desktop output without blocking the XR/resize critical path.");
        // Interactive resize freezes the published swapchain. The swapchain is
        // recreated after the resize when the coalesced extent matches.
        drawingSource.ShouldContain("if (interactiveResize)\n                return false;");
        drawingSource.ShouldContain("Freezing the published swapchain/resource generation during interactive resize.");
        drawingSource.ShouldContain("if (pendingMatchesLive)\n            {\n                TryRecreateSwapchainNow(");
        drawingSource.ShouldContain("SwapchainRecreateDebounce =");
        drawingSource.ShouldContain("TimeSpan.FromMilliseconds(16)");
        drawingSource.ShouldContain("HasTimelineValueCompleted(_commandRuntime.Synchronization._graphicsTimelineSemaphore, slotWaitValue)");
        drawingSource.ShouldContain("VulkanResizeResourceMismatch");
        drawingSource.ShouldContain("SkippedResizeCatchUpThisFrame");
        drawingSource.ShouldContain("skipped command-chain execution this frame while resize resources catch up");
        drawingSource.ShouldNotContain("TryPreparePendingGenerationForResizeCatchUp");
        drawingSource.ShouldNotContain("FramePrepareResizeCatchUp");
        string pipelineInstanceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        pipelineInstanceSource.ShouldContain("FramePrepareResizeCatchUp");
        pipelineInstanceSource.ShouldContain("FrameSkippedForResizeCatchUp");
        pipelineInstanceSource.ShouldContain("_resizeCatchUpSkippedFrameId = RuntimeEngine.Rendering.State.RenderFrameId");
        pipelineInstanceSource.ShouldContain("return PendingGeneration is null &&\n                _requiresManagedResourceGeneration == false &&\n                _classifiedResourceLayoutKey == key;");
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
            "ShouldDeferResourceGenerationForInteractiveWindowResize(viewport) &&\n            ActiveGeneration is { } dragGeneration &&");
        pipelineInstanceSource.ShouldContain("DiscardPendingGeneration(\"InteractiveResize\");");
        resizeResourceSource.ShouldContain("InvalidateAntiAliasingResources(instance, \"ViewportResized\")");
        resizeResourceSource.ShouldNotContain("RemoveTextureResource");
        resizeResourceSource.ShouldNotContain("RemoveFrameBufferResource");

        commandBufferSource.ShouldContain("recordingState.SwapchainPresentTransitions++;");
        commandBufferSource.ShouldContain("recordingState.UsedSwapchainDynamicRendering = true;");
        commandBufferSource.ShouldContain("if (recordingState.UsedSwapchainDynamicRendering && recordingState.SwapchainPresentTransitions != expectedPresentTransitions)");
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
        const string meshRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/";
        string meshSource = ReadExactWorkspaceFile(meshRoot + "VkMeshRenderer.cs").Replace("\r\n", "\n");
        string preparationSource = ReadExactWorkspaceFile(meshRoot + "VkMeshRenderer.Preparation.cs").Replace("\r\n", "\n");
        string drawingSource = ReadExactWorkspaceFile(meshRoot + "VkMeshRenderer.Drawing.cs").Replace("\r\n", "\n");
        string method = SliceBetween(
            meshSource,
            "internal bool TryCreatePreparedIndirectDrawSnapshot",
            "XRFrameBuffer? effectiveTarget");
        string enqueuePreparation = SliceBetween(
            preparationSource,
            "private bool TryPrepareCapturedProgramForIndirectDrawEnqueue(",
            "BuildPrepareSuccessDetail(\"DeferredUntilRecording\")");
        string recordingPreparation = SliceBetween(
            preparationSource,
            "private bool TryPrepareCapturedProgramForRecording(",
            "private bool TryPrepareCapturedProgramForIndirectDrawEnqueue(");
        string indirectRecordingState = SliceBetween(
            drawingSource,
            "internal bool TryPrepareIndirectDrawRecordingState(",
            "VkDataBuffer? indirectIndices");

        // Snapshot capture runs before the frame slot publishes its descriptor
        // authority, for desktop and external OpenXR targets alike. Capture
        // prepares only the program and geometry, and never descriptors.
        method.ShouldContain("if (!TryPrepareCapturedProgramForIndirectDrawEnqueue(");
        method.ShouldNotContain("TryPrepareCapturedProgramForRecording");
        method.ShouldNotContain("TryEnsureDescriptorSetsForPreparation");
        enqueuePreparation.ShouldContain("if (!ActivateCapturedProgram(");
        enqueuePreparation.ShouldNotContain("TryEnsureDescriptorSetsForPreparation");

        // Recording repeats the captured-program preparation and resolves the
        // descriptors under the prepared authority, so a prewarm miss still
        // gets descriptors.
        recordingPreparation.ShouldContain("if (!TryEnsureDescriptorSetsForPreparation(");
        recordingPreparation.ShouldContain("return SetPrepareResult(false, \"DescriptorsPending\", descriptorDetail, out reason);");
        indirectRecordingState.ShouldContain("bool preparedForRecord = draw.PreparedProgram is { } preparedProgram\n\t\t\t? TryPrepareCapturedProgramForRecording(");
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
        const string frameLoopRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/";
        string state = ReadExactWorkspaceFile(frameLoopRoot + "Authority/VulkanFrameLoop.cs");
        string submission = ReadExactWorkspaceFile(frameLoopRoot + "VulkanRenderer.FrameLoop.Submission.cs");
        string recovery = ReadExactWorkspaceFile(frameLoopRoot + "VulkanRenderer.FrameLoop.Recovery.cs");
        string recoverySubmission = ReadExactWorkspaceFile(frameLoopRoot + "VulkanRenderer.FrameLoop.Recovery.Submission.cs");
        string recoveryBridge = ReadExactWorkspaceFile(frameLoopRoot + "VulkanRenderer.FrameLoop.Recovery.SubmissionBridge.cs");
        string presentation = ReadExactWorkspaceFile(frameLoopRoot + "VulkanRenderer.FrameLoop.Presentation.cs");
        string openXr = ReadExactWorkspaceFile(frameLoopRoot + "Authority/VulkanFrameLoop.OpenXR.ResourcesPressure.cs");
        string retirementScope = ReadExactWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Desktop/Runtime/VulkanDesktopFrameRetirementScope.cs");

        // The frame loop is a separate type from the renderer, so the queue
        // label names the renderer callback with a literal string.
        submission.ShouldContain(
            "caller: \"RenderFrameCallback\");");
        presentation.ShouldContain(
            "caller: \"RenderFrameCallback\");");
        recoveryBridge.ShouldContain(
            "nameof(SubmitAcquireSemaphoreBridge));");
        submission.ShouldContain(
            "ConsumedBySubmissionImagePendingPresent");
        submission.ShouldContain(
            "SubmittedDeferredFree");
        recovery.ShouldContain(
            "ConsumedBySubmissionImagePendingPresent");
        recovery.ShouldContain(
            "ConsumedByRecoveryImagePendingPresent");
        recoverySubmission.ShouldContain(
            "ConsumedByRecoveryImagePendingPresent");
        recovery.ShouldContain(
            "PresentSubmittedDesktopFrame(ref attempt)");
        presentation.ShouldContain("dispatch.Dispatched &&\n                VulkanWsiPresentResult.EnqueuesPresentationRelease(result);");
        presentation.ShouldContain("if (!presentationReleaseEnqueued &&");
        presentation.ShouldContain(
            "ResolveDesktopAcquireBySwapchainRecreation(");
        state.ShouldContain(
            "lock (_retirementGate)");
        state.ShouldContain(
            "internal object RetirementGate => _retirementGate;");
        openXr.ShouldContain(
            "using VulkanDesktopFrameRetirementScope retirement =\n            new(_commandRuntime, RetirementGate);");
        retirementScope.ShouldContain("Monitor.Enter(gate);");
        retirementScope.ShouldContain("=> Monitor.Exit(_gate);");
    }

    private static string ReadVulkanDesktopFrameLoopSources()
        => string.Join("\n", SourceContractWorkspace.GetVulkanSourceFiles()
            .Where(file => file.RelativePath.Contains("/Frame/Loop/", StringComparison.Ordinal) &&
                file.Source.Contains("partial class VulkanFrameLoop", StringComparison.Ordinal) &&
                !file.RelativePath.Contains("OpenXR", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Source));

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    private static string ReadExactWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadExactFile(relativePath);

    /// <summary>
    /// Reads every Vulkan source file whose path below the Vulkan source root
    /// starts with the given prefix. The prefix must match at least one file.
    /// </summary>
    private static string ReadVulkanSourcesUnder(string vulkanRelativePrefix)
    {
        string prefix = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/" + vulkanRelativePrefix;
        string[] sources =
        [
            .. SourceContractWorkspace.GetVulkanSourceFiles()
                .Where(file => file.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
                .Select(static file => file.Source),
        ];
        sources.Length.ShouldBeGreaterThan(0, $"No Vulkan source file starts with '{prefix}'.");
        return string.Join("\n", sources);
    }
}
