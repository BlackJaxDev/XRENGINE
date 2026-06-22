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
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.cs");

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
        string vkBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Buffers.cs");

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
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Drawing.cs");
        string resolverSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/MeshRenderMaterialResolver.cs");
        string enqueueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.cs");

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
        string mainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.cs");
        string prepSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Preparation.cs");
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Drawing.cs");

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
        string vkPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Pipeline.cs");
        string vkDrawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Drawing.cs");
        string glBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Buffers.cs");
        string glShaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Shaders.cs");

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
    public void VkMeshRenderer_BufferCollectionMatchesOpenGlRuntimeDeformationRules()
    {
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Buffers.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Pipeline.cs");
        string drawingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Drawing.cs");

        bufferSource.ShouldContain("RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader");
        bufferSource.ShouldContain("RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || useComputeSkinning");
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
        string cleanupSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Cleanup.cs");
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
