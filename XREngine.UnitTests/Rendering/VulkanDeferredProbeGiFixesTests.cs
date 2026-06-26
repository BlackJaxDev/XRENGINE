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
        commandSource.ShouldContain("DefaultRenderPipeline.LightingAccumTextureName");
        commandSource.ShouldContain("LightProbeIrradianceArray");
        commandSource.ShouldContain("LightProbePrefilterArray");
        commandSource.ShouldContain("builder.SampleTexture(MakeTextureResource(textureName))");
        commandSource.ShouldContain("builder.ReadBuffer(bufferName)");

        shaderSource.ShouldContain("layout(std430, binding = 20) buffer LightProbePositions");
        shaderSource.ShouldContain("layout(std430, binding = 21) buffer LightProbeTetrahedra");
        shaderSource.ShouldContain("layout(std430, binding = 22) buffer LightProbeParameters");
        shaderSource.ShouldContain("layout(std430, binding = 23) buffer LightProbeGridCells");
        shaderSource.ShouldContain("layout(std430, binding = 24) buffer LightProbeGridIndices");
    }

    [Test]
    public void DeferredLightCombine_ProbeStorageBindings_DoNotOverlapGBufferSamplers()
    {
        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs");
        string legacyPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs");
        string commandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs");

        shaderSource.ShouldContain("layout(binding = 0) uniform sampler2D AlbedoOpacity");
        shaderSource.ShouldContain("layout(binding = 1) uniform sampler2D Normal");
        shaderSource.ShouldContain("layout(binding = 2) uniform sampler2D RMSE");
        shaderSource.ShouldContain("layout(binding = 5) uniform sampler2D LightingAccumTexture");
        shaderSource.ShouldContain("layout(std430, binding = 20) buffer LightProbePositions");
        shaderSource.ShouldContain("layout(std430, binding = 24) buffer LightProbeGridIndices");

        legacyPipelineSource.ShouldContain("private const uint ForwardLightProbePositionBufferBinding = 0u");
        legacyPipelineSource.ShouldContain("private const uint DeferredLightProbePositionBufferBinding = 20u");
        legacyPipelineSource.ShouldContain("BindPbrLightingResources(program, deferredProbeBufferBindings: true)");
        legacyPipelineSource.ShouldContain("_probePositionBuffer!.BindTo(program, positionBinding);");
        legacyPipelineSource.ShouldContain("_probeGridIndexBuffer!.BindTo(program, gridIndexBinding);");

        pipelineSource.ShouldContain("private const uint LightProbePositionBufferBinding = 20u");
        pipelineSource.ShouldContain("private const uint LightProbeTetraBufferBinding = 21u");
        pipelineSource.ShouldContain("private const uint LightProbeParamBufferBinding = 22u");
        pipelineSource.ShouldContain("private const uint LightProbeGridCellBufferBinding = 23u");
        pipelineSource.ShouldContain("private const uint LightProbeGridIndexBufferBinding = 24u");
        pipelineSource.ShouldContain("BindPbrLightingResources(program, deferredProbeBufferBindings: true)");
        pipelineSource.ShouldContain("_probePositionBuffer!.BindTo(program, LightProbePositionBufferBinding);");
        pipelineSource.ShouldContain("_probeGridIndexBuffer!.BindTo(program, LightProbeGridIndexBufferBinding);");

        commandSource.ShouldContain("x.BindingLocation = LightProbePositionBufferBinding;");
        commandSource.ShouldContain("x.BindingLocation = LightProbeTetraBufferBinding;");
        commandSource.ShouldContain("x.BindingLocation = LightProbeParamBufferBinding;");
        commandSource.ShouldContain("x.BindingLocation = LightProbeGridCellBufferBinding;");
        commandSource.ShouldContain("x.BindingLocation = LightProbeGridIndexBufferBinding;");
    }

    [Test]
    public void AmbientOcclusionQuadMetadata_DeclaresResolveChainDependencies()
    {
        string commandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderQuadToFBO.cs");

        commandSource.ShouldContain("DescribeAmbientOcclusionDependencies(context, builder, SourceQuadFBOName, destination, RenderGraphPassVariant)");
        commandSource.ShouldContain("DefaultRenderPipeline.AmbientOcclusionFBOName");
        commandSource.ShouldContain("DefaultRenderPipeline.AmbientOcclusionBlurFBOName");
        commandSource.ShouldContain("DefaultRenderPipeline.HBAOPlusBlurIntermediateFBOName");
        commandSource.ShouldContain("DefaultRenderPipeline.GTAOBlurIntermediateFBOName");
        commandSource.ShouldContain("builder.DependsOn(GetQuadBlitPassIndex(");
    }

    [Test]
    public void AmbientOcclusionResolveBranches_UseDistinctVulkanPassMetadata()
    {
        string commandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderQuadToFBO.cs");
        string defaultPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs");
        string pipeline2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs");

        commandSource.ShouldContain("RenderGraphPassVariant");
        commandSource.ShouldContain("BuildQuadBlitPassName(SourceQuadFBOName, destination, RenderGraphPassVariant)");
        commandSource.ShouldContain("ResolveAmbientOcclusionRawOutputTexture(variant)");
        commandSource.ShouldNotContain("builder.UseColorAttachment(MakeTextureResource(DefaultRenderPipeline.HBAOPlusRawTextureName), access, colorLoad, colorStore);\n                builder.UseColorAttachment(MakeTextureResource(DefaultRenderPipeline.GTAORawTextureName), access, colorLoad, colorStore);");

        defaultPipelineSource.ShouldContain("SetRenderGraphPassVariant(VPRC_RenderQuadToFBO.AmbientOcclusionResolveVariantGTAO)");
        defaultPipelineSource.ShouldContain("SetRenderGraphPassVariant(VPRC_RenderQuadToFBO.AmbientOcclusionResolveVariantHBAOPlus)");
        defaultPipelineSource.ShouldContain("[(int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion] = CreateSpatialHashAOResolveCommands()");

        pipeline2Source.ShouldContain("SetRenderGraphPassVariant(VPRC_RenderQuadToFBO.AmbientOcclusionResolveVariantGTAO)");
        pipeline2Source.ShouldContain("SetRenderGraphPassVariant(VPRC_RenderQuadToFBO.AmbientOcclusionResolveVariantHBAOPlus)");
        pipeline2Source.ShouldContain("[(int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion] = CreateSpatialHashAOResolveCommands()");
    }

    [Test]
    public void AmbientOcclusionSettingsLookups_DoNotCreateDefaultCameraPipelines()
    {
        string spatialHashSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/AO/VPRC_SpatialHashAOPass.cs");
        string lightingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs");
        string pipelineInstanceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");

        pipelineInstanceSource.ShouldContain("internal RenderPipeline? AssignedPipeline => _pipeline;");
        spatialHashSource.ShouldContain("ResolveCurrentSettings(instance, ResolveSettingsPipeline(instance))");
        spatialHashSource.ShouldContain("camera.GetPostProcessStageState<AmbientOcclusionSettings>(pipeline)");
        spatialHashSource.ShouldContain("IsSpatialHashAmbientOcclusionSelected(instance, ResolveSettingsPipeline(instance))");
        spatialHashSource.ShouldNotContain("GetPostProcessStageState<AmbientOcclusionSettings>();");
        lightingSource.ShouldContain("currentPipeline?.AssignedPipeline");
        lightingSource.ShouldContain("ambientOcclusionCamera?.GetPostProcessStageState<AmbientOcclusionSettings>(currentRenderPipeline)");
        lightingSource.ShouldNotContain("ambientOcclusionCamera?.GetPostProcessStageState<AmbientOcclusionSettings>();");
    }

    [Test]
    public void VulkanTransferReads_RestoreSampledSourcesToShaderReadLayout()
    {
        string blitSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        blitSource.ShouldContain("ResolvePostTransferReadLayout");
        blitSource.ShouldContain("ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit");
        blitSource.ShouldContain(": ImageLayout.ShaderReadOnlyOptimal;");
        blitSource.ShouldContain("ImageLayout postTransferLayout = ResolvePostTransferReadLayout(source);");

        commandBufferSource.ShouldContain("DerivePostBlitLayout(in BlitImageInfo info, bool isDestination)");
        commandBufferSource.ShouldContain("if ((usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0)");
        commandBufferSource.ShouldContain(": ImageLayout.ShaderReadOnlyOptimal;");
    }

    [Test]
    public void VulkanBlitReadback_RefreshesTextureViewsWithoutRegeneratingThem()
    {
        string blitSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs").Replace("\r\n", "\n");
        string textureViewSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");

        blitSource.ShouldContain("textureView.RefreshDescriptorFromViewedTextureIfStale();");
        blitSource.ShouldNotContain("textureView.Destroy();\n                textureView.Generate();");
        textureViewSource.ShouldContain("internal void RefreshDescriptorFromViewedTextureIfStale()");
    }

    [Test]
    public void ProgramBoundSsboDescriptors_AreResolvedAndFingerprintTracked()
    {
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string preparationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");
        string renderingStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string rendererStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");

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
        string preparationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");

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
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

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
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string initSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string exposureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanRenderer.AutoExposure.cs");

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
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");

        source.ShouldContain("TryResolveAllLayerAttachmentLayout");
        source.ShouldContain("ResolvePhysicalGroupWholeImageLayout()");
        source.ShouldContain("return knownLayout != ImageLayout.Undefined");
        source.ShouldContain(": _physicalGroup.LastKnownLayout;");
        source.ShouldContain("BeginPartialAttachmentLayoutTracking()");
    }

    [Test]
    public void PartialAttachmentLayoutTracking_DoesNotPromoteMissingLayersToWholeImageLayout()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");

        source.ShouldContain("if (_hasPartialAttachmentLayouts)");
        source.ShouldContain("if (layerIndex < 0 && TryResolveAllLayerAttachmentLayout((uint)Math.Max(mipLevel, 0), out layout))");
        source.ShouldContain("return ImageLayout.Undefined;");

        int descriptorPartialCheck = source.IndexOf("ImageLayout IVkImageDescriptorSource.TrackedImageLayout", StringComparison.Ordinal);
        int descriptorUndefinedFallback = source.IndexOf(": ImageLayout.Undefined;", descriptorPartialCheck, StringComparison.Ordinal);
        int attachmentQuery = source.IndexOf("ImageLayout IVkFrameBufferAttachmentSource.GetAttachmentTrackedLayout", StringComparison.Ordinal);
        int attachmentPartialFallback = source.IndexOf("return ImageLayout.Undefined;", attachmentQuery, StringComparison.Ordinal);
        int wholeImageResolver = source.IndexOf("private ImageLayout ResolvePhysicalGroupWholeImageLayout()", StringComparison.Ordinal);
        int wholeImageFallback = source.IndexOf(": _physicalGroup.LastKnownLayout;", wholeImageResolver, StringComparison.Ordinal);

        descriptorPartialCheck.ShouldBeGreaterThanOrEqualTo(0);
        descriptorUndefinedFallback.ShouldBeGreaterThan(descriptorPartialCheck);
        attachmentQuery.ShouldBeGreaterThan(descriptorPartialCheck);
        attachmentPartialFallback.ShouldBeGreaterThan(descriptorPartialCheck);
        wholeImageResolver.ShouldBeGreaterThan(attachmentQuery);
        wholeImageFallback.ShouldBeGreaterThan(wholeImageResolver);
    }

    [Test]
    public void VulkanRuntimeCrashFixes_PreventParallelContextAndIncompleteComputeDescriptorHazards()
    {
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs");
        string vmaSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Memory/VulkanVmaAllocator.cs");
        string graphCompilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        string imageTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string gpuBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");
        string bvhRaycastSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/BvhRaycastDispatcher.cs");
        string worldSource = ReadWorkspaceFile("XREngine/Rendering/XRWorldInstance.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string retirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");

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
    public void VulkanFrameOpSort_UsesRenderGraphPassOrderBeforeDependencySafeOriginalOrder()
    {
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        int passOrderSort = compilerSource.IndexOf("x.PassOrder.CompareTo(y.PassOrder)", StringComparison.Ordinal);
        int originalIndexTieBreak = compilerSource.IndexOf("x.OriginalIndex.CompareTo(y.OriginalIndex)", StringComparison.Ordinal);

        passOrderSort.ShouldBeGreaterThanOrEqualTo(0);
        originalIndexTieBreak.ShouldBeGreaterThan(passOrderSort);
        compilerSource.ShouldNotContain("x.GroupOrder.CompareTo(y.GroupOrder)");
        compilerSource.ShouldNotContain(".OrderBy(x => x.GroupOrder)");
        compilerSource.ShouldContain("ArrayPool<FrameOpSortKey>.Shared.Rent(opCount)");
        commandBufferSource.ShouldContain("Always sort frame ops by (PassOrder, safe draw order, OriginalIndex).");
        commandBufferSource.ShouldContain("counters are written before the draw commands that consume them.");
    }

    [Test]
    public void VulkanFrameOpSort_UsesCompiledGraphBeforeContextMetadataFallback()
    {
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");

        compilerSource.ShouldContain("PassOrderCache");
        compilerSource.ShouldContain("ResolvePassOrder(op, graph)");
        compilerSource.ShouldContain("if (graph.PassOrder.TryGetValue(op.PassIndex, out int graphOrder))");
        compilerSource.ShouldContain("op.Context.PassMetadata is { Count: > 0 } metadata");
        compilerSource.ShouldContain("RenderGraphSynchronizationPlanner.TopologicallySort(metadata)");
        compilerSource.ShouldContain("per-context metadata is only");
    }

    [Test]
    public void RuntimeRenderingCamera_FallsBackToPipelineFrameContextForDeferredVulkanWork()
    {
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngineFacade.cs");
        string engineStateSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.State.cs");
        string meshRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string programSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string materialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.RenderState.cs");

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
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string blitSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");
        string clearSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string indirectSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.IndirectDraw.cs");
        string meshletSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Meshlets/VulkanRenderer.Meshlets.cs");

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
