using System;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UberShaderForwardContractTests
{
    [Test]
    public void UberShaderVertexInputs_MatchEngineMeshSemanticNames()
    {
        string source = LoadShaderSource(Path.Combine("Uber", "UberShader.vert"));

        source.ShouldContain("layout(location = 0) in vec3 Position;");
        source.ShouldContain("layout(location = 1) in vec3 Normal;");
        source.ShouldContain("layout(location = 2) in vec4 Tangent;");
        source.ShouldContain("layout(location = 3) in vec2 TexCoord0;");
        source.ShouldContain("layout(location = 7) in vec4 Color0;");

        source.ShouldNotContain("a_Position");
        source.ShouldNotContain("a_Normal");
    }

    [Test]
    public void UberShaderFragment_UsesSharedForwardLightingContracts()
    {
        string source = LoadShaderSource(Path.Combine("Uber", "UberShader.frag"));
        string uniforms = LoadShaderSource(Path.Combine("Uber", "uniforms.glsl"));

        source.ShouldContain("#pragma snippet \"ForwardLighting\"");
        source.ShouldContain("#pragma snippet \"AmbientOcclusionSampling\"");
        source.ShouldContain("XRENGINE_CalculateAmbientPbr");
        source.ShouldContain("XRENGINE_CalcForwardPlusPointLight");
        source.ShouldContain("XRENGINE_CalcPointLight(i, PointLights[i], normal, mesh.worldPos, baseColor, rms, pbr.F0)");
        source.ShouldContain("XRENGINE_CalcSpotLight(i, SpotLights[i], normal, mesh.worldPos, baseColor, rms, pbr.F0)");
        source.ShouldContain("XRENGINE_ReadShadowMapDir");

        uniforms.ShouldContain("uniform float RenderTime;");
        uniforms.ShouldContain("#define u_Time RenderTime");
        uniforms.ShouldNotContain("uniform float Time;");
        uniforms.ShouldNotContain("uniform float ScreenWidth;");
        uniforms.ShouldNotContain("uniform float ScreenHeight;");
    }

    [Test]
    public void UberShaderFragment_SharedScreenOriginUniform_IsGuardedAcrossSnippets()
    {
        string forwardLighting = LoadShaderSource(Path.Combine("Snippets", "ForwardLighting.glsl"));
        string ambientOcclusion = LoadShaderSource(Path.Combine("Snippets", "AmbientOcclusionSampling.glsl"));

        foreach (string source in new[] { forwardLighting, ambientOcclusion })
        {
            source.ShouldContain("#ifndef XRENGINE_SCREEN_ORIGIN_UNIFORM");
            source.ShouldContain("#define XRENGINE_SCREEN_ORIGIN_UNIFORM");
            source.ShouldContain("uniform vec2 ScreenOrigin;");
            source.ShouldContain("#endif");
        }
    }

    [Test]
    public void UberShaderFragment_MvpPath_CompileTimeTrimsOptionalFeatureSurface()
    {
        string source = LoadShaderSource(Path.Combine("Uber", "UberShader.frag"));
        string uniforms = LoadShaderSource(Path.Combine("Uber", "uniforms.glsl"));

        source.ShouldContain("#define XRENGINE_UBER_MVP_FRAGMENT 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_STYLIZED_SHADING 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_DETAIL_TEXTURES 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_DISSOLVE 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_PARALLAX 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_SUBSURFACE 1");
        source.ShouldNotContain("#include \"pbr.glsl\"");
        source.ShouldContain("struct PBRData {");

        uniforms.ShouldContain("#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING");
        uniforms.ShouldContain("#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR");
        uniforms.ShouldContain("#ifndef XRENGINE_UBER_DISABLE_DETAIL_TEXTURES");
        uniforms.ShouldContain("#ifndef XRENGINE_UBER_DISABLE_DISSOLVE");
        uniforms.ShouldContain("#ifndef XRENGINE_UBER_DISABLE_PARALLAX");
    }

    [Test]
    public void DefaultUberMaterialContract_RequestsForwardEngineUniforms_AndFeatureDefaults()
    {
        ShaderVar[] parameters = ModelImporter.CreateDefaultForwardPlusUberShaderParameters();
        var material = new XRMaterial
        {
            Parameters = parameters,
            RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions(),
        };

        material.RenderOptions.RequiredEngineUniforms.ShouldBe(
            EUniformRequirements.Camera
            | EUniformRequirements.Lights
            | EUniformRequirements.ViewportDimensions
            | EUniformRequirements.RenderTime);

        material.Parameter<ShaderFloat>("_LightDataAOStrengthR")?.Value.ShouldBe(0.0f);
        material.Parameter<ShaderFloat>("_PBRBRDF")?.Value.ShouldBe(0.0f);
        material.Parameter<ShaderFloat>("_SpecularStrength")?.Value.ShouldBe(1.0f);
        material.Parameter<ShaderFloat>("_MainVertexColoringEnabled")?.Value.ShouldBe(0.0f);
        material.Parameter<ShaderVector4>("_EmissionMap_ST")?.Value.ShouldBe(new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
        material.Parameter<ShaderVector4>("_MatcapMask_ST")?.Value.ShouldBe(new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
        material.Parameter<ShaderVector4>("_PBRMetallicMaps_ST")?.Value.ShouldBe(new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
    }

    [Test]
    public void UberShaderStereoVertexVariants_ExportForwardViewIndex()
    {
        string monoSource = LoadShaderSource(Path.Combine("Uber", "UberShader.vert"));
        string ovrSource = LoadShaderSource(Path.Combine("Uber", "UberShader_OVR.vert"));
        string nvSource = LoadShaderSource(Path.Combine("Uber", "UberShader_NV.vert"));

        monoSource.ShouldContain("out gl_PerVertex {");
        monoSource.ShouldContain("layout(location = 22) out float FragViewIndex;");
        monoSource.ShouldContain("FragViewIndex = 0.0;");
        ovrSource.ShouldContain("out gl_PerVertex {");
        ovrSource.ShouldContain("layout(location = 22) out float FragViewIndex;");
        ovrSource.ShouldContain("FragViewIndex = float(gl_ViewID_OVR);");
        nvSource.ShouldContain("out gl_PerVertex {");
        nvSource.ShouldContain("layout(location = 22) out float FragViewIndex;");
        nvSource.ShouldContain("FragViewIndex = 0.0;");
    }

    private static string LoadShaderSource(string shaderRelativePath)
    {
        string shaderRoot = ResolveShaderRoot();
        string normalizedRelativePath = shaderRelativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.Combine(shaderRoot, normalizedRelativePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Shader file not found: {fullPath}", fullPath);

        return File.ReadAllText(fullPath);
    }

    private static string ResolveShaderRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Build", "CommonAssets", "Shaders");
            if (Directory.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Build/CommonAssets/Shaders from test base directory.");
    }
}
