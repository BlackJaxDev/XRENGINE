using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanDynamicRenderingMigrationTests
{
    [Test]
    public void RenderTargetMode_HasEnvironmentOverrideAndVisibleUnsupportedDynamicFailure()
    {
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderTargetMode.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs");

        modeSource.ShouldContain("XRE_VK_RENDER_TARGET_MODE");
        modeSource.ShouldContain("VulkanRenderTargetMode.Auto");
        modeSource.ShouldContain("VulkanRenderTargetMode.DynamicRendering");
        modeSource.ShouldContain("VulkanRenderTargetMode.LegacyRenderPass");
        modeSource.ShouldContain("dynamic rendering was explicitly requested");
        logicalDeviceSource.ShouldContain("ResolveRenderTargetMode();");
        logicalDeviceSource.ShouldContain("[Vulkan] Render target mode:");
    }

    [Test]
    public void DynamicCommandRecording_UsesDynamicRenderingAndKeepsLegacyCallsModeGated()
    {
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs");
        string frameBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/FrameBuffers.cs");
        string renderPasses = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/RenderPasses.cs");

        commandBuffers.ShouldContain("UseDynamicRenderingRenderTargets &&");
        commandBuffers.ShouldContain("Api!.CmdBeginRendering(commandBuffer, &renderingInfo);");
        commandBuffers.ShouldContain("Api!.CmdEndRendering(commandBuffer);");
        commandBuffers.ShouldContain("TransitionFboAttachmentsForDynamicRendering");
        commandBuffers.ShouldContain("Api!.CmdBeginRenderPass(commandBuffer, &fboPassInfo, SubpassContents.Inline);");
        frameBuffers.ShouldContain("if (UseDynamicRenderingRenderTargets)");
        frameBuffers.ShouldContain("swapChainFramebuffers = new Framebuffer[swapChainImageViews.Length];");
        renderPasses.ShouldContain("if (UseDynamicRenderingRenderTargets)");
        renderPasses.ShouldContain("_renderPass = default;");
    }

    [Test]
    public void DynamicRenderingAttachmentTransitions_UseLayoutCompatibleStageAccessMasks()
    {
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs");

        commandBuffers.ShouldContain("NormalizeFboAttachmentLayout(");
        commandBuffers.ShouldContain("ImageLayout.ColorAttachmentOptimal => ImageLayout.DepthStencilAttachmentOptimal");
        commandBuffers.ShouldContain("ImageLayout.ShaderReadOnlyOptimal => ImageLayout.DepthStencilReadOnlyOptimal");
        commandBuffers.ShouldContain("if (layout == ImageLayout.ShaderReadOnlyOptimal)");
        commandBuffers.ShouldContain("return PipelineStageFlags.FragmentShaderBit;");
        commandBuffers.ShouldContain("if (layout == ImageLayout.TransferSrcOptimal)");
        commandBuffers.ShouldContain("return AccessFlags.ShaderReadBit;");
        commandBuffers.ShouldContain("access |= AccessFlags.ShaderReadBit;");
        commandBuffers.ShouldNotContain("if (signature.Role == AttachmentRole.Color || layout == ImageLayout.ColorAttachmentOptimal)");
    }

    [Test]
    public void DynamicRenderingDepthAttachments_NormalizeFormatRoleAspectAndGraphLayouts()
    {
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkFrameBuffer.cs");
        string blit = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.Blit.cs");

        frameBuffer.ShouldContain("ResolveAttachmentRole(attachment, source.AspectMask, source.Format)");
        frameBuffer.ShouldContain("NormalizeAttachmentAspectMask(source.DescriptorFormat, source.DescriptorAspect)");
        frameBuffer.ShouldContain("VkFormatConversions.IsDepthStencilFormat(source.DescriptorFormat)");
        frameBuffer.ShouldContain("RenderGraphImageLayout.ColorAttachment => signature.Role == AttachmentRole.Color");
        frameBuffer.ShouldContain(": ImageLayout.DepthStencilAttachmentOptimal");
        blit.ShouldContain("or Format.S8Uint");
        blit.ShouldContain("if (!IsDepthOrStencilFormat(format))");
        blit.ShouldContain("Format.S8Uint => ImageAspectFlags.StencilBit");
    }

    [Test]
    public void RetiredImageResources_AreDeduplicatedBeforeDestroy()
    {
        string retirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.ResourceRetirement.cs");

        retirementSource.ShouldContain("private readonly HashSet<ulong>[] _retiredImageHandles");
        retirementSource.ShouldContain("private readonly HashSet<ulong>[] _retiredImageMemoryHandles");
        retirementSource.ShouldContain("private readonly HashSet<ulong>[] _retiredImageViewHandles");
        retirementSource.ShouldContain("private readonly HashSet<ulong>[] _retiredSamplerHandles");
        retirementSource.ShouldContain("private ImageView[] FilterRetiredAttachmentViews");
        retirementSource.ShouldContain("!_retiredImageHandles[frameSlot].Add(image.Handle)");
        retirementSource.ShouldContain("!_retiredImageMemoryHandles[frameSlot].Add(memory.Handle)");
        retirementSource.ShouldContain("!_retiredImageViewHandles[frameSlot].Add(primaryView.Handle)");
        retirementSource.ShouldContain("!_retiredSamplerHandles[frameSlot].Add(sampler.Handle)");
        retirementSource.ShouldContain("_retiredImageHandles[frameSlot].Remove(resources.Image.Handle)");
        retirementSource.ShouldContain("_retiredImageMemoryHandles[frameSlot].Remove(resources.Memory.Handle)");
        retirementSource.ShouldContain("_retiredImageViewHandles[frameSlot].Remove(resources.PrimaryView.Handle)");
        retirementSource.ShouldContain("_retiredSamplerHandles[frameSlot].Remove(resources.Sampler.Handle)");
        retirementSource.ShouldContain("_imageAllocations.TryRemove(r.Image.Handle, out trackedImageAllocation)");
        retirementSource.ShouldContain("DeviceMemory memory = hasTrackedImageAllocation ? trackedImageAllocation.Memory : r.Memory;");
        retirementSource.ShouldContain("FreeMemoryAllocation(trackedImageAllocation)");
        retirementSource.ShouldContain("Api!.FreeMemory(device, memory, null)");
        retirementSource.ShouldContain("freedMemories++;");
    }

    [Test]
    public void DynamicPipelines_AreKeyedByAttachmentFormatSignatureWithoutRenderPassHandles()
    {
        string meshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.cs");
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Pipeline.cs");
        string prewarm = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanPipelinePrewarmDatabase.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderTargetMode.cs");

        meshRenderer.ShouldContain("DynamicRenderingFormatSignature DynamicRenderingFormats");
        meshPipeline.ShouldContain("useDynamicRendering ? 0UL : renderPass.Handle");
        meshPipeline.ShouldContain("dynamicRenderingFormats.GetColorAttachmentFormat");
        meshPipeline.ShouldContain("dynamicRenderingFormats.CopyColorAttachmentFormats");
        meshPipeline.ShouldContain("DepthAttachmentFormat = request.DynamicRenderingFormats.DepthAttachmentFormat");
        meshPipeline.ShouldContain("StencilAttachmentFormat = request.DynamicRenderingFormats.StencilAttachmentFormat");
        prewarm.ShouldContain("BuildDynamicRenderingSignature(dynamicRenderingFormats)");
        prewarm.ShouldContain("dynamicRenderingFormats.DescribeColorFormats()");
        modeSource.ShouldContain("DescribeColorFormats()");
    }

    [Test]
    public void GraphicsPipelineLibraryExtension_EnablesRequiredKhrDependency()
    {
        string extensionsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Extensions.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs");

        extensionsSource.ShouldContain("\"VK_KHR_pipeline_library\"");
        extensionsSource.ShouldContain("\"VK_EXT_graphics_pipeline_library\"");
        logicalDeviceSource.ShouldContain("optionalExt == \"VK_EXT_graphics_pipeline_library\"");
        logicalDeviceSource.ShouldContain("!availableExtensionSet.Contains(\"VK_KHR_pipeline_library\")");
        logicalDeviceSource.ShouldContain("graphicsPipelineLibraryDependencyEnabled");
        logicalDeviceSource.ShouldContain("extensionsArray.Contains(\"VK_KHR_pipeline_library\")");
    }

    [Test]
    public void GraphicsPipelineLibraryKeys_AreSubsetScopedAndPendingLinksAreNotLoggedAsFailures()
    {
        string meshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.cs");
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Pipeline.cs");

        meshRenderer.ShouldContain("internal readonly record struct GraphicsPipelineLibraryKey(");
        meshRenderer.ShouldContain("GraphicsPipelineLibrarySubset Subset,");
        meshRenderer.ShouldContain("DynamicRenderingFormatSignature DynamicRenderingFormats,");
        meshPipeline.ShouldContain("CreateGraphicsPipelineLibraryKey(GraphicsPipelineLibrarySubset.VertexInputInterface, request.Key)");
        meshPipeline.ShouldContain("hasProgram = subset is GraphicsPipelineLibrarySubset.PreRasterizationShaders or GraphicsPipelineLibrarySubset.FragmentShader");
        meshPipeline.ShouldContain("hasDepthStencil = subset is GraphicsPipelineLibrarySubset.FragmentShader or GraphicsPipelineLibrarySubset.FragmentOutputInterface");
        meshPipeline.ShouldContain("hasBlendState = subset == GraphicsPipelineLibrarySubset.FragmentOutputInterface");
        meshPipeline.ShouldContain("ApplyGraphicsPipelineLibrarySubset(ref libraryPipelineInfo, key.Subset, key.UseDynamicRendering)");
        meshPipeline.ShouldContain("bool preserveDynamicRenderingAttachmentState = useDynamicRendering;");
        meshPipeline.ShouldContain("key.Subset == GraphicsPipelineLibrarySubset.FragmentOutputInterface");
        meshPipeline.ShouldContain("PNext = includeDynamicRenderingInfo ? baseInfo.PNext : null");
        meshPipeline.ShouldContain("linkedRenderingInfo.PNext = &libraryInfo;");
        meshPipeline.ShouldContain("linkedInfo.PNext = &linkedRenderingInfo;");
        meshPipeline.ShouldNotContain("PNext = pipelineInfo.PNext");
        meshPipeline.ShouldContain("if (!preserveDynamicRenderingAttachmentState)");
        meshPipeline.ShouldNotContain("linkedInfo.PDepthStencilState = null;");
        meshPipeline.ShouldNotContain("linkedInfo.PColorBlendState = null;");

        meshPipeline.ShouldContain("XRRenderProgram.ShaderProgramBackendStatus backend = _program.Data.ShaderMetadata.Backend");
        meshPipeline.ShouldContain("backend.Stage == XRRenderProgram.EShaderProgramBackendStage.Failed");
        meshPipeline.ShouldContain("program link failed");
    }

    [Test]
    public void DynamicRenderingDepthOnlyPasses_CreatePipelinesInsteadOfSkippingDraws()
    {
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Pipeline.cs");

        meshPipeline.ShouldContain("ResolveAttachmentCompatibleDrawState(");
        meshPipeline.ShouldContain("colorAttachmentCount == 0");
        meshPipeline.ShouldContain("ColorWriteMask = 0");
        meshPipeline.ShouldContain("BlendEnabled = false");
        meshPipeline.ShouldContain("AlphaToCoverageEnabled = false");
        meshPipeline.ShouldContain("if (colorAttachmentCount == 0)");
        meshPipeline.ShouldContain("stages = stages.Where(static stage => stage.Stage != ShaderStageFlags.FragmentBit).ToArray();");
        meshPipeline.ShouldContain("Vulkan.PipelineLibrary.DepthOnlyMonolithic");
        meshPipeline.ShouldContain("graphics pipeline libraries are bypassed for zero-color pipelines");
        meshPipeline.ShouldContain("return CreateMonolithicGraphicsPipeline(request, ref pipelineInfo, pipelineCache);");
        meshPipeline.ShouldNotContain("Vulkan.MeshRenderer.SkipDraw.NoColorAttachment");
        meshPipeline.ShouldNotContain("dynamic rendering has undefined color attachment format while color writes are enabled");
    }

    [Test]
    public void GeneratedProgramIdentity_UsesStableShaderAndVariantIdentityInsteadOfMaterialRevision()
    {
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Pipeline.cs");

        meshPipeline.ShouldContain("BuildShaderIdentityList(material, generatedVertexIdentity)");
        meshPipeline.ShouldContain("material.ActiveUberVariant.VariantHash.ToString(\"X16\")");
        meshPipeline.ShouldContain("StringComparer.Ordinal.GetHashCode(sourceText)");
        meshPipeline.ShouldNotContain(";shaderRevision=");
        meshPipeline.ShouldNotContain("material.ShaderStateRevision.ToString");
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
