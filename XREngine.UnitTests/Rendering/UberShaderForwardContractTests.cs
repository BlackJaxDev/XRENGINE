using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;
using XREngine.Core.Files;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Generator;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UberShaderForwardContractTests : GpuTestBase
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new FileSystemRuntimeShaderServices(ResolveShaderRoot());
        ShaderHelper.ClearDefinedVariantSourceCache();
    }

    [TearDown]
    public void TearDown()
    {
        ShaderHelper.ClearDefinedVariantSourceCache();
        RuntimeShaderServices.Current = _previousServices;
    }

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
    public void ImportedUberFragmentVariant_DefinesLeanImportFeatureSet()
    {
        XRShader variant = ShaderHelper.UberImportFragForward();

        string source = variant.Source.Text ?? throw new InvalidOperationException("Variant shader source text was null.");
        source.IndexOf("#version 450 core", StringComparison.Ordinal).ShouldBeLessThan(
            source.IndexOf("#define XRENGINE_UBER_IMPORT_MATERIAL", StringComparison.Ordinal));
        source.ShouldContain("#define XRENGINE_UBER_IMPORT_MATERIAL");
        source.ShouldNotContain("#define XRENGINE_UBER_DISABLE_ALPHA_MASKS 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_MATERIAL_AO 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_EMISSION 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_MATCAP 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_RENDER_TIME 1");
    }

    [Test]
    public void ImportedUberTransparentVariant_PreservesImportFeatureSet_WhenNormalized()
    {
        XRShader weighted = ShaderHelper.GetWeightedBlendedOitForwardVariant(ShaderHelper.UberImportFragForward())
            ?? throw new InvalidOperationException("Weighted Uber variant was null.");

        XRShader normalized = ShaderHelper.GetStandardForwardVariant(weighted)
            ?? throw new InvalidOperationException("Normalized Uber variant was null.");

        string source = normalized.Source.Text ?? throw new InvalidOperationException("Normalized shader source text was null.");
        source.ShouldContain("#define XRENGINE_UBER_IMPORT_MATERIAL");
        source.ShouldNotContain("#define XRENGINE_FORWARD_WEIGHTED_OIT");
    }

    [Test]
    public void ImportedUberWeightedOitVariant_UsesTransparentForwardOutputContract()
    {
        XRShader variant = ShaderHelper.GetWeightedBlendedOitForwardVariant(ShaderHelper.UberImportFragForward())
            ?? throw new InvalidOperationException("Weighted Uber variant was null.");

        string source = variant.Source.Text ?? throw new InvalidOperationException("Variant shader source text was null.");
        source.ShouldContain("#define XRENGINE_UBER_IMPORT_MATERIAL");
        source.ShouldContain("#define XRENGINE_FORWARD_WEIGHTED_OIT");
        source.ShouldContain("layout(location = 0) out vec4 OutAccum;");
        source.ShouldContain("layout(location = 1) out vec4 OutRevealage;");
    }

    [Test]
    public void ImportedUberFragmentVariant_CompilesOnOpenGl()
    {
        RunWithGLContext(gl =>
        {
            XRShader fragment = ShaderHelper.UberImportFragForward();
            string resolvedSource = fragment.GetResolvedSource();

            uint shader = CompileShader(gl, ShaderType.FragmentShader, resolvedSource);
            gl.DeleteShader(shader);
        });
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

    [Test]
    public void ImportedUberFragment_ConsumesGeneratedVertexContracts()
    {
        string source = LoadShaderSource(Path.Combine("Uber", "UberShader.frag"));

        source.ShouldContain("layout(location = 0) in vec3 FragPos;");
        source.ShouldContain("layout(location = 1) in vec3 FragNorm;");
        source.ShouldContain("layout(location = 4) in vec2 FragUV0;");
        source.ShouldContain("layout(location = 12) in vec4 FragColor0;");
        source.ShouldContain("layout(location = 20) in vec3 FragPosLocal;");
        source.ShouldContain("mesh.viewDir = normalize(u_CameraPosition - FragPos);");
        source.ShouldContain("mesh.TBN = computeWorldTbn(mesh.vertexNormal, mesh.worldPos, mesh.uv[0]);");
    }

    [Test]
    public void DefaultVertexShaderGenerator_ComputeBlendshapesOnly_StillEmitsBoneSkinning()
    {
        XRMesh mesh = CreateGeneratedContractMesh();

        bool previousComputeSkinning = Engine.Rendering.Settings.CalculateSkinningInComputeShader;
        bool previousComputeBlendshapes = Engine.Rendering.Settings.CalculateBlendshapesInComputeShader;
        bool previousAllowSkinning = Engine.Rendering.Settings.AllowSkinning;
        bool previousAllowBlendshapes = Engine.Rendering.Settings.AllowBlendshapes;

        try
        {
            Engine.Rendering.Settings.AllowSkinning = true;
            Engine.Rendering.Settings.AllowBlendshapes = true;
            Engine.Rendering.Settings.CalculateSkinningInComputeShader = false;
            Engine.Rendering.Settings.CalculateBlendshapesInComputeShader = true;

            string source = new DefaultVertexShaderGenerator(mesh).Generate();

            source.ShouldContain("SkinnedPositionsInput");
            source.ShouldContain("BoneMatricesBuffer");
            source.ShouldContain("BasePosition = SkinnedPositions[gl_VertexID].xyz;");
            source.ShouldContain("FinalPosition += (boneMatrix * vec4(BasePosition, 1.0f)) * weight;");
        }
        finally
        {
            Engine.Rendering.Settings.CalculateSkinningInComputeShader = previousComputeSkinning;
            Engine.Rendering.Settings.CalculateBlendshapesInComputeShader = previousComputeBlendshapes;
            Engine.Rendering.Settings.AllowSkinning = previousAllowSkinning;
            Engine.Rendering.Settings.AllowBlendshapes = previousAllowBlendshapes;
        }
    }

    [Test]
    public void DefaultVertexShaderGenerator_ComputeSkinning_UsesComputeBlendshapePathToo()
    {
        XRMesh mesh = CreateGeneratedContractMesh();

        bool previousComputeSkinning = Engine.Rendering.Settings.CalculateSkinningInComputeShader;
        bool previousComputeBlendshapes = Engine.Rendering.Settings.CalculateBlendshapesInComputeShader;
        bool previousAllowSkinning = Engine.Rendering.Settings.AllowSkinning;
        bool previousAllowBlendshapes = Engine.Rendering.Settings.AllowBlendshapes;

        try
        {
            Engine.Rendering.Settings.AllowSkinning = true;
            Engine.Rendering.Settings.AllowBlendshapes = true;
            Engine.Rendering.Settings.CalculateSkinningInComputeShader = true;
            Engine.Rendering.Settings.CalculateBlendshapesInComputeShader = false;

            string source = new DefaultVertexShaderGenerator(mesh).Generate();

            source.ShouldContain("SkinnedPositionsInput");
            source.ShouldNotContain("BlendshapeDeltasBuffer");
            source.ShouldNotContain("BlendshapeCount);");
        }
        finally
        {
            Engine.Rendering.Settings.CalculateSkinningInComputeShader = previousComputeSkinning;
            Engine.Rendering.Settings.CalculateBlendshapesInComputeShader = previousComputeBlendshapes;
            Engine.Rendering.Settings.AllowSkinning = previousAllowSkinning;
            Engine.Rendering.Settings.AllowBlendshapes = previousAllowBlendshapes;
        }
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

    private static XRMesh CreateGeneratedContractMesh()
    {
        Vector3 normal = new(0.0f, 0.0f, 1.0f);
        Vector3 tangent = new(1.0f, 0.0f, 0.0f);

        List<Vertex> vertices =
        [
            new Vertex(new Vector3(0.0f, 0.0f, 0.0f), normal, new Vector2(0.0f, 0.0f), Vector4.One) { Tangent = tangent },
            new Vertex(new Vector3(1.0f, 0.0f, 0.0f), normal, new Vector2(1.0f, 0.0f), Vector4.One) { Tangent = tangent },
            new Vertex(new Vector3(0.0f, 1.0f, 0.0f), normal, new Vector2(0.0f, 1.0f), Vector4.One) { Tangent = tangent },
        ];

        XRMesh mesh = new(vertices, new List<ushort> { 0, 1, 2 })
        {
            BlendshapeNames = ["Smile"],
            UtilizedBones = [(new Transform(), Matrix4x4.Identity)],
        };

        return mesh;
    }

    private sealed class FileSystemRuntimeShaderServices(string shaderBasePath) : IRuntimeShaderServices
    {
        private readonly string _shaderBasePath = shaderBasePath;

        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
            => typeof(T) == typeof(XRShader) ? (T?)(object?)LoadShader(filePath) : default;

        public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
        {
            if (typeof(T) != typeof(XRShader))
                throw new NotSupportedException($"Test shader services only support {nameof(XRShader)} assets, not '{typeof(T)}'.");

            string fullPath = Path.Combine(_shaderBasePath, relativePath);
            return (T)(XRAsset)LoadShader(fullPath);
        }

        public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => Task.FromResult(LoadEngineAsset<T>(priority, bypassJobThread, assetRoot, relativePath));

        public void LogWarning(string message)
        {
        }

        private static XRShader LoadShader(string fullPath)
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Shader file not found for test runtime services.", fullPath);

            string source = File.ReadAllText(fullPath);
            TextFile text = TextFile.FromText(source);
            text.FilePath = fullPath;
            text.Name = Path.GetFileName(fullPath);

            XRShader shader = new(XRShader.ResolveType(Path.GetExtension(fullPath)), text)
            {
                Name = Path.GetFileNameWithoutExtension(fullPath),
            };

            return shader;
        }
    }
}
