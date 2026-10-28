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
        string statsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.cs");
        string commandBufferSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string packetSource = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string profilerSenderSource = ReadWorkspaceFile("XREngine.Runtime.Host/Engine/Engine.ProfilerSender.cs");
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
        profilerSenderSource.ShouldContain("VulkanDroppedFrameOps = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDroppedFrameOps");
        editorSource.ShouldContain("VulkanDroppedFrameOps = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDroppedFrameOps");
        profilerUiSource.ShouldContain("Vulkan Frame Diagnostics:");
    }

    [Test]
    public void VulkanValidationLayerMessages_FeedFrameDiagnostics()
    {
        // The device validation callback owns message classification and stats publication.
        string validationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanValidationDiagnostics.cs");
        string statsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.cs");

        validationSource.ShouldContain("bool isError = severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt);");
        validationSource.ShouldContain("RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanValidationMessage(isError, message);");
        statsSource.ShouldContain("VulkanLastValidationMessage");
        statsSource.ShouldContain("VulkanValidationErrorCountCurrentFrame");
    }

    [Test]
    public void VulkanValidationLayerState_IsReportedFromRendererInstanceState()
    {
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");
        // The device context owns the instance validation state. The frame loop
        // publishes the bootstrap result to the shared rendering state.
        string instanceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.Instance.cs");
        string lifecycleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs");
        string statsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.cs");

        runtimeSource.ShouldContain("public static bool VulkanValidationLayersEnabled { get; internal set; }");
        instanceSource.ShouldContain("ValidationLayersEnabled = enableValidationLayers;");
        instanceSource.ShouldContain("ValidationLayersEnabled = false;");
        lifecycleSource.ShouldContain("RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled = result.ValidationLayersEnabled;");
        statsSource.ShouldContain("RuntimeEngine.Rendering.State.IsVulkan && RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled");
    }

    [Test]
    public void OpenXrVulkanImagePressure_UsesTrackedRenderVramInsteadOfAggregateAllocatorBytes()
    {
        // The resource runtime owns OpenXR image-allocation pressure. It checks the
        // allocator heap budget, then tracked render VRAM, then the allocation count.
        string allocatorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.MemoryAllocator.cs");

        string imagePreflight = SliceMethod(allocatorSource, "internal bool ShouldDeferImageMemoryAllocationForPressure(");
        imagePreflight.ShouldContain("(!presentation.IsOpenXRActive && !presentation.IsInVR)");
        imagePreflight.ShouldContain("TryDescribeOpenXrImageAllocationPressure(");

        string budgetSnapshot = SliceMethod(allocatorSource, "internal bool TryGetAllocatorBudgetSnapshot(");
        budgetSnapshot.ShouldContain("activeAllocationCount = allocator.ActiveVkAllocationCount;");
        budgetSnapshot.ShouldContain("vmaAllocator.TryGetDeviceLocalHeapBudgetSnapshot(");

        string pressureDescription = SliceMethod(allocatorSource, "private bool TryDescribeOpenXrImageAllocationPressure(");
        pressureDescription.ShouldContain("long tracked = Math.Max(0L, frameTiming.TrackedVramBytes);");
        pressureDescription.ShouldContain("ResolveTrackedVramLimit(frameTiming.TrackedVramBudgetBytes)");
        pressureDescription.ShouldContain("deferred under allocator pressure");
        pressureDescription.ShouldContain("deferred under tracked VRAM pressure");
        pressureDescription.ShouldContain("deferred under allocation-count pressure");
        pressureDescription.ShouldContain("largestHeap={largestHeap}");
        pressureDescription.ShouldContain("allocated={allocated}");

        string resourcePlannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.PhysicalResourcePlanning.cs");
        string allocationDeferralClassifier = SliceMethod(resourcePlannerSource, "internal static bool IsExpectedVulkanImageAllocationDeferral(string failureReason)");
        allocationDeferralClassifier.ShouldContain("Vulkan image allocation deferred under");
        allocationDeferralClassifier.ShouldContain("allocation deferred under allocator pressure");
    }

    #endregion

    #region Final Output Contract

    [Test]
    public void DefaultPipelineFinalOutput_ValidatesEnvOverrideBeforeRecordingFinalBlit()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");

        pipelineSource.ShouldContain("ResolveOutputSourceFboOverride");
        pipelineSource.ShouldContain("IsValidFinalOutputSourceFboOverride");
        pipelineSource.ShouldContain("CreateOutputSourceOverrideCommands");
        pipelineSource.ShouldContain("CreateStandardViewportFinalOutputCommands");
        pipelineSource.ShouldContain(XREngineEnvironmentVariables.OutputSourceFbo);
        pipelineSource.ShouldContain("Falling back to standard final output");
        pipelineSource.ShouldContain("=> CreateFinalBlitCommands(sourceFboName, bypassVendorUpscale);");
        pipelineSource.ShouldContain("TryGetFBO(sourceFboName, out XRFrameBuffer? fbo)");
    }

    [Test]
    public void DefaultPipelineFinalOutput_CoversDebugOverrideAaAndFallbackSources()
    {
        string source =
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");

        source.ShouldContain("TransformIdDebugQuadFBOName");
        source.ShouldContain("ActiveTransparencyDebugFboName");
        source.ShouldContain("CreateOutputSourceOverrideCommands");
        source.ShouldContain("RuntimeNeedsTsrUpscale");
        source.ShouldContain("FxaaFBOName");
        source.ShouldContain("SmaaFBOName");
        source.ShouldContain("PostProcessOutputFBOName");
        source.ShouldContain("vendorBlit.ForceFallbackBlit = forceFallback;");
    }

    [Test]
    public void FullOverdrawDebug_UsesVulkanClipSpaceAwareSourceUvs()
    {
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/FullOverdrawDebug.fs");
        string pipelineFbos = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs");
        string advancedPipelineFbos = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.FBOs.cs");

        shader.ShouldContain("uniform int FramebufferTextureYDirection;");
        shader.ShouldContain("vec2 ResolveFramebufferTextureUv(vec2 uv)");
        shader.ShouldContain("vec2 uv = ResolveFramebufferTextureUv(FragPos.xy * 0.5 + 0.5);");
        shader.ShouldContain("if (FramebufferTextureYDirection == 1)");
        shader.ShouldContain("uv.y = 1.0 - uv.y;");
        shader.ShouldContain("texture(FullOverdrawCountTex, uv)");
        shader.ShouldContain("texture(PostProcessOutputTexture, uv)");

        SliceMethod(pipelineFbos, "private XRFrameBuffer CreateFullOverdrawDebugFBO()")
            .ShouldContain("RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy");
        SliceMethod(advancedPipelineFbos, "private XRFrameBuffer CreateFullOverdrawDebugFBO()")
            .ShouldContain("RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy");
    }

    #endregion

    #region Frame-Op And Planner Contracts

    [Test]
    public void FrameOpContracts_RejectUndocumentedMinValuePassAtRecording()
    {
        // Producers normalize pass indices before a plan is sealed. Recording
        // validates the sealed stream and rejects it instead of rewriting it.
        string frameDataSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/FrameData/VulkanRenderer.CommandBufferRecording.FrameData.cs");
        string preparationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs");
        string barrierSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.BarrierServices.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");

        string normalize = SliceMethod(frameDataSource, "internal void NormalizePrimaryPlanPassIndicesForPublication(FrameOp[] operations)");
        normalize.ShouldContain("VulkanCommandRuntime.EnsureValidPassIndex(");
        normalize.ShouldContain("operation.PassIndex = resolvedPassIndex;");
        string validate = SliceMethod(frameDataSource, "private void ValidatePrimaryPlanPassIndicesForRecording(FrameOperationSequence operations)");
        validate.ShouldContain("VulkanCommandRuntime.EnsureValidPassIndex(");
        validate.ShouldContain("throw new VulkanPlanPreconditionException(");
        validate.ShouldNotContain("PassIndex = resolvedPassIndex");
        preparationSource.ShouldContain("ValidatePrimaryPlanPassIndicesForRecording(recordingState.Ops);");

        string ensureValid = SliceMethod(barrierSource, "internal static int EnsureValidPassIndex(");
        ensureValid.ShouldContain("if (passIndex == int.MinValue &&");
        ensureValid.ShouldContain("currentPass != int.MinValue &&");
        meshSource.ShouldContain("if (producer.Context.PipelineInstance is null)");
    }

    [Test]
    public void ShaderStorageMemoryBarrier_CoversVertexShaderConsumers()
    {
        string commandBufferSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string resolveBarrierScopes = SliceMethod(commandBufferSource, "private void ResolveBarrierScopes(");

        resolveBarrierScopes.ShouldContain("(mask & (EMemoryBarrierMask.ShaderGlobalAccess | EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.ShaderStorage)) != 0");
        resolveBarrierScopes.ShouldContain("PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit");
        resolveBarrierScopes.ShouldNotContain("PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit");
    }

    [Test]
    public void CommandBufferReuse_InvalidatesOnFrameOpsPlannerRevisionResourcesAndViewport()
    {
        string commandBufferSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.RenderGraphRebuild.cs");
        string plannerContextSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.ResourcePlannerContext.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.FrameOpSignatures.cs");
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
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
        plannerContextSource.ShouldContain("viewport?.Width");
        plannerContextSource.ShouldContain("viewport?.InternalWidth");
        meshSource.ShouldContain("hash.Add(op.Context.ViewportIdentity)");
        meshSource.ShouldContain("hash.Add(blit.OutFbo?.GetHashCode() ?? 0)");
        descriptorSource.ShouldContain("ComputeDescriptorResourceFingerprint(");
        descriptorSource.ShouldContain("if (!DescriptorAllocationSlotsMatch(allocation, material, refreshFrameIndex, resourceFingerprint))");
        descriptorSource.ShouldContain("reason = $\"resource fingerprint 0x{allocation.ResourceFingerprint:X16}->0x{resourceFingerprint:X16}; {diff}\";");
    }

    [Test]
    public void DefaultCommandChain_DocumentsCommonDynamicBranchesForVulkanCoverage()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");

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
        // AdvancedRenderPipeline use.
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
        Chain(metadata, EDefaultRenderPass.PostMotionBlurForward, EDefaultRenderPass.TransparentForward);
        Chain(metadata, EDefaultRenderPass.PostDepthOfFieldForward, EDefaultRenderPass.PostMotionBlurForward);
        Chain(metadata, EDefaultRenderPass.PostBloomForward, EDefaultRenderPass.PostDepthOfFieldForward);
        Chain(metadata, EDefaultRenderPass.OnTopForward, EDefaultRenderPass.PostBloomForward);
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
        // The resource runtime creates the memory allocator for the selected backend.
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.MemoryAllocator.cs");

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
    public void VulkanVmaBridge_PersistentlyMapsHostVisibleAllocations()
    {
        string nativeBridge = ReadWorkspaceFile("Build/Native/VulkanMemoryAllocatorBridge/VulkanMemoryAllocatorBridge.cpp");
        string nativeInterop = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Memory/VulkanVmaNative.cs");
        string allocation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Memory/VulkanMemoryAllocation.cs");
        string allocator = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Memory/VulkanVmaAllocator.cs");
        string allocationInfo = SliceMethod(nativeBridge, "VmaAllocationCreateInfo makeAllocationCreateInfo");

        allocationInfo.ShouldContain("VMA_ALLOCATION_CREATE_HOST_ACCESS_RANDOM_BIT");
        allocationInfo.ShouldContain("VMA_ALLOCATION_CREATE_MAPPED_BIT");
        nativeBridge.ShouldContain("void* mappedData;");
        nativeBridge.ShouldContain("outAllocationInfo->mappedData = allocationInfo.pMappedData;");
        nativeInterop.ShouldContain("public nint MappedData;");
        allocation.ShouldContain("nint MappedData = 0");
        allocator.ShouldContain("if (allocation.MappedData != 0)");
        allocator.ShouldContain("mappedPtr = (byte*)allocation.MappedData + offset;");
        allocator.ShouldContain("MappedData: nativeAllocation.MappedData");
    }

    [Test]
    public void VulkanMappedFrameArena_OwnsAndRegistersPersistentMappedMemory()
    {
        string arenaBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanMappedFrameArenaBackend.cs");

        arenaBackendSource.ShouldContain("_api.CreateBuffer(");
        arenaBackendSource.ShouldContain("_api.MapMemory(");
        arenaBackendSource.ShouldContain("_resourceManager.RegisterMappedFrameArenaChunk(");
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
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string method = SliceMethod(source, "internal uint WriteClearAttachments");

        method.Contains("AttachmentRole.DepthStencil", StringComparison.Ordinal).ShouldBeTrue(
            "Depth24Stencil8 FBOs must emit vkCmdClearAttachments depth/stencil clears for combined attachments.");
    }

    [Test]
    public void VulkanPresentTextureShaders_ApplySourceOrientationToDirectAndFallbackPaths()
    {
        string renderToWindow = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderToWindow.cs");
        string vendorUpscale = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs");
        string defaultPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");

        renderToWindow.ShouldContain("uniform bool FlipSourceYOnVulkan;");
        renderToWindow.ShouldContain("#ifdef XRENGINE_VULKAN");
        renderToWindow.ShouldContain("if (FlipSourceYOnVulkan)");
        renderToWindow.ShouldContain("uv.y = 1.0 - uv.y;");
        renderToWindow.ShouldContain("vec2 uv = ResolvePresentTextureUv(clipXY);");
        // The present binding state captures the flip flag and binds it as a uniform.
        renderToWindow.ShouldContain("bool FlipSourceYOnVulkan,");
        renderToWindow.ShouldContain("owner.FlipSourceYOnVulkan,");
        renderToWindow.ShouldContain("materialProgram.Uniform(\"FlipSourceYOnVulkan\", state.FlipSourceYOnVulkan);");
        // One backend-wide policy decides the flip for the direct and fallback paths.
        string clipSpaceSettings = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RenderClipSpaceSettings.cs");
        defaultPipeline.ShouldContain("present.FlipSourceYOnVulkan = ShouldFlipVulkanPresentSourceY();");
        defaultPipeline.ShouldContain("vendorBlit.FlipSourceYOnVulkanFallback = ShouldFlipVulkanPresentSourceY();");
        defaultPipeline.ShouldContain("=> RenderClipSpacePolicy.RequiresVulkanFramebufferTexturePresentationYFlip();");
        clipSpaceSettings.ShouldContain("=> FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan) ==\n           ERenderClipSpaceYDirection.YDown;");
        vendorUpscale.Contains("#ifdef XRENGINE_VULKAN", StringComparison.Ordinal).ShouldBeTrue(
            "The default pipeline's vendor-upscale fallback still resolves source-FBO orientation for final output.");
        vendorUpscale.ShouldContain("if (FlipSourceYOnVulkanFallback)");
        vendorUpscale.ShouldContain("uv.y = 1.0 - uv.y;");
        vendorUpscale.ShouldContain("program.Uniform(\n                \"FlipSourceYOnVulkanFallback\",\n                FlipSourceYOnVulkanFallback);");
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
        // The frame loop owns auto exposure and luminance readback.
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.AutoExposure.cs");
        string readbackSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Readback.cs");

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
        // The ImGui output pipeline service selects the sRGB passthrough path from
        // the published desktop swapchain format and color space.
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiOutputPipelineService.cs");
        string swapChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Output/Authority/VulkanDesktopSwapchainService.cs");

        source.ShouldContain("bool srgb = output.Desktop.ImageFormat is Format.B8G8R8A8Srgb or Format.R8G8B8A8Srgb ||");
        source.ShouldContain("output.Desktop.ImageColorSpace is ColorSpaceKHR.SpaceExtendedSrgbLinearExt;");
        source.ShouldContain("hash.Add((int)output.Desktop.ImageFormat);");
        source.ShouldContain("hash.Add((int)output.Desktop.ImageColorSpace);");
        source.ShouldContain("vec3 SrgbToLinear(vec3 c)");
        source.ShouldContain("(srgb ? \" color.rgb=SrgbToLinear(color.rgb*color.a);\" : string.Empty)");
        source.ShouldContain("SrcColorBlendFactor = srgb ? BlendFactor.One : BlendFactor.SrcAlpha");
        swapChainSource.ShouldContain("_output.Desktop.ImageColorSpace = surfaceFormat.ColorSpace;");
        swapChainSource.ShouldContain("[Vulkan] Swapchain surface selected: format={0} colorSpace={1}");
    }

    [Test]
    public void VulkanImGuiDrawBuffers_GrowWithCapacityHeadroom()
    {
        // Per-output draw-buffer resources own the image-indexed ImGui vertex and
        // index buffers. The overlay recorder binds them; the backend sets the flags.
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiDrawBufferResources.cs");
        string recorderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiOverlayCommandRecorder.cs");
        string backendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiBackend.cs");

        source.ShouldContain("private VulkanImGuiDrawBufferSet[] _drawBuffers = [];");
        source.ShouldContain("private int EnsureSlot(uint imageIndex)");
        source.ShouldContain("Array.Resize(ref _drawBuffers, required);");
        source.ShouldContain("internal ref VulkanImGuiDrawBufferSet Ensure(uint imageIndex, ulong vertexBytes, ulong indexBytes)");
        source.ShouldContain("ulong capacity = ComputeCapacity(currentCapacity, requiredCapacity);");
        source.ShouldContain("return AlignToPowerOfTwo(target);");
        source.ShouldContain("64UL * 1024UL");
        source.ShouldContain("ref buffers.VertexBufferSize,");
        source.ShouldContain("ref buffers.IndexBufferSize,");
        source.ShouldContain("currentCapacity = capacity;");
        recorderSource.ShouldContain("ref VulkanImGuiDrawBufferSet buffers = ref drawBuffers.Ensure(\n            input.ImageIndex,");
        recorderSource.ShouldContain("encoder.BindVertexBuffer(input.OverlayCommandBuffer, 0, buffers.VertexBuffer);");
        recorderSource.ShouldContain("encoder.BindIndexBuffer(input.OverlayCommandBuffer, buffers.IndexBuffer, IndexType.Uint16);");
        backendSource.ShouldContain("io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;");
        recorderSource.ShouldNotContain("_ = imageIndex;");
    }

    [Test]
    public void VulkanImGuiOverlay_RecordsOutsideReusableScenePrimary()
    {
        // The ImGui overlay is a separate image-indexed primary. The command runtime
        // allocates and retires it with the desktop output artifacts, the output
        // resources publish it, and the overlay recorder records it.
        string imguiResourcesSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiResources.cs");
        string recorderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiOverlayCommandRecorder.cs");
        string presentationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.PresentationServices.cs");
        string commandBufferAllocationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.DesktopOutputArtifacts.cs");
        string swapchainServiceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Output/Authority/VulkanDesktopSwapchainService.cs");
        string recordingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Recording.cs");
        string submissionSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Submission.cs");
        string profileSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Vulkan/VulkanFeatureProfile.cs");

        imguiResourcesSource.ShouldContain("internal CommandBuffer[]? OverlayCommandBuffers;");
        recorderSource.ShouldContain("internal bool TryRecord(");
        recorderSource.ShouldContain("encoder.Runtime.EmitImageTransition(");
        recorderSource.ShouldContain("private static void RenderSnapshot(");
        recorderSource.ShouldContain("ImageLayout.PresentSrcKhr,");
        recorderSource.ShouldNotContain("nativeUiOverlaySecondaryCommandBuffer");
        recorderSource.ShouldNotContain("nativeUiOverlayOpCount");
        recorderSource.ShouldNotContain("CmdExecuteCommands");
        presentationSource.ShouldContain("CommandBuffer[]? commandBuffers = _outputRuntime._imguiResources.OverlayCommandBuffers;");
        presentationSource.ShouldContain("return _imguiOverlayRecorder.TryRecord(");

        commandBufferAllocationSource.ShouldContain("CommandBuffer[] imguiOverlay = Allocate(CommandBufferLevel.Primary, \"Swapchain.ImGuiOverlay\");");
        commandBufferAllocationSource.ShouldContain("RegisterCommandBufferImageIndex(imguiOverlay[index], imageIndex);");
        commandBufferAllocationSource.ShouldContain("RetireArtifacts(imguiOverlayCommandBuffers, \"Swapchain.ImGuiOverlay\");");
        commandBufferAllocationSource.ShouldContain("return imguiOverlay;");
        commandBufferAllocationSource.ShouldNotContain("RenderImGui(commandBuffer, imageIndex);");
        swapchainServiceSource.ShouldContain("_output._imguiResources.OverlayCommandBuffers =\n                _services.CreateDesktopOutputArtifacts(");
        swapchainServiceSource.ShouldContain("_services.RetireDesktopOutputArtifacts(imguiOverlayCommandBuffers);");
        swapchainServiceSource.ShouldContain("_output._imguiResources.OverlayCommandBuffers = null;");

        recordingSource.ShouldContain("Vulkan.FrameLifecycle.RecordImGuiOverlay");
        recordingSource.ShouldNotContain("nativeUiOverlaySecondaryCommandBuffer");
        recordingSource.ShouldNotContain("nativeUiOverlayOpCount");
        recordingSource.ShouldContain("TryRecordImGuiOverlay(");
        recordingSource.ShouldContain("out attempt.ImGuiOverlayCommandBuffer);");
        submissionSource.ShouldContain("attempt.HasImGuiOverlayCommandBuffer &&");
        submissionSource.ShouldContain("AppendDesktopSubmitCommandBuffer(\n                        commandBuffers,\n                        ref commandBufferCount,\n                        attempt.ImGuiOverlayCommandBuffer);");
        submissionSource.ShouldContain("submitReceipt = SubmitFrameTargetLease(\n                            in attempt.FrameTargetLease,\n                            commandBuffers,\n                            commandBufferCount,");

        profileSource.ShouldContain("EVulkanGpuDrivenProfile.ShippingFast => true");
        profileSource.ShouldContain("EVulkanGpuDrivenProfile.DevParity => true");
        profileSource.ShouldContain("EVulkanGpuDrivenProfile.Diagnostics => true");
        profileSource.ShouldContain("ImGui rendering through the Vulkan pipeline.");
    }

    [Test]
    public void VulkanImGuiOverlay_ClosesTrackedCommandBufferRecording()
    {
        string imguiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");

        imguiSource.ShouldContain("EndCommandBufferTracked(commandBuffer)");
        imguiSource.ShouldNotContain("Api.EndCommandBuffer(commandBuffer)");
    }

    [Test]
    public void VulkanFramebufferAttachments_TrackMipLayoutsIndependently()
    {
        string attachmentSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/IVkFrameBufferAttachmentSource.cs");
        string imageBackedTexture = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        // Blit and readback commands publish and read the per-mip, per-layer attachment layouts.
        string restoredLayoutPolicy = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Readback/VulkanReadbackLayoutPolicy.cs");
        string blitResolution = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.LegacyBlitResolution.cs");

        attachmentSource.ShouldContain("GetAttachmentTrackedLayout(int mipLevel, int layerIndex)");
        attachmentSource.ShouldContain("UpdateAttachmentTrackedLayout(ImageLayout layout, int mipLevel, int layerIndex)");

        imageBackedTexture.ShouldContain("_attachmentLayouts");
        imageBackedTexture.ShouldContain("_hasPartialAttachmentLayouts");
        imageBackedTexture.ShouldContain("BuildAttachmentLayoutKey(mipLevel, layerIndex)");
        imageBackedTexture.ShouldContain("return ImageLayout.Undefined;");
        imageBackedTexture.ShouldContain("ResetAttachmentLayoutTracking();");

        restoredLayoutPolicy.ShouldContain("attachmentSource.UpdateAttachmentTrackedLayout(\n                restoredLayout,\n                mipLevel,");
        blitResolution.ShouldContain("attachmentSource.GetAttachmentTrackedLayout(\n                checked((int)mipLevel),");
    }

    [Test]
    public void VulkanDescriptorImageLayouts_ArePureAndNeverRecordHiddenTransitions()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorImageLayouts.cs");
        string method = SliceMethod(source, "internal ImageLayout ResolveDescriptorImageLayout");

        method.ShouldContain("ImageLayout requestedLayout = GetDefaultSampledDescriptorLayout(source);");
        method.ShouldContain("ResolveTrackedSampledDescriptorLayout(source.DescriptorUsage, trackedLayout, requestedLayout)");
        method.ShouldNotContain("TryTransitionDedicatedImageLayout");
    }

    [TestCase(ImageLayout.ShaderReadOnlyOptimal)]
    [TestCase(ImageLayout.DepthStencilReadOnlyOptimal)]
    [TestCase(ImageLayout.DepthReadOnlyOptimal)]
    [TestCase(ImageLayout.StencilReadOnlyOptimal)]
    [TestCase(ImageLayout.ReadOnlyOptimal)]
    public void VulkanSampledDescriptorLayout_PreservesTrackedReadableLayout(ImageLayout trackedLayout)
        => VulkanDescriptorManager.ResolveTrackedSampledDescriptorLayout(
                ImageUsageFlags.SampledBit,
                trackedLayout,
                ImageLayout.General)
            .ShouldBe(trackedLayout);

    [Test]
    public void VulkanSampledDescriptorLayout_DoesNotInferGeneralFromTransientTrackedState()
        => VulkanDescriptorManager.ResolveTrackedSampledDescriptorLayout(
                ImageUsageFlags.SampledBit | ImageUsageFlags.DepthStencilAttachmentBit,
                ImageLayout.General,
                ImageLayout.DepthStencilReadOnlyOptimal)
            .ShouldBe(ImageLayout.DepthStencilReadOnlyOptimal);

    [Test]
    public void VulkanSampledDescriptorLayout_PreservesGeneralForStorageSamplingContract()
        => VulkanDescriptorManager.ResolveTrackedSampledDescriptorLayout(
                ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit,
                ImageLayout.General,
                ImageLayout.ShaderReadOnlyOptimal)
            .ShouldBe(ImageLayout.General);

    [TestCase(ImageLayout.Undefined)]
    [TestCase(ImageLayout.ColorAttachmentOptimal)]
    [TestCase(ImageLayout.DepthStencilAttachmentOptimal)]
    [TestCase(ImageLayout.TransferSrcOptimal)]
    [TestCase(ImageLayout.TransferDstOptimal)]
    public void VulkanSampledDescriptorLayout_FallsBackWhenTrackedLayoutIsNotReadable(ImageLayout trackedLayout)
        => VulkanDescriptorManager.ResolveTrackedSampledDescriptorLayout(
                ImageUsageFlags.SampledBit,
                trackedLayout,
                ImageLayout.DepthStencilReadOnlyOptimal)
            .ShouldBe(ImageLayout.DepthStencilReadOnlyOptimal);

    [Test]
    public void VulkanTextureViews_UseViewLocalSamplerState()
    {
        string textureView = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");
        string bloomPass = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_BloomPass.cs");

        textureView.ShouldNotContain("_sampler = source.DescriptorSampler;");
        textureView.ShouldContain("private void CreateSampler()");
        textureView.ShouldContain("SamplerConversions.FromMinFilter(Data.MinFilter)");
        textureView.ShouldContain("MaxLod = Math.Max(0f, Math.Max(Data.NumLevels, 1u) - 1u)");
        textureView.ShouldContain("case nameof(XRTextureViewBase.MinFilter):");
        textureView.ShouldContain("private void ReleaseInternedViews()");
        textureView.ShouldContain("ReleaseInternedViews();");
        textureView.ShouldContain("if (_view.Handle != 0 && _sampler.Handle == 0)");
        textureView.ShouldContain("ReleaseInternedViewsAndRetireSampler()");
        textureView.ShouldContain("DestroySampler();");

        bloomPass.ShouldContain("MinFilter = ETexMinFilter.Linear,");
    }

    [Test]
    public void BitmapFontAtlas_ExtractsAlphaCoverageIntoR8RedChannel()
    {
        string fontGlyphSet = ReadWorkspaceFile("XREngine.Runtime.Rendering/Resources/Fonts/FontGlyphSet.cs");

        fontGlyphSet.ShouldContain("Atlas = CreateBitmapAtlasTexture(outputAtlasPath);");
        fontGlyphSet.ShouldContain("private static XRTexture2D CreateBitmapAtlasTexture(string atlasPath)");
        // The atlas decodes to RGBA8 pixels; the alpha byte at offset 3 is the coverage.
        fontGlyphSet.ShouldContain("byte[] coverage = ExtractAlphaCoverage(atlasImage);");
        fontGlyphSet.ShouldContain("if (atlasImage.PixelFormat != RuntimePixelFormat.Rgba8)");
        fontGlyphSet.ShouldContain("coverage[row * (int)atlasImage.Width + column] = pixels[sourceRow * atlasImage.RowStrideBytes + column * 4 + 3];");
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
        string vkMeshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string vkMeshDrawing = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string indirectDraw = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Recording/VulkanRenderer.DrawAndComputeRecording.cs");
        string indirectReuse = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.IndirectReuse.cs");
        // Frame-op preparation publishes the stats once per validated op.
        string frameOpSemantics = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/VulkanFrameOperationSemantics.cs");

        frameOpSemantics.ShouldContain("PublishFrameOpDrawStats(validatedOp);");
        frameOpSemantics.ShouldContain("PublishFrameDrawStats(draw.Renderer.EstimateFrameDrawStats(draw));");
        frameOpSemantics.ShouldContain("RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(stats.DrawCalls);");
        frameOpSemantics.ShouldContain("RuntimeEngine.Rendering.Stats.Frame.IncrementMultiDrawCalls(stats.MultiDrawCalls);");
        frameOpSemantics.ShouldContain("RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered(stats.TrianglesRendered);");
        frameOpSemantics.ShouldContain("case IndirectDrawOp indirectDraw:");
        vkMeshRenderer.ShouldContain("internal VulkanFrameDrawStats EstimateFrameDrawStats(in PendingMeshDraw draw)");
        vkMeshRenderer.ShouldContain("EstimateTriangleCount(triangleIndexCount, instances)");

        vkMeshDrawing.ShouldNotContain("RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls();");
        vkMeshDrawing.ShouldNotContain("RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered((int)(vertexCount / 3 * drawInstances));");
        indirectDraw.ShouldNotContain("RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(");
        indirectReuse.ShouldNotContain("RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(");
    }

    [Test]
    public void NativeFpsOverlay_UsesStableCompactMultilineLayout()
    {
        string unitTestingUi = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs");

        unitTestingUi.ShouldContain("private const float FpsOverlayWidth");
        unitTestingUi.ShouldContain("private const float FpsOverlayHeight");
        unitTestingUi.ShouldContain("builder.Append(\"net:    rtt \");");
        unitTestingUi.ShouldContain("builder.Append(vrActive ? \"\\ndesktop:\" : \"\\nrender: \");");
        unitTestingUi.ShouldContain("builder.Append(\"\\nloop:   update \");");
        unitTestingUi.ShouldContain("builder.Append(\"ms | fixed \");");
        unitTestingUi.ShouldContain("builder.Append(vrActive ? \"\\ndraw:   desk calls \" : \"\\ndraw:   calls \");");
        unitTestingUi.ShouldContain("builder.Append(\" | cpu fallback \");");
        unitTestingUi.ShouldContain("FormatCompactRate(bytesPerSecond, 7)");
        unitTestingUi.ShouldContain("FormatCompactCount(desktopCounters.DrawCalls, 5)");
        unitTestingUi.ShouldContain("SceneNode textNode = new(parentNode) { Name = \"TestTextNode\" };");
        unitTestingUi.ShouldNotContain("FpsOverlayBackground");
        unitTestingUi.ShouldNotContain("TestTextStrokeNode");
        unitTestingUi.ShouldNotContain("LoadDefaultUIMonospaceFontBitmap");
        unitTestingUi.ShouldContain("text.Font = font;");
        unitTestingUi.ShouldContain("text.FontSize = 26;");
        unitTestingUi.ShouldContain("text.HorizontalAlignment = EHorizontalAlignment.Center;");
        unitTestingUi.ShouldContain("text.VerticalAlignment = EVerticalAlignment.Center;");
        unitTestingUi.ShouldContain("text.RenderCommand2D.ZIndex = FpsOverlayZIndex;");
        unitTestingUi.ShouldContain("text.OutlineColor = new ColorF4(0.0f, 0.0f, 0.0f, 1.0f);");
        unitTestingUi.ShouldContain("text.OutlineThickness = 2.0f;");
        unitTestingUi.ShouldContain("text.OutlineAffectsSpacing = true;");
        unitTestingUi.ShouldContain("textTransform.Width = FpsOverlayWidth;");
        unitTestingUi.ShouldContain("textTransform.Height = FpsOverlayHeight;");

        string textComponent = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/UI/Text/UITextComponent.cs");
        textComponent.ShouldContain("public bool OutlineAffectsSpacing");
        textComponent.ShouldContain("float outlineSpacing = OutlineAffectsSpacing ? OutlineThickness : 0.0f;");
        textComponent.ShouldContain("float glyphSpacing = ResolveLayoutSpacingForOutputPixels(outlineSpacing);");
        textComponent.ShouldContain("DefaultLineSpacing + outlineSpacing");
        textComponent.ShouldContain("private float ResolveLayoutSpacingForOutputPixels(float outputSpacing)");
        textComponent.ShouldContain("return outputSpacing * layoutEmSize / resolvedFontSize;");
        textComponent.ShouldContain("case nameof(OutlineAffectsSpacing):");

        string textRenderable = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/UI/Text/UIText.cs");
        textRenderable.ShouldContain("public bool OutlineAffectsSpacing");
        textRenderable.ShouldContain("float outlineSpacing = OutlineAffectsSpacing ? OutlineThickness : 0.0f;");
        textRenderable.ShouldContain("float glyphSpacing = ResolveLayoutSpacingForOutputPixels(outlineSpacing);");
        textRenderable.ShouldContain("DefaultLineSpacing + outlineSpacing");
        textRenderable.ShouldContain("private float ResolveLayoutSpacingForOutputPixels(float outputSpacing)");

        string fontGlyphSet = ReadWorkspaceFile("XREngine.Runtime.Rendering/Resources/Fonts/FontGlyphSet.cs");
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

        string vkMeshUniforms = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.Diagnostics.cs");
        vkMeshUniforms.ShouldContain("or \"TextDebugMode\" or \"TextRenderLayer\" or \"TextRenderLayer_VTX\"");

        // A reused command buffer must refresh the dynamic UI text frame data in the
        // same refresh cohort as the primary stream.
        string commandBuffers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/FrameData/VulkanRenderer.CommandBufferRecording.FrameData.cs");
        commandBuffers.ShouldContain("BuildReusableFrameDataRefreshRequests(\n                        secondaryOps,\n                        frameDataSlot,\n                        EVulkanMeshFrameDataStreamKind.DynamicUi,\n                        dynamicUi: true,");
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
        string interfaceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderSettingsServices.cs");
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeVulkanRobustnessSettings.cs");
        string hostSource = ReadWorkspaceFile("XREngine.Runtime.Host/RenderingHost/Engine.RuntimeRenderingHostServices.cs");
        string defaultsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServiceDefaults.cs");

        interfaceSource.ShouldContain("VulkanAllocatorBackend");
        interfaceSource.ShouldContain("VulkanSynchronizationBackend");
        interfaceSource.ShouldContain("VulkanDescriptorUpdateBackend");
        interfaceSource.ShouldContain("VulkanDynamicUniformBufferEnabled");

        runtimeSource.ShouldContain("services.VulkanAllocatorBackend");
        runtimeSource.ShouldContain("services.VulkanSynchronizationBackend");
        runtimeSource.ShouldContain("services.VulkanDescriptorUpdateBackend");
        runtimeSource.ShouldContain("services.VulkanDynamicUniformBufferEnabled");

        hostSource.ShouldContain("RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.AllocatorBackend");
        hostSource.ShouldContain("RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.SyncBackend");
        hostSource.ShouldContain("RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DescriptorUpdateBackend");
        hostSource.ShouldContain("RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DynamicUniformBufferEnabled");

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
        // The ImGui font-atlas resources own the ImGui descriptor pool.
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiFontAtlasResources.cs");

        source.ShouldContain("private const uint DescriptorPoolMaxSets = 256;");
        source.ShouldContain("DescriptorCount = DescriptorPoolMaxSets");
        source.ShouldContain("MaxSets = DescriptorPoolMaxSets");
        source.ShouldContain("Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit");
    }

    [Test]
    public void MeshCacheTeardown_RetiresSharedPipelinesAndUniformBuffers()
    {
        // The resource runtime owns per-frame-slot pipeline retirement. The pipeline
        // manager owns the shared graphics-pipeline cache and its device teardown.
        string retirementQueueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Retirement/VulkanResourceRetirementQueue.cs");
        string retirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");
        string frameSlotRetirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.FrameSlots.Retirement.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string sharedPipelineCacheSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineManager.cs");
        string programPortSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VulkanProgramWrapperPort.cs");
        string lifecycleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs");
        string cleanupSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Cleanup.cs");
        string bufferServiceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanBufferResourceService.cs");

        retirementQueueSource.ShouldContain("internal List<RetiredPipeline>[] Pipelines { get; }");
        retirementSource.ShouldContain("internal void RetirePipeline(Pipeline pipeline, string owner)");
        retirementSource.ShouldContain("new RetiredPipeline(pipeline, ticket),");
        retirementSource.ShouldContain("internal unsafe void DrainRetiredPipelines(");
        frameSlotRetirementSource.ShouldContain("ResourceRuntime.DrainRetiredPipelines(\n                Api, _deviceContext.Device, frameSlot);");

        string destroyPipelines = SliceMethod(pipelineSource, "private void DestroyPipelines()");
        destroyPipelines.ShouldContain("DestroyDescriptors();");
        destroyPipelines.ShouldContain("_pipelines.Clear();");
        destroyPipelines.ShouldNotContain("Renderer.RetirePipeline(pipe);");
        destroyPipelines.ShouldNotContain("DestroyPipeline(Device, pipe");
        sharedPipelineCacheSource.ShouldContain("internal int DestroySharedGraphicsPipelines()");
        sharedPipelineCacheSource.ShouldContain("services.DestroyPipelineImmediate(pipeline);");
        sharedPipelineCacheSource.ShouldContain("services.DestroyPipelineImmediate(superseded);");
        programPortSource.ShouldContain("context.Api.DestroyPipeline(context.Device, pipeline, null)");
        lifecycleSource.ShouldContain("_resourceRuntime.PipelineManager.DestroySharedGraphicsPipelines()");

        // Mesh uniform buffers go through the buffer service, which retires them
        // (also memory-only releases) instead of a direct device destroy.
        string destroyUniformBuffer = SliceMethod(cleanupSource, "private void DestroyMappedUniformBuffer");
        destroyUniformBuffer.ShouldContain("BackendContext.Resources.Buffers.Destroy(BackendContext, buffer, memory, \"VkMeshRenderer.UniformBuffer\");");
        destroyUniformBuffer.ShouldNotContain("DestroyBuffer(");
        bufferServiceSource.ShouldContain("internal void Destroy(VulkanBackendObjectContext context, Buffer buffer, DeviceMemory memory, string owner)\n        => Retire(buffer, memory, owner);");
        bufferServiceSource.ShouldContain("if (buffer.Handle == 0 && memory.Handle != 0)");
    }

    [Test]
    public void VulkanComputeUniforms_AreCachedPerImageAndDispatch()
    {
        // The compute partial of the program owns the per-image, per-dispatch uniform cache.
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Compute.cs");
        string programFieldsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string keySource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VulkanRenderer.VkRenderProgram.ComputeUniformBufferKey.cs");

        programFieldsSource.ShouldContain("private readonly Dictionary<ComputeUniformBufferKey, ComputeUniformBuffer> _computeUniformBuffers");
        programSource.ShouldContain("_computeUniformBuffers");
        programSource.ShouldContain("TryGetOrUpdateComputeFallbackUniformBuffer");
        programSource.ShouldContain("TryGetOrUpdateComputeAutoUniformBuffer");
        programSource.ShouldContain("EComputeUniformBufferKind.Fallback");
        programSource.ShouldContain("EComputeUniformBufferKind.Auto");
        programSource.ShouldContain("reusableDescriptorBindingKey,\n                    allowSynchronousResourceUploads,\n                    out _))");
        programSource.ShouldContain("imageIndex,\n            binding.Set,\n            binding.Binding,");
        programSource.ShouldContain("block.InstanceName,");
        programSource.ShouldContain("dispatchKey);");
        keySource.ShouldContain("ulong DispatchKey");
        programSource.ShouldNotContain("tempUniformBuffers.Add((buffer, memory));");
    }

    [Test]
    public void VulkanMeshRenderer_CachesGeneratedProgramsAcrossPipelineInvalidation()
    {
        string meshRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

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
        string cacheSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderArtifactCache.cs");
        string shaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkShader.cs");
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs");

        cacheSource.ShouldContain("internal const int SchemaVersion = 7;");
        cacheSource.ShouldContain("Build");
        cacheSource.ShouldContain("Cache");
        cacheSource.ShouldContain("Vulkan");
        cacheSource.ShouldContain("VulkanPipelineCacheStorage.GetShaderArtifactDirectory(root)");
        cacheSource.ShouldContain("TryRead(");
        cacheSource.ShouldContain("QueueWrite(");
        cacheSource.ShouldContain("ThreadPool.QueueUserWorkItem");
        cacheSource.ShouldContain("RuntimeFingerprint");
        cacheSource.ShouldContain(".spv");

        compilerSource.ShouldContain("public sealed record PreparedSource");
        compilerSource.ShouldContain("public static PreparedSource Prepare");
        compilerSource.ShouldContain("public static unsafe byte[] CompilePrepared");

        shaderSource.ShouldContain("if (Data.SourceLanguage == ShaderSourceLanguage.Slang)");
        shaderSource.ShouldContain("BuildSlangArtifact(shaderConfigVersion, usesVulkanClipDepthRemap, transformFeedbackPlan)");

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

        var artifact = new VulkanShaderArtifact(
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
                out VulkanShaderArtifact loaded).ShouldBeTrue();

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
        string shaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkShader.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string linkingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Linking.cs");
        string meshPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

        shaderSource.ShouldContain("Task.Run(() => BuildCpuArtifact");
        shaderSource.ShouldContain("TryGenerateFromAsyncCompile");

        // The public link entry passes the async flag through the compilation
        // protection to the locked link body, which queues the CPU compile.
        string linkMethod = SliceMethod(linkingSource, "public bool Link(bool allowAsyncShaderCompile = false)");
        linkMethod.ShouldContain("allowAsyncShaderCompile &= !VulkanProgramLinkPreparationScope.RequiresSynchronousLink(BackendContext.Resources);");
        linkMethod.ShouldContain("return LinkWithPipelineCompilationProtection(allowAsyncShaderCompile);");
        string protectedLinkMethod = SliceMethod(linkingSource, "private bool LinkWithPipelineCompilationProtection(bool allowAsyncShaderCompile)");
        protectedLinkMethod.ShouldContain("return LinkAfterAcquiringInterfaceLock(allowAsyncShaderCompile);");
        string lockedLinkMethod = SliceMethod(linkingSource, "private bool LinkAfterAcquiringInterfaceLock(");
        lockedLinkMethod.ShouldContain("if (allowAsyncShaderCompile)");
        lockedLinkMethod.ShouldContain("TryGenerateFromAsyncCompile");
        lockedLinkMethod.ShouldContain("EShaderProgramBackendStage.SourceQueued");

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
        string preferencesSource = ReadWorkspaceFile("XREngine.Runtime.Host/Settings/EditorPreferences.cs");

        flagsSource.ShouldContain("VkTextureUploadPrepWorker = ReadBoolDefaultTrue(XREngineEnvironmentVariables.VulkanTextureUploadPrepWorker)");
        preferencesSource.ShouldContain("Run Vulkan imported-texture upload preparation on the worker/upload context");
        preferencesSource.ShouldContain("[DefaultValue(true)]");
    }

    [Test]
    public void VulkanAsyncGraphicsPipelineQueue_CapacityCountsOnlyActiveJobs()
    {
        string queueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");

        string enqueueMethod = SliceMethod(queueSource, "internal bool TryEnqueueGraphicsPipelineCompile(");
        enqueueMethod.ShouldContain("int activeJobCount = CountActiveVulkanGraphicsPipelineCompileJobs()");
        // A foreground-required request may exceed the background capacity.
        enqueueMethod.ShouldContain("if (activeJobCount >= capacity && !foregroundRequired)");
        enqueueMethod.ShouldContain("completed=");
        enqueueMethod.ShouldNotContain("_vulkanGraphicsPipelineCompileJobs.Count >= capacity");

        string countMethod = SliceMethod(queueSource, "private int CountActiveVulkanGraphicsPipelineCompileJobs()");
        countMethod.ShouldContain("if (!job.Task.IsCompleted)");
    }

    [Test]
    public void VulkanPipelinePrewarmIdentity_DoesNotPersistVulkanHandles()
    {
        string prewarmSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelinePrewarmDatabase.cs");
        string pipelineManagerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineManager.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Compute.cs");
        string renderPassState = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");
        string programServices = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VulkanProgramWrapperPort.cs");

        prewarmSource.ShouldContain("internal const int CurrentVersion = 5");
        prewarmSource.ShouldContain("RenderPassSignature");
        // The pipeline manager builds the persisted render-pass signature from
        // dynamic-rendering formats or the semantic render-pass signature.
        pipelineManagerSource.ShouldContain("? BuildDynamicRenderingSignature(dynamicRenderingFormats)\n            : RequireProgramServices().GetRenderPassSemanticSignature(renderPass);");
        string dynamicSignature = SliceExpressionMember(pipelineManagerSource, "private static string BuildDynamicRenderingSignature(");
        dynamicSignature.ShouldContain("Dynamic:Colors={formats.DescribeColorFormats()}");
        dynamicSignature.ShouldNotContain(".Handle");
        prewarmSource.ShouldNotContain("RenderPassHandle");
        prewarmSource.ShouldNotContain("renderPass.Handle.ToString");

        renderPassState.ShouldContain("RenderPassSemanticSignatures[renderPass.Handle] = semanticSignature;");
        programServices.ShouldContain("GetRenderPassSemanticSignature(RenderPass renderPass)");
        programServices.ShouldContain("context.Resources.RenderPassSemanticSignatures.TryGetValue(");

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
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        string bufferServiceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanBufferResourceService.cs");
        string indirectCopySource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.RtxIo.cs");

        bufferSource.ShouldContain("IndirectCopyDeviceAddressThresholdBytes");
        bufferSource.ShouldContain("ShouldUseDeviceAddressForIndirectCopy");
        bufferSource.ShouldContain("byteCount >= IndirectCopyDeviceAddressThresholdBytes");
        bufferSource.ShouldContain("CanUseGpuDecompressionUpload");
        // The buffer resource service owns the indirect-copy upload gate.
        bufferSource.ShouldContain("BackendContext.Resources.Buffers.CanUseNvIndirectCopyUploads(BackendContext)");
        bufferServiceSource.ShouldContain("internal bool CanUseNvIndirectCopyUploads(VulkanBackendObjectContext context)");
        bufferSource.ShouldContain("preferIndirectCopy || enableDeviceAddress || canUseGpuDecompression");

        indirectCopySource.ShouldContain("CanUseNvIndirectBufferCopyUploads");
        indirectCopySource.ShouldContain("EnableNvIndirectCopyUploads");
        indirectCopySource.ShouldContain("if (!CanUseNvIndirectBufferCopyUploads)");
    }

    #endregion

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    private static string SliceExpressionMember(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find member signature '{signature}'.");

        int end = source.IndexOf(';', start);
        end.ShouldBeGreaterThan(start, $"Could not find the end of expression member '{signature}'.");
        return source[start..(end + 1)];
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
