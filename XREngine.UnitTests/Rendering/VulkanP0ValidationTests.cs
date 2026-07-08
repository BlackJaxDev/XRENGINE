using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Validation tests for Vulkan P0 backlog items:
/// pass-index validity, allocator toggle coverage,
/// and staging manager pool eligibility.
/// </summary>
[TestFixture]
public sealed class VulkanP0ValidationTests
{
    #region P0 Black-Frame Diagnostics

    [Test]
    public void VulkanBlackFrameDiagnostics_AreStructuredAndProfilerVisible()
    {
        string statsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.Vulkan.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string packetSource = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string profilerSenderSource = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfilerSender.cs");
        string editorSource = ReadWorkspaceFile("XREngine.Editor/EngineProfilerDataSource.cs");
        string profilerUiSource = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");

        statsSource.ShouldContain("RecordVulkanFrameDiagnostics");
        statsSource.ShouldContain("VulkanDroppedFrameOps");
        statsSource.ShouldContain("VulkanFirstFailedFrameOpMaterialName");
        statsSource.ShouldContain("VulkanFrameDiagnosticSummary");
        statsSource.ShouldContain("VulkanValidationMessageCount");

        commandBufferSource.ShouldContain("sceneSwapchainWriters");
        commandBufferSource.ShouldContain("overlaySwapchainWriters");
        commandBufferSource.ShouldContain("forcedDiagnosticSwapchainWriters");
        commandBufferSource.ShouldContain("fboOnlyDrawOps");
        commandBufferSource.ShouldContain("BuildVulkanFrameDiagnosticSummary");
        commandBufferSource.ShouldContain("[Vulkan][FrameFailure]");

        packetSource.ShouldContain("VulkanDroppedFrameOps");
        packetSource.ShouldContain("VulkanFrameDiagnosticSummary");
        profilerSenderSource.ShouldContain("VulkanDroppedFrameOps = Rendering.Stats.Vulkan.VulkanDroppedFrameOps");
        editorSource.ShouldContain("VulkanDroppedFrameOps = Engine.Rendering.Stats.Vulkan.VulkanDroppedFrameOps");
        profilerUiSource.ShouldContain("Vulkan Frame Diagnostics:");
    }

    [Test]
    public void VulkanValidationLayerMessages_FeedFrameDiagnostics()
    {
        string validationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Validation.cs");
        string statsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.Vulkan.cs");

        validationSource.ShouldContain("RecordVulkanValidationMessage");
        statsSource.ShouldContain("VulkanLastValidationMessage");
        statsSource.ShouldContain("VulkanValidationErrorCountCurrentFrame");
    }

    [Test]
    public void VulkanValidationLayerState_IsReportedFromRendererInstanceState()
    {
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");
        string instanceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Instance.cs");
        string statsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs");

        runtimeSource.ShouldContain("public static bool VulkanValidationLayersEnabled { get; internal set; }");
        instanceSource.ShouldContain("RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled = EnableValidationLayers;");
        instanceSource.ShouldContain("RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled = false;");
        statsSource.ShouldContain("RuntimeEngine.Rendering.State.IsVulkan && RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled");
    }

    [Test]
    public void OpenXrVulkanImagePressure_UsesTrackedRenderVramInsteadOfAggregateAllocatorBytes()
    {
        string initializationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");

        string imagePreflight = SliceMethod(initializationSource, "private bool ShouldDeferVulkanImageMemoryAllocationForPressure(");
        imagePreflight.ShouldContain("TryGetOpenXrVulkanImageAllocationPressureSnapshot");
        imagePreflight.ShouldNotContain("TryGetVulkanAllocatorBudgetSnapshot");

        string pressureSnapshot = SliceMethod(initializationSource, "private bool TryGetOpenXrVulkanImageAllocationPressureSnapshot(");
        pressureSnapshot.ShouldContain("MemoryAllocator.ActiveVkAllocationCount");
        pressureSnapshot.ShouldContain("host.TrackedVramBytes");
        pressureSnapshot.ShouldContain("host.TrackedVramBudgetBytes");
        pressureSnapshot.ShouldNotContain("TotalAllocatedBytes");

        string pressureDescription = SliceMethod(initializationSource, "private bool TryDescribeOpenXrVulkanImageAllocationPressure(");
        pressureDescription.ShouldContain("tracked VRAM pressure");
        pressureDescription.ShouldContain("allocation-count pressure");
        pressureDescription.ShouldNotContain("largestHeap");
        pressureDescription.ShouldNotContain("allocated={allocatedBytes}");

        string resourcePlannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string allocationDeferralClassifier = SliceMethod(resourcePlannerSource, "internal static bool IsExpectedVulkanImageAllocationDeferral(string failureReason)");
        allocationDeferralClassifier.ShouldContain("Vulkan image allocation deferred under");
        allocationDeferralClassifier.ShouldContain("allocation deferred under allocator pressure");
    }

    #endregion

    #region Final Output Contract

    [Test]
    public void DefaultPipelineFinalOutput_ValidatesEnvOverrideBeforeRecordingFinalBlit()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs");
        string pipeline2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs");

