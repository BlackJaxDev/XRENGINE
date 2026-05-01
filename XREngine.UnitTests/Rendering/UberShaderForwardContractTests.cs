using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
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
        source.ShouldContain("calculateForwardPlusPointLightPbr");
        source.ShouldContain("calculatePointLightPbr(mesh, normal, shadowNormal, baseColor, rms, pbr.F0, i, PointLights[i])");
        source.ShouldContain("calculateSpotLightPbr(mesh, normal, shadowNormal, baseColor, rms, pbr.F0, i, SpotLights[i])");
        source.ShouldContain("XRENGINE_ReadShadowMapDir");
        source.ShouldContain("int getForwardPlusVisibleLightBaseIndex()");
        source.ShouldContain("ivec2 tileCoord = ivec2(floor(gl_FragCoord.xy - ScreenOrigin)) / ForwardPlusTileSize;");
        source.ShouldContain("tileCoord = clamp(tileCoord, ivec2(0), ivec2(tileCountX - 1, tileCountY - 1));");
        source.ShouldContain("XRENGINE_GetForwardViewIndex() * (tileCountX * tileCountY)");
        source.ShouldNotContain("ivec2 tileCoord = ivec2(gl_FragCoord.xy) / ForwardPlusTileSize;");

        uniforms.ShouldContain("uniform float RenderTime;");
        uniforms.ShouldContain("#define u_Time RenderTime");
        uniforms.ShouldNotContain("uniform float Time;");
        uniforms.ShouldNotContain("uniform float ScreenWidth;");
        uniforms.ShouldNotContain("uniform float ScreenHeight;");
    }

    [Test]
    public void UberShaderFragment_LocalShadowVisibilityUsesGeometricReceiverNormal()
    {
        string source = LoadShaderSource(Path.Combine("Uber", "UberShader.frag"));

        source.ShouldContain("vec3 shadowNormal = mesh.vertexNormal;");
        source.ShouldContain("XRENGINE_ReadShadowMapPoint(lightIndex, light, shadowNormal, mesh.worldPos)");
        source.ShouldContain("XRENGINE_ReadShadowMapSpot(lightIndex, light, shadowNormal, mesh.worldPos, lightDir)");
        source.ShouldContain("XRENGINE_ReadShadowMapPoint(sourceIndex, PointLights[sourceIndex], mesh.vertexNormal, mesh.worldPos)");
        source.ShouldContain("XRENGINE_ReadShadowMapSpot(sourceIndex, SpotLights[sourceIndex], mesh.vertexNormal, mesh.worldPos, lightToPosN)");
        source.ShouldNotContain("XRENGINE_ReadShadowMapPoint(lightIndex, light, normal, mesh.worldPos)");
        source.ShouldNotContain("XRENGINE_ReadShadowMapSpot(lightIndex, light, normal, mesh.worldPos, lightDir)");
    }

    [Test]
    public void UberShaderFragment_StylizedModesDriveAllDirectLights()
    {
        string source = LoadShaderSource(Path.Combine("Uber", "UberShader.frag"));

        source.ShouldContain("vec3 calculateStylizedAdditionalLighting(ToonMesh mesh, vec3 baseColor, vec3 normal)");
        source.ShouldContain("fragData.finalColor += calculateStylizedAdditionalLighting(mesh, fragData.baseColor, mesh.worldNormal);");
        source.ShouldNotContain("fragData.finalColor += calculateForwardDirectLighting(mesh, fragData.baseColor, mesh.worldNormal, surfacePbr, true);");
        source.ShouldContain("case 5: // Flat");
        source.ShouldContain("finalLight = light.color;");
    }

    [Test]
    public void UberShaderFragment_StylizedPrimaryLighting_AppliesEngineShadowsAndAmbientSeparately()
    {
        string source = LoadShaderSource(Path.Combine("Uber", "UberShader.frag"));

        source.ShouldContain("float combinedShadow = saturate(shadow * shadowMapFactor);");
        source.ShouldContain("float directVisibility = mix(1.0, saturate(shadowMapFactor), _ShadowStrength);");
        source.ShouldContain("vec3 result = baseColor * finalLight * directVisibility * shadowTint + light.indirectColor;");
        source.ShouldContain("finalLight = mix(light.color * shadowColor, light.color, shadow);");
        source.ShouldContain("finalLight = light.color * shadow;");
        source.ShouldNotContain("finalLight = mix(shadowColor * light.indirectColor, light.color, shadow);");
        source.ShouldNotContain("finalLight = light.color * shadow + light.indirectColor;");
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

        // The raw canonical shader is a safe fallback before per-material
        // variant adoption. The variant builder strips these and reinjects the
        // authored feature mask.
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_STYLIZED_SHADING 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_DETAIL_TEXTURES 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_DISSOLVE 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_PARALLAX 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_SUBSURFACE 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_EMISSION 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_MATCAP 1");
        source.ShouldContain("#define XRENGINE_UBER_DISABLE_RIM_LIGHTING 1");
        source.ShouldNotContain("UBER_IMPORT");

        // Runtime feature-toggle uniforms must not reappear: feature gating
        // is compile-time only.
        source.ShouldNotContain("_EnableEmission");
        source.ShouldNotContain("_MatcapEnable");
        source.ShouldNotContain("_EnableRimLighting");
        source.ShouldNotContain("_EnableParallax");
        source.ShouldNotContain("_EnableDissolve");
        source.ShouldNotContain("_DetailEnabled");
        source.ShouldNotContain("_EnableBackFace");
        source.ShouldNotContain("_EnableSSS");
        source.ShouldNotContain("_EnableGlitter");
        source.ShouldNotContain("_EnableFlipbook");
        source.ShouldNotContain("_ShadingEnabled");
        source.ShouldNotContain("_MainColorAdjustToggle");

        source.ShouldNotContain("#include \"pbr.glsl\"");
        source.ShouldContain("struct PBRData {");

        uniforms.ShouldContain("#ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING");
        uniforms.ShouldContain("#ifndef XRENGINE_UBER_DISABLE_ADVANCED_SPECULAR");
        uniforms.ShouldContain("#ifndef XRENGINE_UBER_DISABLE_DETAIL_TEXTURES");
        uniforms.ShouldContain("#ifndef XRENGINE_UBER_DISABLE_DISSOLVE");
        uniforms.ShouldContain("#ifndef XRENGINE_UBER_DISABLE_PARALLAX");
        uniforms.ShouldNotContain("uniform float _EnableEmission;");
        uniforms.ShouldNotContain("uniform float _MatcapEnable;");
        uniforms.ShouldNotContain("uniform float _ShadingEnabled;");
    }

    [Test]
    public void UberTransparentVariant_NormalizesToCanonicalForwardShader()
    {
        XRShader weighted = ShaderHelper.GetWeightedBlendedOitForwardVariant(ShaderHelper.UberFragForward())
            ?? throw new InvalidOperationException("Weighted Uber variant was null.");

        XRShader normalized = ShaderHelper.GetStandardForwardVariant(weighted)
            ?? throw new InvalidOperationException("Normalized Uber variant was null.");

        string source = normalized.Source.Text ?? throw new InvalidOperationException("Normalized shader source text was null.");
        source.ShouldContain("#define XRENGINE_UBER_MVP_FRAGMENT 1");
        source.ShouldNotContain("#define XRENGINE_FORWARD_WEIGHTED_OIT");
    }

    [Test]
    public void UberWeightedOitVariant_UsesTransparentForwardOutputContract()
    {
        XRShader variant = ShaderHelper.GetWeightedBlendedOitForwardVariant(ShaderHelper.UberFragForward())
            ?? throw new InvalidOperationException("Weighted Uber variant was null.");

        string source = variant.Source.Text ?? throw new InvalidOperationException("Variant shader source text was null.");
        source.ShouldContain("#define XRENGINE_FORWARD_WEIGHTED_OIT");
        source.ShouldContain("layout(location = 0) out vec4 OutAccum;");
        source.ShouldContain("layout(location = 1) out vec4 OutRevealage;");
    }

    [Test]
    public void UberFragmentVariant_CompilesOnOpenGl()
    {
        RunWithGLContext(gl =>
        {
            XRShader fragment = ShaderHelper.UberFragForward();
            string resolvedSource = fragment.GetResolvedSource();

            uint shader = CompileShader(gl, ShaderType.FragmentShader, resolvedSource);
            gl.DeleteShader(shader);
        });
    }

    [Test]
    public void ForwardLighting_UsesStorageBuffersForForwardLightArrays()
    {
        string forwardLighting = LoadShaderSource(Path.Combine("Snippets", "ForwardLighting.glsl"));

        forwardLighting.ShouldContain("layout(std430, binding = 22) readonly buffer ForwardDirectionalLightsBuffer");
        forwardLighting.ShouldContain("layout(std430, binding = 23) readonly buffer ForwardPointLightsBuffer");
        forwardLighting.ShouldContain("layout(std430, binding = 26) readonly buffer ForwardSpotLightsBuffer");
        forwardLighting.ShouldContain("layout(std430, binding = 27) readonly buffer ForwardPointShadowMetadataBuffer");
        forwardLighting.ShouldContain("layout(std430, binding = 28) readonly buffer ForwardSpotShadowMetadataBuffer");
        forwardLighting.ShouldNotContain("uniform DirLight DirectionalLights");
        forwardLighting.ShouldNotContain("uniform PointLight PointLights");
        forwardLighting.ShouldNotContain("uniform SpotLight SpotLights");
        forwardLighting.ShouldNotContain("XRENGINE_MAX_FORWARD_LOCAL_LIGHTS");
    }

    [Test]
    public void ForwardLightingUpload_OwnsContactShadowCameraMatrices()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs");

        source.ShouldContain("SetForwardLightingCameraUniforms(program);");
        source.ShouldContain("EEngineUniform.ViewMatrix");
        source.ShouldContain("EEngineUniform.InverseViewMatrix");
        source.ShouldContain("EEngineUniform.InverseProjMatrix");
        source.ShouldContain("EEngineUniform.ProjMatrix");
        source.ShouldContain("EEngineUniform.ViewProjectionMatrix");
        source.ShouldContain("EEngineUniform.DepthMode");
        source.ShouldContain("camera.ViewProjectionMatrixUnjittered");
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
    public void UberFragment_ConsumesGeneratedVertexContracts()
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
    public void UberStaticVertexVariants_EmitFragmentInputContracts()
    {
        foreach (string shaderPath in new[]
        {
            Path.Combine("Uber", "UberShader.vert"),
            Path.Combine("Uber", "UberShader_OVR.vert"),
            Path.Combine("Uber", "UberShader_NV.vert"),
        })
        {
            string source = LoadShaderSource(shaderPath);

            source.ShouldContain("layout(location = 0) out vec3 FragPos;");
            source.ShouldContain("layout(location = 1) out vec3 FragNorm;");
            source.ShouldContain("layout(location = 4) out vec2 FragUV0;");
            source.ShouldContain("layout(location = 12) out vec4 FragColor0;");
            source.ShouldContain("layout(location = 20) out vec3 FragPosLocal;");
        }
    }

    [Test]
    public void DefaultVertexShaderGenerator_ComputeBlendshapesOnly_StillEmitsBoneSkinning()
    {
        XRMesh mesh = CreateGeneratedContractMesh();

        bool previousComputeSkinning = GetRuntimeRenderingBool("CalculateSkinningInComputeShader");
        bool previousComputeBlendshapes = GetRuntimeRenderingBool("CalculateBlendshapesInComputeShader");
        bool previousAllowSkinning = GetRuntimeRenderingBool("AllowSkinning");
        bool previousAllowBlendshapes = GetRuntimeRenderingBool("AllowBlendshapes");

        try
        {
            SetRuntimeRenderingBool("AllowSkinning", true);
            SetRuntimeRenderingBool("AllowBlendshapes", true);
            SetRuntimeRenderingBool("CalculateSkinningInComputeShader", false);
            SetRuntimeRenderingBool("CalculateBlendshapesInComputeShader", true);

            string source = new DefaultVertexShaderGenerator(mesh).Generate();

            source.ShouldContain("SkinnedPositionsInput");
            source.ShouldContain("BoneMatricesBuffer");
            source.ShouldContain("BasePosition = SkinnedPositions[gl_VertexID].xyz;");
            source.ShouldContain("FinalPosition += (boneMatrix * vec4(BasePosition, 1.0f)) * weight;");
        }
        finally
        {
            SetRuntimeRenderingBool("CalculateSkinningInComputeShader", previousComputeSkinning);
            SetRuntimeRenderingBool("CalculateBlendshapesInComputeShader", previousComputeBlendshapes);
            SetRuntimeRenderingBool("AllowSkinning", previousAllowSkinning);
            SetRuntimeRenderingBool("AllowBlendshapes", previousAllowBlendshapes);
        }
    }

    [Test]
    public void DefaultVertexShaderGenerator_ComputeSkinning_UsesComputeBlendshapePathToo()
    {
        XRMesh mesh = CreateGeneratedContractMesh();

        bool previousComputeSkinning = GetRuntimeRenderingBool("CalculateSkinningInComputeShader");
        bool previousComputeBlendshapes = GetRuntimeRenderingBool("CalculateBlendshapesInComputeShader");
        bool previousAllowSkinning = GetRuntimeRenderingBool("AllowSkinning");
        bool previousAllowBlendshapes = GetRuntimeRenderingBool("AllowBlendshapes");

        try
        {
            SetRuntimeRenderingBool("AllowSkinning", true);
            SetRuntimeRenderingBool("AllowBlendshapes", true);
            SetRuntimeRenderingBool("CalculateSkinningInComputeShader", true);
            SetRuntimeRenderingBool("CalculateBlendshapesInComputeShader", false);

            string source = new DefaultVertexShaderGenerator(mesh).Generate();

            source.ShouldContain("SkinnedPositionsInput");
            source.ShouldNotContain("BlendshapeDeltasBuffer");
            source.ShouldNotContain("BlendshapeCount);");
        }
        finally
        {
            SetRuntimeRenderingBool("CalculateSkinningInComputeShader", previousComputeSkinning);
            SetRuntimeRenderingBool("CalculateBlendshapesInComputeShader", previousComputeBlendshapes);
            SetRuntimeRenderingBool("AllowSkinning", previousAllowSkinning);
            SetRuntimeRenderingBool("AllowBlendshapes", previousAllowBlendshapes);
        }
    }

    private static bool GetRuntimeRenderingBool(string propertyName)
        => (bool)GetRuntimeRenderingSettingsProperty(propertyName).GetValue(GetRuntimeRenderingSettings())!;

    private static void SetRuntimeRenderingBool(string propertyName, bool value)
        => GetRuntimeRenderingSettingsProperty(propertyName).SetValue(GetRuntimeRenderingSettings(), value);

    private static PropertyInfo GetRuntimeRenderingSettingsProperty(string propertyName)
    {
        object settings = GetRuntimeRenderingSettings();
        return settings.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Runtime render setting '{propertyName}' was not found.");
    }

    private static object GetRuntimeRenderingSettings()
    {
        Type engineType = typeof(DefaultVertexShaderGenerator).Assembly.GetType("XREngine.Engine", throwOnError: true)!;
        Type renderingType = engineType.GetNestedType("Rendering", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Runtime rendering facade was not found.");
        return renderingType.GetProperty("Settings", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException("Runtime render settings were not found.");
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

    private static string ReadWorkspaceFile(string relativePath)
    {
        string workspaceRoot = Path.GetFullPath(Path.Combine(ResolveShaderRoot(), "..", "..", ".."));
        string normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.Combine(workspaceRoot, normalizedRelativePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Workspace file not found: {fullPath}", fullPath);

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
