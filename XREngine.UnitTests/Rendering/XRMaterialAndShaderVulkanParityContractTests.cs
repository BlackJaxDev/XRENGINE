using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class XRMaterialAndShaderVulkanParityContractTests
{
    [Test]
    public void VulkanMaterialDescriptorsUseSharedTextureBindingLadder()
    {
        string resolverSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/MaterialTextureBindingResolver.cs");
        string vkMaterialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string vkMeshDescriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");

        resolverSource.ShouldContain("MaterialTextureBindingResolver");
        resolverSource.ShouldContain("ResolveSamplerName");
        resolverSource.ShouldContain("XRTexture.GetIndexedSamplerName");
        resolverSource.ShouldContain("ProgramSamplerName");
        resolverSource.ShouldContain("MaterialSamplerName");
        resolverSource.ShouldContain("IndexedTextureAlias");
        resolverSource.ShouldContain("NumericTextureSlot");
        resolverSource.ShouldContain("BindlessMaterialArray");

        vkMaterialSource.ShouldContain("MaterialTextureBindingResolver.Resolve");
        vkMaterialSource.ShouldContain("program.AddSamplerResourceFingerprint(ref hash);");
        vkMeshDescriptorSource.ShouldContain("MaterialTextureBindingResolver.Resolve");
    }

    [Test]
    public void VulkanMaterialUniformUploadMatchesOpenGlShadowAndEngineUniformSources()
    {
        string drawStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.RenderState.cs");
        string resolverSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/MeshRenderMaterialResolver.cs");
        string xrProgramSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Resources/Shaders/XRRenderProgram.cs");

        drawStateSource.ShouldContain("ShadowBindingSourceMaterial");
        drawStateSource.ShouldContain("MaterialTextureBindingResolver.BuildShadowBindingPlan");
        drawStateSource.ShouldContain("program.GetActiveEngineUniformRequirements()");
        drawStateSource.ShouldContain("EUniformRequirements.AmbientOcclusion");
        drawStateSource.ShouldContain("Lights3DCollection.SetForwardAmbientOcclusionUniforms(program)");
        drawStateSource.ShouldContain("XRTexture.GetIndexedSamplerName(textureIndex)");
        drawStateSource.ShouldContain("program.Sampler(indexedSamplerName, texture, textureIndex);");

        resolverSource.ShouldContain("shadowBindingSource.HasSettingShadowUniformHandlers");
        resolverSource.ShouldContain("shadowBindingSource.OnSettingUniforms(program)");
        resolverSource.ShouldContain("RuntimeEngine.Rendering.State.RenderingPipelineState?.ApplyScopedProgramBindings(program);");

        xrProgramSource.ShouldContain("public EUniformRequirements GetActiveEngineUniformRequirements()");
        xrProgramSource.ShouldContain("UniformRequirementsDetection.GetAutoDetectedRequirement");
        xrProgramSource.ShouldContain("TextureBindings.Keys");
    }

    [Test]
    public void VulkanMaterialUniformBuffersCoverExpandedShaderVarTypesAndArrays()
    {
        string vkMaterialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");

        vkMaterialSource.ShouldContain("ShaderArrayBase");
        vkMaterialSource.ShouldContain("GetShaderVarArrayStride");
        vkMaterialSource.ShouldContain("EShaderVarType._double");
        vkMaterialSource.ShouldContain("EShaderVarType._dvec2");
        vkMaterialSource.ShouldContain("EShaderVarType._dvec3");
        vkMaterialSource.ShouldContain("EShaderVarType._bvec2");
        vkMaterialSource.ShouldContain("EShaderVarType._bvec3");
        vkMaterialSource.ShouldContain("EShaderVarType._mat3");
        vkMaterialSource.ShouldContain("WriteBoolVector4");
        vkMaterialSource.ShouldContain("WriteMatrix3x3Std140");
    }

    [Test]
    public void MaterialTableAndDescriptorIndexedPathsShareMaterialLayoutContract()
    {
        string layoutSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Materials/MaterialBindingLayout.cs");
        string tableSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialTable.cs");
        string passSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");
        string vulkanTableSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.BindlessMaterialTextureTable.cs");
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        layoutSource.ShouldContain("public sealed class MaterialBindingLayout");
        layoutSource.ShouldContain("public uint RowByteCount => RowWordCount * sizeof(uint);");
        layoutSource.ShouldContain("public string LayoutHash { get; }");
        layoutSource.ShouldContain("MaterialBindingRowPacker");
        layoutSource.ShouldContain("MaterialBindingResolverResult");
        layoutSource.ShouldContain("return texture(XR_BindlessMaterialTextures[nonuniformEXT(descriptorIndex)], uv);");

        tableSource.ShouldContain("public GPUMaterialTableDirtyRange MaterialDirtyRange");
        tableSource.ShouldContain("public GPUMaterialTableDirtyRange TextureHandleDirtyRange");
        tableSource.ShouldContain("MarkMaterialRowDirty(materialID);");
        tableSource.ShouldContain("MarkTextureHandleRowDirty(index);");
        tableSource.ShouldContain("public void PushDirtyRanges()");
        tableSource.ShouldContain("buffer.PushSubData((int)offset, length);");
        tableSource.ShouldContain("MaterialBindingRowPacker.TryWriteOpaqueDeferred");
        passSource.ShouldContain("_materialTable.PushDirtyRanges();");

        vulkanTableSource.ShouldContain("TryGetOrCreateMaterialTextureDescriptorIndex");
        vulkanTableSource.ShouldContain("_dirtyGlobalMaterialTextureDescriptorSlots");
        vulkanTableSource.ShouldContain("FlushGlobalMaterialTextureDescriptorUpdates");
        vulkanTableSource.ShouldContain("GlobalMaterialTextureDescriptorFallbackReferencesTotal");
        vulkanTableSource.ShouldContain("RecordVulkanDescriptorFallback");

        hybridSource.ShouldContain("EMaterialTableTextureReferenceMode.VulkanDescriptorIndexTable");
        hybridSource.ShouldContain("EMaterialTableTextureReferenceMode.OpenGLBindlessHandleTable");
        hybridSource.ShouldContain("layout.LayoutHash");
        hybridSource.ShouldContain("BeginGlobalMaterialTextureDescriptorScope");
    }

    [Test]
    public void VulkanMaterialsUsePreparedDescriptorPlansAndVisibleFallbackDiagnostics()
    {
        string vkMaterialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string vkProgramSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string glMaterialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Materials/GLMaterial.cs");
        string glProgramSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.UniformBinding.cs");

        vkMaterialSource.ShouldContain("private sealed class ProgramDescriptorState");
        vkMaterialSource.ShouldContain("public required IReadOnlyList<DescriptorBindingInfo> Bindings { get; init; }");
        vkMaterialSource.ShouldContain("public required Dictionary<(uint set, uint binding), UniformBindingResource> UniformBindings { get; init; }");
        vkMaterialSource.ShouldContain("public required bool HasMaterialParameterOrSamplerBindings { get; init; }");
        vkMaterialSource.ShouldContain("public AutoUniformBlockInfo? ReflectedBlock { get; init; }");
        vkMaterialSource.ShouldContain("program.TryGetAutoUniformBlockFuzzy(binding.Name, binding.Set, binding.Binding, out AutoUniformBlockInfo block)");
        vkMaterialSource.ShouldContain("bufferSize = block.Size;");
        vkMaterialSource.ShouldContain("TryWriteReflectedUniformBlock(data, reflectedBlock)");
        vkMaterialSource.ShouldContain("return TryWriteSingleUniform(destination, member.Offset, value.Type, value.Value);");
        vkMaterialSource.ShouldContain("private bool TryEnsureState(VkRenderProgram program, out ProgramDescriptorState? state)");
        vkMaterialSource.ShouldContain("private static bool HasMaterialParameterOrSamplerBindings(IReadOnlyList<DescriptorBindingInfo> bindings)");
        vkMaterialSource.ShouldContain("WarnNoMaterialBindings(program);");
        vkMaterialSource.ShouldContain("has no Vulkan parameter or sampler bindings after descriptor resolution");
        vkMaterialSource.ShouldContain("RecordDescriptorFallback(binding);");
        vkMaterialSource.ShouldContain("RecordDescriptorFailure(binding");
        vkMaterialSource.ShouldContain("binding.ExpectedImageViewType");
        vkMaterialSource.ShouldContain("ImageUsageFlags.SampledBit");
        vkMaterialSource.ShouldContain("ImageUsageFlags.StorageBit");
        vkMaterialSource.ShouldContain("source.GetDepthOnlyDescriptorView()");
        vkMaterialSource.ShouldContain("source.GetStencilOnlyDescriptorView()");

        vkProgramSource.ShouldContain("ComputeGraphicsPipelineFingerprint");
        vkProgramSource.ShouldContain("BuildDescriptorLayoutsShared");
        vkProgramSource.ShouldContain("ComputeComputeDescriptorSchemaFingerprint");
        vkProgramSource.ShouldContain("TryGetAutoUniformBlockFuzzy");
        vkProgramSource.ShouldContain("TryGetVertexInputLocation");
        vkProgramSource.ShouldContain("CreateCommonPushConstantRange");
        vkProgramSource.ShouldContain("VulkanBindlessMaterialDescriptors.ResolveDescriptorCount");

        glMaterialSource.ShouldContain("TextureRuntimeDiagnostics.LogMaterialBinding");
        glMaterialSource.ShouldContain("TextureRuntimeDiagnostics.LogBindingRisk");
        glProgramSource.ShouldContain("WarnIfNoUniformOrSamplerBindings");
    }

    [Test]
    public void ShaderSourceAndTypeChangesInvalidateSharedProgramInterfaces()
    {
        string xrShaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Resources/Shaders/XRShader.cs");
        string xrProgramSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Resources/Shaders/XRRenderProgram.cs");

        xrShaderSource.ShouldContain("public event Action<XRShader>? SourceChanged;");
        xrShaderSource.ShouldContain("case nameof(Type):");
        xrShaderSource.ShouldContain("SourceChanged?.Invoke(this);");

        xrProgramSource.ShouldContain("nameof(XRShader.Type)");
        xrProgramSource.ShouldContain("shader.SourceChanged += sourceChangedHandler;");
        xrProgramSource.ShouldContain("shader.SourceChanged -= subscription.SourceChangedHandler;");
        xrProgramSource.ShouldContain("MarkShaderInterfaceDirty();");
    }

    [Test]
    public void VulkanShadersExposeCompileStatusArtifactsAndDiagnostics()
    {
        string statusSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Resources/Shaders/ShaderCompileStatus.cs");
        string vkShaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkShader.cs");
        string vkProgramSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs");

        statusSource.ShouldContain("EShaderCompileFailureKind");
        statusSource.ShouldContain("SpirvCompilation");
        statusSource.ShouldContain("ShaderModuleCreation");

        vkShaderSource.ShouldContain("public bool IsCompiled");
        vkShaderSource.ShouldContain("public bool IsCompilePending");
        vkShaderSource.ShouldContain("public ShaderCompileStatus CompileStatus");
        vkShaderSource.ShouldContain("VulkanShaderArtifact");
        vkShaderSource.ShouldContain("VulkanShaderCompileFailure");
        vkShaderSource.ShouldContain("internal event Action<VkShader>? ShaderInvalidated;");
        vkShaderSource.ShouldContain("nameof(XRShader.Type)");
        vkShaderSource.ShouldContain("WriteCompileFailureDiagnosticsFile");

        vkProgramSource.ShouldContain("vkShader.ShaderInvalidated += OnShaderInvalidated;");
        vkProgramSource.ShouldContain("vkShader.ShaderInvalidated -= OnShaderInvalidated;");
        vkProgramSource.ShouldContain("shader.CompileStatus.FailureReason");
        vkProgramSource.ShouldContain("!shader.IsGenerated || !shader.IsCompiled");

        compilerSource.ShouldContain("BuildArtifactIdentity");
        compilerSource.ShouldContain("SHA256");
        compilerSource.ShouldContain("XRENGINE_VULKAN");
        compilerSource.ShouldContain("GeneratedUberVariantHash");
        compilerSource.ShouldContain("ShaderConfigVersion");
        compilerSource.ShouldContain("useVulkanClipDepthRemap");
    }

    [Test]
    public void ShaderArtifactsCarryReflectionIdentityAndPrewarmInputs()
    {
        string resolvedSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Resources/Shaders/ResolvedShaderSource.cs");
        string resolverSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Resources/Shaders/ShaderSourceResolver.cs");
        string vkShaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkShader.cs");
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs");
        string artifactCacheSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderArtifactCache.cs");
        string prewarmSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelinePrewarmDatabase.cs");
        string glDiagnosticsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Programs/GLRenderProgram.Diagnostics.cs");
        string glLifecycleSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Pipelines/ShaderProgramLifecycleDiagnostics.cs");

        resolvedSource.ShouldContain("SourceIdentity");
        resolverSource.ShouldContain("IncludeExpansionCacheKey");
        resolverSource.ShouldContain("ResolvedPaths");

        vkShaderSource.ShouldContain("VulkanShaderArtifact");
        vkShaderSource.ShouldContain("byte[] SpirV");
        vkShaderSource.ShouldContain("DescriptorBindings");
        vkShaderSource.ShouldContain("VertexInputLocations");
        vkShaderSource.ShouldContain("AutoUniformBlock");
        vkShaderSource.ShouldContain("ArtifactIdentity");

        compilerSource.ShouldContain("public sealed record PreparedSource");
        compilerSource.ShouldContain("string RewrittenSource");
        compilerSource.ShouldContain("CompilePrepared");
        compilerSource.ShouldContain("BuildArtifactIdentity");
        compilerSource.ShouldContain("ResolvedSourceIdentity");
        compilerSource.ShouldContain("RewrittenSourceHash");
        compilerSource.ShouldContain("VulkanShaderReflection");
        compilerSource.ShouldContain("CollectDescriptorBindings");
        compilerSource.ShouldContain("PushConstant");

        artifactCacheSource.ShouldContain("VulkanShaderArtifactRuntimeFingerprint");
        artifactCacheSource.ShouldContain("TargetEnvironment: \"Vulkan\"");
        artifactCacheSource.ShouldContain("RewriteIdentity");
        artifactCacheSource.ShouldContain("DescriptorBindings = [.. artifact.DescriptorBindings]");
        artifactCacheSource.ShouldContain("VertexInputLocations = new Dictionary<string, uint>(artifact.VertexInputLocations, StringComparer.Ordinal)");

        prewarmSource.ShouldContain("CreateGraphicsEntry");
        prewarmSource.ShouldContain("CreateComputeEntry");
        prewarmSource.ShouldContain("RecordVulkanPipelineCacheMiss(entry.ToProfilerSummary");
        prewarmSource.ShouldContain("VulkanFeatureProfile.ActiveProfile");

        glDiagnosticsSource.ShouldContain("LinkDiagnosticsSnapshot");
        glDiagnosticsSource.ShouldContain("PreparedHash");
        glDiagnosticsSource.ShouldContain("PreparedBinaryCacheHit");
        glDiagnosticsSource.ShouldContain("BackendCompileMilliseconds");
        glDiagnosticsSource.ShouldContain("OpenGLShaderCompilerThreadCount");
        glDiagnosticsSource.ShouldContain("AsyncCompileLinkPending");

        glLifecycleSource.ShouldContain("RecordBinaryCacheHit");
        glLifecycleSource.ShouldContain("RecordBinaryCacheMiss");
        glLifecycleSource.ShouldContain("LogSummary");
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
