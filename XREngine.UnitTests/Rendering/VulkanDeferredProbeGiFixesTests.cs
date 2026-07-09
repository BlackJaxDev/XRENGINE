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

        descriptorSource.ShouldContain("ComputeReferencedProgramBufferResourceFingerprint(bindings)");
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
        commandBufferSource.ShouldContain("Always sort frame ops by (PassOrder, safe draw order, OriginalIndex)");
        commandBufferSource.ShouldContain("normalize same-target clears before first same-target use");
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
    public void VulkanFboReentry_PreservesTrackedClearLoadsAfterFirstUse()
    {
        string frameBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        frameBufferSource.ShouldContain("bool preserveTrackedClearLoads = false");
        frameBufferSource.ShouldContain("AttachmentLoadOp.Clear when preserveClearLoads => AttachmentLoadOp.Load");
        commandBufferSource.ShouldContain("bool targetReenteredThisCommandBuffer = fboLayoutTracking.ContainsKey(target);");
        commandBufferSource.ShouldContain("preserveTrackedClearLoads: targetReenteredThisCommandBuffer");
    }

    [Test]
    public void VulkanFrameOpSort_LiftsSameTargetClearBeforeFirstUse()
    {
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");

        compilerSource.ShouldContain("MoveTargetClearsBeforeFirstSameTargetUse(sortKeys, opCount)");
        compilerSource.ShouldContain("clearKey.Operation is not ClearOp clear");
        compilerSource.ShouldContain("IsSameSchedulingTarget(clear, previous.Operation)");
        compilerSource.ShouldContain("IsTargetUseThatClearMustPrecede(previous.Operation)");
        compilerSource.ShouldContain("ReferenceEquals(x.Target, y.Target)");
        compilerSource.ShouldContain("op is MeshDrawOp or BlitOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp or TransformFeedbackOp");
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
    public void VulkanLightProbeCapture_UsesDirectFboRenderPath()
    {
        string sceneCaptureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs");
        string lightProbeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");

        sceneCaptureSource.ShouldContain("protected virtual bool UseDirectFboTargetCommandsForCapture");
        sceneCaptureSource.ShouldContain("viewport.AllowAutomaticInternalResolution = false;");
        sceneCaptureSource.ShouldContain("viewport.UseDirectFboTargetCommandsWhenRenderingToFbo = UseDirectFboTargetCommandsForCapture;");
        lightProbeSource.ShouldContain("protected override bool UseDirectFboTargetCommandsForCapture");
        lightProbeSource.ShouldContain("=> RuntimeEngine.Rendering.State.IsVulkan;");
    }

    [Test]
    public void VulkanDeviceLossDiagnostics_IncludeLastSubmissionContext()
    {
        string diagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DeviceLossDiagnostics.cs");
        string syncSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SyncObjects.cs");
        string submitSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");

        diagnosticsSource.ShouldContain("VulkanSubmissionDiagnosticContext");
        diagnosticsSource.ShouldContain("RecordLastVulkanSubmissionDiagnosticContext");
        diagnosticsSource.ShouldContain("LastSubmit kind=");
        diagnosticsSource.ShouldContain("frameOp=");
        diagnosticsSource.ShouldContain("target=");
        diagnosticsSource.ShouldContain("cmdGen=");
        diagnosticsSource.ShouldContain("timeline(wait=");

        syncSource.ShouldContain("_deviceLostTransitionLock");
        syncSource.ShouldContain("BuildDeviceLostReasonWithSubmissionContext(reason)");
        syncSource.ShouldContain("Reason={DeviceLostReason ?? \"<unknown>\"}");

        submitSource.ShouldContain("VulkanSubmissionDiagnosticContext diagnosticContext = default");
        submitSource.ShouldContain("CompleteSubmissionDiagnosticContext(queue, ref submitInfo, fence, diagnosticContext, caller)");
        submitSource.ShouldContain("RecordLastVulkanSubmissionDiagnosticContext(diagnosticContext)");
    }

    [Test]
    public void VulkanDeviceLossDiagnostics_TagSwapchainAndOpenXrSubmissions()
    {
        string frameLoopSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrWorkerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.EyeRecordWorkers.cs");

        frameLoopSource.ShouldContain("CreateSwapchainSubmissionDiagnosticContext(");
        frameLoopSource.ShouldContain("\"SwapchainDraw\"");
        frameLoopSource.ShouldContain("sceneCommandBufferDirtyGeneration");
        frameLoopSource.ShouldContain("_commandBufferFrameOpSignatures");

        openXrSource.ShouldContain("CreateOpenXrSubmissionDiagnosticContext(");
        openXrSource.ShouldContain("\"OpenXrEyeSubmit\"");
        openXrSource.ShouldContain("\"OpenXrEyeMirrorSubmit\"");
        openXrSource.ShouldContain("\"OpenXrEyeMirrorRenderPublishSubmit\"");
        openXrSource.ShouldContain("\"OpenXrStereoLayerRenderPublishSubmit\"");
        openXrSource.ShouldContain("SubmitToQueueTracked(graphicsQueue, ref submitInfo, fence, diagnosticContext)");
        openXrWorkerSource.ShouldContain("\"OpenXrEyeParallelBatchSubmit\"");
    }

    [Test]
    public void VulkanPhase1Diagnostics_DefinePresetFlagsSettingsAndEnvironmentBridge()
    {
        string profileSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanFeatureProfile.cs");
        string backendSettingsSource = ReadWorkspaceFile("XREngine/Settings/Rendering/BackendRenderSettings.cs");
        string environmentSource = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string runtimeEffectiveSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEffectiveSettings.cs");
        string hostServicesSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderingHostServices.cs");
        string editorPreferencesSource = ReadWorkspaceFile("XREngine/Settings/EditorPreferences.cs");

        profileSource.ShouldContain("public enum EVulkanDiagnosticPreset");
        profileSource.ShouldContain("Off");
        profileSource.ShouldContain("StandardValidation");
        profileSource.ShouldContain("SyncValidation");
        profileSource.ShouldContain("GpuAssisted");
        profileSource.ShouldContain("BestPractices");
        profileSource.ShouldContain("CrashDiagnostics");
        profileSource.ShouldContain("RenderDocFriendly");
        profileSource.ShouldContain("[Flags]");
        profileSource.ShouldContain("SynchronizationValidation");
        profileSource.ShouldContain("DeviceFault");
        profileSource.ShouldContain("DeviceFaultDeviceLostOnMasked");
        profileSource.ShouldContain("NvDiagnosticCheckpoints");

        backendSettingsSource.ShouldContain("public EVulkanDiagnosticPreset DiagnosticPreset");
        backendSettingsSource.ShouldContain("public EVulkanDiagnosticFlags DiagnosticFlags");
        environmentSource.ShouldContain("VulkanDiagnosticPreset");
        environmentSource.ShouldContain("VulkanDiagnosticFlags");
        environmentSource.ShouldContain("VulkanSynchronizationValidation");
        environmentSource.ShouldContain("VulkanGpuAssistedValidation");
        environmentSource.ShouldContain("VulkanRenderDocFriendly");
        runtimeEffectiveSource.ShouldContain("public EVulkanDiagnosticPreset VulkanDiagnosticPreset");
        runtimeEffectiveSource.ShouldContain("public EVulkanDiagnosticFlags VulkanDiagnosticFlags");
        hostServicesSource.ShouldContain("EVulkanDiagnosticPreset VulkanDiagnosticPreset");
        hostServicesSource.ShouldContain("EVulkanDiagnosticFlags VulkanDiagnosticFlags");
        editorPreferencesSource.ShouldContain("VkDiagnosticPreset");
        editorPreferencesSource.ShouldContain("VkDiagnosticFlags");
    }

    [Test]
    public void VulkanPhase1Diagnostics_WireValidationFeaturesAndCapabilityReports()
    {
        string resolverSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanDiagnosticOptions.cs");
        string validationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Validation.cs");
        string instanceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Instance.cs");
        string extensionsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");

        resolverSource.ShouldContain("FlagsForPreset");
        resolverSource.ShouldContain("EVulkanDiagnosticPreset.CrashDiagnostics");
        resolverSource.ShouldContain("XREngineEnvironmentVariables.VulkanValidation");
        resolverSource.ShouldContain("ApplyBooleanEnvOverride");
        resolverSource.ShouldContain("BuildOverheadWarnings");

        validationSource.ShouldContain("PopulateEnabledValidationFeatures");
        validationSource.ShouldContain("ValidationFeatureEnableEXT.SynchronizationValidationExt");
        validationSource.ShouldContain("ValidationFeatureEnableEXT.GpuAssistedExt");
        validationSource.ShouldContain("ValidationFeatureEnableEXT.BestPracticesExt");
        validationSource.ShouldContain("RecordStructuredVulkanValidationMessage");
        validationSource.ShouldContain("DescribeVulkanValidationSummary");
        instanceSource.ShouldContain("ValidationFeaturesEXT");
        instanceSource.ShouldContain("LogResolvedVulkanDiagnosticOptions(extensions)");
        extensionsSource.ShouldContain("ExtDebugUtils.ExtensionName");

        logicalDeviceSource.ShouldContain("ExtDeviceFaultExtensionName");
        logicalDeviceSource.ShouldContain("KhrDeviceFaultExtensionName");
        logicalDeviceSource.ShouldContain("NvDeviceDiagnosticCheckpointsExtensionName");
        logicalDeviceSource.ShouldContain("PhysicalDeviceFaultFeaturesEXT");
        logicalDeviceSource.ShouldContain("VulkanKhrPhysicalDeviceFaultFeatures");
        logicalDeviceSource.ShouldContain("QueryKhrDeviceFaultCapabilities");
        logicalDeviceSource.ShouldContain("TryLoadKhrDeviceFaultFunctionPointers");
        logicalDeviceSource.ShouldContain("DeviceDiagnosticsConfigCreateInfoNV");
        logicalDeviceSource.ShouldContain("LogVulkanDiagnosticDeviceCapabilities");
        logicalDeviceSource.ShouldContain("Requested diagnostic device extension is unsupported");
    }

    [Test]
    public void VulkanPhase1Diagnostics_DeviceLossFooterIncludesBreadcrumbsFaultsAndNamedSyncObjects()
    {
        string diagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DeviceLossDiagnostics.cs");
        string phase1Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DeviceLossDiagnostics.Phase1.cs");
        string syncSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SyncObjects.cs");
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string oneTimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.OneTimeSubmit.cs");

        diagnosticsSource.ShouldContain("VulkanCrashBreadcrumbCapacity");
        diagnosticsSource.ShouldContain("RecordVulkanCrashBreadcrumb");
        diagnosticsSource.ShouldContain("DescribeVulkanCrashBreadcrumbTail");
        diagnosticsSource.ShouldContain("DescribeVulkanValidationSummary");
        diagnosticsSource.ShouldContain("DescribeVulkanFaultDiagnosticsAfterDeviceLoss");
        diagnosticsSource.ShouldContain("LastCommandMarkerSerial");
        diagnosticsSource.ShouldContain("ImageLayoutTransitionSerial");
        diagnosticsSource.ShouldContain("DescriptorTableGeneration");
        diagnosticsSource.ShouldContain("FirstFailingApi");
        diagnosticsSource.ShouldContain("AppendDeviceAddressBindingSummary");
        phase1Source.ShouldContain("GetDeviceFaultInfo");
        phase1Source.ShouldContain("GetQueueCheckpointData2");

        syncSource.ShouldContain("Timeline.Graphics");
        syncSource.ShouldContain("AcquireBridge[");
        syncSource.ShouldContain("PresentBridge[");
        openXrSource.ShouldContain("OpenXR.SubmitAndWaitFence");
        oneTimeSource.ShouldContain("OneShot.TransferFence");
        oneTimeSource.ShouldContain("OneShot.GraphicsFence");
    }

    [Test]
    public void VulkanPhase1Diagnostics_CollectFaultArtifactsAddressBindingsAndCheckpoints()
    {
        string diagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DeviceLossDiagnostics.cs");
        string phase1Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DeviceLossDiagnostics.Phase1.cs");
        string validationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Validation.cs");
        string synchronizationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string recordingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        string descriptorHeapSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorHeap.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");

        diagnosticsSource.ShouldContain("FirstFailingApi");
        phase1Source.ShouldContain("DeviceFaultInfoEXT");
        phase1Source.ShouldContain("PAddressInfos");
        phase1Source.ShouldContain("PVendorInfos");
        phase1Source.ShouldContain("PVendorBinaryData");
        phase1Source.ShouldContain("vulkan-device-fault-report.log");
        phase1Source.ShouldContain("vulkan-device-fault-vendor-");
        phase1Source.ShouldContain("Result.Incomplete");
        phase1Source.ShouldContain("vendorBinaryStatus");
        phase1Source.ShouldContain("DeviceAddressBindingCallbackDataEXT");
        phase1Source.ShouldContain("RegisterVulkanDeviceAddressRange");
        phase1Source.ShouldContain("DescribeVulkanAddressCorrelation");
        phase1Source.ShouldContain("CmdSetCheckpoint");
        phase1Source.ShouldContain("VulkanNvCheckpointMarkerCapacity");
        phase1Source.ShouldContain("GetQueueCheckpointData2");
        validationSource.ShouldContain("RecordVulkanDeviceAddressBindingCallback(pCallbackData)");
        synchronizationSource.ShouldContain("RecordVulkanImageLayoutTransitionBreadcrumb(imageBarrierCount, imageBarriers, caller)");
        recordingSource.ShouldContain("RecordVulkanCommandDiagnosticMarker(commandBuffer, op, opPassIndex, opIndex)");
        bufferSource.ShouldContain("RegisterVulkanDeviceAddressRange(buffer, address, bufferSize");
        descriptorHeapSource.ShouldContain("RecordVulkanDescriptorTableGeneration");
        logicalDeviceSource.ShouldContain("VendorCrashHooks");
    }

    [Test]
    public void VulkanPhase1Diagnostics_DebugNamesCoverPhase1ObjectTypes()
    {
        string validationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Validation.cs");
        string swapchainFramebufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Framebuffers/VulkanRenderer.SwapchainFramebuffers.cs");
        string framebufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string swapchainDescriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorSets.cs");
        string materialTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.BindlessMaterialTextureTable.cs");
        string imguiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string phase1Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DeviceLossDiagnostics.Phase1.cs");

        validationSource.ShouldContain("SetDebugDescriptorSetName");
        validationSource.ShouldContain("SetDebugDescriptorSetNames");
        swapchainFramebufferSource.ShouldContain("ObjectType.Framebuffer");
        swapchainFramebufferSource.ShouldContain("Swapchain.Framebuffer[");
        framebufferSource.ShouldContain("ObjectType.Framebuffer");
        framebufferSource.ShouldContain("FBO.");
        swapchainDescriptorSource.ShouldContain("SetDebugDescriptorSetNames(descriptorSets, \"Swapchain.DescriptorSet\")");
        materialTextureSource.ShouldContain("GlobalMaterialTexture.DescriptorSet");
        imguiSource.ShouldContain("ImGui.Font.DescriptorSet");
        openXrSource.ShouldContain("OpenXR.SwapchainImageView");
        phase1Source.ShouldContain("FrameOpContext.");
    }

    [Test]
    public void VulkanPhase11Diagnostics_KhrDeviceFaultShimIsWired()
    {
        string shimSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.KhrDeviceFault.cs");
        string logicalDeviceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");
        string phase1Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.DeviceLossDiagnostics.Phase1.cs");
        string todoSource = ReadWorkspaceFile("docs/work/todo/rendering/vulkan-core-hardening-and-device-loss-todo.md");

        shimSource.ShouldContain("VulkanKhrDeviceFaultPhysicalDeviceFeaturesSType = 1000573000");
        shimSource.ShouldContain("VulkanKhrDeviceFaultPhysicalDevicePropertiesSType = 1000573001");
        shimSource.ShouldContain("VulkanKhrDeviceFaultInfoSType = 1000573002");
        shimSource.ShouldContain("VulkanKhrDeviceFaultDebugInfoSType = 1000573003");
        shimSource.ShouldContain("VulkanKhrPhysicalDeviceFaultFeatures");
        shimSource.ShouldContain("VulkanKhrPhysicalDeviceFaultProperties");
        shimSource.ShouldContain("VulkanKhrDeviceFaultAddressInfo");
        shimSource.ShouldContain("VulkanKhrDeviceFaultVendorInfo");
        shimSource.ShouldContain("VulkanKhrDeviceFaultInfo");
        shimSource.ShouldContain("VulkanKhrDeviceFaultDebugInfo");
        shimSource.ShouldContain("VulkanKhrDeviceFaultVendorBinaryHeaderVersionOne");
        shimSource.ShouldContain("VkGetDeviceFaultReportsKhrDelegate");
        shimSource.ShouldContain("VkGetDeviceFaultDebugInfoKhrDelegate");
        shimSource.ShouldContain("vkGetDeviceFaultReportsKHR");
        shimSource.ShouldContain("vkGetDeviceFaultDebugInfoKHR");
        shimSource.ShouldContain("GetDeviceProcAddr");
        shimSource.ShouldContain("DeviceFaultKHR active");
        shimSource.ShouldContain("KHR advertised but function pointer unavailable");
        shimSource.ShouldContain("vulkan-device-fault-khr-reports.log");
        shimSource.ShouldContain("vulkan-device-fault-khr-debug-info.log");
        shimSource.ShouldContain("vulkan-device-fault-khr-vendor-");

        logicalDeviceSource.ShouldContain("AddDiagnosticDeviceExtensionIfRequested(KhrDeviceFaultExtensionName");
        logicalDeviceSource.ShouldContain("enableKhrDeviceFaultFeature");
        logicalDeviceSource.ShouldContain("enableExtDeviceFaultFeature");
        logicalDeviceSource.ShouldContain("enableKhrDeviceFaultReportMasked");
        logicalDeviceSource.ShouldContain("RequestDeviceFaultDeviceLostOnMasked");
        logicalDeviceSource.ShouldContain("DeviceFaultEXT compatibility active");
        logicalDeviceSource.ShouldContain("activePath=");

        phase1Source.ShouldContain("TryAppendKhrDeviceFaultSummary(builder)");
        phase1Source.ShouldContain("_deviceFaultUsingKhr && khrQueried");
        phase1Source.ShouldContain("_supportsExtDeviceFault");

        todoSource.ShouldContain("- [x] Add a small local KHR shim partial");
        todoSource.ShouldContain("- [x] A driver that advertises `VK_KHR_device_fault` uses the KHR query path");
        todoSource.ShouldContain("- [x] Logs make KHR-vs-EXT coverage unambiguous.");
    }

    [Test]
    public void VulkanPhase2_FrameOpContextContractIncludesIsolationFingerprint()
    {
        string plannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");

        plannerSource.ShouldContain("internal enum EVulkanFrameOpContextKind");
        plannerSource.ShouldContain("MainViewport");
        plannerSource.ShouldContain("OpenXrEye");
        plannerSource.ShouldContain("OpenXrMirror");
        plannerSource.ShouldContain("SceneCapture");
        plannerSource.ShouldContain("LightProbeCapture");
        plannerSource.ShouldContain("Shadow");
        plannerSource.ShouldContain("UiPreview");
        plannerSource.ShouldContain("DiagnosticCapture");
        plannerSource.ShouldContain("ulong ContextId");
        plannerSource.ShouldContain("ulong RecordingFingerprint");
        plannerSource.ShouldContain("uint SubmissionQueueFamily");
        plannerSource.ShouldContain("bool StereoEnabled");
        plannerSource.ShouldContain("bool MultiviewEnabled");
        plannerSource.ShouldContain("ulong ResourceGeneration");
        plannerSource.ShouldContain("ulong DescriptorGeneration");
        plannerSource.ShouldContain("CompleteFrameOpContext");
        plannerSource.ShouldContain("ComputeFrameOpContextRecordingFingerprint");
        plannerSource.ShouldContain("RefreshFrameOpContextRecordingFingerprint");
        plannerSource.ShouldContain("ResolveFrameOpContextKind");

        stateSource.ShouldContain("EVulkanFrameOpContextKind ContextKind");
        stateSource.ShouldContain("int PassMetadataSignature");
        stateSource.ShouldContain("ulong ResourceGeneration");
        stateSource.ShouldContain("ulong DescriptorGeneration");
        stateSource.ShouldContain("uint SubmissionQueueFamily");
        stateSource.ShouldContain("kind={ContextKind} contextId={ContextId} context=0x{RecordingFingerprint:X16}");

        meshSource.ShouldContain("hash.Add((int)op.Context.ContextKind)");
        meshSource.ShouldContain("hash.Add(op.Context.RecordingFingerprint)");
        meshSource.ShouldContain("hash.Add(op.Context.OutputFrameBufferIdentity)");
        plannerSource.ShouldContain("metadata-only graph change");
    }

    [Test]
    public void VulkanPhase2_CommandBufferReuseRejectsFrameOpContextMismatch()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferState.cs");
        string recordingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string allocationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferAllocation.cs");
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrScopeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXrExternalSwapchainRenderScope.cs");

        stateSource.ShouldContain("RecordedFrameOpContextFingerprint");
        stateSource.ShouldContain("RecordedFrameOpContextId");
        allocationSource.ShouldContain("evicted.RecordedFrameOpContextFingerprint = ulong.MaxValue");
        recordingSource.ShouldContain("ComputeCommandBufferFrameOpContextFingerprint");
        recordingSource.ShouldContain("TryValidateCommandBufferVariantContext");
        recordingSource.ShouldContain("EnsureCommandBufferVariantContextBeforeSubmit");
        recordingSource.ShouldContain("Vulkan command buffer frame-op context mismatch before submit");
        recordingSource.ShouldContain("LogCommandBufferFrameOpContextMismatch");
        recordingSource.ShouldContain("frame-op context mismatch");
        recordingSource.ShouldContain("ShouldFailFastOnFrameOpContextMismatch");
        recordingSource.ShouldContain("variant.RecordedFrameOpContextFingerprint = frameOpContextFingerprint");
        recordingSource.ShouldContain("\"last-swapchain-writer\"");
        recordingSource.ShouldContain("\"command-chain-primary\"");

        openXrSource.ShouldContain("_threadOpenXrExternalSwapchainContextKind");
        openXrSource.ShouldContain("EVulkanFrameOpContextKind.OpenXrMirror");
        openXrSource.ShouldContain("openxr-primary-miss:context");
        openXrSource.ShouldContain("openxr-mirror-primary-miss:context");
        openXrSource.ShouldContain("frameOpContextFingerprint");
        openXrSource.ShouldContain("hash = (hash * 397) ^ 0x53494E54");
        openXrSource.ShouldContain("HashCode.Combine(\"OpenXR\", viewIndex, imageIndex)");
        openXrScopeSource.ShouldContain("_previousThreadContextKind");
        openXrScopeSource.ShouldContain("_threadOpenXrExternalSwapchainContextKind = contextKind");
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
