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

        commandBuffers.ShouldContain("if (layout == ImageLayout.ShaderReadOnlyOptimal)");
        commandBuffers.ShouldContain("return PipelineStageFlags.FragmentShaderBit;");
        commandBuffers.ShouldContain("if (layout == ImageLayout.TransferSrcOptimal)");
        commandBuffers.ShouldContain("return AccessFlags.ShaderReadBit;");
        commandBuffers.ShouldContain("access |= AccessFlags.ShaderReadBit;");
        commandBuffers.ShouldNotContain("if (signature.Role == AttachmentRole.Color || layout == ImageLayout.ColorAttachmentOptimal)");
    }

    [Test]
    public void RetiredImageResources_AreDeduplicatedBeforeDestroy()
    {
        string retirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.ResourceRetirement.cs");

        retirementSource.ShouldContain("private readonly HashSet<ulong>[] _retiredImageHandles");
        retirementSource.ShouldContain("private ImageView[] FilterRetiredAttachmentViews");
        retirementSource.ShouldContain("destroyedSamplers.Add(r.Sampler.Handle)");
        retirementSource.ShouldContain("destroyedViews.Add(r.PrimaryView.Handle)");
        retirementSource.ShouldContain("destroyedImages.Add(r.Image.Handle)");
        retirementSource.ShouldContain("_imageAllocations.TryRemove(r.Image.Handle, out trackedImageAllocation)");
        retirementSource.ShouldContain("DeviceMemory memory = hasTrackedImageAllocation ? trackedImageAllocation.Memory : r.Memory;");
        retirementSource.ShouldContain("freedMemories.Add(memory.Handle)");
    }

    [Test]
    public void DynamicPipelines_AreKeyedByAttachmentFormatSignatureWithoutRenderPassHandles()
    {
        string meshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs");
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Pipeline.cs");
        string prewarm = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanPipelinePrewarmDatabase.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderTargetMode.cs");

        meshRenderer.ShouldContain("DynamicRenderingFormatSignature DynamicRenderingFormats");
        meshPipeline.ShouldContain("useDynamicRendering ? 0UL : renderPass.Handle");
        meshPipeline.ShouldContain("dynamicRenderingFormats.GetColorAttachmentFormat");
        meshPipeline.ShouldContain("dynamicRenderingFormats.CopyColorAttachmentFormats");
        meshPipeline.ShouldContain("DepthAttachmentFormat = dynamicRenderingFormats.DepthAttachmentFormat");
        meshPipeline.ShouldContain("StencilAttachmentFormat = dynamicRenderingFormats.StencilAttachmentFormat");
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
        string meshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs");
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Pipeline.cs");

        meshRenderer.ShouldContain("private readonly record struct GraphicsPipelineLibraryKey(");
        meshRenderer.ShouldNotContain("PipelineKey Pipeline");
        meshPipeline.ShouldContain("CreateGraphicsPipelineLibraryKey(GraphicsPipelineLibrarySubset.VertexInputInterface, key)");
        meshPipeline.ShouldContain("hasProgram = subset is GraphicsPipelineLibrarySubset.PreRasterizationShaders or GraphicsPipelineLibrarySubset.FragmentShader");
        meshPipeline.ShouldContain("hasDepthStencil = subset == GraphicsPipelineLibrarySubset.FragmentShader");
        meshPipeline.ShouldContain("hasBlendState = subset == GraphicsPipelineLibrarySubset.FragmentOutputInterface");

        meshPipeline.ShouldContain("XRRenderProgram.ShaderProgramBackendStatus backend = _program.Data.ShaderMetadata.Backend");
        meshPipeline.ShouldContain("backend.Stage == XRRenderProgram.EShaderProgramBackendStage.Failed");
        meshPipeline.ShouldContain("program link failed");
    }

    [Test]
    public void GeneratedProgramIdentity_UsesStableShaderAndVariantIdentityInsteadOfMaterialRevision()
    {
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Pipeline.cs");

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
