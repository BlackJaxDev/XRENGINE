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
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");

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
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string frameBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Framebuffers/VulkanRenderer.SwapchainFramebuffers.cs");
        string renderPasses = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderer.RenderPasses.cs");

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
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

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
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string blit = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");

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
        string retirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");

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
        string meshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string prewarm = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelinePrewarmDatabase.cs");
        string modeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanRenderTargetMode.cs");

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
        string extensionsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");

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
        string meshRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

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
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

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
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

        meshPipeline.ShouldContain("BuildShaderIdentityList(material, generatedVertexIdentity)");
        meshPipeline.ShouldContain("material.ActiveUberVariant.VariantHash.ToString(\"X16\")");
        meshPipeline.ShouldContain("StringComparer.Ordinal.GetHashCode(sourceText)");
        meshPipeline.ShouldNotContain(";shaderRevision=");
        meshPipeline.ShouldNotContain("material.ShaderStateRevision.ToString");
    }

    [Test]
    public void SynchronousDepthReadback_UsesBoundFramebufferBeforeSwapchainFallback()
    {
        string readback = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Readback.cs");
        string blit = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");

        int getDepthIndex = readback.IndexOf("public override float GetDepth(int x, int y)", StringComparison.Ordinal);
        int boundFramebufferIndex = readback.IndexOf("_boundReadFrameBuffer is not null", getDepthIndex, StringComparison.Ordinal);
        int swapchainFallbackIndex = readback.IndexOf("TryReadSwapchainDepthPixel", getDepthIndex, StringComparison.Ordinal);
        int depthReadIndex = blit.IndexOf("private bool TryReadDepthPixel", StringComparison.Ordinal);
        int liveDepthIndex = blit.IndexOf("TryResolveLiveBlitImage(source, out BlitImageInfo liveSource)", depthReadIndex, StringComparison.Ordinal);
        int liveDepthCopyIndex = blit.IndexOf("liveSource.Image", liveDepthIndex, StringComparison.Ordinal);

        getDepthIndex.ShouldBeGreaterThanOrEqualTo(0);
        boundFramebufferIndex.ShouldBeGreaterThan(getDepthIndex);
        swapchainFallbackIndex.ShouldBeGreaterThan(boundFramebufferIndex);
        readback.ShouldContain("TryResolveBlitImage(");
        readback.ShouldContain("wantDepth: true");
        readback.ShouldContain("TryReadDepthPixel(depthSource, x, y, out float fboDepth)");
        readback.ShouldContain("Vulkan.Readback.DepthBoundFboFailed");
        depthReadIndex.ShouldBeGreaterThanOrEqualTo(0);
        liveDepthIndex.ShouldBeGreaterThan(depthReadIndex);
        liveDepthCopyIndex.ShouldBeGreaterThan(liveDepthIndex);
    }

    [Test]
    public void EditorDepthHit_ConvertsVulkanReadbackToTopLeftFramebufferCoordinates()
    {
        string editorPawn = ReadWorkspaceFile("XREngine.Editor/EditorFlyingCameraPawnComponent.cs");

        editorPawn.ShouldContain("GetDepthReadbackCoordinate(fbo, internalSizeCoordinate)");
        editorPawn.ShouldContain("RuntimeRenderingHostServices.Current.CurrentRenderBackend != RuntimeGraphicsApiKind.Vulkan");
        editorPawn.ShouldContain("int maxY = Math.Max((int)fbo.Height - 1, 0);");
        editorPawn.ShouldContain("coordinate.Y = maxY - coordinate.Y;");
    }

    [Test]
    public void CommonPushConstants_AreVisibleToGeometryShaders()
    {
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string renderProgram = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string programPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgramPipeline.cs");
        string meshDrawing = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        commandBuffers.ShouldContain("internal const ShaderStageFlags CommonPushConstantStageFlags");
        commandBuffers.ShouldContain("ShaderStageFlags.GeometryBit |");
        commandBuffers.ShouldContain("ShaderStageFlags.TessellationEvaluationBit |");
        commandBuffers.ShouldContain("CommonPushConstantStageFlags,");
        renderProgram.ShouldContain("StageFlags = CommonPushConstantStageFlags");
        programPipeline.ShouldContain("StageFlags = CommonPushConstantStageFlags");
        meshDrawing.ShouldContain("CommonPushConstantStageFlags,");
    }

    [Test]
    public void FboDepthStencilMetadata_PreservesStencilForOnTopAndPostProcessPasses()
    {
        string viewportCommand = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/ViewportRenderCommand.cs");
        string quadBlit = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderQuadToFBO.cs");
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string bindFbo = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/State/VPRC_BindFBOByName.cs");

        viewportCommand.ShouldContain("MakeFboStencilResource(target.Name)");
        viewportCommand.ShouldContain("builder.UseStencilAttachment(");
        viewportCommand.ShouldNotContain("RenderTargetHasStencilAttachment");

        int sharedDepthIndex = quadBlit.IndexOf("if (SamplesSharedDepthView(SourceQuadFBOName, destination))", StringComparison.Ordinal);
        int sharedStencilIndex = quadBlit.IndexOf("MakeFboStencilResource(destination)", sharedDepthIndex, StringComparison.Ordinal);
        sharedDepthIndex.ShouldBeGreaterThanOrEqualTo(0);
        sharedStencilIndex.ShouldBeGreaterThan(sharedDepthIndex);
        quadBlit.ShouldContain("ERenderGraphAccess.Read");

        frameBuffer.ShouldContain("if (usage.ResourceType == ERenderPassResourceType.StencilAttachment)");
        frameBuffer.ShouldContain("return [];");

        bindFbo.ShouldContain("string stencilResource = MakeFboStencilResource(frameBufferName);");
        bindFbo.ShouldContain("usage.ResourceType == ERenderPassResourceType.StencilAttachment");
        bindFbo.ShouldContain("string.Equals(usage.ResourceName, stencilResource, StringComparison.OrdinalIgnoreCase)");
    }

    [Test]
    public void ReadOnlyDepthStencilCompatibility_DoesNotStripGizmoStencilWritesFromMergedPasses()
    {
        string meshPipeline = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string frameBuffer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        string passUsesReadOnly = SliceMethod(meshPipeline, "private static bool PassUsesReadOnlyDepthStencil(");
        passUsesReadOnly.ShouldContain("bool hasDepthStencilWriteUsage = false;");
        passUsesReadOnly.ShouldContain("usage.Access is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite");
        passUsesReadOnly.ShouldContain("return hasDepthStencilUsage && !hasDepthStencilWriteUsage;");

        frameBuffer.ShouldContain("HashSet<int> writeCapableDepthStencilAttachments = CollectWriteCapableDepthStencilAttachments(planned, pass, frameBufferName);");
        frameBuffer.ShouldContain("ResolveAttachmentReferenceLayout(updated, usage, writeCapableDepthStencilAttachments.Contains(index))");

        string collectWrites = SliceMethod(frameBuffer, "private static HashSet<int> CollectWriteCapableDepthStencilAttachments(");
        collectWrites.ShouldContain("usage.Access == ERenderGraphAccess.Read");
        collectWrites.ShouldContain("ResolveMatchingAttachmentIndices(signatures, slot, usage, pass)");

        string referenceLayout = SliceMethod(frameBuffer, "private static ImageLayout ResolveAttachmentReferenceLayout(");
        referenceLayout.ShouldContain("usage.Access == ERenderGraphAccess.Read && !passHasWriteCapableDepthStencilUsage");
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
}
