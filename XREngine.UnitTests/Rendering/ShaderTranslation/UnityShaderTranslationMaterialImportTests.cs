using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene.Importers;
using XREngine.ShaderTranslation.Compilation;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

/// <summary>
/// Imports a Unity material through the material importer with shader translation enabled.
/// </summary>
/// <remarks>
/// The fixture sets the process environment variable that enables the translation, so it must not run in parallel.
/// The test is ignored when <c>slangc</c> or <c>spirv-cross</c> is not installed.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class UnityShaderTranslationMaterialImportTests
{
    private const string ShaderGuid = "71111111111111111111111111111111";
    private const string TextureGuid = "72222222222222222222222222222222";

    private IRuntimeRenderingHostServices? _previousRenderingServices;
    private string? _previousEnableValue;

    [SetUp]
    public void SetUp()
    {
        _previousRenderingServices = RuntimeRenderingHostServices.Current;
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
        _previousEnableValue = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnityShaderTranslation);
        Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnityShaderTranslation, "1");
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.UnityShaderTranslation, _previousEnableValue);
        RuntimeRenderingHostServices.Current = _previousRenderingServices!;
    }

    [Test]
    public void ImportWithReport_TranslatedShader_BindsStagesStateAndProperties()
    {
        if (SlangSpirvCompiler.FindDefault() is null || SpirvCrossGlslEmitter.FindDefault() is null)
            Assert.Ignore("slangc and spirv-cross are not installed (set XRE_SLANGC and XRE_SPIRV_CROSS, or install the Vulkan SDK).");

        string projectRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"translated-project-{Guid.NewGuid():N}");
        string shaderPath = Path.Combine(projectRoot, "Assets", "Shaders", "TranslatedMaterial.shader");
        string materialPath = Path.Combine(projectRoot, "Assets", "Materials", "Translated.mat");
        WriteAsset(shaderPath, ShaderSource, ShaderGuid);
        Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
        File.WriteAllText(materialPath, MaterialSource);

        SerializedMaterialImportResult result = SerializedMaterialImporter.ImportWithReport(materialPath, projectRoot);

        XRMaterial material = result.Material.ShouldNotBeNull();
        result.Warnings.ShouldNotContain(warning => warning.Contains("did not translate", StringComparison.Ordinal));
        material.GetShader(EShaderType.Vertex).ShouldNotBeNull();
        material.GetShader(EShaderType.Fragment).ShouldNotBeNull();

        material.RenderPass.ShouldBe((int)EDefaultRenderPass.TransparentForward);
        material.TransparencyMode.ShouldBe(ETransparencyMode.AlphaBlend);
        material.RenderOptions.CullMode.ShouldBe(ECullMode.None);
        material.RenderOptions.DepthTest.UpdateDepth.ShouldBeFalse();
        BlendMode blend = material.RenderOptions.BlendModeAllDrawBuffers.ShouldNotBeNull();
        blend.Enabled.ShouldBe(ERenderParamUsage.Enabled);
        blend.RgbSrcFactor.ShouldBe(EBlendingFactor.SrcAlpha);
        blend.RgbDstFactor.ShouldBe(EBlendingFactor.OneMinusSrcAlpha);

        // A project with no ProjectSettings is linear, so the color changes from sRGB to linear.
        AssertVector4(material, "_Color", new Vector4(0.2140411f, 0.2140411f, 0.2140411f, 0.8f));
        AssertVector4(material, "_MainTex_ST", new Vector4(2.0f, 3.0f, 0.5f, 0.25f));
        material.Parameter<ShaderFloat>("_Strength").ShouldNotBeNull().Value.ShouldBe(1.5f, 0.0001f);

        // The texture GUID has no asset, so _MainTex uses its default texture. All samplers are bound by name.
        // _GlobalTex is not a material texture, so it gets the black texture on every backend.
        string[] samplers = [.. material.Textures.OfType<XRTexture2D>().Select(texture => texture.SamplerName ?? string.Empty)];
        samplers.ShouldContain("_MainTex");
        samplers.ShouldContain("_MaskTex");
        samplers.ShouldContain("_GlobalTex");
        result.Warnings.ShouldContain(warning => warning.Contains("'_GlobalTex'", StringComparison.Ordinal) && warning.Contains("black", StringComparison.Ordinal));
    }

    private static void WriteAsset(string path, string content, string guid)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.WriteAllText($"{path}.meta", $"fileFormatVersion: 2{Environment.NewLine}guid: {guid}{Environment.NewLine}");
    }

    private static void AssertVector4(XRMaterial material, string name, Vector4 expected)
    {
        ShaderVector4 parameter = material.Parameter<ShaderVector4>(name).ShouldNotBeNull();
        parameter.Value.X.ShouldBe(expected.X, 0.0001f);
        parameter.Value.Y.ShouldBe(expected.Y, 0.0001f);
        parameter.Value.Z.ShouldBe(expected.Z, 0.0001f);
        parameter.Value.W.ShouldBe(expected.W, 0.0001f);
    }

    private const string ShaderSource = """
        Shader "XRTests/Translated Material"
        {
            Properties
            {
                _MainTex ("Texture", 2D) = "white" {}
                _MaskTex ("Mask", 2D) = "black" {}
                _Color ("Color", Color) = (1, 1, 1, 1)
                _Strength ("Strength", Range(0, 2)) = 1
                [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
            }

            SubShader
            {
                Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
                Cull [_Cull]
                ZWrite Off
                Blend SrcAlpha OneMinusSrcAlpha

                Pass
                {
                    CGPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
                    #include "UnityCG.cginc"

                    sampler2D _MainTex;
                    float4 _MainTex_ST;
                    sampler2D _MaskTex;
                    sampler2D _GlobalTex;
                    float4 _Color;
                    float _Strength;

                    struct v2f
                    {
                        float4 pos : SV_POSITION;
                        float2 uv : TEXCOORD0;
                    };

                    v2f vert(appdata_base v)
                    {
                        v2f o;
                        o.pos = UnityObjectToClipPos(v.vertex);
                        o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                        return o;
                    }

                    float4 frag(v2f i) : SV_Target
                    {
                        return tex2D(_MainTex, i.uv) * tex2D(_MaskTex, i.uv).r * tex2D(_GlobalTex, i.uv).r * _Color * _Strength;
                    }
                    ENDCG
                }
            }
        }
        """;

    private const string MaterialSource = $$"""
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!21 &2100000
        Material:
          serializedVersion: 8
          m_Name: Translated
          m_Shader: {fileID: 4800000, guid: {{ShaderGuid}}, type: 3}
          m_CustomRenderQueue: -1
          m_SavedProperties:
            serializedVersion: 3
            m_TexEnvs:
            - _MainTex:
                m_Texture: {fileID: 2800000, guid: {{TextureGuid}}, type: 3}
                m_Scale: {x: 2, y: 3}
                m_Offset: {x: 0.5, y: 0.25}
            - _MaskTex:
                m_Texture: {fileID: 0}
                m_Scale: {x: 1, y: 1}
                m_Offset: {x: 0, y: 0}
            m_Floats:
            - _Cull: 0
            - _Strength: 1.5
            m_Colors:
            - _Color: {r: 0.5, g: 0.5, b: 0.5, a: 0.8}
        """;
}