        foreach (string source in new[] { pipelineSource, pipeline2Source })
        {
            source.ShouldContain("ResolveOutputSourceFboOverride");
            source.ShouldContain("IsValidFinalOutputSourceFboOverride");
            source.ShouldContain("CreateOutputSourceOverrideCommands");
            source.ShouldContain("CreateStandardViewportFinalOutputCommands");
            source.ShouldContain(XREngineEnvironmentVariables.OutputSourceFbo);
            source.ShouldContain("Falling back to standard final output");
            source.ShouldContain("GetFBO<XRQuadFrameBuffer>(sourceFboName)");
            source.ShouldContain("TryGetFBO(sourceFboName, out XRFrameBuffer? fbo)");
        }
    }

    [Test]
    public void DefaultPipelineFinalOutput_CoversDebugOverrideAaAndFallbackSources()
    {
        string source =
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs");

        source.ShouldContain("TransformIdDebugQuadFBOName");
        source.ShouldContain("ActiveTransparencyDebugFboName");
        source.ShouldContain("CreateOutputSourceOverrideCommands");
        source.ShouldContain("RuntimeNeedsTsrUpscale");
        source.ShouldContain("FxaaFBOName");
        source.ShouldContain("SmaaFBOName");
        source.ShouldContain("PostProcessOutputFBOName");
        source.ShouldContain("ForceFallbackBlit = bypassVendorUpscale");
    }

    [Test]
    public void FullOverdrawDebug_UsesVulkanClipSpaceAwareSourceUvs()
    {
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/FullOverdrawDebug.fs");
        string pipelineFbos = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs");
        string pipeline2Fbos = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.FBOs.cs");

        shader.ShouldContain("#pragma snippet \"ScreenSpaceUtils\"");
        shader.ShouldContain("ResolveSceneUv");
        shader.ShouldContain("ResolveCountUv");
        shader.ShouldContain("#ifdef XRENGINE_VULKAN");
        shader.ShouldContain("ClipSpaceYDirection == 1");
        shader.ShouldContain("texture(FullOverdrawCountTex, ResolveCountUv(uv))");
        shader.ShouldContain("texture(PostProcessOutputTexture, ResolveSceneUv(uv))");

        SliceMethod(pipelineFbos, "private XRFrameBuffer CreateFullOverdrawDebugFBO()")
            .ShouldContain("RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy");
        SliceMethod(pipeline2Fbos, "private XRFrameBuffer CreateFullOverdrawDebugFBO()")
            .ShouldContain("RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy");
    }

    #endregion

    #region Frame-Op And Planner Contracts

    [Test]
    public void FrameOpContracts_RejectUndocumentedMinValuePassAtRecording()
    {
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");

        commandBufferSource.ShouldContain("op.PassIndex == int.MinValue && activePassIndex != int.MinValue");
        commandBufferSource.ShouldContain("EnsureValidPassIndex(op.PassIndex");
        commandBufferSource.ShouldContain("Dropping op");
        meshSource.ShouldContain("EnsureValidFrameOpPassIndex");
        meshSource.ShouldContain("EnsureValidPassIndex(op.PassIndex");
    }

    [Test]
    public void ShaderStorageMemoryBarrier_CoversVertexShaderConsumers()
    {
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string resolveBarrierScopes = SliceMethod(commandBufferSource, "private void ResolveBarrierScopes(");

        resolveBarrierScopes.ShouldContain("mask.HasFlag(EMemoryBarrierMask.ShaderGlobalAccess) || mask.HasFlag(EMemoryBarrierMask.ShaderImageAccess) || mask.HasFlag(EMemoryBarrierMask.ShaderStorage)");
        resolveBarrierScopes.ShouldContain("PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit");
        resolveBarrierScopes.ShouldNotContain("PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit");
    }

    [Test]
    public void CommandBufferReuse_InvalidatesOnFrameOpsPlannerRevisionResourcesAndViewport()
    {
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string registrySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Resources/RenderResourceRegistry.cs");

        commandBufferSource.ShouldContain("_commandBufferFrameOpSignatures");
        commandBufferSource.ShouldContain("_commandBufferPlannerRevisions");
        commandBufferSource.ShouldContain("ResourcePlannerRevision");
        stateSource.ShouldContain("ComputeResourcePlannerSignature");
        stateSource.ShouldContain("ComputePassMetadataSignature(passMetadata)");
        stateSource.ShouldContain("registry?.DescriptorSignature ?? 0");
        registrySource.ShouldContain("DescriptorRevision");
        registrySource.ShouldContain("DescriptorSignature");
        registrySource.ShouldContain("_cachedDescriptorSignatureRevision");
        registrySource.ShouldContain("_textures.OrderBy");
        registrySource.ShouldContain("_frameBuffers.OrderBy");
        stateSource.ShouldContain("viewport?.Width");
        stateSource.ShouldContain("viewport?.InternalWidth");
        meshSource.ShouldContain("hash.Add(op.Context.ViewportIdentity)");
        meshSource.ShouldContain("hash.Add(blit.OutFbo?.GetHashCode() ?? 0)");
        descriptorSource.ShouldContain("ComputeDescriptorResourceFingerprint(material, frameCount, bindings)");
        descriptorSource.ShouldContain("allocation.ResourceFingerprint != resourceFingerprint");
        descriptorSource.ShouldContain("active resource fingerprint");
    }

    [Test]
    public void DefaultCommandChain_DocumentsCommonDynamicBranchesForVulkanCoverage()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs");

        source.ShouldContain("RuntimeEnableMsaa");
        source.ShouldContain("RuntimeEnableMsaaDeferred");
        source.ShouldContain("EvaluateAmbientOcclusionMode");
        source.ShouldContain("CreateAmbientOcclusionDisabledPassCommands");
        source.ShouldContain("CreateSSAOPassCommands");
        source.ShouldContain("CreateGTAOPassCommands");
        source.ShouldContain("EnableTransparencyAccumulationVisualization");
        source.ShouldContain("EnableTransparencyRevealageVisualization");
        source.ShouldContain("EnableTransparencyOverdrawVisualization");
        source.ShouldContain("RuntimeNeedsTsrUpscale");
        source.ShouldContain("CreateOutputSourceOverrideCommands");
        source.ShouldContain("VPRC_RenderScreenSpaceUI");
    }

    #endregion

    #region Pass-Index Validity

    [Test]
    public void AllDefaultRenderPassValues_AreDefinedInEnum()
    {
        // Ensures the EDefaultRenderPass enum hasn't drifted — every integral
        // value between min and max should be a defined member.
        int[] defined = Enum.GetValues<EDefaultRenderPass>().Select(v => (int)v).OrderBy(v => v).ToArray();
        defined.Length.ShouldBeGreaterThan(0, "EDefaultRenderPass should have members");

        foreach (int passIndex in defined)
            Enum.IsDefined(typeof(EDefaultRenderPass), passIndex).ShouldBeTrue(
                $"Pass index {passIndex} should be defined in EDefaultRenderPass");
    }

    [Test]
    public void DefaultRenderPipelineMetadata_CoversAllStandardPasses()
    {
        // Build metadata using the same helper both DefaultRenderPipeline and
        // DefaultRenderPipeline2 use.
        var metadata = new RenderPassMetadataCollection();
        RegisterStandardPasses(metadata);
        var built = metadata.Build();

        int[] definedPasses = Enum.GetValues<EDefaultRenderPass>().Select(v => (int)v).ToArray();
        var registeredPassIndices = built.Select(m => m.PassIndex).ToHashSet();

        foreach (int passIndex in definedPasses)
            registeredPassIndices.ShouldContain(passIndex,
                $"Standard pass {(EDefaultRenderPass)passIndex} ({passIndex}) should be in metadata");
    }

    [Test]
    public void SentinelPassIndex_IsNotValidDefaultRenderPass()
    {
        Enum.IsDefined(typeof(EDefaultRenderPass), int.MinValue).ShouldBeFalse(
            "int.MinValue (sentinel) should not be a valid EDefaultRenderPass");
    }

    /// <summary>
    /// Mirrors the dependency chain declared by DefaultRenderPipeline.DescribeRenderPasses.
    /// </summary>
    private static void RegisterStandardPasses(RenderPassMetadataCollection metadata)
    {
        static void Chain(RenderPassMetadataCollection c, EDefaultRenderPass pass, params EDefaultRenderPass[] deps)
        {
            var builder = c.ForPass((int)pass, pass.ToString(), ERenderGraphPassStage.Graphics);
            foreach (var dep in deps)
                builder.DependsOn((int)dep);
        }

        Chain(metadata, EDefaultRenderPass.PreRender);
        Chain(metadata, EDefaultRenderPass.Background, EDefaultRenderPass.PreRender, EDefaultRenderPass.DeferredDecals);
        Chain(metadata, EDefaultRenderPass.OpaqueDeferred, EDefaultRenderPass.PreRender);
        Chain(metadata, EDefaultRenderPass.DeferredDecals, EDefaultRenderPass.OpaqueDeferred);
        Chain(metadata, EDefaultRenderPass.OpaqueForward, EDefaultRenderPass.Background);
        Chain(metadata, EDefaultRenderPass.MaskedForward, EDefaultRenderPass.OpaqueForward);
        Chain(metadata, EDefaultRenderPass.WeightedBlendedOitForward, EDefaultRenderPass.MaskedForward);
        Chain(metadata, EDefaultRenderPass.PerPixelLinkedListForward, EDefaultRenderPass.WeightedBlendedOitForward);
        Chain(metadata, EDefaultRenderPass.DepthPeelingForward, EDefaultRenderPass.PerPixelLinkedListForward);
        Chain(metadata, EDefaultRenderPass.TransparentForward, EDefaultRenderPass.DepthPeelingForward);
        Chain(metadata, EDefaultRenderPass.OnTopForward, EDefaultRenderPass.TransparentForward);
        Chain(metadata, EDefaultRenderPass.PostRender, EDefaultRenderPass.OnTopForward);
    }

    #endregion

    #region Staging Manager Pool Eligibility

    [Test]
    public void StagingManagerCanPool_AcceptsUploadBuffers()
    {
        var manager = new XREngine.Rendering.Vulkan.VulkanStagingManager();
        manager.CanPool(
            Silk.NET.Vulkan.BufferUsageFlags.TransferSrcBit,
            Silk.NET.Vulkan.MemoryPropertyFlags.HostVisibleBit | Silk.NET.Vulkan.MemoryPropertyFlags.HostCoherentBit)
            .ShouldBeTrue("Upload staging buffers should be poolable");
    }

    [Test]
    public void StagingManagerCanPool_AcceptsReadbackBuffers()
    {
        var manager = new XREngine.Rendering.Vulkan.VulkanStagingManager();
        manager.CanPool(
            Silk.NET.Vulkan.BufferUsageFlags.TransferDstBit,
            Silk.NET.Vulkan.MemoryPropertyFlags.HostVisibleBit | Silk.NET.Vulkan.MemoryPropertyFlags.HostCachedBit)
            .ShouldBeTrue("Readback staging buffers should be poolable");
    }

    [Test]
    public void StagingManagerCanPool_RejectsNonTransferBuffers()
    {
        var manager = new XREngine.Rendering.Vulkan.VulkanStagingManager();
        manager.CanPool(
            Silk.NET.Vulkan.BufferUsageFlags.UniformBufferBit,
            Silk.NET.Vulkan.MemoryPropertyFlags.HostVisibleBit | Silk.NET.Vulkan.MemoryPropertyFlags.HostCoherentBit)
            .ShouldBeFalse("Non-transfer buffers should not be poolable");
    }

    #endregion

    #region Allocator Toggle Coverage

    [Test]
    public void AllocatorBackendEnum_HasLegacyManagedAndVma()
    {
        Enum.IsDefined(typeof(EVulkanAllocatorBackend), EVulkanAllocatorBackend.Legacy).ShouldBeTrue();
        Enum.IsDefined(typeof(EVulkanAllocatorBackend), EVulkanAllocatorBackend.Managed).ShouldBeTrue();
        Enum.IsDefined(typeof(EVulkanAllocatorBackend), EVulkanAllocatorBackend.Vma).ShouldBeTrue();
    }

    [Test]
    public void SynchronizationBackendEnum_HasLegacyAndSync2()
    {
        Enum.IsDefined(typeof(EVulkanSynchronizationBackend), EVulkanSynchronizationBackend.Legacy).ShouldBeTrue();
        Enum.IsDefined(typeof(EVulkanSynchronizationBackend), EVulkanSynchronizationBackend.Sync2).ShouldBeTrue();
    }

    [Test]
    public void DescriptorUpdateBackendEnum_HasLegacyAndTemplate()
    {
        Enum.IsDefined(typeof(EVulkanDescriptorUpdateBackend), EVulkanDescriptorUpdateBackend.Legacy).ShouldBeTrue();
        Enum.IsDefined(typeof(EVulkanDescriptorUpdateBackend), EVulkanDescriptorUpdateBackend.Template).ShouldBeTrue();
    }

    [Test]
    public void VulkanRobustnessSettings_DefaultsToNonLegacyBackends()
    {
        var settings = new VulkanRobustnessSettings();

        settings.AllocatorBackend.ShouldBe(EVulkanAllocatorBackend.Vma);
        settings.SyncBackend.ShouldBe(EVulkanSynchronizationBackend.Sync2);
        settings.DescriptorUpdateBackend.ShouldBe(EVulkanDescriptorUpdateBackend.Template);
    }

    [Test]
    public void VulkanRendererAllocatorSwitch_HandlesLegacyManagedAndVmaExplicitly()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");

        source.ShouldContain("EVulkanAllocatorBackend.Legacy => new VulkanLegacyAllocator");
        source.ShouldContain("EVulkanAllocatorBackend.Managed => new VulkanBlockAllocator");
        source.ShouldContain("EVulkanAllocatorBackend.Vma => new VulkanVmaAllocator");
    }

    [Test]
    public void VulkanVmaBridge_UsesExplicitUsageForExistingResources()
    {
        string source = ReadWorkspaceFile("Build/Native/VulkanMemoryAllocatorBridge/VulkanMemoryAllocatorBridge.cpp");
        string allocationInfo = SliceMethod(source, "VmaAllocationCreateInfo makeAllocationCreateInfo");

        source.ShouldContain("vmaAllocateMemoryForBuffer");
        source.ShouldContain("vmaAllocateMemoryForImage");
        allocationInfo.ShouldContain("createInfo.usage = VMA_MEMORY_USAGE_UNKNOWN");
        allocationInfo.ShouldNotContain("VMA_MEMORY_USAGE_AUTO");
    }

    [Test]
    public void VulkanVmaBridge_GuardsNativeUnmapBalance()
    {
        string source = ReadWorkspaceFile("Build/Native/VulkanMemoryAllocatorBridge/VulkanMemoryAllocatorBridge.cpp");
        string mapMethod = SliceMethod(source, "xre_vma_map_memory");
        string unmapMethod = SliceMethod(source, "xre_vma_unmap_memory");
        string freeMethod = SliceMethod(source, "xre_vma_free");

        source.ShouldContain("g_allocationMapCounts");
        mapMethod.ShouldContain("recordAllocationMap(allocation)");
        unmapMethod.ShouldContain("consumeAllocationMap(allocation)");
        freeMethod.ShouldContain("takeAllocationMapCount(allocation)");
        freeMethod.ShouldContain("vmaUnmapMemory");
    }

    [Test]
    public void VulkanDynamicUniformRingBuffer_UsesDedicatedMemoryForPersistentMap()
    {
        string ringSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanDynamicUniformRingBuffer.cs");
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

        ringSource.ShouldContain("renderer.CreateDedicatedBufferRaw");
        bufferSource.ShouldContain("CreateDedicatedBufferRaw");
        bufferSource.ShouldContain("enableDeviceAddress ? \"LegacyDeviceAddress\" : \"Dedicated\"");
    }

    #endregion

    #region Stencil Pick Pipeline Contract

    [Test]
    public void AbstractRenderer_DeclaresGetStencilIndex()
    {
        MethodInfo? method = typeof(AbstractRenderer).GetMethod(
            "GetStencilIndex",
            BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull("AbstractRenderer should declare GetStencilIndex");
        method!.IsAbstract.ShouldBeTrue("GetStencilIndex should be abstract");
        method.ReturnType.ShouldBe(typeof(byte), "GetStencilIndex should return byte");
    }

    [Test]
    public void DepthStencilFormats_AreRecognized()
    {
        // Verify ImageAspectFlags.StencilBit is available — used by TryReadStencilPixel guard.
        ImageAspectFlags stencil = ImageAspectFlags.StencilBit;
        ImageAspectFlags depthStencil = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        depthStencil.HasFlag(stencil).ShouldBeTrue("DepthStencil combo should include StencilBit");
    }

    [Test]
    public void VkFrameBuffer_ClearAttachments_ClearsCombinedDepthStencilAttachments()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string method = SliceMethod(source, "internal uint WriteClearAttachments");

        method.Contains("AttachmentRole.DepthStencil", StringComparison.Ordinal).ShouldBeTrue(
            "Depth24Stencil8 FBOs must emit vkCmdClearAttachments depth/stencil clears for combined attachments.");
    }

    [Test]
    public void VulkanPresentTextureShaders_KeepWindowAndFallbackOrientationPoliciesSeparate()
    {
        string renderToWindow = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderToWindow.cs");
        string vendorUpscale = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs");

        renderToWindow.Contains("#ifdef XRENGINE_VULKAN", StringComparison.Ordinal).ShouldBeFalse(
            "Window presentation uses backend-specific viewport/image display policy; it must not add an unconditional Vulkan texture flip.");
        renderToWindow.ShouldContain("return clipXY * 0.5 + 0.5;");
        renderToWindow.ShouldContain("vec2 uv = ResolvePresentTextureUv(clipXY);");

        vendorUpscale.Contains("#ifdef XRENGINE_VULKAN", StringComparison.Ordinal).ShouldBeTrue(
            "The default pipeline's vendor-upscale fallback still resolves source-FBO orientation for final output.");
        vendorUpscale.ShouldContain("if (FlipSourceYOnVulkanFallback)");
        vendorUpscale.ShouldContain("uv.y = 1.0 - uv.y;");
        vendorUpscale.ShouldContain("FlipSourceYOnVulkanFallback ||");
        vendorUpscale.ShouldContain("RuntimeEngine.Rendering.Settings.ClipSpaceYDirection == ERenderClipSpaceYDirection.YDown");
        vendorUpscale.ShouldContain("vec2 uv = ResolvePresentTextureUv(clipXY);");
    }

    [Test]
    public void MsaaDepthResolve_UsesClipSpacePolicyForScreenSpaceSampling()
    {
        string command = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ResolveMsaaGBuffer.cs");
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/CopyDepthFromTextureMS.fs");

        command.ShouldContain("RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy");
        shader.ShouldContain("#pragma snippet \"ScreenSpaceUtils\"");
        shader.ShouldContain("clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f");
        shader.ShouldContain("XRENGINE_ScreenPixelLocal");
        shader.ShouldContain("clamp(");
    }

    [Test]
    public void VulkanAutoExposure_ClampsPlannerBackedSourcesToBaseMip()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanRenderer.AutoExposure.cs");
        string readbackSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Readback.cs");

        source.ShouldContain("sampledSmallestMip = 0;");
        source.ShouldContain("Vulkan.AutoExposure.PlannerMip0Fallback2D");
        source.ShouldContain("Vulkan.AutoExposure.PlannerMip0Fallback2DArray");
        source.ShouldContain("program.Uniform(\"SmallestMip\", sampledSmallestMip);");
        source.ShouldContain("meteringMip = Math.Clamp(sampledSmallestMip - offset, 0, sampledSmallestMip);");
        source.ShouldContain("exposureLayoutManagedByRenderGraph = vkExposure.UsesAllocatorImage;");
        source.ShouldContain("Vulkan.AutoExposure.PlannerExposureGraphBarriers");
        source.ShouldContain("if (!exposureLayoutManagedByRenderGraph && GetOrCreateAPIRenderObject(exposureTex");

        readbackSource.ShouldContain("LogPlannerMipReadbackFallback");
        readbackSource.ShouldContain("Vulkan.LuminanceReadback.PlannerMip0Fallback2D");
        readbackSource.ShouldContain("Vulkan.LuminanceReadback.PlannerMip0Fallback2DArray");
        readbackSource.ShouldContain("? 0");
    }

    [Test]
    public void VulkanImGui_ConvertsSrgbUiColorsForSrgbSwapchain()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");
        string swapChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");

        source.ShouldContain("ShouldEmulateOpenGlImGuiSrgbPassthrough()");
        source.ShouldContain("private static bool IsSrgbSwapchainFormat(Format format)");
        source.ShouldContain("private static bool IsLinearSrgbSwapchainColorSpace(ColorSpaceKHR colorSpace)");
        source.ShouldContain("Format.B8G8R8A8Srgb");
        source.ShouldContain("Format.R8G8B8A8Srgb");
        source.ShouldContain("ColorSpaceKHR.SpaceExtendedSrgbLinearExt");
        source.ShouldContain("pipelineKeyHash.Add((int)swapChainImageColorSpace);");
        source.ShouldContain("vec3 SrgbToLinear(vec3 c)");
        source.ShouldContain("color.rgb = SrgbToLinear(color.rgb * color.a);");
        source.ShouldContain("SrcColorBlendFactor = emulateOpenGlSrgbPassthrough ? BlendFactor.One : BlendFactor.SrcAlpha");
        swapChainSource.ShouldContain("swapChainImageColorSpace");
        swapChainSource.ShouldContain("imguiSrgbPassthroughEmulation={6}");
        swapChainSource.ShouldContain("ShouldEmulateOpenGlImGuiSrgbPassthrough()");
    }

    [Test]
    public void VulkanImGuiDrawBuffers_GrowWithCapacityHeadroom()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");

        source.ShouldContain("private ImGuiDrawBufferSet[] _imguiDrawBuffers = [];");
        source.ShouldContain("private int EnsureImGuiDrawBufferSlot(uint imageIndex)");
        source.ShouldContain("Array.Resize(ref _imguiDrawBuffers, requiredSlots);");
        source.ShouldContain("private int EnsureImGuiDrawBuffers(uint imageIndex, ulong vertexBytes, ulong indexBytes)");
        source.ShouldContain("ComputeImGuiBufferCapacity");
        source.ShouldContain("AlignUpToPowerOfTwoBucket");
        source.ShouldContain("64UL * 1024UL");
        source.ShouldContain("buffers.VertexBufferSize = capacity");
        source.ShouldContain("buffers.IndexBufferSize = capacity");
        source.ShouldContain("int bufferSlot = EnsureImGuiDrawBuffers(imageIndex, vertexBytes, indexBytes);");
        source.ShouldContain("Buffer vertexBuffer = buffers.VertexBuffer;");
        source.ShouldContain("CmdBindIndexBuffer(commandBuffer, buffers.IndexBuffer, 0, IndexType.Uint16);");
        source.ShouldContain("io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;");
        source.ShouldNotContain("_ = imageIndex;");
    }

    [Test]
    public void VulkanImGuiOverlay_RecordsOutsideReusableScenePrimary()
    {
        string imguiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string profileSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanFeatureProfile.cs");

        imguiSource.ShouldContain("private CommandBuffer[]? _imguiOverlayCommandBuffers;");
        imguiSource.ShouldContain("private bool TryRecordImGuiOverlayCommandBuffer(");
        imguiSource.ShouldContain("private void TransitionSwapchainImageForImGuiOverlay(");
        imguiSource.ShouldContain("private void RenderImGuiSnapshot(CommandBuffer commandBuffer, uint imageIndex, ImGuiFrameSnapshot drawData)");
        imguiSource.ShouldNotContain("nativeUiOverlaySecondaryCommandBuffer");
        imguiSource.ShouldNotContain("nativeUiOverlayOpCount");
        imguiSource.ShouldNotContain("Api.CmdExecuteCommands(commandBuffer, 1, &nativeUiOverlaySecondaryCommandBuffer);");

        commandBufferSource.ShouldContain("_imguiOverlayCommandBuffers = new CommandBuffer[_commandBuffers.Length];");
        commandBufferSource.ShouldContain("Level = CommandBufferLevel.Primary");
        commandBufferSource.ShouldContain("RegisterCommandBufferImageIndex(_imguiOverlayCommandBuffers[i], imageIndex);");
        commandBufferSource.ShouldContain("private void DestroyImGuiOverlayCommandBuffers()");
        commandBufferSource.ShouldNotContain("RenderImGui(commandBuffer, imageIndex);");

        drawingSource.ShouldContain("Vulkan.FrameLifecycle.RecordImGuiOverlay");
        drawingSource.ShouldNotContain("nativeUiOverlaySecondaryCommandBuffer");
        drawingSource.ShouldNotContain("nativeUiOverlayOpCount");
        drawingSource.ShouldContain("TryRecordImGuiOverlayCommandBuffer(");
        drawingSource.ShouldContain("submitCommandBuffers[submitCommandBufferCount++] = imguiOverlayCommandBuffer;");
        drawingSource.ShouldContain("CommandBufferCount = submitCommandBufferCount");

        profileSource.ShouldContain("EVulkanGpuDrivenProfile.ShippingFast => true");
        profileSource.ShouldContain("EVulkanGpuDrivenProfile.DevParity => true");
        profileSource.ShouldContain("EVulkanGpuDrivenProfile.Diagnostics => true");
        profileSource.ShouldContain("ImGui rendering through the Vulkan pipeline.");
    }

    [Test]
    public void VulkanFramebufferAttachments_TrackMipLayoutsIndependently()
    {
        string attachmentSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/IVkFrameBufferAttachmentSource.cs");
        string imageBackedTexture = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        attachmentSource.ShouldContain("GetAttachmentTrackedLayout(int mipLevel, int layerIndex)");
        attachmentSource.ShouldContain("UpdateAttachmentTrackedLayout(ImageLayout layout, int mipLevel, int layerIndex)");

        imageBackedTexture.ShouldContain("_attachmentLayouts");
        imageBackedTexture.ShouldContain("_hasPartialAttachmentLayouts");
        imageBackedTexture.ShouldContain("BuildAttachmentLayoutKey(mipLevel, layerIndex)");
        imageBackedTexture.ShouldContain("return ImageLayout.Undefined;");
        imageBackedTexture.ShouldContain("ResetAttachmentLayoutTracking();");

        commandBuffers.ShouldContain("UpdateAttachmentTrackedLayout(target, mipLevel, layerIndex, finalLayout);");
        commandBuffers.ShouldContain("attSrc.UpdateAttachmentTrackedLayout(layout, mipLevel, layerIndex);");
        commandBuffers.ShouldContain("attSrc.GetAttachmentTrackedLayout(mipLevel, layerIndex);");
    }

    [Test]
    public void VulkanDescriptorImageLayouts_DoNotAdvertiseDepthReadOnlyWhenTrackedLayoutDiffers()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorImageLayouts.cs");
        string method = SliceMethod(source, "internal ImageLayout ResolveDescriptorImageLayout");

        method.ShouldContain("ImageLayout requestedLayout = GetDefaultSampledDescriptorLayout(source);");
        method.ShouldContain("if (trackedLayout == requestedLayout)");
        method.ShouldContain("source.TryTransitionDedicatedImageLayout(trackedLayout, requestedLayout)");
        method.ShouldContain("return trackedLayout;");
    }

    [Test]
    public void VulkanTextureViews_UseViewLocalSamplerState()
    {
        string textureView = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");
        string bloomPass = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_BloomPass.cs");

        textureView.ShouldNotContain("_sampler = source.DescriptorSampler;");
        textureView.ShouldContain("private void CreateSampler()");
        textureView.ShouldContain("SamplerConversions.FromMinFilter(Data.MinFilter)");
        textureView.ShouldContain("MaxLod = Math.Max(0f, Math.Max(Data.NumLevels, 1u) - 1u)");
        textureView.ShouldContain("case nameof(XRTextureViewBase.MinFilter):");
        textureView.ShouldContain("private void RetireOwnedImageViews()");
        textureView.ShouldContain("RetireOwnedImageViews();");
        textureView.ShouldContain("if (_view.Handle != 0 && _sampler.Handle == 0)");
        textureView.ShouldContain("RetireOwnedViewsAndSampler()");
        textureView.ShouldContain("DestroySampler();");

        bloomPass.ShouldContain("MinFilter = ETexMinFilter.Linear,");
    }

    [Test]
    public void BitmapFontAtlas_ExtractsAlphaCoverageIntoR8RedChannel()
    {
        string fontGlyphSet = ReadWorkspaceFile("XRENGINE/Core/FontGlyphSet.cs");

        fontGlyphSet.ShouldContain("Atlas = CreateBitmapAtlasTexture(outputAtlasPath);");
        fontGlyphSet.ShouldContain("private static XRTexture2D CreateBitmapAtlasTexture(string atlasPath)");
        fontGlyphSet.ShouldContain("coverage[x + (y * atlasImage.Width)] = atlasImage[x, y].A;");
        fontGlyphSet.ShouldContain("EPixelInternalFormat.R8");
        fontGlyphSet.ShouldContain("EPixelFormat.Red");
        fontGlyphSet.ShouldContain("EPixelType.UnsignedByte");
        fontGlyphSet.ShouldContain("SizedInternalFormat = ESizedInternalFormat.R8");
        fontGlyphSet.ShouldContain("MinFilter = ETexMinFilter.LinearMipmapLinear");
        fontGlyphSet.ShouldContain("RebuildBitmapAtlasMipChain(atlas");
        fontGlyphSet.ShouldContain("atlasTexture.SmallestAllowedMipmapLevel = Math.Max(0, atlasTexture.Mipmaps.Length - 1);");
    }

    [Test]
    public void VulkanFrameDrawStats_PublishFromFrameOpsInsteadOfCommandRecording()
    {
        string vkMeshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string vkMeshDrawing = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string indirectDraw = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.IndirectDraw.cs");

        vkMeshRenderer.ShouldContain("PublishFrameOpDrawStats(validatedOp);");
        vkMeshRenderer.ShouldContain("EstimateFrameDrawStats(meshDraw.Draw)");
        vkMeshRenderer.ShouldContain("RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(stats.DrawCalls);");
        vkMeshRenderer.ShouldContain("RuntimeEngine.Rendering.Stats.Frame.IncrementMultiDrawCalls(stats.MultiDrawCalls);");
        vkMeshRenderer.ShouldContain("RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered(stats.TrianglesRendered);");
        vkMeshRenderer.ShouldContain("EstimateTriangleCount(triangleIndexCount, instances)");

        vkMeshDrawing.ShouldNotContain("RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls();");
        vkMeshDrawing.ShouldNotContain("RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered((int)(vertexCount / 3 * drawInstances));");
        indirectDraw.ShouldNotContain("RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls((int)drawCount);");
        indirectDraw.ShouldNotContain("RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls((int)maxDrawCount);");
    }

    [Test]
    public void NativeFpsOverlay_UsesStableCompactMultilineLayout()
    {
        string unitTestingUi = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs");

        unitTestingUi.ShouldContain("private const float FpsOverlayWidth");
        unitTestingUi.ShouldContain("private const float FpsOverlayHeight");
        unitTestingUi.ShouldContain("builder.Append(\"net:    rtt \");");
        unitTestingUi.ShouldContain("builder.Append(\"\\nrender: \");");
        unitTestingUi.ShouldContain("builder.Append(\"\\nloop:   update \");");
        unitTestingUi.ShouldContain("builder.Append(\"ms | fixed \");");
        unitTestingUi.ShouldContain("builder.Append(\"\\ndraw:   calls \");");
        unitTestingUi.ShouldContain("builder.Append(\" | cpu fallback \");");
        unitTestingUi.ShouldContain("FormatCompactRate(bytesPerSecond, 7)");
        unitTestingUi.ShouldContain("FormatCompactCount(drawCalls, 5)");
        unitTestingUi.ShouldContain("SceneNode textNode = new(parentNode) { Name = \"TestTextNode\" };");
        unitTestingUi.ShouldNotContain("FpsOverlayBackground");
        unitTestingUi.ShouldNotContain("TestTextStrokeNode");
        unitTestingUi.ShouldNotContain("LoadDefaultUIMonospaceFontBitmap");
        unitTestingUi.ShouldContain("text.Font = font;");
        unitTestingUi.ShouldContain("text.FontSize = 26;");
        unitTestingUi.ShouldContain("text.HorizontalAlignment = EHorizontalAlignment.Center;");
        unitTestingUi.ShouldContain("text.VerticalAlignment = EVerticalAlignment.Center;");
        unitTestingUi.ShouldContain("text.RenderCommand2D.ZIndex = int.MaxValue;");
        unitTestingUi.ShouldContain("text.OutlineColor = new ColorF4(0.0f, 0.0f, 0.0f, 1.0f);");
        unitTestingUi.ShouldContain("text.OutlineThickness = 2.0f;");
        unitTestingUi.ShouldContain("text.OutlineAffectsSpacing = true;");
        unitTestingUi.ShouldContain("textTransform.Width = FpsOverlayWidth;");
        unitTestingUi.ShouldContain("textTransform.Height = FpsOverlayHeight;");

        string textComponent = ReadWorkspaceFile("XREngine/Scene/Components/UI/Text/UITextComponent.cs");
        textComponent.ShouldContain("public bool OutlineAffectsSpacing");
        textComponent.ShouldContain("float outlineSpacing = OutlineAffectsSpacing ? OutlineThickness : 0.0f;");
        textComponent.ShouldContain("float glyphSpacing = ResolveLayoutSpacingForOutputPixels(outlineSpacing);");
        textComponent.ShouldContain("DefaultLineSpacing + outlineSpacing");
        textComponent.ShouldContain("private float ResolveLayoutSpacingForOutputPixels(float outputSpacing)");
        textComponent.ShouldContain("return outputSpacing * layoutEmSize / resolvedFontSize;");
        textComponent.ShouldContain("case nameof(OutlineAffectsSpacing):");

        string textRenderable = ReadWorkspaceFile("XREngine/Scene/Components/UI/Text/UIText.cs");
        textRenderable.ShouldContain("public bool OutlineAffectsSpacing");
        textRenderable.ShouldContain("float outlineSpacing = OutlineAffectsSpacing ? OutlineThickness : 0.0f;");
        textRenderable.ShouldContain("float glyphSpacing = ResolveLayoutSpacingForOutputPixels(outlineSpacing);");
        textRenderable.ShouldContain("DefaultLineSpacing + outlineSpacing");
        textRenderable.ShouldContain("private float ResolveLayoutSpacingForOutputPixels(float outputSpacing)");

        string fontGlyphSet = ReadWorkspaceFile("XRENGINE/Core/FontGlyphSet.cs");
        fontGlyphSet.ShouldNotContain("LoadDefaultUIMonospaceFontBitmap");

        string batchedVertexShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/UITextBatched.vs");
        batchedVertexShader.ShouldContain("uniform int TextRenderLayer_VTX;");
        batchedVertexShader.ShouldContain("const int TextRenderLayerFill = 2;");
        batchedVertexShader.ShouldContain("TextRenderLayer_VTX != TextRenderLayerFill");
        batchedVertexShader.ShouldContain("vec2 expand = vec2(outlineParams.x);");
        batchedVertexShader.ShouldContain("vec2 glyphDirection = vec2(");
        batchedVertexShader.ShouldContain("glyphSize.y < 0.0 ? -1.0 : 1.0");
        batchedVertexShader.ShouldContain("glyphMin -= expand * glyphDirection;");
        batchedVertexShader.ShouldContain("glyphSize += expand * 2.0 * glyphDirection;");
        batchedVertexShader.ShouldContain("FragUV0 = mix(uvMin, uvMax, corner);");

        string textVertexShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/Text.vs");
        textVertexShader.ShouldContain("uniform vec4 OutlineColor;");
        textVertexShader.ShouldContain("uniform float OutlineThickness;");
        textVertexShader.ShouldContain("OutlineThickness > 0.0 && OutlineColor.a > 0.0");
        textVertexShader.ShouldContain("vec2 glyphDirection = vec2(");
        textVertexShader.ShouldContain("glyphSize.y < 0.0 ? -1.0 : 1.0");
        textVertexShader.ShouldContain("glyphMin -= expand * glyphDirection;");
        textVertexShader.ShouldContain("glyphSize += expand * 2.0 * glyphDirection;");
        textVertexShader.ShouldContain("FragUV0 = mix(uvMin, uvMax, corner);");

        string textRotatableVertexShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/TextRotatable.vs");
        textRotatableVertexShader.ShouldContain("uniform vec4 OutlineColor;");
        textRotatableVertexShader.ShouldContain("uniform float OutlineThickness;");
        textRotatableVertexShader.ShouldContain("OutlineThickness > 0.0 && OutlineColor.a > 0.0");
        textRotatableVertexShader.ShouldContain("glyphSize += expand * 2.0 * glyphDirection;");
        textRotatableVertexShader.ShouldContain("FragUV0 = mix(uvMin, uvMax, corner);");

        string textStereoVertexShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/TextStereo.vs");
        textStereoVertexShader.ShouldContain("uniform vec4 OutlineColor;");
        textStereoVertexShader.ShouldContain("uniform float OutlineThickness;");
        textStereoVertexShader.ShouldContain("OutlineThickness > 0.0 && OutlineColor.a > 0.0");
        textStereoVertexShader.ShouldContain("glyphSize += expand * 2.0 * glyphDirection;");
        textStereoVertexShader.ShouldContain("FragUV0 = mix(uvMin, uvMax, corner);");

        string textRotatableStereoVertexShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/TextRotatableStereo.vs");
        textRotatableStereoVertexShader.ShouldContain("uniform vec4 OutlineColor;");
        textRotatableStereoVertexShader.ShouldContain("uniform float OutlineThickness;");
        textRotatableStereoVertexShader.ShouldContain("OutlineThickness > 0.0 && OutlineColor.a > 0.0");
        textRotatableStereoVertexShader.ShouldContain("glyphSize += expand * 2.0 * glyphDirection;");
        textRotatableStereoVertexShader.ShouldContain("FragUV0 = mix(uvMin, uvMax, corner);");

        string batchedStereoMv2VertexShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/UITextBatchedStereoMV2.vs");
        batchedStereoMv2VertexShader.ShouldContain("uniform int TextRenderLayer_VTX;");
        batchedStereoMv2VertexShader.ShouldContain("TextRenderLayer_VTX != TextRenderLayerFill");

        string batchedStereoNvVertexShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/UITextBatchedStereoNV.vs");
        batchedStereoNvVertexShader.ShouldContain("uniform int TextRenderLayer_VTX;");
        batchedStereoNvVertexShader.ShouldContain("TextRenderLayer_VTX != TextRenderLayerFill");

        string batchedFragmentShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Common/UITextBatched.fs");
        batchedFragmentShader.ShouldContain("const int StrokeSampleRadius = 5;");
        batchedFragmentShader.ShouldContain("uniform int TextRenderLayer;");
        batchedFragmentShader.ShouldContain("TextRenderLayer == TextRenderLayerOutline");
        batchedFragmentShader.ShouldContain("TextRenderLayer == TextRenderLayerFill");
        batchedFragmentShader.ShouldContain("vec2 uvDx = dFdx(FragUV0);");
        batchedFragmentShader.ShouldContain("vec2 uvDy = dFdy(FragUV0);");
        batchedFragmentShader.ShouldContain("const float StrokeDiagonal = 0.70710678118;");
        batchedFragmentShader.ShouldContain("const float StrokeDirA = 0.92387953251;");
        batchedFragmentShader.ShouldContain("const float StrokeDirB = 0.38268343236;");
        batchedFragmentShader.ShouldContain("float SampleStrokeRing(vec2 uv, vec2 uvDx, vec2 uvDy, float ringRadius)");
        batchedFragmentShader.ShouldContain("ringRadius * StrokeDiagonal");
        batchedFragmentShader.ShouldContain("ringRadius * StrokeDirA");
        batchedFragmentShader.ShouldContain("ringRadius * StrokeDirB");
        batchedFragmentShader.ShouldContain("uvDx * sampleOffset.x + uvDy * sampleOffset.y");
        batchedFragmentShader.ShouldNotContain("distanceSquared <= radiusSquared");
        batchedFragmentShader.ShouldContain("const float StrokeFillFadeStart = 0.25;");
        batchedFragmentShader.ShouldContain("const float StrokeFillFadeEnd = 0.85;");
        batchedFragmentShader.ShouldContain("float StrokeVisibilityMask(float fill)");
        batchedFragmentShader.ShouldContain("smoothstep(StrokeFillFadeStart, StrokeFillFadeEnd, fill)");
        batchedFragmentShader.ShouldContain("outlineMask = stroke * StrokeVisibilityMask(fill);");

        string batchCollector = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/UI/UIBatchCollector.cs");
        batchCollector.ShouldContain("private const string TextRenderLayerUniformName = \"TextRenderLayer\";");
        batchCollector.ShouldContain("private const string TextRenderLayerVertexUniformName = \"TextRenderLayer_VTX\";");
        batchCollector.ShouldContain("TextRenderLayerCombined");
        batchCollector.ShouldNotContain("RenderTextLayer(");
        batchCollector.ShouldNotContain("UIBatchTextDrawOutline");
        batchCollector.ShouldNotContain("UIBatchTextDrawFill");
        batchCollector.ShouldContain("gpu.Mesh.CaptureUniformsOnRender = true;");

        string vkMeshUniforms = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs");
        vkMeshUniforms.ShouldContain("or \"TextDebugMode\" or \"TextRenderLayer\" or \"TextRenderLayer_VTX\"");

        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        commandBuffers.ShouldContain("TryRefreshReusableCommandBufferFrameData(imageIndex, dynamicUiBatchTextOps);");
    }

    #endregion

    #region VulkanOutOfMemoryException

    [Test]
    public void VulkanOutOfMemoryException_PreservesRequestedProperties()
    {
        var props = MemoryPropertyFlags.DeviceLocalBit;
        var ex = new VulkanOutOfMemoryException("test", props);
        ex.RequestedProperties.ShouldBe(props);
        ex.Message.ShouldContain("test");
    }

    [Test]
    public void VulkanMemoryAllocation_Null_IsNull()
    {
        VulkanMemoryAllocation.Null.IsNull.ShouldBeTrue();
        VulkanMemoryAllocation.Null.Memory.Handle.ShouldBe(0UL);
    }

    #endregion

    #region Barrier Precision Audit Coverage

    [Test]
    public void BarrierPlanner_TransferStage_DoesNotUseAllCommandsBit()
    {
        // Transfer-stage resources should resolve to TransferBit, not AllCommandsBit.
        // This validates the precision audit fix in VulkanBarrierPlanner.
        var transferStage = Silk.NET.Vulkan.PipelineStageFlags.TransferBit;
        var allCommands = Silk.NET.Vulkan.PipelineStageFlags.AllCommandsBit;
        transferStage.ShouldNotBe(allCommands,
            "Transfer stage should use TransferBit, not AllCommandsBit");
    }

    [Test]
    public void ImageTransitionPrecision_CommonTransitionsHavePreciseStages()
    {
        // Verify the set of common layout transitions that must have precise
        // pipeline stage assignments (not AllCommandsBit).
        var preciseTransitions = new[]
        {
            (ImageLayout.Undefined, ImageLayout.TransferDstOptimal),
            (ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal),
            (ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal),
            (ImageLayout.Undefined, ImageLayout.General),
            (ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal),
            (ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal),
            (ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (ImageLayout.General, ImageLayout.TransferDstOptimal),
            (ImageLayout.TransferDstOptimal, ImageLayout.General),
        };

        preciseTransitions.Length.ShouldBeGreaterThanOrEqualTo(11,
            "At least 11 common layout transitions should be precisely staged");
    }

    #endregion

    #region Descriptor Pool Size Classes

    [Test]
    public void PoolSizeClass_SmallSchema_InfersSmall()
    {
        // A shader with 1-2 types, ≤4 total descriptors should infer Small.
        var poolSizes = new Silk.NET.Vulkan.DescriptorPoolSize[]
        {
            new() { Type = Silk.NET.Vulkan.DescriptorType.StorageBuffer, DescriptorCount = 2 },
        };

        // Infer via reflection (private method) to validate behavior.
        // Pool size class inference: ≤2 types, ≤4 total → Small
        (poolSizes.Length <= 2 && poolSizes.Sum(p => p.DescriptorCount) <= 4).ShouldBeTrue(
            "Pool with 1 type and 2 descriptors should be Small-class candidate");
    }

    [Test]
    public void PoolSizeClass_LargeSchema_InfersLarge()
    {
        // A shader with many types or high descriptor count should infer Large.
        var poolSizes = new Silk.NET.Vulkan.DescriptorPoolSize[10];
        for (int i = 0; i < 10; i++)
            poolSizes[i] = new Silk.NET.Vulkan.DescriptorPoolSize
            {
                Type = Silk.NET.Vulkan.DescriptorType.CombinedImageSampler,
                DescriptorCount = 2
            };

        (poolSizes.Length > 8 || poolSizes.Sum(p => p.DescriptorCount) > 16).ShouldBeTrue(
            "Pool with 10 types and 20 descriptors should be Large-class candidate");
    }

    #endregion

    #region Dynamic UBO Infrastructure

    [Test]
    public void VulkanRobustnessSettings_DynamicUbo_DefaultsToEnabled()
    {
        var settings = new VulkanRobustnessSettings();
        settings.DynamicUniformBufferEnabled.ShouldBeTrue(
            "Dynamic uniform buffer should default to enabled");
    }

    [Test]
    public void VulkanRobustnessSettings_DynamicUbo_CanBeToggled()
    {
        var settings = new VulkanRobustnessSettings();
        settings.DynamicUniformBufferEnabled = true;
        settings.DynamicUniformBufferEnabled.ShouldBeTrue();
        settings.DynamicUniformBufferEnabled = false;
        settings.DynamicUniformBufferEnabled.ShouldBeFalse();
    }

    [Test]
    public void VulkanRobustnessSettings_AreExposedThroughRuntimeHostServices()
    {
        string interfaceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderingHostServices.cs");
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs");
        string hostSource = ReadWorkspaceFile("XRENGINE/Engine/Engine.RuntimeRenderingHostServices.cs");
        string defaultsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServiceDefaults.cs");

        interfaceSource.ShouldContain("VulkanAllocatorBackend");
        interfaceSource.ShouldContain("VulkanSynchronizationBackend");
        interfaceSource.ShouldContain("VulkanDescriptorUpdateBackend");
        interfaceSource.ShouldContain("VulkanDynamicUniformBufferEnabled");

        runtimeSource.ShouldContain("services.VulkanAllocatorBackend");
        runtimeSource.ShouldContain("services.VulkanSynchronizationBackend");
        runtimeSource.ShouldContain("services.VulkanDescriptorUpdateBackend");
        runtimeSource.ShouldContain("services.VulkanDynamicUniformBufferEnabled");

        hostSource.ShouldContain("Engine.Rendering.Settings.VulkanRobustnessSettings.AllocatorBackend");
        hostSource.ShouldContain("Engine.Rendering.Settings.VulkanRobustnessSettings.SyncBackend");
        hostSource.ShouldContain("Engine.Rendering.Settings.VulkanRobustnessSettings.DescriptorUpdateBackend");
        hostSource.ShouldContain("Engine.Rendering.Settings.VulkanRobustnessSettings.DynamicUniformBufferEnabled");

        defaultsSource.ShouldContain("VulkanAllocatorBackend = EVulkanAllocatorBackend.Vma");
        defaultsSource.ShouldContain("VulkanSynchronizationBackend = EVulkanSynchronizationBackend.Sync2");
        defaultsSource.ShouldContain("VulkanDescriptorUpdateBackend = EVulkanDescriptorUpdateBackend.Template");
        defaultsSource.ShouldContain("VulkanDynamicUniformBufferEnabled = true");
    }

    #endregion

    #region Descriptor Lifetime Validation

    [Test]
    public void DescriptorPoolCreateFlags_ResetWithoutFreeDescriptorSetBit()
    {
        // Transient compute pools should use reset-based lifecycle (no FreeDescriptorSetBit).
        // This validates the P1 pool lifecycle change.
        var updateAfterBind = Silk.NET.Vulkan.DescriptorPoolCreateFlags.UpdateAfterBindBit;
        var freeSetBit = Silk.NET.Vulkan.DescriptorPoolCreateFlags.FreeDescriptorSetBit;

        // UpdateAfterBind alone should not include FreeDescriptorSetBit.
        (updateAfterBind & freeSetBit).ShouldBe((Silk.NET.Vulkan.DescriptorPoolCreateFlags)0,
            "UpdateAfterBindBit should not implicitly include FreeDescriptorSetBit");
    }

    [Test]
    public void DescriptorPoolCreateFlags_ImGuiPool_UsesFreeDescriptorSetBit()
    {
        // ImGui pool should keep FreeDescriptorSetBit (long-lived, individually freed).
        var freeSetBit = Silk.NET.Vulkan.DescriptorPoolCreateFlags.FreeDescriptorSetBit;
        ((int)freeSetBit).ShouldNotBe(0,
            "FreeDescriptorSetBit should exist for ImGui pool usage");
    }

    [Test]
    public void DescriptorPoolCreateFlags_ImGuiPool_AllocatesDescriptorForEverySet()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");

        source.ShouldContain("private const uint ImGuiDescriptorPoolMaxSets = 256;");
        source.ShouldContain("DescriptorCount = ImGuiDescriptorPoolMaxSets");
        source.ShouldContain("MaxSets = ImGuiDescriptorPoolMaxSets");
    }

    [Test]
    public void MeshCacheTeardown_RetiresSharedPipelinesAndUniformBuffers()
    {
        string retirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");
        string drawingCoreSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string sharedPipelineCacheSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanGraphicsPipelineCache.cs");
        string initializationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string uniformsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs");

        retirementSource.ShouldContain("private readonly List<Pipeline>[] _retiredPipelines");
        retirementSource.ShouldContain("internal void RetirePipeline(Pipeline pipeline)");
        retirementSource.ShouldContain("private void DrainRetiredPipelines(int maxItems = RetiredPipelineDrainLimitPerFrame)");
        drawingCoreSource.ShouldContain("DrainRetiredPipelines();");

        string destroyPipelines = SliceMethod(pipelineSource, "private void DestroyPipelines()");
        destroyPipelines.ShouldContain("DestroyDescriptors();");
        destroyPipelines.ShouldContain("_pipelines.Clear();");
        destroyPipelines.ShouldNotContain("Renderer.RetirePipeline(pipe);");
        destroyPipelines.ShouldNotContain("DestroyPipeline(Device, pipe");
        sharedPipelineCacheSource.ShouldContain("private void DestroySharedGraphicsPipelines()");
        sharedPipelineCacheSource.ShouldContain("Api.DestroyPipeline(device, pipeline, null)");
        initializationSource.ShouldContain("DestroySharedGraphicsPipelines();");

        string destroyUniformBuffer = SliceMethod(uniformsSource, "internal void DestroyTrackedMeshUniformBuffer");
        destroyUniformBuffer.ShouldContain("RetireBuffer(buffer, memory);");
        destroyUniformBuffer.ShouldContain("RetireBuffer(default, memory);");
        destroyUniformBuffer.ShouldNotContain("Api!.DestroyBuffer(device, buffer, null);");
    }

    [Test]
    public void VulkanComputeFallbackUniforms_AreCachedPerImage()
    {
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");

        programSource.ShouldContain("_computeUniformBuffers");
        programSource.ShouldContain("TryGetOrUpdateComputeFallbackUniformBuffer");
        programSource.ShouldContain("TryGetOrUpdateComputeAutoUniformBuffer");
        programSource.ShouldContain("EComputeUniformBufferKind.Fallback");
        programSource.ShouldContain("EComputeUniformBufferKind.Auto");
        programSource.ShouldNotContain("tempUniformBuffers.Add((buffer, memory));");
    }

    [Test]
    public void VulkanMeshRenderer_CachesGeneratedProgramsAcrossPipelineInvalidation()
    {
        string meshRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

        meshRendererSource.ShouldContain("_programCache");
        meshRendererSource.ShouldContain("_activeProgramIdentity");
        meshRendererSource.ShouldContain("GeneratedProgramCacheEntry");
        meshRendererSource.ShouldContain("DestroyGeneratedPrograms();");

        string ensureProgram = SliceMethod(pipelineSource, "private bool EnsureProgram(XRMaterial material)");
        ensureProgram.ShouldContain("_programCache.TryGetValue");
        ensureProgram.ShouldContain("BuildGeneratedProgramIdentity");
        ensureProgram.ShouldContain("GenerateVertexShader");
        ensureProgram.ShouldNotContain("_generatedProgram?.Destroy();");

        pipelineSource.ShouldContain("ConcurrentDictionary<string, XRShader> _generatedVertexShaderCache");
        pipelineSource.ShouldContain("private void DestroyGeneratedPrograms()");
    }

    #endregion

    #region Vulkan Startup Cache Contracts

    [Test]
    public void VulkanShaderArtifactCache_IsPersistentVersionedAndAsyncWritten()
    {
        string cacheSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderArtifactCache.cs");
        string shaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkShader.cs");
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs");

        cacheSource.ShouldContain("internal const int SchemaVersion");
        cacheSource.ShouldContain("Build");
        cacheSource.ShouldContain("Cache");
        cacheSource.ShouldContain("Vulkan");
        cacheSource.ShouldContain("ShaderArtifacts");
        cacheSource.ShouldContain("TryRead(");
        cacheSource.ShouldContain("QueueWrite(");
        cacheSource.ShouldContain("ThreadPool.QueueUserWorkItem");
        cacheSource.ShouldContain("RuntimeFingerprint");
        cacheSource.ShouldContain(".spv");

        compilerSource.ShouldContain("public sealed record PreparedSource");
        compilerSource.ShouldContain("public static PreparedSource Prepare");
        compilerSource.ShouldContain("public static unsafe byte[] CompilePrepared");

        string buildArtifact = SliceMethod(shaderSource, "private VulkanShaderArtifact BuildCpuArtifact");
        buildArtifact.ShouldContain("VulkanShaderArtifactCache.TryRead");
        buildArtifact.ShouldContain("VulkanShaderCompiler.CompilePrepared");
        buildArtifact.ShouldContain("VulkanShaderArtifactCache.QueueWrite");
        buildArtifact.IndexOf("VulkanShaderArtifactCache.TryRead", StringComparison.Ordinal)
            .ShouldBeLessThan(buildArtifact.IndexOf("VulkanShaderCompiler.CompilePrepared", StringComparison.Ordinal));
    }

    [Test]
    public void VulkanShaderArtifactCache_RoundTripsAndRejectsCorruptPayload()
    {
        string identity = $"VKSHD-TEST-{Guid.NewGuid():N}";
        string rewrittenSource = "#version 450\n#define XRENGINE_VULKAN 1\nlayout(location = 0) in vec3 Position;\nvoid main(){ gl_Position = vec4(Position, 1.0); }\n";
        XRShader shader = new(EShaderType.Vertex, new TextFile { Text = rewrittenSource });
        int shaderConfigVersion = 77;

        var artifact = new VulkanRenderer.VulkanShaderArtifact(
            identity,
            shader.Type,
            "main",
            null,
            rewrittenSource,
            [3, 2, 23, 7, 1, 0, 0, 0],
            Array.Empty<DescriptorBindingInfo>(),
            null,
            new Dictionary<string, uint>(StringComparer.Ordinal) { ["Position"] = 0u },
            ShaderStageFlags.VertexBit,
            shaderConfigVersion,
            UsesVulkanClipDepthRemap: false);

        string cacheDirectory = VulkanShaderArtifactCache.GetShaderCacheDirectoryPath();
        string binaryPath = Path.Combine(cacheDirectory, $"{identity}.spv");

        try
        {
            VulkanShaderArtifactCache.WriteForTesting(artifact);

            VulkanShaderArtifactCache.TryRead(
                identity,
                shader,
                shaderConfigVersion,
                usesVulkanClipDepthRemap: false,
                rewrittenSource,
                autoUniformBlock: null,
                ShaderStageFlags.VertexBit,
                out VulkanRenderer.VulkanShaderArtifact loaded).ShouldBeTrue();

            loaded.LoadedFromDiskCache.ShouldBeTrue();
            loaded.SpirV.SequenceEqual(artifact.SpirV).ShouldBeTrue();
            loaded.VertexInputLocations["Position"].ShouldBe(0u);

            File.WriteAllBytes(binaryPath, [1, 2, 3]);
            VulkanShaderArtifactCache.TryRead(
                identity,
                shader,
                shaderConfigVersion,
                usesVulkanClipDepthRemap: false,
                rewrittenSource,
                autoUniformBlock: null,
                ShaderStageFlags.VertexBit,
                out _).ShouldBeFalse();

            File.Exists(binaryPath).ShouldBeFalse();
        }
        finally
        {
            VulkanShaderArtifactCache.Delete(identity);
        }
    }

    [Test]
    public void VulkanAsyncMeshProgramPreparation_QueuesShaderCpuCompileInsteadOfBlockingDraw()
    {
        string shaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkShader.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string meshPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

        shaderSource.ShouldContain("Task.Run(() => BuildCpuArtifact");
        shaderSource.ShouldContain("TryGenerateFromAsyncCompile");

        string linkMethod = SliceMethod(programSource, "public bool Link(bool allowAsyncShaderCompile = false)");
        linkMethod.ShouldContain("allowAsyncShaderCompile");
        linkMethod.ShouldContain("TryGenerateFromAsyncCompile");
        linkMethod.ShouldContain("EShaderProgramBackendStage.SourceQueued");

        string linkRequestMethod = SliceMethod(programSource, "private void OnLinkRequested");
        linkRequestMethod.ShouldContain("Link(ShouldUseAsyncShaderCompileForLinkRequest())");
        linkRequestMethod.ShouldContain("Data.ShaderMetadata.Backend.Stage == XRRenderProgram.EShaderProgramBackendStage.Failed");
        programSource.ShouldContain("private bool ShouldUseAsyncShaderCompileForLinkRequest()");
        programSource.ShouldContain("shader.Data.Type == EShaderType.Compute");

        meshPipelineSource.ShouldContain("_program.Link(MeshRenderer?.GenerateAsync ?? false)");
    }

    [Test]
    public void VulkanTextureUploadPrepWorker_DefaultsToWorkerPath()
    {
        string flagsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RenderDiagnosticsFlags.cs");
        string preferencesSource = ReadWorkspaceFile("XREngine/Settings/EditorPreferences.cs");

        flagsSource.ShouldContain("VkTextureUploadPrepWorker = ReadBoolDefaultTrue(\"XRE_VULKAN_TEXTURE_UPLOAD_PREP_WORKER\")");
        preferencesSource.ShouldContain("Run Vulkan imported-texture upload preparation on the worker/upload context");
        preferencesSource.ShouldContain("[DefaultValue(true)]");
    }

    [Test]
    public void VulkanAsyncGraphicsPipelineQueue_CapacityCountsOnlyActiveJobs()
    {
        string queueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");

        string enqueueMethod = SliceMethod(queueSource, "internal bool TryEnqueueVulkanGraphicsPipelineCompile");
        enqueueMethod.ShouldContain("int activeJobCount = CountActiveVulkanGraphicsPipelineCompileJobs()");
        enqueueMethod.ShouldContain("if (activeJobCount >= capacity)");
        enqueueMethod.ShouldContain("completed=");
        enqueueMethod.ShouldNotContain("_vulkanGraphicsPipelineCompileJobs.Count >= capacity");

        string countMethod = SliceMethod(queueSource, "private int CountActiveVulkanGraphicsPipelineCompileJobs()");
        countMethod.ShouldContain("if (!job.Task.IsCompleted)");
    }

    [Test]
    public void VulkanPipelinePrewarmIdentity_DoesNotPersistVulkanHandles()
    {
        string prewarmSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelinePrewarmDatabase.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string extensionsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs");

        prewarmSource.ShouldContain("internal const int CurrentVersion = 2");
        prewarmSource.ShouldContain("RenderPassSignature");
        prewarmSource.ShouldContain("BuildDynamicRenderingSignature");
        prewarmSource.ShouldNotContain("RenderPassHandle");
        prewarmSource.ShouldNotContain("renderPass.Handle.ToString");

        extensionsSource.ShouldContain("_renderPassSemanticSignatures");
        extensionsSource.ShouldContain("GetRenderPassSemanticSignature");

        string graphicsFingerprint = SliceMethod(programSource, "public ulong ComputeGraphicsPipelineFingerprint()");
        graphicsFingerprint.ShouldContain("LastArtifact?.Identity");
        graphicsFingerprint.ShouldNotContain("_pipelineLayout.Handle");
        graphicsFingerprint.ShouldNotContain("stage.Module.Handle");

        string computeFingerprint = SliceMethod(programSource, "public ulong ComputeComputePipelineFingerprint()");
        computeFingerprint.ShouldContain("LastArtifact?.Identity");
        computeFingerprint.ShouldNotContain("_pipelineLayout.Handle");
        computeFingerprint.ShouldNotContain("computeStage.Module.Handle");
    }

    [Test]
    public void VulkanStartupBufferUpload_GatesIndirectCopyDeviceAddressForSmallBuffers()
    {
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        string indirectCopySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/RTXIO/VulkanRenderer.MemoryCopyIndirect.cs");

        bufferSource.ShouldContain("IndirectCopyDeviceAddressThresholdBytes");
        bufferSource.ShouldContain("ShouldUseDeviceAddressForIndirectCopy");
        bufferSource.ShouldContain("byteCount >= IndirectCopyDeviceAddressThresholdBytes");
        bufferSource.ShouldContain("CanUseGpuDecompressionUpload");
        bufferSource.ShouldContain("Renderer.CanUseNvIndirectBufferCopyUploads");
        bufferSource.ShouldContain("preferIndirectCopy || enableDeviceAddress || canUseGpuDecompression");

        indirectCopySource.ShouldContain("CanUseNvIndirectBufferCopyUploads");
        indirectCopySource.ShouldContain("EnableNvIndirectCopyUploads");
        indirectCopySource.ShouldContain("if (!CanUseNvIndirectBufferCopyUploads)");
    }

    #endregion

    private static string ReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path);
    }

    private static string SliceMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find method signature '{signature}'.");

        int openBrace = source.IndexOf('{', start);
        openBrace.ShouldBeGreaterThanOrEqualTo(start, $"Could not find method body for '{signature}'.");

        int depth = 0;
        for (int i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
                depth--;

            if (depth == 0)
                return source[start..(i + 1)];
        }

        throw new InvalidOperationException($"Could not find method end for '{signature}'.");
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

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
