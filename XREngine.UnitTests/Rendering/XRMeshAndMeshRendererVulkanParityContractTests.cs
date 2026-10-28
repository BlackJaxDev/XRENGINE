using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class XRMeshAndMeshRendererVulkanParityContractTests
{
    [Test]
    public void XRMeshParity_DoesNotIntroduceStandaloneBackendMeshWrappers()
    {
        string repoRoot = ResolveWorkspaceRoot();
        string[] runtimeFiles =
        [
            .. Directory.GetFiles(
                Path.Combine(repoRoot, "XREngine.Runtime.Rendering"),
                "*.cs",
                SearchOption.AllDirectories),
            .. Directory.GetFiles(
                Path.Combine(repoRoot, "XREngine.Runtime.Rendering.OpenGL"),
                "*.cs",
                SearchOption.AllDirectories),
            .. Directory.GetFiles(
                Path.Combine(repoRoot, "XREngine.Runtime.Rendering.Vulkan"),
                "*.cs",
                SearchOption.AllDirectories),
        ];

        runtimeFiles.Any(path => string.Equals(Path.GetFileName(path), "VkMesh.cs", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
        runtimeFiles.Any(path => string.Equals(Path.GetFileName(path), "GLMesh.cs", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Test]
    public void VkMeshRenderer_MeshReplacementUnsubscribesOldMeshAndBufferEvents()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");

        source.ShouldContain("MeshRenderer.PropertyChanging += OnMeshRendererPropertyChanging;");
        source.ShouldContain("MeshRenderer.PropertyChanging -= OnMeshRendererPropertyChanging;");
        source.ShouldContain("private void OnMeshRendererPropertyChanging");
        source.ShouldContain("e.CurrentValue is XRMesh currentMesh");
        source.ShouldContain("currentMesh.DataChanged -= OnMeshChanged;");
        source.ShouldContain("SubscribeMeshBufferCollection(null);");
        source.ShouldContain("SubscribeRendererBuffers(MeshRenderer.Buffers);");
        source.ShouldContain("SubscribeMeshBufferCollection(MeshRenderer.Mesh?.Buffers);");
        source.ShouldContain("InvalidateGeometryLayout(\"MeshChanged\", collectBuffers: true);");
        source.ShouldNotContain("if (Mesh is not null)\r\n                    {\r\n                        Mesh.DataChanged -= OnMeshChanged;");
    }

    [Test]
    public void VkMeshRenderer_UsesSharedOpenGlMaterialResolutionSemantics()
    {
        string resolverSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/MeshRenderMaterialResolver.cs");
        string vkBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs");
        string glRenderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Rendering.cs");

        resolverSource.ShouldContain("GlobalOverride");
        resolverSource.ShouldContain("PipelineOverride");
        resolverSource.ShouldContain("LocalOverride");
        resolverSource.ShouldContain("InvalidMaterial");
        resolverSource.ShouldContain("DirectionalCascadeShadowMaterialKind");
        resolverSource.ShouldContain("ResolveDirectionalCascadeShadowMaterial");
        resolverSource.ShouldContain("PointShadowMaterialKind");
        resolverSource.ShouldContain("ResolvePointLightShadowMaterial");
        resolverSource.ShouldContain("ShadowUniformSourceMaterial");
        resolverSource.ShouldContain("UseDepthNormalMaterialVariants");
        resolverSource.ShouldContain("DepthNormalPrePassVariant");
        // The shared Uber shadow fallback is an OpenGL linker workaround. It
        // stays in the OpenGL renderer. The shared resolver keeps the exact
        // shadow caster variants that Vulkan uses.
        resolverSource.ShouldNotContain("CanUseSharedUberShadowFallback");
        resolverSource.ShouldContain("GetDirectionalCascadeShadowCasterVariant(");
        resolverSource.ShouldContain("GetPointShadowCasterVariant(GetPointLightGeometryFallbackKind(overrideKind))");
        glRenderSource.ShouldContain("private static bool CanUseSharedUberShadowFallback(XRMaterial globalMaterialOverride, XRMaterial? shadowSourceMaterial)");

        vkBufferSource.ShouldContain("MeshRenderMaterialResolver.Resolve(");
        vkBufferSource.ShouldNotContain("shadowSourceMaterial?.ShadowCasterVariant");
    }

    [Test]
    public void VkMeshRenderer_ShadowDrawsSuppressLinePointAndUploadLayeredUniforms()
    {
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string resolverSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/MeshRenderMaterialResolver.cs");
        string enqueueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");

        Regex.Replace(enqueueSource, @"\s+", "").ShouldContain(
            "MeshRenderMaterialResolver.ResolveLayeredShadowInstanceCount(resolvedMaterial.Material,instances,shadowUniformState,shadowCasterRelevance)");
        drawingSource.ShouldContain("bool skipLinePointDraws = MeshRenderMaterialResolver.RequiresTriangleOnlyDrawsForCurrentPass();");
        drawingSource.ShouldContain("Suppressed line/point index draws for shadow geometry pass");
        drawingSource.ShouldContain("Suppressed non-indexed {0} fallback for shadow geometry pass");
        Regex.Replace(drawingSource, @"\s+", "").ShouldContain(
            "MeshRenderMaterialResolver.ApplyShadowUniforms(programData,material,draw.ShadowUniformState,draw.ShadowCasterRelevance);");

        resolverSource.ShouldContain("CascadeLayerCount");
        resolverSource.ShouldContain("CascadeViewProjectionMatrices");
        resolverSource.ShouldContain("PointShadowFaceCount");
        resolverSource.ShouldContain("PointShadowViewProjectionMatrices");
        resolverSource.ShouldContain("OnSettingShadowUniforms(program)");
    }

    [Test]
    public void VkMeshRenderer_ImplementsExplicitPreparationGateSeparateFromGeneration()
    {
        string mainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string prepSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        mainSource.ShouldContain("IRenderPreparationState");
        mainSource.ShouldContain("public override bool IsGenerated => IsActive;");
        mainSource.ShouldContain("prepared = TryPrepareForDrawEnqueue(");
        mainSource.ShouldContain("Skipping mesh draw enqueue");

        prepSource.ShouldContain("public bool IsPreparedForRendering");
        prepSource.ShouldContain("private bool TryPrepareForDrawEnqueue(");
        prepSource.ShouldContain("return SetPrepareResult(false, \"MaterialMissing\"");
        prepSource.ShouldContain("public string LastPrepareDetail => _lastPrepareDetail;");
        prepSource.ShouldContain("BuffersPending");
        prepSource.ShouldContain("ProgramsPending");
        prepSource.ShouldContain("DescriptorsPending");
        prepSource.ShouldContain("pipeline=DeferredUntilPass");
        prepSource.ShouldContain("AreCachedBuffersReadyForRendering");

        drawingSource.ShouldContain("TryPrepareForRendering(material, out string prepareReason)");
    }

    [Test]
    public void MeshGeometryLayoutSignature_FeedsBothBackendsAndVulkanPipelineKeys()
    {
        string signatureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/MeshGeometryLayoutSignature.cs");
        string signatureBuilderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/MeshGeometryLayoutSignatureBuilder.cs");
        string vkPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string vkDrawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string glBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Buffers.cs");
        string glShaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Shaders.cs");

        signatureSource.ShouldContain("MeshGeometryLayoutSignature");
        signatureBuilderSource.ShouldContain("InterleavedAttribute");
        signatureBuilderSource.ShouldContain("InstanceDivisor");
        signatureSource.ShouldContain("HasRuntimeDeformationBuffers");
        signatureSource.ShouldContain("HasMeshletPayload");
        signatureSource.ShouldContain("DrawCountSource");

        vkPipelineSource.ShouldContain("MeshGeometryLayoutSignatureBuilder.Create");
        vkPipelineSource.ShouldContain("descriptorLayoutHash");
        vkPipelineSource.ShouldNotContain("materialLayoutHash");
        vkPipelineSource.ShouldNotContain("MaterialLayoutHash");
        vkPipelineSource.ShouldContain("passMetadataHash");
        vkPipelineSource.ShouldContain("featureProfileHash");
        vkDrawingSource.ShouldContain("_geometryLayoutSignature.StableHash");

        glBufferSource.ShouldContain("CaptureGeometryLayoutSignature");
        glBufferSource.ShouldContain("layout={_geometryLayoutSignature.DebugSummary}");
        glShaderSource.ShouldContain("pipelineStateKey=");
        glShaderSource.ShouldContain("ComputeOpenGLPipelineStateKey");
    }

    [Test]
    public void VulkanGraphicsPipelines_AreSharedAcrossEquivalentMeshRenderers()
    {
        string pipelineKeySource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Pipelines/VkMeshRenderer.PipelineKey.cs");
        string compileKeySource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Pipelines/VkMeshRenderer.GraphicsPipelineCompileKey.cs");
        string meshPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string managerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineManager.cs");
        string queueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");
        string lifecycleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs");

        pipelineKeySource.ShouldContain("ulong DescriptorLayoutHash");
        pipelineKeySource.ShouldContain("ulong PipelineLayoutHandle");
        pipelineKeySource.ShouldNotContain("MaterialLayoutHash");

        compileKeySource.ShouldContain("PipelineKey Pipeline");
        compileKeySource.ShouldNotContain("OwnerIdentity");

        meshPipelineSource.ShouldContain("BackendContext.Resources.PipelineManager.TryGetSharedGraphicsPipeline(key, out pipeline)");
        meshPipelineSource.ShouldContain("_program.PipelineLayout.Handle");
        meshPipelineSource.ShouldContain("BackendContext.Resources.PipelineManager.StoreOrRetireSharedGraphicsPipeline(key, asyncResult.Pipeline)");
        meshPipelineSource.ShouldContain("BackendContext.Resources.PipelineManager.StoreOrRetireSharedGraphicsPipeline(key, foregroundResult.Pipeline)");
        meshPipelineSource.ShouldContain("Final graphics pipeline handles are owned by the renderer-level shared cache");
        meshPipelineSource.ShouldNotContain("Renderer.RetirePipeline(pipe)");

        // The renderer-level shared cache now lives in the pipeline manager.
        managerSource.ShouldContain("private readonly Dictionary<VulkanGraphicsPipelineKey, Pipeline> _sharedGraphicsPipelines");
        managerSource.ShouldContain("internal bool TryGetSharedGraphicsPipeline");
        managerSource.ShouldContain("internal Pipeline StoreSharedGraphicsPipeline");
        managerSource.ShouldContain("internal Pipeline StoreOrRetireSharedGraphicsPipeline");
        managerSource.ShouldContain("RequireProgramServices().RetirePipeline(pipeline);");
        managerSource.ShouldContain("internal int DestroySharedGraphicsPipelines()");
        managerSource.ShouldContain("services.DestroyPipelineImmediate(pipeline);");
        managerSource.ShouldContain("services.DestroyPipelineImmediate(superseded);");
        queueSource.ShouldContain("StoreOrRetireSharedGraphicsPipeline(job.Request.Key, result.Pipeline)");
        lifecycleSource.ShouldContain("_resourceRuntime.PipelineManager.DestroySharedGraphicsPipelines()");
    }

    [Test]
    public void MeshSubmissionDiagnosticsExposeRequestedSelectedFallbackAndCapabilityState()
    {
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");
        string sharedPassSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/MeshRendering/Shared/VPRC_RenderMeshesPassShared.cs");
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs");

        runtimeSource.ShouldContain("ResolveMeshSubmissionStrategy");
        runtimeSource.ShouldContain("LastMeshletDowngradeRequested");
        runtimeSource.ShouldContain("LastMeshletDowngradeResolved");
        runtimeSource.ShouldContain("LastMeshletDowngradeReason");
        runtimeSource.ShouldContain("LastResolvedRendererBackend");
        runtimeSource.ShouldContain("LastResolvedMeshShaderDialect");
        runtimeSource.ShouldContain("LastResolvedSupportsMeshletDispatch");
        runtimeSource.ShouldContain("Mesh submission strategy downgraded");

        sharedPassSource.ShouldContain("Requested={MeshSubmissionStrategy};Selected={selectedStrategy}");
        sharedPassSource.ShouldContain("FallbackReason={fallbackReason}");
        sharedPassSource.ShouldContain("SupportsMeshletDispatch()");
        sharedPassSource.ShouldContain("MeshletDispatchUnsupportedReason");
        sharedPassSource.ShouldContain("RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy(true)");
        sharedPassSource.ShouldContain("VPRC_RenderMeshesPassMeshlet.Execute(this, meshSubmissionStrategy)");

        hybridSource.ShouldContain("AssertZeroReadbackProductionInvariants");
        hybridSource.ShouldContain("AssertZeroReadbackUsesGpuCountPath");
        hybridSource.ShouldContain("RecordGpuMeshletStrategyRequested");
        hybridSource.ShouldContain("WarnMeshletMaterialFallback");
        hybridSource.ShouldContain("RecordForbiddenGpuFallback");

        rendererSource.ShouldContain("public virtual bool SupportsMeshletDispatch()");
        rendererSource.ShouldContain("public virtual string MeshletDispatchUnsupportedReason");
    }

    [Test]
    public void MeshGeometryLayoutFeedsGpuSceneIndirectAndMeshletRecords()
    {
        string gpuSceneCommandBuffersSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandBuffers.cs");
        string gpuSceneSoaSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Soa.cs");
        string gpuSceneAddRemoveSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AddRemove.cs");
        string gpuSceneMeshletGenerationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.MeshletBufferGenerations.cs");
        string gpuMeshletResourcesSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURendering/Resources/GPUMeshletResources.cs");
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        gpuSceneCommandBuffersSource.ShouldContain("MakeDrawMetadataBuffer(\"DrawMetadataBuffer\")");
        gpuSceneCommandBuffersSource.ShouldContain("MakeMeshletRangeBuffer()");
        gpuSceneCommandBuffersSource.ShouldContain("MeshletRangeBuffer/MeshletDescriptorBuffer/MeshletVertexIndexBuffer/MeshletTriangleIndexBuffer");
        gpuSceneCommandBuffersSource.ShouldContain("public XRDataBuffer DrawMetadataBuffer");
        gpuSceneCommandBuffersSource.ShouldContain("public XRDataBuffer MeshletRangeBuffer");

        gpuSceneSoaSource.ShouldContain("private void WriteDrawMetadata(uint drawId, in DrawMetadata metadata)");
        gpuSceneSoaSource.ShouldContain("published.DrawID = drawId;");
        gpuSceneSoaSource.ShouldContain("UpdatingDrawMetadataBuffer.SetDataRawAtIndex(drawId, published);");
        gpuSceneSoaSource.ShouldContain("_drawMetadataDirtyRange.Mark(drawId);");

        gpuSceneAddRemoveSource.ShouldContain("WriteDrawMetadata(index, commandValue);");
        gpuSceneAddRemoveSource.ShouldContain("EnsureMeshletRangeForMesh");
        gpuSceneAddRemoveSource.ShouldContain("MeshletPayload");
        // Registration stages meshlet records on the CPU. The frame boundary publishes them as one new buffer generation.
        gpuSceneAddRemoveSource.ShouldContain("_meshletDescriptors.Add(descriptor);");
        gpuSceneAddRemoveSource.ShouldContain("_meshletVertexIndices.AddRange(payload.VertexIndices);");
        gpuSceneAddRemoveSource.ShouldContain("_meshletTriangleIndices.AddRange(payload.TriangleIndices);");
        gpuSceneAddRemoveSource.ShouldContain("_meshletBufferGenerationDirty = true;");
        gpuSceneAddRemoveSource.ShouldContain("FlushMeshletRangeDirtyRange");
        gpuSceneCommandBuffersSource.ShouldContain("PublishMeshletBufferGenerationAtFrameBoundary();");
        gpuSceneMeshletGenerationSource.ShouldContain("XRDataBuffer descriptorBuffer = CreateMeshletDescriptorBufferSnapshot(descriptors);");
        gpuSceneMeshletGenerationSource.ShouldContain("XRDataBuffer vertexIndexBuffer = CreateMeshletVertexIndexBufferSnapshot(vertexIndices);");
        gpuSceneMeshletGenerationSource.ShouldContain("XRDataBuffer triangleIndexBuffer = CreateMeshletTriangleIndexBufferSnapshot(triangleIndices);");
        gpuSceneMeshletGenerationSource.ShouldContain("buffer.SetDataRawAtIndex((uint)index, descriptors[index]);");
        gpuSceneMeshletGenerationSource.ShouldContain("buffer.SetDataRawAtIndex((uint)index, indices[index]);");
        gpuSceneMeshletGenerationSource.ShouldContain("_retiredMeshletBufferGenerations.Enqueue(new RetiredMeshletBufferGeneration(");

        gpuMeshletResourcesSource.ShouldContain("public struct GpuMeshletTaskRecord");
        gpuMeshletResourcesSource.ShouldContain("MeshletTaskRecordStride");
        gpuMeshletResourcesSource.ShouldContain("public XRDataBuffer DrawMetadataBuffer { get; }");
        gpuMeshletResourcesSource.ShouldContain("public XRDataBuffer MeshletRangeBuffer { get; }");

        hybridSource.ShouldContain("DrawMetadataSsboBinding");
        hybridSource.ShouldContain("MeshletTaskRecordSsboBinding");
        hybridSource.ShouldContain("SceneDatabaseDrawMetadataAddressUniform");
        hybridSource.ShouldContain("TryBindSceneDatabaseDeviceAddressUniforms");
    }

    [Test]
    public void VkMeshRenderer_BufferCollectionMatchesOpenGlRuntimeDeformationRules()
    {
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        bufferSource.ShouldContain("RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader");
        bufferSource.ShouldContain("RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || useComputeSkinning");
        bufferSource.ShouldContain("FilterRuntimeDeformationSourceBuffers");
        bufferSource.ShouldContain("RemoveCollectedBuffer(ECommonBufferType.BoneInfluenceCoreIndices.ToString())");
        bufferSource.ShouldContain("RemoveCollectedBuffer($\"{ECommonBufferType.BoneMatrices}Buffer\")");
        bufferSource.ShouldContain("RemoveCollectedBuffer($\"{ECommonBufferType.SkinPalette}Buffer\")");
        bufferSource.ShouldContain("RemoveCollectedBuffer($\"{ECommonBufferType.BlendshapeWeights}Buffer\")");
        bufferSource.ShouldContain("RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass");
        bufferSource.ShouldContain("blendshapeState.IsPrecombinedValidFor(mesh)");
        bufferSource.ShouldContain("AddMeshDeformSourceBuffers");
        bufferSource.ShouldContain("\"DeformerPositionsBuffer\"");
        bufferSource.ShouldContain("assignBindingOverride: false");
        // An async index build publishes one atomic readiness edge. The next
        // buffer refresh consumes it while it holds the buffer-state lock.
        bufferSource.ShouldContain("Interlocked.Exchange(ref _pendingAsyncIndexBufferReady, 1);");
        bufferSource.ShouldContain("if (Interlocked.Exchange(ref _pendingAsyncIndexBufferReady, 0) != 0)");
        bufferSource.ShouldContain("ApplyIndexBufferReadyNoLock();");
        bufferSource.ShouldContain("GetIndexBufferForBinding(EPrimitiveType.Triangles");
        bufferSource.ShouldContain("GetIndexBufferForBinding(EPrimitiveType.Lines");
        bufferSource.ShouldContain("GetIndexBufferForBinding(EPrimitiveType.Points");
        bufferSource.ShouldContain(
            "return mesh.GetIndexBuffer(\n" +
            "\t\t\ttype,\n" +
            "\t\t\tout elementSize,\n" +
            "\t\t\tEBufferTarget.ElementArrayBuffer,\n" +
            "\t\t\tonReady,\n" +
            "\t\t\trequireSynchronous);");

        string drawingSourceWithIndexTypes = drawingSource;
        string cleanupSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Cleanup.cs");
        drawingSourceWithIndexTypes.ShouldContain("size == IndexSize.Byte && !BackendContext.Supports(EVulkanDeviceCapability.IndexTypeUint8)");
        cleanupSource.ShouldContain("IndexSize.Byte => IndexType.Uint8Ext");
        cleanupSource.ShouldContain("IndexSize.TwoBytes => IndexType.Uint16");
        cleanupSource.ShouldContain("IndexSize.FourBytes => IndexType.Uint32");

        pipelineSource.ShouldContain("pair.Value.Data.Target == EBufferTarget.ArrayBuffer");
        pipelineSource.ShouldContain("AllocateNextVertexBinding");
        pipelineSource.ShouldContain("WarnMissingVertexAttribute");
        pipelineSource.ShouldContain("buffer.Data.Normalize");
        pipelineSource.ShouldContain("buffer.Data.InstanceDivisor");

        drawingSource.ShouldContain("_singleVertexBindingBuffer");
        drawingSource.ShouldNotContain(".OrderBy(b => b.Binding)");
    }

    [Test]
    public void VkMeshRenderer_BindsAndTracksTheActiveGpuDrivenSkinPalette()
    {
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs");

        bufferSource.ShouldContain("OverrideCollectedSkinPaletteWithActiveSource();");
        bufferSource.ShouldContain("MeshRenderer.ActiveSkinPaletteBuffer is not { } activeSkinPalette");
        bufferSource.ShouldContain("_bufferCache[shaderName] = ProgramCreationPort.GetOrCreateBuffer(activeSkinPalette, generateNow: false);");
        bufferSource.ShouldContain("_cachedActiveSkinPaletteBuffer = MeshRenderer.ActiveSkinPaletteBuffer;");
        bufferSource.ShouldContain("XRDataBuffer? activeSkinPalette = MeshRenderer.ActiveSkinPaletteBuffer;");
        bufferSource.ShouldContain("!ReferenceEquals(_cachedActiveSkinPaletteBuffer, activeSkinPalette)");
        bufferSource.ShouldContain("_cachedActiveSkinPaletteIdentity != CaptureBufferStructuralIdentity(activeSkinPalette)");
    }

    [Test]
    public void VkMeshRenderer_SharedBufferStateIsSerializedAcrossDesktopAndOpenXrViews()
    {
        string mainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string preparationSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");
        string snapshotSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/Classes/VkMeshRenderer.BufferReadinessSnapshot.cs");

        mainSource.ShouldContain("private readonly object _bufferStateSync = new();");
        AssertMethodLocksBefore(mainSource, "private void InvalidateGeometryLayout", "_triangleIndexBuffer = null;");
        AssertMethodLocksBefore(bufferSource, "private void CollectBuffers", "_bufferCache.Clear();");
        AssertMethodLocksBefore(bufferSource, "private void EnsureBuffers", "foreach (var buffer in _bufferCache.Values)");
        bufferSource.ShouldContain("PublishBufferReadinessSnapshot();");
        preparationSource.ShouldContain("BufferReadinessSnapshot snapshot = Volatile.Read(ref _bufferReadinessSnapshot);");
        preparationSource.ShouldContain("for (int i = 0; i < buffers.Length; i++)");
        int readinessStart = preparationSource.IndexOf(
            "private bool AreCachedBuffersReadyForRendering",
            StringComparison.Ordinal);
        int readinessEnd = preparationSource.IndexOf("private bool SetPrepareResult", readinessStart, StringComparison.Ordinal);
        readinessStart.ShouldBeGreaterThanOrEqualTo(0);
        readinessEnd.ShouldBeGreaterThan(readinessStart);
        preparationSource[readinessStart..readinessEnd].ShouldNotContain("lock (_bufferStateSync)");
        int drawStatsStart = mainSource.IndexOf("internal VulkanFrameDrawStats EstimateFrameDrawStats", StringComparison.Ordinal);
        int drawStatsEnd = mainSource.IndexOf("private static int EstimateTriangleCount", drawStatsStart, StringComparison.Ordinal);
        drawStatsStart.ShouldBeGreaterThanOrEqualTo(0);
        drawStatsEnd.ShouldBeGreaterThan(drawStatsStart);
        string drawStats = mainSource[drawStatsStart..drawStatsEnd];
        drawStats.ShouldContain("Volatile.Read(ref _bufferReadinessSnapshot)");
        drawStats.ShouldContain("snapshot.TriangleIndexCount");
        drawStats.ShouldContain("snapshot.FallbackVertexCount");
        drawStats.ShouldNotContain("lock (_bufferStateSync)");
        snapshotSource.ShouldContain("public uint TriangleIndexCount { get; }");
        snapshotSource.ShouldContain("public uint FallbackVertexCount { get; }");
        AssertMethodLocksBefore(pipelineSource, "private void BuildVertexInputState", "foreach (var pair in _bufferCache)");
        AssertMethodLocksBefore(drawingSource, "private bool TryPrepareVertexBufferSnapshot", "_vertexBuffersByBinding.TryGetValue");
        AssertMethodLocksBefore(drawingSource, "private bool TryCreateIndirectVertexBufferSnapshot", "_vertexBuffersByBinding.TryGetValue");
        AssertMethodLocksBefore(drawingSource, "private bool BindVertexBuffersForCurrentPipeline(CommandBuffer commandBuffer, out string failureReason)", "_vertexBuffersByBinding.TryGetValue");

        // The buffer fingerprint is published while the collection lock is held.
        // Descriptor validation then reads the published value without a lock.
        string descriptorFingerprintSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorFingerprints.cs");
        string descriptorBufferSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorBuffers.cs");
        descriptorSource.ShouldContain("ComputeCachedBufferResourceFingerprintCore()");
        descriptorFingerprintSource.ShouldContain(
            "private ulong ComputeCachedBufferResourceFingerprintCore()\n" +
            "\t\t=> System.Threading.Volatile.Read(ref _cachedBufferResourceFingerprint);");
        AssertMethodLocksBefore(bufferSource, "private void CollectBuffers", "PublishCachedBufferResourceFingerprint();");
        int publishStart = descriptorFingerprintSource.IndexOf(
            "private void PublishCachedBufferResourceFingerprint()",
            StringComparison.Ordinal);
        publishStart.ShouldBeGreaterThanOrEqualTo(0);
        descriptorFingerprintSource.IndexOf(
                "foreach (KeyValuePair<string, VkDataBuffer> pair in _bufferCache)",
                publishStart,
                StringComparison.Ordinal)
            .ShouldBeGreaterThan(publishStart);
        AssertMethodLocksBefore(descriptorBufferSource, "private bool TryResolveCachedBufferByName", "_bufferCache.TryGetValue");
    }

    [Test]
    public void RenderableMesh_SingleLodUpdateAvoidsPerFrameLock()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs");

        source.ShouldContain("Volatile.Write(ref _lodCount, LODs.Count);");
        source.ShouldContain("if (Volatile.Read(ref _lodCount) <= 1)");
        source.ShouldContain("Volatile.Write(ref _lodCount, 0);");
    }

    [Test]
    public void VkMeshRenderer_ResolvesGeneratedSkinningAndBlendshapeUniformsLikeOpenGl()
    {
        string vkMainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string vkUniformSource =
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.Values.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.EngineUpload.cs");
        string glRenderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Rendering.cs");

        foreach (string uniformName in new[]
        {
            "skinPaletteBase",
            "skinPaletteCount",
            "skinningInfluenceCap",
            "blendshapeActiveCount",
            "blendshapeWeightThreshold",
            "usePrecombinedBlendshapeDeltas",
        })
        {
            glRenderSource.ShouldContain($"Uniform(\"{uniformName}\"");
            vkMainSource.ShouldContain(uniformName);
            vkUniformSource.ShouldContain($"case {ToConstantName(uniformName)}:");
        }

        vkUniformSource.ShouldContain("return UploadUniform(buffer, MeshRenderer.ActiveSkinPaletteBase);");
        vkUniformSource.ShouldContain("return UploadUniform(buffer, MeshRenderer.ActiveSkinPaletteCount);");
        vkUniformSource.ShouldContain("return UploadUniform(buffer, MeshRenderer.ActiveSkinningInfluenceCap);");
        vkUniformSource.ShouldContain("return UploadUniform(buffer, MeshRenderer.ActiveBlendshapeCount);");
        vkUniformSource.ShouldContain("return UploadUniform(buffer, MeshRenderer.BlendshapeActiveWeightThreshold);");
        vkUniformSource.ShouldContain("MeshRenderer.HasValidPrecombinedBlendshapeDeltas");
        vkUniformSource.ShouldContain("SkinPaletteBaseUniformName or SkinPaletteCountUniformName or SkinningInfluenceCapUniformName");
    }

    [Test]
    public void SkinningPrepass_BindsVulkanComputeDescriptorsThroughSsboPath()
    {
        string dispatcherSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/SkinningPrepassDispatcher.cs");
        string resourcesSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/SkinningPrepassDispatcher/SkinningPrepassDispatcher.RendererResources.cs");
        string bindingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/SkinningPrepassDispatcher/SkinningPrepassDispatcher.RendererResources.Bindings.cs");
        string residencySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/SkinningPrepassDispatcher/SkinningPrepassDispatcher.RendererResources.Residency.cs");
        string vkDataBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

        dispatcherSource.ShouldContain("EmptyStorageBuffers emptyBuffers = GetEmptyStorageBuffers();");
        dispatcherSource.ShouldContain("private sealed class EmptyStorageBuffers");
        dispatcherSource.ShouldContain("apiBuffer.EnsureStorageAllocatedForGpuUse();");
        dispatcherSource.ShouldContain("buffer.TryGetApiBufferForCurrentOwner(");
        dispatcherSource.ShouldContain("!apiBuffer.BackendIsReadyForGpuUse)");

        bindingSource.ShouldContain("EmptyStorageBuffers emptyBuffers");
        bindingSource.ShouldContain("emptyBuffers.ZeroScalar");
        bindingSource.ShouldContain("emptyBuffers.SpillHeaders");
        bindingSource.ShouldContain("emptyBuffers.SpillEntries");
        bindingSource.ShouldContain("buffer.BindTo(program, binding);");
        bindingSource.ShouldNotContain("buffer.SetBlockIndex(binding);");

        residencySource.ShouldContain("apiBuffer.EnsureStorageAllocatedForGpuUse();");
        residencySource.ShouldContain("wrapper is IApiDataBuffer apiBuffer");
        residencySource.ShouldContain("buffer.TryGetApiBufferForCurrentOwner(");
        residencySource.ShouldContain("!apiBuffer.BackendIsReadyForGpuUse)");

        resourcesSource.ShouldContain("_lastDispatchedPoseHash");
        resourcesSource.ShouldContain("_lastDispatchedPoseHash != _renderer.ComputeCurrentBonePoseHash()");
        resourcesSource.ShouldContain("_lastDispatchedPoseHash = doSkinning ? _renderer.ComputeCurrentBonePoseHash() : 0;");

        vkDataBufferSource.ShouldContain("private bool _requiresStorageBufferUsage;");
        vkDataBufferSource.ShouldContain("internal void EnsureStorageAllocatedForGpuUse()");
        vkDataBufferSource.ShouldContain("!hasStorageUsage");
        vkDataBufferSource.ShouldContain("ShouldAddStorageUsageForComputeDeformationSource");
        vkDataBufferSource.ShouldContain("BufferUsageFlags.StorageBufferBit");
    }

    private static string ToConstantName(string uniformName)
        => uniformName switch
        {
            "skinPaletteBase" => "SkinPaletteBaseUniformName",
            "skinPaletteCount" => "SkinPaletteCountUniformName",
            "skinningInfluenceCap" => "SkinningInfluenceCapUniformName",
            "blendshapeActiveCount" => "BlendshapeActiveCountUniformName",
            "blendshapeWeightThreshold" => "BlendshapeWeightThresholdUniformName",
            "usePrecombinedBlendshapeDeltas" => "UsePrecombinedBlendshapeDeltasUniformName",
            _ => uniformName
        };

    private static void AssertMethodLocksBefore(string source, string methodStart, string protectedAccess)
    {
        string normalizedSource = source.Replace("\r\n", "\n");
        int methodIndex = normalizedSource.IndexOf(methodStart, StringComparison.Ordinal);
        methodIndex.ShouldBeGreaterThanOrEqualTo(0, $"Expected method '{methodStart}' in source.");

        int lockIndex = normalizedSource.IndexOf("lock (_bufferStateSync)", methodIndex, StringComparison.Ordinal);
        lockIndex.ShouldBeGreaterThanOrEqualTo(methodIndex, $"Expected '{methodStart}' to lock _bufferStateSync.");

        int accessIndex = normalizedSource.IndexOf(protectedAccess, methodIndex, StringComparison.Ordinal);
        accessIndex.ShouldBeGreaterThanOrEqualTo(methodIndex, $"Expected '{protectedAccess}' in '{methodStart}'.");
        lockIndex.ShouldBeLessThan(accessIndex, $"Expected '{methodStart}' to lock before '{protectedAccess}'.");
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XRENGINE.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not resolve workspace root from test base directory '{AppContext.BaseDirectory}'.");
    }
}
