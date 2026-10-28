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
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipelineQuadDescriptors.cs");
        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs");

        descriptorSource.ShouldContain("DeferredLightCombine()");
        descriptorSource.ShouldContain("DefaultRenderPipeline.AlbedoOpacityTextureName");
        descriptorSource.ShouldContain("DefaultRenderPipeline.NormalTextureName");
        descriptorSource.ShouldContain("DefaultRenderPipeline.RMSETextureName");
        descriptorSource.ShouldContain("DefaultRenderPipeline.DepthViewTextureName");
        descriptorSource.ShouldContain("DefaultRenderPipeline.LightingAccumTextureName");
        descriptorSource.ShouldContain("LightProbeIrradianceArray");
        descriptorSource.ShouldContain("LightProbePrefilterArray");
        descriptorSource.ShouldContain(".SampleTexture(");
        descriptorSource.ShouldContain(".ReadBuffer(");

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
        string legacyPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");

        shaderSource.ShouldContain("layout(binding = 0) uniform sampler2D AlbedoOpacity");
        shaderSource.ShouldContain("layout(binding = 1) uniform sampler2D Normal");
        shaderSource.ShouldContain("layout(binding = 2) uniform sampler2D RMSE");
        shaderSource.ShouldContain("layout(binding = 5) uniform sampler2D LightingAccumTexture");
        shaderSource.ShouldContain("layout(std430, binding = 20) buffer LightProbePositions");
        shaderSource.ShouldContain("layout(std430, binding = 24) buffer LightProbeGridIndices");

        legacyPipelineSource.ShouldContain("private const uint ForwardLightProbePositionBufferBinding = 0u");
        legacyPipelineSource.ShouldContain("private const uint DeferredLightProbePositionBufferBinding = 20u");
        legacyPipelineSource.ShouldContain("BindPbrLightingResources(program, deferredProbeBufferBindings: true)");
        legacyPipelineSource.ShouldContain("state.PositionBuffer!.BindTo(program, positionBinding);");
        legacyPipelineSource.ShouldContain("state.GridIndexBuffer!.BindTo(program, gridIndexBinding);");

    }

    [Test]
    public void AmbientOcclusionQuadMetadata_DeclaresResolveChainDependencies()
    {
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipelineQuadDescriptors.cs");

        descriptorSource.ShouldContain("AmbientOcclusionFinal(");
        descriptorSource.ShouldContain("DefaultRenderPipeline.AmbientOcclusionFBOName");
        descriptorSource.ShouldContain("DefaultRenderPipeline.AmbientOcclusionBlurFBOName");
        descriptorSource.ShouldContain(".DependsOnQuadBlit(");
    }

    [Test]
    public void AmbientOcclusionResolveBranches_UseDistinctVulkanPassMetadata()
    {
        string commandSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderQuadToFBO.cs");
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipelineQuadDescriptors.cs");
        string defaultPipelineSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");

        commandSource.ShouldContain("RenderGraphPassVariant");
        commandSource.ShouldContain("BuildQuadBlitPassName(SourceQuadFBOName, destination, RenderGraphPassVariant)");
        commandSource.ShouldNotContain("builder.UseColorAttachment(MakeTextureResource(DefaultRenderPipeline.HBAOPlusRawTextureName), access, colorLoad, colorStore);\n                builder.UseColorAttachment(MakeTextureResource(DefaultRenderPipeline.GTAORawTextureName), access, colorLoad, colorStore);");
        descriptorSource.ShouldContain("AmbientOcclusionResolveVariantHBAOPlus");
        descriptorSource.ShouldContain("AmbientOcclusionResolveVariantGTAO");
        descriptorSource.ShouldContain("AmbientOcclusionResolveVariantDisabled");
        descriptorSource.ShouldContain("AmbientOcclusionGenerate(");
        descriptorSource.ShouldContain("AmbientOcclusionFinal(");
        descriptorSource.ShouldContain("AmbientOcclusionIntermediateBlur(");
        descriptorSource.ShouldContain("AmbientOcclusionFinalBlur(");
        descriptorSource.ShouldContain(".UseColorTexture(");
        descriptorSource.ShouldContain(".DependsOnQuadBlit(");

        defaultPipelineSource.ShouldContain("SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantGTAO)");
        defaultPipelineSource.ShouldContain("SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantHBAOPlus)");
        defaultPipelineSource.ShouldContain("[(int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion] = CreateSpatialHashAOResolveCommands()");

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
        string policySource = ReadWorkspaceFile(VulkanRoot + "Commands/Readback/VulkanReadbackLayoutPolicy.cs");
        string readbackSource = ReadWorkspaceFile(VulkanRoot + "Commands/Readback/VulkanCommandRuntime.PixelReadback.cs");
        string commandBufferSource = ReadWorkspaceFile(VulkanRoot + "Commands/Transfers/VulkanRenderer.BlitRecording.cs");

        policySource.ShouldContain("internal static ImageLayout ResolvePostTransfer(in BlitImageInfo info)");
        policySource.ShouldContain("(ImageUsageFlags.SampledBit |\n              ImageUsageFlags.InputAttachmentBit)) != 0)");
        policySource.ShouldContain(": ImageLayout.ShaderReadOnlyOptimal;");
        readbackSource.ShouldContain("ImageLayout postTransferLayout = VulkanReadbackLayoutPolicy.ResolvePostTransfer(liveSource);");

        commandBufferSource.ShouldContain("DerivePostBlitLayout(in BlitImageInfo info, bool isDestination)");
        commandBufferSource.ShouldContain("if ((usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0)");
        commandBufferSource.ShouldContain(": ImageLayout.ShaderReadOnlyOptimal;");
    }

    [Test]
    public void VulkanBlitReadback_RefreshesTextureViewsWithoutRegeneratingThem()
    {
        string blitSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.LegacyBlitResolution.cs");
        string textureViewSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/Textures/VkTextureView.cs");

        blitSource.ShouldContain("textureView.RefreshDescriptorFromViewedTextureIfStale();");
        blitSource.ShouldNotContain("textureView.Destroy();");
        blitSource.ShouldNotContain("textureView.Generate();");
        textureViewSource.ShouldContain("internal void RefreshDescriptorFromViewedTextureIfStale()");
    }

    [Test]
    public void ProgramBoundSsboDescriptors_AreResolvedAndFingerprintTracked()
    {
        string fingerprintSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/MeshRendering/VkMeshRenderer.DescriptorFingerprints.cs");
        string bufferResolutionSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/MeshRendering/VkMeshRenderer.DescriptorBuffers.cs");
        string bindingInfoSource = ReadWorkspaceFile(VulkanRoot + "Shaders/DescriptorBindingInfo.cs");
        string preparationSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");
        string renderingStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs");
        string programSource = SourceContractWorkspace.ReadPartialType(VulkanRoot + "BackendObjects/Programs/VkRenderProgram.cs");
        string dataBufferSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/Buffers/VkDataBuffer.cs");
        string plannerPortSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/Programs/VulkanProgramPlannerPort.cs");
        string publicationSource = ReadWorkspaceFile(VulkanRoot + "RenderGraph/Authority/VulkanResourcePlannerPublicationReader.cs");

        fingerprintSource.ShouldContain("ComputeReferencedProgramBufferResourceFingerprint(bindings)");
        bufferResolutionSource.ShouldContain("TryResolvePipelineResourceBuffer(binding, out buffer)");
        bufferResolutionSource.ShouldContain("pipeline.TryGetBuffer(name, out XRDataBuffer? buffer)");
        bufferResolutionSource.ShouldContain("IsDescriptorCompatibleBufferTarget(descriptorType, buffer.Target)");
        bufferResolutionSource.ShouldContain("TryResolveProgramBoundBuffer(binding, out buffer)");
        bufferResolutionSource.ShouldContain("_program.TryGetBoundBuffer(binding.Binding");
        fingerprintSource.ShouldContain("_program.TryGetBoundBuffer(binding.Binding");
        bufferResolutionSource.ShouldContain("private static bool IsOptionalPipelineStorageBuffer(DescriptorBindingInfo binding)");
        bufferResolutionSource.ShouldContain("binding.Requirement == EVulkanDescriptorBindingRequirement.Optional");
        bufferResolutionSource.ShouldContain("!IsOptionalPipelineStorageBuffer(binding)");
        bindingInfoSource.ShouldContain("name is (\"LightProbePositions\" or");
        bindingInfoSource.ShouldContain("? EVulkanDescriptorBindingRequirement.Optional");
        preparationSource.ShouldContain("ApplyScopedProgramBindingsForPreparation(material);");
        preparationSource.ShouldContain("ApplyScopedProgramBindings(program)");
        renderingStateSource.ShouldContain("RuntimeEngine.Rendering.State.CurrentRenderingPipeline");

        programSource.ShouldContain("_buffersByBinding[index] = buffer");
        programSource.ShouldContain("internal bool TryGetBoundBuffer");
        programSource.ShouldContain("internal void AddBoundBufferResourceFingerprint");
        programSource.ShouldContain("SetSamplerNoLock(name, xrTexture, unit);");

        // Named buffers are published to the frame planner so that render-graph
        // resource names resolve to the bound buffer.
        dataBufferSource.ShouldContain("BackendContext.Resources.PlannerPublications.TrackBufferBinding(Data);");
        plannerPortSource.ShouldContain("internal void TrackBufferBinding(XRDataBuffer buffer)");
        publicationSource.ShouldContain("internal void TrackBufferBinding(XRDataBuffer buffer)");
        publicationSource.ShouldContain("_owner.TrackedBuffersByName[name] = buffer;");
    }

    [Test]
    public void VulkanPreparation_PreBindsDescriptorAffectingLightingResources()
    {
        string preparationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");

        int prebindIndex = preparationSource.IndexOf("ApplyScopedProgramBindingsForPreparation(material);", StringComparison.Ordinal);
        int descriptorIndex = preparationSource.IndexOf("EnsureDescriptorSets(", prebindIndex, StringComparison.Ordinal);

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
        string drawingSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        drawingSource.ShouldContain("renderer-owned descriptor path");
        drawingSource.ShouldNotContain("vkMaterial.TryBindDescriptorSets(commandBuffer, _program, imageIndex)");

        // The descriptor bind for a mesh draw must follow the per-draw engine
        // and auto uniform uploads.
        int bindMethodIndex = drawingSource.IndexOf("private bool BindDescriptorsIfAvailable(", StringComparison.Ordinal);
        int engineUploadIndex = drawingSource.IndexOf("UpdateEngineUniformBuffersForDraw(imageIndex, drawUniformSlot, draw);", bindMethodIndex, StringComparison.Ordinal);
        int autoUploadIndex = drawingSource.IndexOf("if (!UpdateAutoUniformBuffersForDraw(\n\t\t\t\timageIndex,\n\t\t\t\tdrawUniformSlot,\n\t\t\t\tmaterial,\n\t\t\t\tdraw,", engineUploadIndex, StringComparison.Ordinal);
        int bindIndex = drawingSource.IndexOf("CommandOperations.BindDescriptorSetsTracked(", autoUploadIndex, StringComparison.Ordinal);

        bindMethodIndex.ShouldBeGreaterThanOrEqualTo(0);
        engineUploadIndex.ShouldBeGreaterThan(bindMethodIndex);
        autoUploadIndex.ShouldBeGreaterThan(engineUploadIndex);
        bindIndex.ShouldBeGreaterThan(autoUploadIndex);
    }

    [Test]
    public void PostProcessCompositeInputs_AreConcreteAndClearedBeforeOptionalFogPasses()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");

        AssertPostProcessCompositeInputDefaults(pipelineSource);
    }

    private static void AssertPostProcessCompositeInputDefaults(string source)
    {
        source.ShouldContain("AppendPostProcessCompositeInputDefaults(");
        source.ShouldContain("AtmosphereUpscaleFBOName");
        source.ShouldContain("VolumetricFogUpscaleFBOName");
        source.ShouldContain("ColorF4.Transparent");
        source.ShouldContain("clearColor: true, clearDepth: false, clearStencil: false");

        int defaultsIndex = source.IndexOf("AppendPostProcessCompositeInputDefaults(", StringComparison.Ordinal);
        int atmosphereIndex = source.IndexOf("AppendAtmosphericScattering(", defaultsIndex, StringComparison.Ordinal);
        int fogIndex = source.IndexOf("AppendVolumetricFog(", atmosphereIndex, StringComparison.Ordinal);
        int postProcessIndex = source.IndexOf("AppendFinalPostProcess(", fogIndex, StringComparison.Ordinal);

        defaultsIndex.ShouldBeGreaterThanOrEqualTo(0);
        atmosphereIndex.ShouldBeGreaterThan(defaultsIndex);
        fogIndex.ShouldBeGreaterThan(atmosphereIndex);
        postProcessIndex.ShouldBeGreaterThan(fogIndex);
    }

    [Test]
    public void DefaultRenderPipeline_DeclaresProbeTextureArrayResourceNames()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");

        pipelineSource.ShouldContain("private const string LightProbeIrradianceArrayName = \"LightProbeIrradianceArray\"");
        pipelineSource.ShouldContain("private const string LightProbePrefilterArrayName = \"LightProbePrefilterArray\"");
    }


    [Test]
    public void ComputeDispatch_UsesLastActiveContextAndFuzzyAutoUniformBlocks()
    {
        string stateSource = ReadWorkspaceFile(VulkanRoot + "RenderGraph/Authority/ResourcePlannerRuntimeState.cs");
        string plannerContextSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.PlannerContext.cs");
        string legacyCommandSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.LegacyCommandApi.cs");
        string plannerPortSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/Programs/VulkanProgramPlannerPort.cs");
        string programSource = SourceContractWorkspace.ReadPartialType(VulkanRoot + "BackendObjects/Programs/VkRenderProgram.cs");
        string exposureSource = ReadWorkspaceFile(VulkanRoot + "Resources/Authority/VulkanResourceRuntime.AutoExposureComputeResources.cs");

        stateSource.ShouldContain("public FrameOpContext? LastActiveFrameOpContext;");
        plannerContextSource.ShouldContain("internal FrameOpContext CaptureFrameOpContextOrLastActive()");
        plannerContextSource.ShouldContain(".LastActiveFrameOpContext ?? default;");
        legacyCommandSource.ShouldContain("FrameOpContext context = CaptureFrameOpContextOrLastActive();");
        legacyCommandSource.ShouldContain("context.PassMetadata");

        // Compute dispatches requested outside a pipeline scope use the last
        // active frame-op context of the current planner generation.
        string computeDispatch = Slice(plannerPortSource, "ComputeDispatchSnapshot snapshot = program.CaptureComputeSnapshot();", "internal void EnqueueTransformFeedback(");
        plannerPortSource.ShouldContain("FrameOpContext frameContext = GetCurrentGeneration().State.LastActiveFrameOpContext ?? default;");
        computeDispatch.ShouldContain("ComputeDispatchOp operation = ComputeDispatchOp.Rent(");
        computeDispatch.ShouldContain("frameContext);");

        programSource.ShouldContain("TryGetAutoUniformBlockFuzzy(binding.Name, binding.Set, binding.Binding");
        programSource.ShouldContain("candidate.BlockName");
        exposureSource.ShouldContain("Name = \"VulkanAutoExposure2D\"");
        exposureSource.ShouldContain("Name = \"VulkanAutoExposure2DArray\"");
    }

    [Test]
    public void ProbeTetraBufferName_MatchesDeferredShaderBlock()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");
        string advancedPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs");
        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs");

        pipelineSource.ShouldContain("private const string LightProbeTetraBufferName = \"LightProbeTetrahedra\"");
        pipelineSource.ShouldContain("tetrahedronBuffer = new XRDataBuffer(\n                    LightProbeTetraBufferName,");
        advancedPipelineSource.ShouldContain("private const string LightProbeTetraBufferName = \"LightProbeTetrahedra\"");
        shaderSource.ShouldContain("buffer LightProbeTetrahedra");
    }

    [Test]
    public void AttachmentLayoutTracking_FallsBackToKnownWholeImageLayout()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");

        source.ShouldContain("TryResolveAllLayerAttachmentLayout");
        source.ShouldContain("ResolvePhysicalGroupWholeImageLayout()");
        source.ShouldContain("return knownLayout != ImageLayout.Undefined");
        source.ShouldContain(": _physicalGroup.LastKnownLayout;");
        source.ShouldContain("BeginPartialAttachmentLayoutTracking()");
    }

    [Test]
    public void PartialAttachmentLayoutTracking_DoesNotPromoteMissingLayersToWholeImageLayout()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");

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
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");
        string vmaSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Memory/VulkanVmaAllocator.cs");
        string schedulerSource = ReadWorkspaceFile(VulkanRoot + "RenderGraph/VulkanFrameOperationScheduler.cs");
        string programSource = SourceContractWorkspace.ReadPartialType(VulkanRoot + "BackendObjects/Programs/VkRenderProgram.cs");
        string bufferSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/Buffers/VkDataBuffer.cs");
        string bufferOperationsSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.BufferOperations.cs");
        string imageTextureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string gpuBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");
        string bvhRaycastSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/BvhRaycastDispatcher.cs");
        string worldSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/RuntimeWorldRenderer.Picking.cs");
        string oneTimeSubmitSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.OneTimeSubmit.cs");
        string retirementSource = ReadWorkspaceFile(VulkanRoot + "Resources/Authority/VulkanResourceRuntime.cs");

        runtimeSource.ShouldContain("[ThreadStatic]");
        runtimeSource.ShouldContain("private static Stack<XRRenderPipelineInstance?>? t_pipelineOverrideStack");
        runtimeSource.ShouldContain("PipelineOverrideStack => t_pipelineOverrideStack ??= new()");

        vmaSource.ShouldContain("private readonly object _mapCountsGate = new();");
        vmaSource.ShouldContain("lock (_mapCountsGate)");
        vmaSource.ShouldContain("DrainMappedAllocation_NoLock");
        vmaSource.ShouldContain("Skipping VMA allocator destruction because");

        // Parallel secondary recording groups only homogeneous, recording-compatible
        // runs. Compute dispatches use their own family and frozen dispatch
        // snapshots; graphics transfer and indirect draws stay on the primary.
        string secondaryFamilies = Slice(
            schedulerSource,
            "private static bool TryResolveSecondaryCommandFamily(",
            "private static Type GetOperationType(");
        secondaryFamilies.ShouldContain("case EVulkanPrimaryPlanNodeKind.ComputeDispatch:");
        secondaryFamilies.ShouldContain("family = EVulkanSecondaryCommandFamily.Compute;");
        secondaryFamilies.ShouldNotContain("EVulkanPrimaryPlanNodeKind.Blit");
        secondaryFamilies.ShouldNotContain("EVulkanPrimaryPlanNodeKind.IndirectDraw");
        secondaryFamilies.ShouldNotContain("EVulkanPrimaryPlanNodeKind.MeshDraw");
        schedulerSource.ShouldContain("runFamily == family &&");
        schedulerSource.ShouldContain("FrameOpContextCompatibility.AreRecordingCompatible(runContext, context);");

        programSource.ShouldContain("ConcurrentDictionary<string, byte> _computeWarnings");
        programSource.ShouldContain("bool unresolved = false;");
        programSource.ShouldContain("Compute dispatch will be skipped.");
        programSource.ShouldContain("if (!unresolved)\n            return scratch.WriteCount != 0;");
        programSource.ShouldContain("compute descriptor build had unresolved required bindings");
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
        worldSource.ShouldContain("TryCreateUnsupportedGpuMeshBvhCoarsePick(");
        bvhRaycastSource.ShouldContain("if (buffer.IsMapped)");
        bvhRaycastSource.ShouldContain("GPU BVH raycast is currently OpenGL-only");
        bvhRaycastSource.ShouldContain("Reset(\"renderer backend does not support GPU BVH raycast fences/readback\")");

        bufferOperationsSource.ShouldContain("ThrowIfDeviceLostForResourceCreation");
        bufferSource.ShouldContain("SkippedDeviceLost");
        imageTextureSource.ShouldContain("if (!BackendContext.IsDeviceOperational || _image.Handle == 0)");
        oneTimeSubmitSource.ShouldContain("if (!DeviceContext.IsOperational)\n                throw new InvalidOperationException(\n                    $\"Cannot start a one-time command while device state is {DeviceContext.State}.\");");
        oneTimeSubmitSource.ShouldContain("if (submitFence.Handle != 0)");
        oneTimeSubmitSource.ShouldContain("submitResult != Result.ErrorDeviceLost");
        retirementSource.ShouldContain("Allocations.Images.Allocations.TryRemove(");
        retirementSource.ShouldContain("out trackedImageAllocation);");
        retirementSource.ShouldContain("if (canDestroyImage &&\n                hasTrackedImageAllocation &&\n                trackedImageAllocation.Memory.Handle != 0)");
    }

    [Test]
    public void VulkanFrameOpSort_UsesRenderGraphPassOrderBeforeDependencySafeOriginalOrder()
    {
        string schedulerSource = ReadWorkspaceFile(VulkanRoot + "RenderGraph/VulkanFrameOperationScheduler.cs");
        string planBuilderSource = ReadWorkspaceFile(VulkanRoot + "Commands/FrameOps/FramePlanBuilder.cs");

        int passOrderSort = schedulerSource.IndexOf("x.PassOrder.CompareTo(y.PassOrder)", StringComparison.Ordinal);
        int originalIndexTieBreak = schedulerSource.IndexOf("x.OriginalIndex.CompareTo(y.OriginalIndex)", StringComparison.Ordinal);

        passOrderSort.ShouldBeGreaterThanOrEqualTo(0);
        originalIndexTieBreak.ShouldBeGreaterThan(passOrderSort);
        schedulerSource.ShouldNotContain("x.GroupOrder.CompareTo(y.GroupOrder)");
        schedulerSource.ShouldNotContain(".OrderBy(x => x.GroupOrder)");

        // Sort keys use reusable scratch storage that grows only on demand.
        schedulerSource.ShouldContain("private FrameOpSortKey[] _sortKeyScratch = new FrameOpSortKey[256];");
        schedulerSource.ShouldContain("EnsureSortScratchCapacity(opCount);");
        schedulerSource.ShouldContain("Array.Resize(ref _sortKeyScratch, capacity);");
        schedulerSource.ShouldNotContain("new FrameOpSortKey[opCount]");

        schedulerSource.ShouldContain("1) the operation pipeline's topological pass order");
        schedulerSource.ShouldContain("3) original index for all dependency-carrying operations,");
        schedulerSource.ShouldContain("4) same-pass target clear-before-use normalization.");
        planBuilderSource.ShouldContain("_frameScheduler.SortLoweredOperations(slot.Operations, graph);");
        planBuilderSource.ShouldContain("_frameScheduler.SortLoweredOperations(slot.DynamicOverlayOperations, graph);");
    }

    [Test]
    public void VulkanFrameOpSort_UsesCompiledGraphBeforeContextMetadataFallback()
    {
        string schedulerSource = ReadWorkspaceFile(VulkanRoot + "RenderGraph/VulkanFrameOperationScheduler.cs");

        schedulerSource.ShouldContain("PassOrderCacheEntry");
        schedulerSource.ShouldContain("_passOrderCache.GetOrAdd(");
        schedulerSource.ShouldContain("RenderGraphSynchronizationPlanner.TopologicallySort(metadata)");

        // The operation's own pipeline metadata is authoritative. The published
        // compiled graph can belong to another planner context, so it is only
        // the fallback rank for passes that the metadata does not describe.
        string resolvePassOrder = Slice(
            schedulerSource,
            "private int ResolvePassOrder(",
            "private static bool TryResolveNestedScreenSpaceUiPassOrder(");
        int contextMetadata = resolvePassOrder.IndexOf("if (context.PassMetadata is { Count: > 0 } metadata)", StringComparison.Ordinal);
        int contextLookup = resolvePassOrder.IndexOf("cachedContextPassOrder.TryGetValue(header.PassIndex, out int contextOrder)", StringComparison.Ordinal);
        int graphFallback = resolvePassOrder.IndexOf("if (graph.Plan.Execution.TryGetPassOrder(header.PassIndex, out int graphOrder))", StringComparison.Ordinal);
        contextMetadata.ShouldBeGreaterThanOrEqualTo(0);
        contextLookup.ShouldBeGreaterThan(contextMetadata);
        graphFallback.ShouldBeGreaterThan(contextLookup);
        resolvePassOrder.ShouldContain("Never mix ranks from that partial graph with");
        resolvePassOrder.ShouldContain("return int.MaxValue;");
    }

    [Test]
    public void VulkanFboReentry_PreservesTrackedClearLoadsAfterFirstUse()
    {
        string frameBufferSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string renderScopeSource = ReadWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.RenderScopes.cs");
        string preparationSource = ReadWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs");

        frameBufferSource.ShouldContain("bool preserveTrackedClearLoads = false");
        frameBufferSource.ShouldContain("AttachmentLoadOp.Clear when preserveClearLoads => AttachmentLoadOp.Load");

        // Re-entry is tracked per target, pass, and scheduling context inside
        // one command buffer.
        renderScopeSource.ShouldContain("VulkanBegunFboPassKey begunPassKey = new(target, passIndex, context.SchedulingIdentity);");
        renderScopeSource.ShouldContain("bool targetReenteredThisCommandBuffer = recordingState.RecordingScratch.BegunFboPasses.Contains(begunPassKey);");
        renderScopeSource.ShouldContain("preserveTrackedClearLoads: targetReenteredThisCommandBuffer");
        renderScopeSource.ShouldContain("recordingState.RecordingScratch.BegunFboPasses.Add(begunPassKey);");
        preparationSource.ShouldContain("recordingState.RecordingScratch.BegunFboPasses.Clear();");
    }

    [Test]
    public void VulkanFrameOpSort_LiftsSameTargetClearBeforeFirstUse()
    {
        string schedulerSource = ReadWorkspaceFile(VulkanRoot + "RenderGraph/VulkanFrameOperationScheduler.cs");

        schedulerSource.ShouldContain("bool movedTargetClear = MoveTargetClearsBeforeFirstSameTargetUse(sortKeys, opCount);");
        schedulerSource.ShouldContain("if (IsClear(sortKey) &&\n                _earliestTargetUseScratch.TryGetValue(targetKey, out int earliestUseIndex) &&\n                index > earliestUseIndex)");
        schedulerSource.ShouldContain("SchedulingTargetKey targetKey = CreateSchedulingTargetKey(sortKey);");
        schedulerSource.ShouldContain("if (IsTargetUseThatClearMustPrecede(sortKey))");
        schedulerSource.ShouldContain("ReferenceEquals(_target, other._target)");
        schedulerSource.ShouldContain("=> sortKey.OpCode is EVulkanPrimaryPlanNodeKind.MeshDraw or\n            EVulkanPrimaryPlanNodeKind.Query or\n            EVulkanPrimaryPlanNodeKind.Blit or\n            EVulkanPrimaryPlanNodeKind.IndirectDraw or\n            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount or\n            EVulkanPrimaryPlanNodeKind.TransformFeedback;");
    }

    [Test]
    public void RuntimeRenderingCamera_FallsBackToPipelineFrameContextForDeferredVulkanWork()
    {
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");
        string engineStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");
        string meshRendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string programSource = SourceContractWorkspace.ReadPartialType(VulkanRoot + "BackendObjects/Programs/VkRenderProgram.cs");
        string materialSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.MeshMaterialOperations.cs");

        runtimeSource.ShouldContain("XRRenderPipelineInstance? pipeline = CurrentRenderingPipeline;");
        runtimeSource.ShouldContain("?? pipeline?.RenderState.RenderingCamera");
        runtimeSource.ShouldContain("?? pipeline?.RenderState.SceneCamera");
        runtimeSource.ShouldContain("?? pipeline?.LastSceneCamera");
        runtimeSource.ShouldContain("?? pipeline?.LastRenderingCamera");






        meshRendererSource.ShouldContain("RuntimeEngine.Rendering.State.RenderingCamera");
        programSource.ShouldContain("XRCamera? camera = RuntimeEngine.Rendering.State.RenderingCamera;");
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
    public void LightProbeCapture_UsesExplicitMinimalDirectFboPolicy()
    {
        string sceneCaptureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs");
        string lightProbeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs");

        sceneCaptureSource.ShouldContain("protected virtual RenderCapturePolicy CaptureRenderPolicy");
        sceneCaptureSource.ShouldContain("viewport.ApplyCapturePolicy(CaptureRenderPolicy);");
        lightProbeSource.ShouldContain("protected override RenderCapturePolicy CaptureRenderPolicy");
        lightProbeSource.ShouldContain("=> RenderCapturePolicy.LightProbe;");
    }

    [Test]
    public void VulkanDeviceLossDiagnostics_IncludeLastSubmissionContext()
    {
        string contextSource = ReadWorkspaceFile(VulkanRoot + "Frame/Diagnostics/VulkanRenderer.VulkanSubmissionDiagnosticContext.cs");
        string deviceLossSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.DeviceLoss.cs");
        string deviceContextSource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceContext.cs");
        string telemetrySource = ReadWorkspaceFile(VulkanRoot + "Frame/Telemetry/VulkanFrameTelemetry.cs");
        string submitSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs");

        contextSource.ShouldContain("internal readonly record struct VulkanSubmissionDiagnosticContext");
        contextSource.ShouldContain("public string? FrameOpKind { get; init; }");
        contextSource.ShouldContain("public string? OutputTargetName { get; init; }");
        contextSource.ShouldContain("public long CommandBufferDirtyGeneration { get; init; }");
        contextSource.ShouldContain("public ulong WaitTimelineValue { get; init; }");
        contextSource.ShouldContain("public ulong SignalTimelineValue { get; init; }");

        // The tracked submission gateway completes and records the context of
        // every submission, and keeps a ring of successful submissions.
        submitSource.ShouldContain("in VulkanSubmissionDiagnosticContext diagnosticContext,");
        submitSource.ShouldContain("VulkanSubmissionDiagnosticContext diagnostics =\n            BuildTrackedSubmissionDiagnostics(");
        submitSource.ShouldContain("DeviceContext.RecordSubmissionDiagnostics(diagnostics);");
        submitSource.ShouldContain("FrameTelemetry.RecordSuccessfulSubmissionBreadcrumb(in diagnostics);");
        deviceContextSource.ShouldContain("_lastSubmissionDiagnostics = diagnostics;");
        telemetrySource.ShouldContain("FrameOpKind = diagnostics.FrameOpKind,");
        telemetrySource.ShouldContain("OutputTargetName = diagnostics.OutputTargetName,");
        telemetrySource.ShouldContain("CommandBufferDirtyGeneration = diagnostics.CommandBufferDirtyGeneration,");
        telemetrySource.ShouldContain("WaitTimelineValue = diagnostics.WaitTimelineValue,");

        deviceLossSource.ShouldContain("lock (_telemetry._deviceLostTransitionLock)");
        deviceLossSource.ShouldContain("string deviceLostReason = BuildDeviceLostReason(reason);");
        deviceLossSource.ShouldContain("VulkanSubmissionDiagnosticContext submission = _deviceContext.SnapshotSubmissionDiagnostics();");
        deviceLossSource.ShouldContain("builder.Append(\"; LastSubmission kind=\")");
        deviceLossSource.ShouldContain("_deviceContext.SnapshotSubmissionDiagnostics(),");
        deviceLossSource.ShouldContain("Reason={_deviceContext.DeviceFaultFacility.DeviceLostReason ?? \"<unknown>\"}");
    }

    [Test]
    public void VulkanDeviceLossDiagnostics_TagSwapchainAndOpenXrSubmissions()
    {
        string submissionSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/VulkanRenderer.FrameLoop.Submission.cs");
        string presentationSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.PresentationServices.cs");
        string diagnosticsSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.SubmissionDiagnostics.cs");
        string openXrEyeSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.EyeRendering.cs");
        string openXrMirrorSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.MirrorPreview.cs");
        string openXrWorkerSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.EyeRecordWorkers.cs");
        string openXrSubmitSource = ReadWorkspaceFile(VulkanRoot + "OpenXR/VulkanCommandRuntime.OpenXrSubmission.cs");

        submissionSource.ShouldContain("CreateDesktopSubmissionDiagnosticContext(");
        submissionSource.ShouldContain("\"SwapchainDraw\"");
        submissionSource.ShouldContain("attempt.SceneCommandBufferDirtyGeneration,");
        submissionSource.ShouldContain("_commandBufferFrameOpSignatures");
        presentationSource.ShouldContain("private VulkanSubmissionDiagnosticContext CreateDesktopSubmissionDiagnosticContext(");

        diagnosticsSource.ShouldContain("internal VulkanSubmissionDiagnosticContext CreateOpenXrSubmissionDiagnosticContext(");
        openXrEyeSource.ShouldContain("_commandRuntime.CreateOpenXrSubmissionDiagnosticContext(");
        openXrEyeSource.ShouldContain("\"OpenXrEyeSubmit\"");
        openXrMirrorSource.ShouldContain("\"OpenXrEyeMirrorSubmit\"");
        openXrMirrorSource.ShouldContain("\"OpenXrEyeMirrorRenderPublishSubmit\"");
        openXrMirrorSource.ShouldContain("\"OpenXrStereoLayerRenderPublishSubmit\"");
        openXrWorkerSource.ShouldContain("\"OpenXrEyeParallelBatchSubmit\"");

        // OpenXR submissions pass their context through the tracked timeline
        // submission gateway.
        openXrSubmitSource.ShouldContain("VulkanSubmissionDiagnosticContext diagnosticContext =\n                input.DiagnosticContext;");
        openXrSubmitSource.ShouldContain("submitReceipt = SubmitToGraphicsTimelineTrackedWithDisposition(");
        openXrSubmitSource.ShouldContain("ref submitInfo,");
        openXrSubmitSource.ShouldContain("in diagnosticContext,");
    }

    [Test]
    public void VulkanPhase1Diagnostics_DefinePresetFlagsSettingsAndEnvironmentBridge()
    {
        string profileSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/VulkanFeatureProfile.cs");
        string backendSettingsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Settings/BackendRenderSettings.cs");
        string environmentSource = ReadWorkspaceFile("XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string runtimeEffectiveSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEffectiveSettings.cs");
        string hostServicesSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderSettingsServices.cs");
        string editorPreferencesSource = ReadWorkspaceFile("XREngine.Runtime.Host/Settings/EditorPreferences.cs");

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
        string resolverSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanDiagnosticOptions.cs");
        string instanceSource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceContext.Instance.cs");
        string validationSource = instanceSource;
        string validationDiagnosticsSource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanValidationDiagnostics.cs");
        string extensionsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.Instance.cs");
        string logicalDeviceSource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");

        resolverSource.ShouldContain("FlagsForPreset");
        resolverSource.ShouldContain("EVulkanDiagnosticPreset.CrashDiagnostics");
        resolverSource.ShouldContain("XREngineEnvironmentVariables.VulkanValidation");
        resolverSource.ShouldContain("ApplyBooleanEnvOverride");
        resolverSource.ShouldContain("BuildOverheadWarnings");

        validationSource.ShouldContain("PopulateEnabledValidationFeatures");
        validationSource.ShouldContain("ValidationFeatureEnableEXT.SynchronizationValidationExt");
        validationSource.ShouldContain("ValidationFeatureEnableEXT.GpuAssistedExt");
        validationSource.ShouldContain("ValidationFeatureEnableEXT.BestPracticesExt");
        validationDiagnosticsSource.ShouldContain("Record(severity, callbackData, message, submission);");
        validationDiagnosticsSource.ShouldContain("builder.Append(\"ValidationSummary count=\")");
        validationSource.ShouldContain("public string DescribeValidationSummary(int maxEntries = 6)");
        instanceSource.ShouldContain("ValidationFeaturesEXT");
        instanceSource.ShouldContain("LogResolvedDiagnosticOptions(validation, enableValidationLayers, extensions);");
        extensionsSource.ShouldContain("ExtDebugUtils.ExtensionName");

        logicalDeviceSource.ShouldContain("ExtDeviceFaultExtensionName");
        logicalDeviceSource.ShouldContain("KhrDeviceFaultExtensionName");
        logicalDeviceSource.ShouldContain("NvDeviceDiagnosticCheckpointsExtensionName");
        logicalDeviceSource.ShouldContain("PhysicalDeviceFaultFeaturesEXT");
        logicalDeviceSource.ShouldContain("VulkanKhrPhysicalDeviceFaultFeatures");
        logicalDeviceSource.ShouldContain("QueryKhrDeviceFaultCapabilities");
        logicalDeviceSource.ShouldContain("_deviceContext.TryLoadKhrDeviceFaultCommandTable(");
        logicalDeviceSource.ShouldContain("DeviceDiagnosticsConfigCreateInfoNV");
        logicalDeviceSource.ShouldContain("LogVulkanDiagnosticDeviceCapabilities");
        logicalDeviceSource.ShouldContain("Requested diagnostic device extension is unsupported");
    }

    [Test]
    public void VulkanPhase1Diagnostics_DeviceLossFooterIncludesBreadcrumbsFaultsAndNamedSyncObjects()
    {
        // Frame telemetry owns the breadcrumb ring. The frame loop builds the
        // device-loss footer. The device fault facility owns fault capture.
        string telemetrySource = ReadWorkspaceFile(VulkanRoot + "Frame/Telemetry/VulkanFrameTelemetry.cs");
        string breadcrumbSource = ReadWorkspaceFile(VulkanRoot + "Frame/Diagnostics/VulkanRenderer.VulkanCrashBreadcrumb.cs");
        string deviceLossSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.DeviceLoss.cs");
        string trackedSubmissionSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs");
        string faultReportingSource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceFaultFacility.Reporting.cs");
        string syncSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.Synchronization.cs");
        string swapchainSource = ReadWorkspaceFile(VulkanRoot + "Frame/Output/Authority/VulkanDesktopSwapchainService.cs");
        string openXrSource = ReadWorkspaceFile(VulkanRoot + "OpenXR/VulkanCommandRuntime.OpenXrSubmission.cs");
        string oneTimeSource = ReadWorkspaceFile(VulkanRoot + "Commands/VulkanRenderer.OneTimeSubmit.cs");

        telemetrySource.ShouldContain("VulkanCrashBreadcrumbCapacity");
        telemetrySource.ShouldContain("internal void RecordSuccessfulSubmissionBreadcrumb(");
        trackedSubmissionSource.ShouldContain("FrameTelemetry.RecordSuccessfulSubmissionBreadcrumb(in diagnostics);");
        telemetrySource.ShouldContain("internal string DescribeVulkanCrashBreadcrumbTail(int maxEntries)");
        breadcrumbSource.ShouldContain("LastCommandMarkerSerial");
        breadcrumbSource.ShouldContain("ImageLayoutTransitionSerial");
        breadcrumbSource.ShouldContain("DescriptorTableGeneration");
        breadcrumbSource.ShouldContain("FirstFailingApi");

        string footer = Slice(deviceLossSource, "private string BuildDeviceLostReason(string? reason)", "return builder.ToString();");
        footer.ShouldContain("_telemetry.DescribeVulkanCrashBreadcrumbTail(DeviceLossBreadcrumbTailEntries)");
        footer.ShouldContain("_deviceContext.DescribeValidationSummary()");
        footer.ShouldContain("AppendNvCheckpointDiagnostics(builder, in submission);");
        footer.ShouldContain("_deviceContext.TryAppendPersistedDeviceFaultSummary(");
        footer.ShouldContain("AppendDeviceAddressBindingDiagnostics(builder);");
        faultReportingSource.ShouldContain("GetDeviceFaultInfo");
        deviceLossSource.ShouldContain("checkpoints.GetQueueCheckpointData(queue, ref reportedCount, (CheckpointDataNV*)null);");

        syncSource.ShouldContain("\"Timeline.Graphics\"");
        syncSource.ShouldContain("\"Timeline.Present\"");
        syncSource.ShouldContain("\"Timeline.Transfer\"");
        syncSource.ShouldContain("$\"AcquireBridge[{index}]\"");
        swapchainSource.ShouldContain("$\"PresentBridge[{index}]\"");
        // OpenXR submit-and-wait signals the named graphics timeline. It does
        // not use a separate fence.
        openXrSource.ShouldContain("submitReceipt = SubmitToGraphicsTimelineTrackedWithDisposition(");
        openXrSource.ShouldContain("\"OpenXR.SubmitAndWait\"");
        oneTimeSource.ShouldContain("OneShot.TransferFence");
        oneTimeSource.ShouldContain("OneShot.GraphicsFence");
    }

    [Test]
    public void VulkanPhase1Diagnostics_CollectFaultArtifactsAddressBindingsAndCheckpoints()
    {
        string breadcrumbSource = ReadWorkspaceFile(VulkanRoot + "Frame/Diagnostics/VulkanRenderer.VulkanCrashBreadcrumb.cs");
        string faultReportingSource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceFaultFacility.Reporting.cs");
        string validationSource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanValidationDiagnostics.cs");
        string deviceLossSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.DeviceLoss.cs");
        string telemetrySource = ReadWorkspaceFile(VulkanRoot + "Frame/Telemetry/VulkanFrameTelemetry.cs");
        string nativeRecordingSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs");
        string recordingSource = ReadWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs");
        string descriptorHeapSource = ReadWorkspaceFile(VulkanRoot + "Resources/Authority/VulkanRenderer.DescriptorHeap.cs");
        string descriptorSource = ReadWorkspaceFile(VulkanRoot + "Descriptors/VulkanDescriptorManager.cs");
        string capabilitySource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceCapabilityReporter.cs");

        breadcrumbSource.ShouldContain("FirstFailingApi");
        faultReportingSource.ShouldContain("DeviceFaultInfoEXT");
        faultReportingSource.ShouldContain("PAddressInfos");
        faultReportingSource.ShouldContain("PVendorInfos");
        faultReportingSource.ShouldContain("PVendorBinaryData");
        faultReportingSource.ShouldContain("vulkan-device-fault-report.log");
        faultReportingSource.ShouldContain("vulkan-device-fault-vendor-");
        faultReportingSource.ShouldContain("Result.Incomplete");
        faultReportingSource.ShouldContain("vendorBinaryStatus");

        // Validation collects device-address binding events. The device-loss
        // footer drains them into a persisted artifact.
        validationSource.ShouldContain("DeviceAddressBindingCallbackDataEXT");
        validationSource.ShouldContain("RecordDeviceAddressBindings(callbackData)");
        validationSource.ShouldContain("DrainDeviceAddressBindings(");
        deviceLossSource.ShouldContain("_deviceContext.ValidationDiagnostics.DrainDeviceAddressBindings(");
        deviceLossSource.ShouldContain("artifact=vulkan-device-address-bindings.log");
        telemetrySource.ShouldContain("internal void RegisterDeviceAddressRange(Silk.NET.Vulkan.Buffer buffer, ulong baseAddress, ulong size, string label)");
        descriptorHeapSource.ShouldContain("FrameTelemetry.RegisterDeviceAddressRange(buffer, address, size, $\"DescriptorHeap.{name}\");");

        // NV diagnostic checkpoints mark each recorded operation and are read
        // back after device loss.
        nativeRecordingSource.ShouldContain("checkpoints.CmdSetCheckpoint(commandBuffer, markerToken);");
        telemetrySource.ShouldContain("VulkanNvCheckpointMarkerCapacity");
        deviceLossSource.ShouldContain("artifact=vulkan-nv-diagnostic-checkpoints.log");
        deviceLossSource.ShouldContain("checkpoints.GetQueueCheckpointData(queue, ref returnedCount, checkpointData);");
        recordingSource.ShouldContain("RecordVulkanCommandDiagnosticMarker(");
        descriptorSource.ShouldContain("RecordVulkanDescriptorTableGeneration");
        capabilitySource.ShouldContain("VendorCrashHooks");
    }

    [Test]
    public void VulkanPhase1Diagnostics_DebugNamesCoverPhase1ObjectTypes()
    {
        string validationSource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceContext.Instance.cs");
        string swapchainFramebufferSource = ReadWorkspaceFile(VulkanRoot + "Frame/Output/Authority/VulkanDesktopSwapchainService.RenderPasses.cs");
        string framebufferSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string rendererDescriptorSource = ReadWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/State/VulkanRenderer.CommandBufferState.cs");
        string materialTextureSource = ReadWorkspaceFile(VulkanRoot + "Resources/Authority/VulkanRenderer.BindlessMaterialTextureTable.cs");
        string imguiSource = ReadWorkspaceFile(VulkanRoot + "UI/VulkanImGuiFontAtlasResources.cs");
        string openXrSource = ReadWorkspaceFile(VulkanRoot + "OpenXR/VulkanOpenXrOutputResourceService.cs");
        string recordingSource = ReadWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Setup.cs");

        validationSource.ShouldContain("public void SetDebugDescriptorSetName(DescriptorSet descriptorSet, string name)");
        validationSource.ShouldContain("public void SetDebugDescriptorSetNames(DescriptorSet[]? descriptorSets, string prefix)");
        swapchainFramebufferSource.ShouldContain("_device.SetDebugObjectName(ObjectType.Framebuffer, framebuffer.Handle, framebufferName);");
        swapchainFramebufferSource.ShouldContain("Swapchain.Framebuffer[");
        framebufferSource.ShouldContain("ObjectType.Framebuffer,\n            frameBuffer.Handle,");
        framebufferSource.ShouldContain("$\"FBO.{Data?.Name ?? \"<unnamed>\"}\"");
        rendererDescriptorSource.ShouldContain("_deviceContext.SetDebugDescriptorSetNames(sets, \"RendererOwned.DescriptorSet\")");
        materialTextureSource.ShouldContain("DeviceContext.SetDebugDescriptorSetName(_globalMaterialTextureDescriptorSet, \"GlobalMaterialTexture.DescriptorSet\")");
        imguiSource.ShouldContain("DescriptorLifetime.SetDebugName(resources.FontDescriptorSet, \"ImGui.Font.DescriptorSet\")");
        openXrSource.ShouldContain("\"OpenXR.SwapchainImageView\"");
        recordingSource.ShouldContain("$\"FrameCmd[{recordingState.ImageIndex}]\"");
    }

    [Test]
    public void VulkanPhase11Diagnostics_KhrDeviceFaultShimIsWired()
    {
        // The native structures live in the diagnostics folder. The device
        // fault facility owns the command table, capture, and report files.
        string shimSource = ReadWorkspaceFiles(VulkanRoot + "Frame/Diagnostics/KhrDeviceFault", "*.cs") +
            ReadWorkspaceFiles(VulkanRoot + "Bootstrap/Device", "VulkanDeviceFaultFacility*.cs") +
            ReadWorkspaceFiles(VulkanRoot + "Bootstrap/Device", "VkGetDeviceFault*KhrDelegate.cs");
        string logicalDeviceSource = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs") +
            ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceCapabilityReporter.cs");
        string phase1Source = ReadWorkspaceFile(VulkanRoot + "Bootstrap/Device/VulkanDeviceContext.DeviceFaults.cs");

        shimSource.ShouldContain("PhysicalDeviceFeaturesSType = 1000573000");
        shimSource.ShouldContain("PhysicalDevicePropertiesSType = 1000573001");
        shimSource.ShouldContain("FaultInfoSType = 1000573002");
        shimSource.ShouldContain("FaultDebugInfoSType = 1000573003");
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

        // KHR is preferred when its command table is loaded. EXT is the
        // compatibility path.
        int khrPath = phase1Source.IndexOf("if (DeviceFaultFacility.IsUsingKhrDeviceFault)", StringComparison.Ordinal);
        int khrSummary = phase1Source.IndexOf("return TryAppendPersistedKhrDeviceFaultSummary(", khrPath, StringComparison.Ordinal);
        int extPath = phase1Source.IndexOf("if (!DeviceFaultFacility.SupportsExtDeviceFault || extension is null)", khrPath, StringComparison.Ordinal);
        khrPath.ShouldBeGreaterThanOrEqualTo(0);
        khrSummary.ShouldBeGreaterThan(khrPath);
        extPath.ShouldBeGreaterThan(khrSummary);
        phase1Source.ShouldContain("return TryAppendPersistedExtDeviceFaultSummary(");
    }

    [Test]
    public void VulkanPhase2_FrameOpContextContractIncludesIsolationFingerprint()
    {
        // The context kind, the frame-op context record, and the planner
        // context completion now live in separate files.
        string plannerSource =
            ReadWorkspaceFile(VulkanRoot + "RenderGraph/EVulkanFrameOpContextKind.cs") +
            ReadWorkspaceFile(VulkanRoot + "RenderGraph/FrameOpContext.cs") +
            ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.ResourcePlannerContext.cs") +
            ReadWorkspaceFile(VulkanRoot + "RenderGraph/Authority/VulkanFramePlanner.ContextSignatures.cs");
        string stateSource =
            ReadWorkspaceFile(VulkanRoot + "RenderGraph/VulkanFrameOpPlannerStateKey.cs") +
            ReadWorkspaceFile(VulkanRoot + "RenderGraph/Authority/ResourcePlannerSignatureBreakdown.cs");
        string meshSource = ReadWorkspaceFile(VulkanRoot + "Commands/FrameOps/VulkanFrameOperationSemantics.cs");
        string physicalPlanSource = ReadWorkspaceFile(VulkanRoot + "RenderGraph/VulkanRenderer.PhysicalResourcePlanning.cs");

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
        stateSource.ShouldContain("kind={ContextKind} contextId={ContextId} plan=0x{CompatibilityFingerprint:X16}");

        // The frame-op stream signature isolates operations by context kind,
        // recording fingerprint, and output framebuffer.
        string frameOpsSignature = Slice(meshSource, "internal static ulong ComputeFrameOpsSignature(FrameOperationSequence ops)", "switch (header.OpCode)");
        frameOpsSignature.ShouldContain("hash.Add((int)context.ContextKind);");
        frameOpsSignature.ShouldContain("hash.Add(context.RecordingFingerprint);");
        frameOpsSignature.ShouldContain("hash.Add(context.OutputFrameBufferIdentity);");

        // A graph change that keeps the allocation signature (a metadata-only
        // change) keeps the active physical resource plan.
        physicalPlanSource.ShouldContain("bool allocationChanged = request.AllocationSignature != state.ResourceAllocationSignature;");
        string commitPlan = Slice(physicalPlanSource, "private void CommitPhysicalAllocatorPlan(", "if (retiredImageCount > 0 || retiredBufferCount > 0)");
        commitPlan.ShouldContain("if (!physicalPlanChanged)\n            return;");
    }

    [Test]
    public void VulkanPhase2_CommandBufferReuseRejectsFrameOpContextMismatch()
    {
        // Each primary command artifact owner starts with no recorded context.
        // The shared primary recording path records the context fingerprint,
        // and every reuse checks it before submit. OpenXR eye and mirror
        // recording use the same shared path.
        string stateSource =
            ReadWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Reuse/VulkanRenderer.PrimaryCommandArtifactOwner.cs");
        string recordingSource =
            ReadWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Reuse/Dependencies/VulkanRenderer.CommandBufferReuse.Dependencies.cs") +
            ReadWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/FrameData/VulkanRenderer.CommandBufferReuse.FrameData.cs");
        string primaryRecordingSource =
            ReadWorkspaceFile(VulkanRoot + "Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs");
        string openXrSource =
            ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.cs") +
            ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.OpenXR.MirrorPreview.cs");
        string openXrRecordingSource =
            ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanOpenXrCommandRecordingService.cs");
        string openXrScopeSource = ReadWorkspaceFile(VulkanRoot + "OpenXR/OpenXrExternalSwapchainRenderScope.cs");

        stateSource.ShouldContain("public ulong RecordedFrameOpContextFingerprint { get; set; } = ulong.MaxValue;");
        stateSource.ShouldContain("public ulong RecordedFrameOpContextId { get; set; }");
        recordingSource.ShouldContain("ComputeCommandBufferFrameOpContextFingerprint");
        recordingSource.ShouldContain("TryValidateCommandBufferVariantContext");
        recordingSource.ShouldContain("EnsureCommandBufferVariantContextBeforeSubmit");
        recordingSource.ShouldContain("Vulkan command buffer frame-op context mismatch before submit");
        recordingSource.ShouldContain("LogCommandBufferFrameOpContextMismatch");
        recordingSource.ShouldContain("frame-op context mismatch");
        recordingSource.ShouldContain("ShouldFailFastOnFrameOpContextMismatch");
        recordingSource.ShouldContain("variant.RecordedFrameOpContextFingerprint = frameOpContextFingerprint");
        recordingSource.ShouldContain(
            "variant.RecordedFrameOpContextFingerprint !=\n" +
            "                    frameOpContextFingerprint)");
        recordingSource.ShouldContain("\"command-chain-primary\"");

        primaryRecordingSource.ShouldContain(
            "ulong frameOpContextFingerprint =\n" +
            "                ComputeCommandBufferFrameOpContextFingerprint(");
        primaryRecordingSource.ShouldContain("owner.RecordedFrameOpContextFingerprint = contextHash.ToHash();");
        primaryRecordingSource.ShouldContain("contextHash.Add((int)operationContext.ContextKind);");

        openXrSource.ShouldContain("EVulkanFrameOpContextKind contextKind = EVulkanFrameOpContextKind.OpenXrEye,");
        openXrSource.ShouldContain("EVulkanFrameOpContextKind.OpenXrMirror");
        openXrSource.ShouldContain("hash = (hash * 397) ^ 0x53494E54");
        openXrSource.ShouldContain("HashCode.Combine(\"OpenXR\", viewIndex, imageIndex)");
        openXrRecordingSource.ShouldContain("commandRuntime.RecordPrimary(in commandInput);");
        openXrScopeSource.ShouldContain("_previousFrameContext = _threadState.FrameContext;");
        openXrScopeSource.ShouldContain("_threadState.FrameContext = frameContext;");
        openXrScopeSource.ShouldContain("_threadState.FrameContext = _previousFrameContext;");
    }

    [Test]
    public void FrameOps_CaptureContextBeforePassValidation()
    {
        // Each frame-op producer captures the frame-op context first. Pass
        // validation then uses the pass metadata of that captured context.
        string meshSource = ReadWorkspaceFile(VulkanRoot + "BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string meshQueueSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs");
        string blitSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.BlitApi.cs");
        string clearSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.RendererApi.cs");
        string legacyCommandSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.LegacyCommandApi.cs");
        string indirectSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.IndirectDrawProducer.cs");
        string featureSource = ReadWorkspaceFile(VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.FeatureOperations.cs");
        string meshletSource = ReadWorkspaceFile(VulkanRoot + "Commands/Authority/VulkanCommandRuntime.OrderedComputeApi.cs");

        meshSource.ShouldContain("FrameOpContext context = _programPlanner?.CaptureFrameOpContext() ?? default;");
        meshQueueSource.ShouldContain(
            "int passIndex = VulkanCommandRuntime.EnsureValidPassIndex(\n" +
            "            request.PassIndex,\n" +
            "            nameof(MeshDrawOp),\n" +
            "            request.Context.PassMetadata);");

        string blitSlice = Slice(blitSource, "FrameOpContext context = CaptureFrameOpContextForCurrentPipelineScope();", "EnqueueFrameOp(operation);");
        blitSlice.ShouldContain("VulkanCommandRuntime.EnsureValidPassIndex(passIndex, \"Blit\", context.PassMetadata)");

        string clearSlice = Slice(clearSource, "internal void Clear(bool color, bool depth, bool stencil)", "_commandRuntime.ActiveState.GetClearStencilValue(), rect, context));");
        int clearCapture = clearSlice.IndexOf("FrameOpContext context = CaptureFrameOpContextForCurrentPipelineScope();", StringComparison.Ordinal);
        int clearValidation = clearSlice.IndexOf("EnsureValidPassIndex(RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, \"Clear\", context.PassMetadata)", StringComparison.Ordinal);
        clearCapture.ShouldBeGreaterThanOrEqualTo(0);
        clearValidation.ShouldBeGreaterThan(clearCapture);

        legacyCommandSource.ShouldContain("RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextForCurrentPipelineScope());");
        indirectSource.ShouldContain(
            "int passIndex = EnsureValidPassIndex(\n" +
            "            currentPassIndex,\n" +
            "            useCount ? \"IndirectCountDraw\" : \"IndirectDraw\",\n" +
            "            context.PassMetadata);");

        featureSource.ShouldContain("RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextForCurrentPipelineScope(), AllowSynchronousResourceUploads, out failureReason);");
        meshletSource.ShouldContain(
            "int passIndex = ResolveOrderedPrimaryWorkPassIndex(\n" +
            "            \"MeshTaskDispatchIndirectCount\",\n" +
            "            currentPassIndex,\n" +
            "            context.PassMetadata);");
        meshletSource.ShouldContain("return EnsureValidPassIndex(passIndex, operationName, passMetadata);");
    }

    private const string VulkanRoot = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    /// <summary>Returns the source text from the start marker up to the end marker.</summary>
    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Missing start marker '{startMarker}'.");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Missing end marker '{endMarker}'.");
        return source[start..end];
    }

    private static string ReadWorkspaceFiles(string relativeDirectory, string searchPattern)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(path).ShouldBeTrue($"Expected workspace directory '{path}' to exist.");
        string[] files = Directory.GetFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
        files.Length.ShouldBeGreaterThan(0, $"Expected workspace files matching '{searchPattern}' under '{path}'.");
        return string.Join(Environment.NewLine, Array.ConvertAll(files, File.ReadAllText));
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
