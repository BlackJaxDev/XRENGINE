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
        string vkMaterialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMaterial.cs");
        string vkMeshDescriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Descriptors.cs");

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
        string drawStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.RenderState.cs");
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
        string vkMaterialSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMaterial.cs");

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
        string vkShaderSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkShader.cs");
        string vkProgramSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs");
        string compilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanShaderTools.cs");

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
