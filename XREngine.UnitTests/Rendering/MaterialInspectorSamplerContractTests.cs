using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class MaterialInspectorSamplerContractTests
{
    [Test]
    public void UniformRequirementsDetection_RecognizesEngineManagedForwardSamplers()
    {
        string[] engineSamplerNames =
        [
            EngineShaderBindingNames.Samplers.AmbientOcclusionTexture,
            EngineShaderBindingNames.Samplers.AmbientOcclusionTextureArray,
            EngineShaderBindingNames.Samplers.BRDF,
            EngineShaderBindingNames.Samplers.IrradianceArray,
            EngineShaderBindingNames.Samplers.PrefilterArray,
            EngineShaderBindingNames.Samplers.ShadowMap,
            EngineShaderBindingNames.Samplers.ShadowMapArray,
        ];

        foreach (string samplerName in engineSamplerNames)
            UniformRequirementsDetection.GetAllProviders(samplerName).ShouldBe(EUniformRequirements.Lights, samplerName);
    }

    [Test]
    public void XRMaterialInspector_MapsSamplersByNameInsteadOfReflectionRowIndex()
    {
        string source = ReadWorkspaceFile("XREngine.Editor/AssetEditors/XRMaterialInspector.cs");

        source.ShouldNotContain("XRTexture? texture = i < material.Textures.Count ? material.Textures[i] : null;");
        source.ShouldContain("TryGetTextureSlotForSampler");
        source.ShouldContain("GetBaseSamplerName");
        source.ShouldContain("[EngineShaderBindingNames.Samplers.PrevPeelDepth] = \"Exact transparency depth peeling\"");
    }

    [Test]
    public void DefaultPipelines_AliasCanonicalEngineSamplerNames()
    {
        DefaultRenderPipeline.AmbientOcclusionIntensityTextureName.ShouldBe(EngineShaderBindingNames.Samplers.AmbientOcclusionTexture);
        DefaultRenderPipeline.BRDFTextureName.ShouldBe(EngineShaderBindingNames.Samplers.BRDF);
        DefaultRenderPipeline2.AmbientOcclusionIntensityTextureName.ShouldBe(EngineShaderBindingNames.Samplers.AmbientOcclusionTexture);
        DefaultRenderPipeline2.BRDFTextureName.ShouldBe(EngineShaderBindingNames.Samplers.BRDF);
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