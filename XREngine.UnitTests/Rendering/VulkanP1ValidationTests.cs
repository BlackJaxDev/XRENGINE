using System;
using System.IO;
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
        string statsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.Vulkan.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string materialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string packetSource = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string senderSource = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfilerSender.cs");
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
        senderSource.ShouldContain("VulkanDescriptorFallbackSampledImages = Rendering.Stats.Vulkan.VulkanDescriptorFallbackSampledImages");
        editorSource.ShouldContain("VulkanDescriptorFallbackSampledImages = Engine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackSampledImages");
        profilerUiSource.ShouldContain("Descriptor Fallbacks:");
        profilerUiSource.ShouldContain("Descriptor Failures:");
    }

    [Test]
    public void DescriptorUpdateTemplates_AreBackendGatedAcrossDescriptorPaths()
    {
        string templateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorUpdateTemplates.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string materialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");

        templateSource.ShouldContain("TryUpdateDescriptorSetWithTemplate");
        templateSource.ShouldContain("_descriptorUpdateTemplateCache");
        templateSource.ShouldContain("TryGetOrCreateDescriptorUpdateTemplate");
        templateSource.ShouldContain("ComputeDescriptorUpdateTemplateHash");
        templateSource.ShouldContain("DescriptorUpdateTemplateCreateInfo");
        templateSource.ShouldContain("CreateDescriptorUpdateTemplate");
        templateSource.ShouldContain("UpdateDescriptorSetWithTemplate");
        templateSource.ShouldContain("DestroyDescriptorUpdateTemplateCache");
        logicalDeviceSource.ShouldContain("DestroyDescriptorUpdateTemplateCache()");

        meshSource.ShouldContain("DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template");
        meshSource.ShouldContain("TryUpdateDescriptorSetWithTemplate");
        meshSource.ShouldContain("Api!.UpdateDescriptorSets");
        materialSource.ShouldContain("DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template");
        materialSource.ShouldContain("TryUpdateDescriptorSetWithTemplate");
        materialSource.ShouldContain("Api!.UpdateDescriptorSets");
        programSource.ShouldContain("DescriptorUpdateBackend != EVulkanDescriptorUpdateBackend.Template");
        programSource.ShouldContain("TryUpdateDescriptorSetWithTemplate");
        programSource.ShouldContain("Api!.UpdateDescriptorSets");
    }

    [Test]
    public void CanonicalImmutableSamplers_AreCreatedDestroyedAndAppliedToSamplerLayouts()
    {
        string samplerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.ImmutableSamplers.cs");
        string initSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");
        string layoutCacheSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLayoutCache.cs");

        samplerSource.ShouldContain("VulkanCanonicalSampler");
        samplerSource.ShouldContain("LinearClamp");
        samplerSource.ShouldContain("NearestClamp");
        samplerSource.ShouldContain("LinearRepeat");
        samplerSource.ShouldContain("Anisotropic");
        samplerSource.ShouldContain("ShadowComparison");
        samplerSource.ShouldContain("CreateCanonicalSampler");
        samplerSource.ShouldContain("DestroyCanonicalImmutableSamplers");

        initSource.ShouldContain("InitializeCanonicalImmutableSamplers()");
        logicalDeviceSource.ShouldContain("DestroyCanonicalImmutableSamplers()");
        layoutCacheSource.ShouldContain("DescriptorType.Sampler");
        layoutCacheSource.ShouldContain("TryGetCanonicalImmutableSampler(VulkanCanonicalSampler.LinearClamp");
        layoutCacheSource.ShouldContain("PImmutableSamplers");
        layoutCacheSource.ShouldContain("NativeMemory.Alloc");
        layoutCacheSource.ShouldContain("NativeMemory.Free");
    }

    [Test]
    public void PushConstants_CoverGraphicsComputeAndImGuiPaths()
    {
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string renderProgramSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string renderProgramPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgramPipeline.cs");
        string imguiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");

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
        meshSource.ShouldContain("Renderer.PushConstantsTracked");
        renderProgramSource.ShouldContain("CreateCommonPushConstantRange");
        renderProgramSource.ShouldContain("StageFlags = CommonPushConstantStageFlags");
        renderProgramPipelineSource.ShouldContain("CreateCommonPushConstantRange");
        renderProgramPipelineSource.ShouldContain("StageFlags = CommonPushConstantStageFlags");
        imguiSource.ShouldContain("PushConstantsTracked(commandBuffer, _imguiPipelineLayout");
    }

    [Test]
    public void DynamicUniformRingBuffer_IsInstrumentedForProfilingBeforeAdoption()
    {
        string ringSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanDynamicUniformRingBuffer.cs");
        string statsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.Vulkan.cs");
        string packetSource = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string profilerUiSource = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");

        ringSource.ShouldContain("RecordVulkanDynamicUniformAllocation");
        ringSource.ShouldContain("RecordVulkanDynamicUniformExhaustion");
        statsSource.ShouldContain("VulkanDynamicUniformAllocations");
        statsSource.ShouldContain("VulkanDynamicUniformAllocatedBytes");
        statsSource.ShouldContain("VulkanDynamicUniformExhaustions");
        packetSource.ShouldContain("VulkanDynamicUniformAllocatedBytes");
        profilerUiSource.ShouldContain("Dynamic UBO Ring:");
    }

    [Test]
    public void ResourcePlanReplacements_AreFenceRetiredAndObservable()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string plannerUpdate = SliceBetween(
            stateSource,
            "private void UpdateResourcePlannerFromContext",
            "private static ulong ComputeResourcePlannerSignature");

        plannerUpdate.ShouldContain("RecordVulkanRetiredResourcePlanReplacement");
        plannerUpdate.ShouldContain("DestroyPhysicalImages(this)");
        plannerUpdate.ShouldContain("DestroyPhysicalBuffers(this)");
        plannerUpdate.ShouldNotContain("WaitForAllInFlightWork()");
        stateSource.ShouldContain("RetireBuffer(buffer, memory)");

        string statsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.Vulkan.cs");
        string packetSource = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string profilerUiSource = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");

        statsSource.ShouldContain("RecordVulkanRetiredResourcePlanReplacement");
        packetSource.ShouldContain("VulkanRetiredResourcePlanReplacements");
        profilerUiSource.ShouldContain("Retired Plan Resources:");
    }

    [Test]
    public void ResourcePlannerMergedRegistry_ReusesPrimaryWhenOtherContextsAreCovered()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string mergeSource = SliceBetween(
            stateSource,
            "private RenderResourceRegistry? BuildMergedFrameOpRegistry",
            "private static void AddRegistryDescriptors");

        mergeSource.ShouldContain("RegistriesCoveredByPrimary(registries, primaryRegistry)");
        mergeSource.ShouldContain("return primaryRegistry;");
        mergeSource.ShouldContain("FrameBufferDescriptorsEquivalent");
        mergeSource.ShouldContain("TryGetCachedMergedFrameOpRegistry");
        mergeSource.ShouldContain("RememberMergedFrameOpRegistry");
        mergeSource.ShouldContain("ResourceGenerationStamp");
    }

    [Test]
    public void ResourcePlanner_SplitsPhysicalAllocationSignatureFromGraphSignature()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string plannerUpdate = SliceBetween(
            stateSource,
            "private void UpdateResourcePlannerFromContext",
            "private IReadOnlyCollection<RenderPassMetadata>? FilterActivePassMetadata");

        stateSource.ShouldContain("_resourceAllocationSignature");
        stateSource.ShouldContain("_resourcePlannerFastPathKey");
        stateSource.ShouldContain("_barrierPlanFastPathKey");
        stateSource.ShouldContain("ComputeResourceAllocationSignature");
        plannerUpdate.ShouldContain("PrepareResourcePlanningInputs");
        plannerUpdate.ShouldContain("CanReuseResourcePlannerFastPath");
        plannerUpdate.ShouldContain("BuildResourceDescriptorPlan");
        plannerUpdate.ShouldContain("BuildPhysicalAllocationPlan");
        plannerUpdate.ShouldContain("TryBuildPhysicalAllocator");
        plannerUpdate.ShouldContain("CommitPhysicalAllocatorPlan");
        plannerUpdate.ShouldContain("RebuildRenderGraphAndBarriers");
        plannerUpdate.ShouldContain("Reusing physical resource plan for metadata-only graph change");
        plannerUpdate.ShouldContain("_resourceAllocationSignature = allocationPlan.Signature;");
        plannerUpdate.ShouldContain("RememberResourcePlannerFastPath");
        stateSource.ShouldContain("BarrierPlanFastPathKey");
        stateSource.ShouldContain("barrierKey.Matches(_barrierPlanFastPathKey)");

        string allocatorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/VulkanResourceAllocator.cs");
        allocatorSource.ShouldContain("ComputePhysicalPlanUsageSignature");
        allocatorSource.ShouldContain("BuildUsageProfiles(passMetadata, planner)");
        allocatorSource.ShouldContain("pair.Value.Signature");
    }

    [Test]
    public void ResourcePlanner_CachesActivePassMetadataAndCompiledGraphs()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");

        stateSource.ShouldContain("_lastActiveFilterSourcePassMetadata");
        stateSource.ShouldContain("_lastActiveFilterPassSetSignature");
        stateSource.ShouldContain("ComputeActivePassSetSignature");
        stateSource.ShouldContain("ComputePassMetadataRevisionStamp");
        stateSource.ShouldContain("return _lastActiveFilterResult;");
        stateSource.ShouldContain("ChangedFields=[{3}]");
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
    public void CommandChainResourcePlanFreeze_PreventsPlannerMutationDuringLowering()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string loweringSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandChainLowering.cs");

        stateSource.ShouldContain("_commandChainFrozenPlanReaders");
        stateSource.ShouldContain("_commandChainFrozenResourcePlanRevision");
        stateSource.ShouldContain("private bool IsCommandChainResourcePlanFrozen => Volatile.Read(ref _commandChainFrozenPlanReaders) > 0;");
        stateSource.ShouldContain("private readonly struct CommandChainResourcePlanReadScope : IDisposable");
        stateSource.ShouldContain("Refusing lazy physical-image plan rebuild");
        stateSource.ShouldContain("Resource planner cannot be replaced while command-chain readers are using frozen plan revision");
        loweringSource.ShouldContain("BeginCommandChainResourcePlanReadScope(resourcePlanRevision)");
        loweringSource.ShouldContain("using CommandChainResourcePlanReadScope resourcePlanReadScope");

        string ensurePhysicalImageSource = SliceBetween(
            stateSource,
            "internal bool TryEnsurePhysicalImageForTextureResource",
            "private FrameOpContext PrepareResourcePlannerForFrameOps");
        int frozenGuardIndex = ensurePhysicalImageSource.IndexOf("if (IsCommandChainResourcePlanFrozen)", StringComparison.Ordinal);
        int updatePlannerIndex = ensurePhysicalImageSource.IndexOf("UpdateResourcePlannerFromContext(context);", StringComparison.Ordinal);
        frozenGuardIndex.ShouldBeGreaterThanOrEqualTo(0);
        updatePlannerIndex.ShouldBeGreaterThan(frozenGuardIndex);

        string plannerUpdateSource = SliceBetween(
            stateSource,
            "private void UpdateResourcePlannerFromContext",
            "private ResourcePlanningInputs PrepareResourcePlanningInputs");
        plannerUpdateSource.ShouldContain("if (IsCommandChainResourcePlanFrozen)");
        plannerUpdateSource.IndexOf("if (IsCommandChainResourcePlanFrozen)", StringComparison.Ordinal)
            .ShouldBeLessThan(plannerUpdateSource.IndexOf("PrepareResourcePlanningInputs", StringComparison.Ordinal));
    }

    [Test]
    public void DescriptorPoolRetirement_IsFrameSlotAndTimelineBased()
    {
        string retirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string meshCleanupSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Cleanup.cs");
        string materialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");

        retirementSource.ShouldContain("Per-frame-slot retirement queue for descriptor pools whose descriptor");
        retirementSource.ShouldContain("private readonly List<DescriptorPool>[] _retiredDescriptorPools");
        retirementSource.ShouldContain("private readonly HashSet<ulong>[] _retiredDescriptorPoolHandles");
        retirementSource.ShouldContain("int frameSlot = currentFrame;");
        retirementSource.ShouldContain("_retiredDescriptorPools[frameSlot].Add(descriptorPool);");
        retirementSource.ShouldContain("DrainRetiredDescriptorPools(currentFrame, RetiredDescriptorPoolDrainLimitPerFrame)");
        retirementSource.ShouldContain("Api!.DestroyDescriptorPool(device, pool, null);");
        retirementSource.ShouldContain("RecordVulkanRetiredResourceDrain(descriptorPools: destroyedPools)");

        string frameSlotWaitSource = SliceBetween(
            drawingSource,
            "// 1. Wait for the previous submission associated with this in-flight slot.",
            "// 2. Acquire the next image from the swap chain");
        int waitIndex = frameSlotWaitSource.IndexOf("WaitForTimelineValue(_graphicsTimelineSemaphore, slotWaitValue);", StringComparison.Ordinal);
        int drainIndex = frameSlotWaitSource.IndexOf("DrainRetiredDescriptorPools();", StringComparison.Ordinal);
        waitIndex.ShouldBeGreaterThanOrEqualTo(0);
        drainIndex.ShouldBeGreaterThan(waitIndex);

        meshCleanupSource.ShouldContain("Renderer.RetireDescriptorPool(descriptorPool);");
        materialSource.ShouldContain("Renderer.RetireDescriptorPool(state.DescriptorPool);");
    }

    [Test]
    public void CommandRecording_ReusesPerFrameScratchCollections()
    {
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string frameOpSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string recordSource = SliceBetween(
            commandBufferSource,
            "private ImageLayout RecordCommandBuffer",
            "private void RecordFrameOp");
        string drainSource = SliceBetween(
            frameOpSource,
            "internal FrameOp[] DrainFrameOps(out ulong signature)",
            "private static ulong ComputeFrameOpsSignature");

        commandBufferSource.ShouldContain("_secondaryBucketByStartScratch");
        commandBufferSource.ShouldContain("_swapchainWritesByPipelineScratch");
        commandBufferSource.ShouldContain("_swapchainWriterOpByPipelineScratch");
        commandBufferSource.ShouldContain("_swapchainWriterDynamicUiDrawCountByPipelineScratch");
        commandBufferSource.ShouldContain("_recordMeshDrawSlotsByRendererScratch");
        commandBufferSource.ShouldContain("_refreshMeshDrawSlotsByRendererScratch");
        commandBufferSource.ShouldContain("_dynamicUiMeshDrawSlotsByRendererScratch");
        commandBufferSource.ShouldContain("_fboLayoutTrackingScratch");
        commandBufferSource.ShouldContain("_swapchainWriterCountSortScratch");
        commandBufferSource.ShouldContain("_swapchainWriterSummaryBuilder");
        commandBufferSource.ShouldContain("_recordSwapchainWriterCapacityHint");
        commandBufferSource.ShouldContain("_recordFboLayoutCapacityHint");
        commandBufferSource.ShouldContain("XRE_VULKAN_DISABLE_PARALLEL_SECONDARY_RECORDING");
        commandBufferSource.ShouldContain("IsParallelSecondaryCommandBufferRecordingDisabled");
        recordSource.ShouldContain("secondaryBucketByStart = _secondaryBucketByStartScratch;");
        recordSource.ShouldContain("secondaryBucketByStart.Clear();");
        recordSource.ShouldContain("secondaryBucketByStart.EnsureCapacity(Math.Max(_secondaryBucketByStartCapacityHint, secondaryBuckets.Count));");
        recordSource.ShouldContain("swapchainWritesByPipeline.Clear();");
        recordSource.ShouldContain("swapchainWriterOpByPipeline.Clear();");
        recordSource.ShouldContain("meshDrawSlotsByRenderer.Clear();");
        recordSource.ShouldContain("fboLayoutTracking.Clear();");
        recordSource.ShouldContain("fboLayoutTracking.EnsureCapacity(Math.Max(1, _recordFboLayoutCapacityHint));");
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
        frameOpSource.ShouldContain("private struct FrameOpSignatureHasher");
        frameOpSource.ShouldNotContain("hash.Add(op.GetType().Name, StringComparer.Ordinal);");
        drainSource.ShouldContain("_frameOps.CopyTo(_drainedFrameOpsBuffer);");
        drainSource.ShouldNotContain("_frameOps.ToArray()");

        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string statsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.Vulkan.cs");
        string profileCaptureSource = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfileCapture.cs");
        string measureSource = ReadWorkspaceFile("Tools/Measure-GameLoopRenderPipeline.ps1");
        drawingSource.ShouldContain("GC.GetAllocatedBytesForCurrentThread()");
        drawingSource.ShouldContain("RecordVulkanRecordCommandBufferAllocation");
        statsSource.ShouldContain("VulkanRecordCommandBufferAllocatedBytes");
        profileCaptureSource.ShouldContain("vulkan_record_command_buffer_allocated_bytes");
        measureSource.ShouldContain("FailOnSteadyStateCommandBufferAllocations");
    }

    [Test]
    public void SwapchainResizeAndPresentation_HaveRecoveryAndPresentTransitionDiagnostics()
    {
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string syncSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SyncObjects.cs");
        string win32ResizeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/InteractiveResize/Win32ModalLoopTimerInteractiveResizeStrategy.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string resizeResourceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/RenderPipelineAntiAliasingResources.cs");
        string resizeRecoveryTextures = SliceBetween(
            resizeResourceSource,
            "internal static readonly string[] ResizeRecoveryTextureDependencies",
            "internal static readonly string[] ResizeRecoveryFrameBufferDependencies");

        drawingSource.ShouldContain("AcquireNextImage");
        drawingSource.ShouldContain("Result.ErrorOutOfDateKhr");
        drawingSource.ShouldContain("Result.SuboptimalKhr");
        drawingSource.ShouldContain("Result.NotReady");
        drawingSource.ShouldContain("MaxConsecutiveNotReadyBeforeRecreate");
        drawingSource.ShouldContain("ScheduleSwapchainRecreate");
        drawingSource.ShouldContain("RecreateSwapchainImmediately");
        drawingSource.ShouldContain("QueuePresent returned ErrorOutOfDateKhr");
        drawingSource.ShouldContain("QueuePresent returned SuboptimalKhr");
        drawingSource.ShouldContain("InteractiveResizeAcquireTimeoutNanoseconds");
        drawingSource.ShouldContain("acquireTimeoutNanoseconds = interactiveResize");
        drawingSource.ShouldContain("Result.NotReady || result == Result.Timeout");
        drawingSource.ShouldContain("AcquireNextImage returned {0} during interactive resize; skipping this repaint tick.");
        drawingSource.ShouldContain("pendingMatchesLive && ShouldRunInteractiveSwapchainRecreate()");
        drawingSource.ShouldContain("InteractiveSwapchainRecreateMinInterval = TimeSpan.FromMilliseconds(16)");
        drawingSource.ShouldContain("HasTimelineValueCompleted(_graphicsTimelineSemaphore, slotWaitValue)");
        drawingSource.ShouldContain("PendingResizeResourceCatchUp");
        drawingSource.ShouldContain("Allowing active presentation-size mismatch while pending generation catches up");
        drawingSource.ShouldContain("VulkanResizeResourceMismatch");
        drawingSource.ShouldContain("SkippedResizeCatchUpThisFrame");
        drawingSource.ShouldContain("skipped command-chain execution this frame while resize resources catch up");
        drawingSource.ShouldNotContain("TryPreparePendingGenerationForResizeCatchUp");
        drawingSource.ShouldNotContain("FramePrepareResizeCatchUp");
        string pipelineInstanceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        pipelineInstanceSource.ShouldContain("FramePrepareResizeCatchUp");
        pipelineInstanceSource.ShouldContain("FrameSkippedForResizeCatchUp");
        pipelineInstanceSource.ShouldContain("_resizeCatchUpSkippedFrameId = RuntimeEngine.Rendering.State.RenderFrameId");
        pipelineInstanceSource.ShouldContain("return PendingGeneration is null;");
        pipelineInstanceSource.ShouldContain("legacy");
        syncSource.ShouldContain("GetSemaphoreCounterValue");
        win32ResizeSource.ShouldContain("private const int VulkanActiveSizingRenderHz = 60;");
        win32ResizeSource.ShouldNotContain("if (ApplyCoalescedClientPresentationResize(\"win32-timer\"))");
        resizeRecoveryTextures.ShouldNotContain("AutoExposureTextureName");

        commandBufferSource.ShouldContain("swapchainPresentTransitions");
        commandBufferSource.ShouldContain("usedSwapchainDynamicRendering");
        commandBufferSource.ShouldContain("presentTransitions=");
        commandBufferSource.ShouldContain("expectedPresentTransitions");
        commandBufferSource.ShouldContain("expected {1}");
        commandBufferSource.ShouldContain("ImageLayout.PresentSrcKhr");
    }

    [Test]
    public void P1Coverage_IsIncludedInVulkanFocusedCiLane()
    {
        string? workflowSource = TryReadWorkspaceFile(".github/workflows/vulkan-tests.yml");
        if (workflowSource is null)
            Assert.Ignore("No Vulkan-focused CI workflow is present in this checkout.");

        workflowSource.ShouldContain("VulkanP1ValidationTests");
        workflowSource.ShouldContain("VulkanP0ValidationTests");
        workflowSource.ShouldContain("VulkanTodoP2ValidationTests");
    }

    private static string SliceBetween(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Expected to find start token '{startToken}'.");

        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Expected to find end token '{endToken}' after '{startToken}'.");

        return source[start..end];
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? source = TryReadWorkspaceFile(relativePath);
        source.ShouldNotBeNull($"Expected workspace file '{relativePath}' to exist.");
        return source;
    }

    private static string? TryReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllText(path) : null;
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
