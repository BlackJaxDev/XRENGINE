using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanDeferredProbeGiFixesTests
{
    [Test]
    public void ForwardFboClear_UsesRenderGraphMetadataPassIdentity()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/State/VPRC_BindFBOByName.cs");

        source.ShouldContain("ResolveClearPassIndex");
        source.ShouldContain("RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(clearPassIndex)");
        source.ShouldContain("ConditionalWeakTable");
        source.ShouldContain("GetTopologicalPassOrder(metadata)");
        source.ShouldContain("RenderGraphSynchronizationPlanner.TopologicallySort(metadata)");
        source.ShouldContain("usage.LoadOp != ERenderPassLoadOp.Clear");
        source.ShouldContain("MakeFboColorResource(frameBufferName)");
        source.ShouldContain("MakeFboDepthResource(frameBufferName)");
    }

    [Test]
    public void DeferredLightCombineMetadata_DeclaresGBufferProbeAndLightingInputs()
    {
        string commandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderQuadToFBO.cs");
        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs");

        commandSource.ShouldContain("DescribeDeferredLightCombineInputs(builder)");
        commandSource.ShouldContain("DefaultRenderPipeline.AlbedoOpacityTextureName");
        commandSource.ShouldContain("DefaultRenderPipeline.NormalTextureName");
        commandSource.ShouldContain("DefaultRenderPipeline.RMSETextureName");
        commandSource.ShouldContain("DefaultRenderPipeline.DepthViewTextureName");
        commandSource.ShouldContain("LightProbeIrradianceArray");
        commandSource.ShouldContain("LightProbePrefilterArray");
        commandSource.ShouldContain("builder.SampleTexture(MakeTextureResource(textureName))");
        commandSource.ShouldContain("builder.ReadBuffer(bufferName)");

        shaderSource.ShouldContain("layout(std430, binding = 0) buffer LightProbePositions");
        shaderSource.ShouldContain("layout(std430, binding = 1) buffer LightProbeTetrahedra");
        shaderSource.ShouldContain("layout(std430, binding = 2) buffer LightProbeParameters");
        shaderSource.ShouldContain("layout(std430, binding = 3) buffer LightProbeGridCells");
        shaderSource.ShouldContain("layout(std430, binding = 4) buffer LightProbeGridIndices");
    }

    [Test]
    public void ProgramBoundSsboDescriptors_AreResolvedAndFingerprintTracked()
    {
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Descriptors.cs");
        string preparationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Preparation.cs");
        string renderingStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs");
        string rendererStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderer.State.cs");

        descriptorSource.ShouldContain("_program?.AddBoundBufferResourceFingerprint(ref hash)");
        descriptorSource.ShouldContain("TryResolvePipelineResourceBuffer(binding, out buffer)");
        descriptorSource.ShouldContain("pipeline.TryGetBuffer(name, out XRDataBuffer? buffer)");
        descriptorSource.ShouldContain("IsDescriptorCompatibleBufferTarget(descriptorType, buffer.Target)");
        descriptorSource.ShouldContain("TryResolveProgramBoundBuffer(binding, out buffer)");
        descriptorSource.ShouldContain("_program.TryGetBoundBuffer(binding.Binding");
        descriptorSource.ShouldContain("Renderer.TrackBufferBinding(dataBuffer)");
        descriptorSource.ShouldContain("IsOptionalPipelineStorageBuffer");
        descriptorSource.ShouldContain("\"LightProbePositions\"");
        descriptorSource.ShouldContain("!IsOptionalPipelineStorageBuffer(binding)");
        preparationSource.ShouldContain("ApplyScopedProgramBindingsForPreparation(material);");
        preparationSource.ShouldContain("ApplyScopedProgramBindings(program)");
        renderingStateSource.ShouldContain("RuntimeEngine.Rendering.State.CurrentRenderingPipeline");

        programSource.ShouldContain("_buffersByBinding[index] = buffer");
        programSource.ShouldContain("internal bool TryGetBoundBuffer");
        programSource.ShouldContain("internal void AddBoundBufferResourceFingerprint");
        programSource.ShouldContain("Renderer.TrackTextureBinding(xrTexture)");

        rendererStateSource.ShouldContain("internal void TrackBufferBinding(XRDataBuffer buffer)");
        rendererStateSource.ShouldContain("internal void TrackTextureBinding(XRTexture texture)");
    }

    [Test]
    public void VulkanPreparation_PreBindsDescriptorAffectingLightingResources()
    {
        string preparationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Preparation.cs");

        int prebindIndex = preparationSource.IndexOf("ApplyScopedProgramBindingsForPreparation(material);", StringComparison.Ordinal);
        int descriptorIndex = preparationSource.IndexOf("EnsureDescriptorSets(material)", StringComparison.Ordinal);

        prebindIndex.ShouldBeGreaterThanOrEqualTo(0);
        descriptorIndex.ShouldBeGreaterThan(prebindIndex);
        preparationSource.ShouldContain("EUniformRequirements.Lights");
        preparationSource.ShouldContain("SetForwardLightingUniforms(program)");
        preparationSource.ShouldContain("EUniformRequirements.AmbientOcclusion");
        preparationSource.ShouldContain("Lights3DCollection.SetForwardAmbientOcclusionUniforms(program)");
    }

    [Test]
    public void VulkanMeshDrawDescriptors_DoNotBypassPerDrawUniformUploads()
    {
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Drawing.cs");

        drawingSource.ShouldContain("renderer-owned descriptor path");
        drawingSource.ShouldNotContain("vkMaterial.TryBindDescriptorSets(commandBuffer, _program, imageIndex)");

        int engineUploadIndex = drawingSource.IndexOf("UpdateEngineUniformBuffersForDraw(imageIndex, draw);", StringComparison.Ordinal);
        int autoUploadIndex = drawingSource.IndexOf("UpdateAutoUniformBuffersForDraw(imageIndex, material, draw);", StringComparison.Ordinal);
        int bindIndex = drawingSource.IndexOf("Renderer.BindDescriptorSetsTracked(commandBuffer, PipelineBindPoint.Graphics, _program.PipelineLayout, 0, sets);", StringComparison.Ordinal);

        engineUploadIndex.ShouldBeGreaterThanOrEqualTo(0);
        autoUploadIndex.ShouldBeGreaterThan(engineUploadIndex);
        bindIndex.ShouldBeGreaterThan(autoUploadIndex);
    }

    [Test]
    public void PostProcessCompositeInputs_AreConcreteAndClearedBeforeOptionalFogPasses()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs");
        string pipeline2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs");

        AssertPostProcessCompositeInputDefaults(pipelineSource);
        AssertPostProcessCompositeInputDefaults(pipeline2Source);
    }

    private static void AssertPostProcessCompositeInputDefaults(string source)
    {
        source.ShouldContain("AppendPostProcessCompositeInputDefaults(c);");
        source.ShouldContain("AtmosphereColorTextureName");
        source.ShouldContain("VolumetricFogColorTextureName");
        source.ShouldContain("AtmosphereUpscaleFBOName");
        source.ShouldContain("VolumetricFogUpscaleFBOName");
        source.ShouldContain("ColorF4.Transparent");
        source.ShouldContain("clearColor: true, clearDepth: false, clearStencil: false");

        int defaultsIndex = source.IndexOf("AppendPostProcessCompositeInputDefaults(c);", StringComparison.Ordinal);
        int atmosphereIndex = source.IndexOf("AppendAtmosphericScattering(c);", defaultsIndex, StringComparison.Ordinal);
        int fogIndex = source.IndexOf("AppendVolumetricFog(c);", atmosphereIndex, StringComparison.Ordinal);
        int postProcessIndex = source.IndexOf("AppendPostProcessResourceCaching(c);", fogIndex, StringComparison.Ordinal);

        defaultsIndex.ShouldBeGreaterThanOrEqualTo(0);
        atmosphereIndex.ShouldBeGreaterThan(defaultsIndex);
        fogIndex.ShouldBeGreaterThan(atmosphereIndex);
        postProcessIndex.ShouldBeGreaterThan(fogIndex);
    }

    [Test]
    public void DefaultRenderPipeline_RegistersProbeResourcesWithRenderRegistry()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs");

        pipelineSource.ShouldContain("private const string LightProbeIrradianceArrayName = \"LightProbeIrradianceArray\"");
        pipelineSource.ShouldContain("private const string LightProbePrefilterArrayName = \"LightProbePrefilterArray\"");
        pipelineSource.ShouldContain("RegisterProbeTextureArrays();");
        pipelineSource.ShouldContain("RegisterProbeBuffer(_probePositionBuffer)");
        pipelineSource.ShouldContain("RegisterProbeBuffer(_probeParamBuffer)");
        pipelineSource.ShouldContain("RegisterProbeBuffer(_probeTetraBuffer)");
        pipelineSource.ShouldContain("RemoveProbeTextureResource(LightProbeIrradianceArrayName)");
        pipelineSource.ShouldContain("RemoveProbeBufferResource(LightProbeTetraBufferName)");
    }


    [Test]
    public void ComputeDispatch_UsesLastActiveContextAndFuzzyAutoUniformBlocks()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderer.State.cs");
        string initSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Init.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs");
        string exposureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanAutoExposure.cs");

        stateSource.ShouldContain("private FrameOpContext? _lastActiveFrameOpContext");
        stateSource.ShouldContain("internal FrameOpContext CaptureFrameOpContextOrLastActive()");
        initSource.ShouldContain("FrameOpContext context = CaptureFrameOpContextOrLastActive();");
        initSource.ShouldContain("context.PassMetadata");

        programSource.ShouldContain("TryGetAutoUniformBlockFuzzy(binding.Name, binding.Set, binding.Binding");
        programSource.ShouldContain("candidate.BlockName");
        exposureSource.ShouldContain("Name = \"VulkanAutoExposure2D\"");
        exposureSource.ShouldContain("Name = \"VulkanAutoExposure2DArray\"");
    }

    [Test]
    public void ProbeTetraBufferName_MatchesDeferredShaderBlock()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs");
        string pipeline2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs");
        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs");

        pipelineSource.ShouldContain("private const string LightProbeTetraBufferName = \"LightProbeTetrahedra\"");
        pipelineSource.ShouldContain("new XRDataBuffer(LightProbeTetraBufferName");
        pipeline2Source.ShouldContain("private const string LightProbeTetraBufferName = \"LightProbeTetrahedra\"");
        shaderSource.ShouldContain("buffer LightProbeTetrahedra");
    }

    [Test]
    public void AttachmentLayoutTracking_FallsBackToKnownWholeImageLayout()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkImageBackedTexture.cs");

        source.ShouldContain("TryResolveAllLayerAttachmentLayout");
        source.ShouldContain("return _physicalGroup is not null ? _physicalGroup.LastKnownLayout : _currentImageLayout;");
        source.ShouldContain("BeginPartialAttachmentLayoutTracking()");
    }

    [Test]
    public void PartialAttachmentLayoutTracking_DoesNotPromoteMissingLayersToWholeImageLayout()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkImageBackedTexture.cs");

        source.ShouldContain("if (_hasPartialAttachmentLayouts)");
        source.ShouldContain("return TryResolveAllLayerAttachmentLayout(mipLevel, out ImageLayout layout)");
        source.ShouldContain("return ImageLayout.Undefined;");

        int descriptorPartialCheck = source.IndexOf("ImageLayout IVkImageDescriptorSource.TrackedImageLayout", StringComparison.Ordinal);
        int descriptorUndefinedFallback = source.IndexOf(": ImageLayout.Undefined;", descriptorPartialCheck, StringComparison.Ordinal);
        int attachmentQuery = source.IndexOf("ImageLayout IVkFrameBufferAttachmentSource.GetAttachmentTrackedLayout", StringComparison.Ordinal);
        int attachmentPartialFallback = source.IndexOf("return ImageLayout.Undefined;", attachmentQuery, StringComparison.Ordinal);
        int wholeImageFallback = source.IndexOf(
            "return _physicalGroup is not null ? _physicalGroup.LastKnownLayout : _currentImageLayout;",
            attachmentPartialFallback,
            StringComparison.Ordinal);

        descriptorPartialCheck.ShouldBeGreaterThanOrEqualTo(0);
        descriptorUndefinedFallback.ShouldBeGreaterThan(descriptorPartialCheck);
        attachmentQuery.ShouldBeGreaterThan(descriptorPartialCheck);
        attachmentPartialFallback.ShouldBeGreaterThan(descriptorPartialCheck);
        wholeImageFallback.ShouldBeGreaterThan(attachmentPartialFallback);
    }

    [Test]
    public void VulkanRuntimeCrashFixes_PreventParallelContextAndIncompleteComputeDescriptorHazards()
    {
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs");
        string vmaSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Memory/VulkanVmaAllocator.cs");
        string graphCompilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderGraphCompiler.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs");
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkDataBuffer.cs");
        string imageTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkImageBackedTexture.cs");
        string gpuBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");
        string bvhRaycastSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/BvhRaycastDispatcher.cs");
        string worldSource = ReadWorkspaceFile("XREngine/Rendering/XRWorldInstance.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs");
        string retirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.ResourceRetirement.cs");

        runtimeSource.ShouldContain("[ThreadStatic]");
        runtimeSource.ShouldContain("private static Stack<XRRenderPipelineInstance?>? t_pipelineOverrideStack");
        runtimeSource.ShouldContain("PipelineOverrideStack => t_pipelineOverrideStack ??= new()");

        vmaSource.ShouldContain("private readonly object _mapCountsGate = new();");
        vmaSource.ShouldContain("lock (_mapCountsGate)");
        vmaSource.ShouldContain("DrainMappedAllocation_NoLock");
        vmaSource.ShouldContain("Skipping VMA allocator destruction because");

        graphCompilerSource.ShouldContain("=> op is BlitOp or IndirectDrawOp;");
        graphCompilerSource.ShouldNotContain("=> op is BlitOp or IndirectDrawOp or ComputeDispatchOp;");

        programSource.ShouldContain("ConcurrentDictionary<string, byte> _computeWarnings");
        programSource.ShouldContain("bool hasUnresolvedBinding = false;");
        programSource.ShouldContain("Compute dispatch will be skipped.");
        programSource.ShouldContain("if (hasUnresolvedBinding)");
        programSource.ShouldContain("skippedDispatch: true");

        bufferSource.ShouldContain("internal BufferUsageFlags LastUsageFlags => _lastUsageFlags;");
        bufferSource.ShouldContain("internal bool SupportsDescriptorType(DescriptorType descriptorType)");
        bufferSource.ShouldContain("BufferUsageFlags.StorageBufferBit");
        programSource.ShouldContain("TryCreateDescriptorBufferInfo(");
        programSource.ShouldContain("!vkBuffer.SupportsDescriptorType(binding.DescriptorType)");
        programSource.ShouldContain("not {binding.DescriptorType}. Compute dispatch will be skipped.");

        gpuBvhSource.ShouldContain("GetOrCreateStorageView(");
        gpuBvhSource.ShouldContain("source.Clone(cloneBuffer: true, target: EBufferTarget.ShaderStorageBuffer)");
        gpuBvhSource.ShouldContain("GpuMeshBvh_Positions_Storage");
        gpuBvhSource.ShouldContain("ReleaseStaticStorageViews()");

        worldSource.ShouldContain("ShouldMap = false,");
        worldSource.ShouldContain("CanUseGpuMeshBvhPicking()");
        worldSource.ShouldContain("RuntimeGraphicsApiKind.OpenGL");
        worldSource.ShouldContain("using CPU mesh picking for this backend");
        bvhRaycastSource.ShouldContain("if (buffer.IsMapped)");
        bvhRaycastSource.ShouldContain("GPU BVH raycast is currently OpenGL-only");
        bvhRaycastSource.ShouldContain("Reset(\"renderer backend does not support GPU BVH raycast fences/readback\")");

        bufferSource.ShouldContain("ThrowIfDeviceLostForResourceCreation");
        bufferSource.ShouldContain("SkippedDeviceLost");
        imageTextureSource.ShouldContain("if (Renderer.IsDeviceLost || Image.Handle == 0)");
        commandBufferSource.ShouldContain("Cannot allocate a Vulkan one-shot command buffer after the device was lost.");
        commandBufferSource.ShouldContain("if (submitFence.Handle != 0)");
        commandBufferSource.ShouldNotContain("submitResult != Result.ErrorDeviceLost");
        retirementSource.ShouldContain("_imageAllocations.TryRemove(r.Image.Handle, out trackedImageAllocation)");
        retirementSource.ShouldContain("DeviceMemory memory = hasTrackedImageAllocation ? trackedImageAllocation.Memory : r.Memory;");
    }

    [Test]
    public void VulkanFrameOpSort_UsesRenderGraphPassOrderBeforeContextGrouping()
    {
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderGraphCompiler.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs");

        int passOrderSort = compilerSource.IndexOf(".OrderBy(x => x.PassOrder)", StringComparison.Ordinal);
        int groupOrderTieBreak = compilerSource.IndexOf(".ThenBy(x => x.GroupOrder)", StringComparison.Ordinal);

        passOrderSort.ShouldBeGreaterThanOrEqualTo(0);
        groupOrderTieBreak.ShouldBeGreaterThan(passOrderSort);
        compilerSource.ShouldNotContain(".OrderBy(x => x.GroupOrder)");
        commandBufferSource.ShouldContain("Always sort frame ops by (PassOrder, GroupOrder, OriginalIndex).");
    }

    [Test]
    public void VulkanFrameOpSort_UsesEachOperationContextMetadata()
    {
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderGraphCompiler.cs");

        compilerSource.ShouldContain("PassOrderCache");
        compilerSource.ShouldContain("ResolvePassOrder(op, graph)");
        compilerSource.ShouldContain("op.Context.PassMetadata is { Count: > 0 } metadata");
        compilerSource.ShouldContain("RenderGraphSynchronizationPlanner.TopologicallySort(metadata)");
        compilerSource.ShouldContain("rank is resolved from each op's own context metadata");
    }

    [Test]
    public void RuntimeRenderingCamera_FallsBackToPipelineFrameContextForDeferredVulkanWork()
    {
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs");
        string engineStateSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.State.cs");
        string meshRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs");
        string materialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.RenderState.cs");

        runtimeSource.ShouldContain("XRRenderPipelineInstance? pipeline = CurrentRenderingPipeline;");
        runtimeSource.ShouldContain("?? pipeline?.RenderState.RenderingCamera");
        runtimeSource.ShouldContain("?? pipeline?.RenderState.SceneCamera");
        runtimeSource.ShouldContain("?? pipeline?.LastSceneCamera");
        runtimeSource.ShouldContain("?? pipeline?.LastRenderingCamera");

        engineStateSource.ShouldContain("?? CurrentRenderingPipeline?.RenderState.RenderingCamera");
        engineStateSource.ShouldContain("?? CurrentRenderingPipeline?.RenderState.SceneCamera");
        engineStateSource.ShouldContain("?? CurrentRenderingPipeline?.LastSceneCamera");
        engineStateSource.ShouldContain("?? CurrentRenderingPipeline?.LastRenderingCamera");

        meshRendererSource.ShouldContain("RuntimeEngine.Rendering.State.RenderingCamera");
        programSource.ShouldContain("RuntimeEngine.Rendering.State.RenderingCamera");
        materialSource.ShouldContain("RuntimeEngine.Rendering.State.RenderingCamera?.SetUniforms(program, true);");
    }

    [Test]
    public void SceneCapture_InitializesProgressiveCubemapFacesBeforeProbeSampling()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs");

        source.ShouldContain("InitializeCaptureTextureContents(_environmentTextureCubemap);");
        source.ShouldContain("AbstractRenderer.Current?.GetOrCreateAPIRenderObject(cubemap, generateNow: true);");
        source.ShouldContain("cubemap.Clear(ColorF4.Black);");
        source.ShouldContain("Progressive capture renders one face at a time");
    }

    [Test]
    public void FrameOps_CaptureContextBeforePassValidation()
    {
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs");
        string blitSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.Blit.cs");
        string clearSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Init.cs");
        string indirectSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.IndirectDraw.cs");
        string meshletSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderer.Meshlets.cs");

        meshSource.ShouldContain("FrameOpContext context = Renderer.CaptureFrameOpContext();");
        meshSource.ShouldContain("Renderer.EnsureValidPassIndex(passIndex, \"MeshDraw\", context.PassMetadata)");
        blitSource.ShouldContain("FrameOpContext context = CaptureFrameOpContext();");
        blitSource.ShouldContain("EnsureValidPassIndex(passIndex, \"Blit\", context.PassMetadata)");
        clearSource.ShouldContain("EnsureValidPassIndex(passIndex, \"Clear\", context.PassMetadata)");
        indirectSource.ShouldContain("EnsureValidPassIndex(passIndex, \"IndirectDraw\", context.PassMetadata)");
        indirectSource.ShouldContain("EnsureValidPassIndex(passIndex, \"IndirectCountDraw\", context.PassMetadata)");
        meshletSource.ShouldContain("EnsureValidPassIndex(passIndex, \"MeshTaskDispatchIndirectCount\", context.PassMetadata)");
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
