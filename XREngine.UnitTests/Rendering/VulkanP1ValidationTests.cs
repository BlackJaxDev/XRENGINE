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
        string statsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs");
        string meshSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Descriptors.cs");
        string materialSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMaterial.cs");
        string programSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs");
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
        senderSource.ShouldContain("VulkanDescriptorFallbackSampledImages = Rendering.Stats.VulkanDescriptorFallbackSampledImages");
        editorSource.ShouldContain("VulkanDescriptorFallbackSampledImages = Engine.Rendering.Stats.VulkanDescriptorFallbackSampledImages");
        profilerUiSource.ShouldContain("Descriptor Fallbacks:");
        profilerUiSource.ShouldContain("Descriptor Failures:");
    }

    [Test]
    public void DescriptorUpdateTemplates_AreBackendGatedAcrossDescriptorPaths()
    {
        string templateSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/VulkanDescriptorUpdateTemplates.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs");
        string meshSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Descriptors.cs");
        string materialSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMaterial.cs");
        string programSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs");

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
        string samplerSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/VulkanImmutableSamplers.cs");
        string initSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Init.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs");
        string layoutCacheSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/VulkanDescriptorLayoutCache.cs");

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
        string commandBufferSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs");
        string meshSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Drawing.cs");
        string renderProgramSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs");
        string renderProgramPipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgramPipeline.cs");
        string imguiSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderer.ImGui.cs");

        commandBufferSource.ShouldContain("CommonPushConstantSize = 16");
        commandBufferSource.ShouldContain("PushConstantsTracked");
        commandBufferSource.ShouldContain("RecordVulkanBindChurn(pushConstantWrites: 1)");
        commandBufferSource.ShouldContain("ComputeDispatchPushConstants");
        commandBufferSource.ShouldContain("Api!.CmdPushConstants");
        meshSource.ShouldContain("MeshDrawPushConstants");
        meshSource.ShouldContain("PushPerDrawConstants");
        meshSource.ShouldContain("Renderer.PushConstantsTracked");
        renderProgramSource.ShouldContain("CreateCommonPushConstantRange");
        renderProgramSource.ShouldContain("ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit");
        renderProgramPipelineSource.ShouldContain("CreateCommonPushConstantRange");
        imguiSource.ShouldContain("PushConstantsTracked(commandBuffer, _imguiPipelineLayout");
    }

    [Test]
    public void DynamicUniformRingBuffer_IsInstrumentedForProfilingBeforeAdoption()
    {
        string ringSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/VulkanDynamicUniformRingBuffer.cs");
        string statsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs");
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
        string stateSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderer.State.cs");
        string plannerUpdate = SliceBetween(
            stateSource,
            "private void UpdateResourcePlannerFromContext",
            "private static ulong ComputeResourcePlannerSignature");

        plannerUpdate.ShouldContain("RecordVulkanRetiredResourcePlanReplacement");
        plannerUpdate.ShouldContain("DestroyPhysicalImages(this)");
        plannerUpdate.ShouldContain("DestroyPhysicalBuffers(this)");
        plannerUpdate.ShouldNotContain("WaitForAllInFlightWork()");
        stateSource.ShouldContain("RetireBuffer(buffer, memory)");

        string statsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs");
        string packetSource = ReadWorkspaceFile("XREngine.Data/Profiling/ProfilerStatsPacket.cs");
        string profilerUiSource = ReadWorkspaceFile("XREngine.Profiler.UI/ProfilerPanelRenderer.cs");

        statsSource.ShouldContain("RecordVulkanRetiredResourcePlanReplacement");
        packetSource.ShouldContain("VulkanRetiredResourcePlanReplacements");
        profilerUiSource.ShouldContain("Retired Plan Resources:");
    }

    [Test]
    public void SwapchainResizeAndPresentation_HaveRecoveryAndPresentTransitionDiagnostics()
    {
        string drawingSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.Core.cs");
        string commandBufferSource = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs");

        drawingSource.ShouldContain("AcquireNextImage");
        drawingSource.ShouldContain("Result.ErrorOutOfDateKhr");
        drawingSource.ShouldContain("Result.SuboptimalKhr");
        drawingSource.ShouldContain("Result.NotReady");
        drawingSource.ShouldContain("MaxConsecutiveNotReadyBeforeRecreate");
        drawingSource.ShouldContain("ScheduleSwapchainRecreate");
        drawingSource.ShouldContain("RecreateSwapchainImmediately");
        drawingSource.ShouldContain("QueuePresent returned ErrorOutOfDateKhr");
        drawingSource.ShouldContain("QueuePresent returned SuboptimalKhr");

        commandBufferSource.ShouldContain("swapchainPresentTransitions");
        commandBufferSource.ShouldContain("usedSwapchainDynamicRendering");
        commandBufferSource.ShouldContain("presentTransitions=");
        commandBufferSource.ShouldContain("expected exactly once");
        commandBufferSource.ShouldContain("ImageLayout.PresentSrcKhr");
    }

    [Test]
    public void P1Coverage_IsIncludedInVulkanFocusedCiLane()
    {
        string workflowSource = ReadWorkspaceFile(".github/workflows/vulkan-tests.yml");

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
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path);
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
