using System;
using System.IO;
using System.Linq;
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
        string[] runtimeFiles = Directory.GetFiles(
            Path.Combine(repoRoot, "XREngine.Runtime.Rendering"),
            "*.cs",
            SearchOption.AllDirectories);

        runtimeFiles.Any(path => string.Equals(Path.GetFileName(path), "VkMesh.cs", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
        runtimeFiles.Any(path => string.Equals(Path.GetFileName(path), "GLMesh.cs", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();

        string vulkanMap = ReadWorkspaceFile("docs/architecture/rendering/code-map.md");
        vulkanMap.ShouldContain("has no standalone OpenGL or Vulkan API wrapper");
        vulkanMap.ShouldContain("own mesh draw readiness");
    }

    [Test]
    public void VkMeshRenderer_MeshReplacementUnsubscribesOldMeshAndBufferEvents()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");

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
        string vkBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs");

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
        resolverSource.ShouldContain("CanUseSharedUberShadowFallback");

        vkBufferSource.ShouldContain("MeshRenderMaterialResolver.Resolve(");
        vkBufferSource.ShouldNotContain("shadowSourceMaterial?.ShadowCasterVariant");
    }

    [Test]
    public void VkMeshRenderer_ShadowDrawsSuppressLinePointAndUploadLayeredUniforms()
    {
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string resolverSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/MeshRenderMaterialResolver.cs");
        string enqueueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");

        enqueueSource.ShouldContain("MeshRenderMaterialResolver.ResolveLayeredShadowInstanceCount(effectiveMaterial, instances)");
        drawingSource.ShouldContain("bool skipLinePointDraws = MeshRenderMaterialResolver.RequiresTriangleOnlyDrawsForCurrentPass();");
        drawingSource.ShouldContain("Suppressed line/point index draws for shadow geometry pass");
        drawingSource.ShouldContain("Suppressed non-indexed {0} fallback for shadow geometry pass");
        drawingSource.ShouldContain("MeshRenderMaterialResolver.ApplyShadowUniforms(programData, material, draw.ShadowUniformState);");

        resolverSource.ShouldContain("CascadeLayerCount");
        resolverSource.ShouldContain("CascadeViewProjectionMatrices");
        resolverSource.ShouldContain("PointShadowFaceCount");
        resolverSource.ShouldContain("PointShadowViewProjectionMatrices");
        resolverSource.ShouldContain("OnSettingShadowUniforms(program)");
    }

    [Test]
    public void VkMeshRenderer_ImplementsExplicitPreparationGateSeparateFromGeneration()
    {
        string mainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string prepSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        mainSource.ShouldContain("IRenderPreparationState");
        mainSource.ShouldContain("public override bool IsGenerated => IsActive;");
        mainSource.ShouldContain("TryPrepareForRendering(effectiveMaterial, out string prepareReason)");
        mainSource.ShouldContain("Skipping mesh draw enqueue");

        prepSource.ShouldContain("public bool IsPreparedForRendering");
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
        string vkPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string vkDrawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string glBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Buffers.cs");
        string glShaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Shaders.cs");

        signatureSource.ShouldContain("MeshGeometryLayoutSignature");
        signatureSource.ShouldContain("InterleavedAttribute");
        signatureSource.ShouldContain("InstanceDivisor");
        signatureSource.ShouldContain("HasRuntimeDeformationBuffers");
        signatureSource.ShouldContain("HasMeshletPayload");
        signatureSource.ShouldContain("DrawCountSource");

        vkPipelineSource.ShouldContain("MeshGeometryLayoutSignatureBuilder.Create");
        vkPipelineSource.ShouldContain("descriptorLayoutHash");
        vkPipelineSource.ShouldContain("materialLayoutHash");
        vkPipelineSource.ShouldContain("passMetadataHash");
        vkPipelineSource.ShouldContain("featureProfileHash");
        vkDrawingSource.ShouldContain("_geometryLayoutSignature.StableHash");

        glBufferSource.ShouldContain("CaptureGeometryLayoutSignature");
        glBufferSource.ShouldContain("layout={_geometryLayoutSignature.DebugSummary}");
        glShaderSource.ShouldContain("pipelineStateKey=");
        glShaderSource.ShouldContain("ComputeOpenGLPipelineStateKey");
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
        sharedPassSource.ShouldContain("VPRC_RenderMeshesPassMeshlet.Execute(this)");

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
        string gpuMeshletResourcesSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURendering/Resources/GPUMeshletResources.cs");
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        gpuSceneCommandBuffersSource.ShouldContain("MakeDrawMetadataBuffer(\"DrawMetadataBuffer\")");
        gpuSceneCommandBuffersSource.ShouldContain("MakeMeshletRangeBuffer()");
        gpuSceneCommandBuffersSource.ShouldContain("MeshletRangeBuffer/MeshletDescriptorBuffer/MeshletVertexIndexBuffer/MeshletTriangleIndexBuffer");
        gpuSceneCommandBuffersSource.ShouldContain("public XRDataBuffer DrawMetadataBuffer");
        gpuSceneCommandBuffersSource.ShouldContain("public XRDataBuffer MeshletRangeBuffer");

        gpuSceneSoaSource.ShouldContain("UpdatingDrawMetadataBuffer.SetDataRawAtIndex(drawId, command.ToDrawMetadata(drawId));");
        gpuSceneSoaSource.ShouldContain("_drawMetadataDirtyRange.Mark(drawId);");

        gpuSceneAddRemoveSource.ShouldContain("EnsureMeshletRangeForMesh");
        gpuSceneAddRemoveSource.ShouldContain("MeshletPayload");
        gpuSceneAddRemoveSource.ShouldContain("MeshletDescriptorBuffer.SetDataRawAtIndex");
        gpuSceneAddRemoveSource.ShouldContain("MeshletVertexIndexBuffer.SetDataRawAtIndex");
        gpuSceneAddRemoveSource.ShouldContain("MeshletTriangleIndexBuffer.SetDataRawAtIndex");
        gpuSceneAddRemoveSource.ShouldContain("FlushMeshletRangeDirtyRange");

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
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        bufferSource.ShouldContain("RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader");
        bufferSource.ShouldContain("RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || useComputeSkinning");
        bufferSource.ShouldContain("FilterRuntimeDeformationSourceBuffers");
        bufferSource.ShouldContain("RemoveCollectedBuffer(ECommonBufferType.BoneInfluenceCoreIndices.ToString())");
        bufferSource.ShouldContain("RemoveCollectedBuffer($\"{ECommonBufferType.BoneMatrices}Buffer\")");
        bufferSource.ShouldContain("RemoveCollectedBuffer($\"{ECommonBufferType.SkinPalette}Buffer\")");
        bufferSource.ShouldContain("RemoveCollectedBuffer($\"{ECommonBufferType.BlendshapeWeights}Buffer\")");
        bufferSource.ShouldContain("RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass");
        bufferSource.ShouldContain("MeshRenderer.HasValidPrecombinedBlendshapeDeltas");
        bufferSource.ShouldContain("AddMeshDeformSourceBuffers");
        bufferSource.ShouldContain("\"DeformerPositionsBuffer\"");
        bufferSource.ShouldContain("assignBindingOverride: false");
        bufferSource.ShouldContain("RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask");
        bufferSource.ShouldContain("MarkIndexBuffersDirty");
        bufferSource.ShouldContain("GetIndexBufferForBinding(EPrimitiveType.Triangles");
        bufferSource.ShouldContain("GetIndexBufferForBinding(EPrimitiveType.Lines");
        bufferSource.ShouldContain("GetIndexBufferForBinding(EPrimitiveType.Points");
        bufferSource.ShouldContain("return mesh.GetIndexBuffer(type, out elementSize, EBufferTarget.ElementArrayBuffer, onReady);");

        string drawingSourceWithIndexTypes = drawingSource;
        string cleanupSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Cleanup.cs");
        drawingSourceWithIndexTypes.ShouldContain("size == IndexSize.Byte && !Renderer.SupportsIndexTypeUint8");
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
    public void VkMeshRenderer_ResolvesGeneratedSkinningAndBlendshapeUniformsLikeOpenGl()
    {
        string vkMainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string vkUniformSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs");
        string glRenderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/MeshRendering/GLMeshRenderer.Rendering.cs");

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

        vkUniformSource.ShouldContain("value = MeshRenderer.ActiveSkinPaletteBase;");
        vkUniformSource.ShouldContain("value = MeshRenderer.ActiveSkinPaletteCount;");
        vkUniformSource.ShouldContain("value = MeshRenderer.ActiveSkinningInfluenceCap;");
        vkUniformSource.ShouldContain("value = MeshRenderer.ActiveBlendshapeCount;");
        vkUniformSource.ShouldContain("value = MeshRenderer.BlendshapeActiveWeightThreshold;");
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
        string vkDataBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

        dispatcherSource.ShouldContain("EmptyStorageBuffers emptyBuffers = GetEmptyStorageBuffers();");
        dispatcherSource.ShouldContain("private sealed class EmptyStorageBuffers");
        dispatcherSource.ShouldContain("vk.EnsureStorageAllocatedForGpuUse();");

        bindingSource.ShouldContain("EmptyStorageBuffers emptyBuffers");
        bindingSource.ShouldContain("emptyBuffers.ZeroScalar");
        bindingSource.ShouldContain("emptyBuffers.SpillHeaders");
        bindingSource.ShouldContain("emptyBuffers.SpillEntries");
        bindingSource.ShouldContain("buffer.BindTo(program, binding);");
        bindingSource.ShouldNotContain("buffer.SetBlockIndex(binding);");

        residencySource.ShouldContain("vk.EnsureStorageAllocatedForGpuUse();");
        residencySource.ShouldContain("wrapper is VulkanRenderer.VkDataBuffer vk");
        residencySource.ShouldContain("wrapper is VulkanRenderer.VkDataBuffer vk && !vk.IsReadyForRendering");

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

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = Path.Combine(ResolveWorkspaceRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

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
