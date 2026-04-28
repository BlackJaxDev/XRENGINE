using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UnityPoiyomiMaterialImporterTests
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new FileBackedRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousServices;
    }

    [Test]
    public void ImportWithReport_PoiyomiToon93_MapsMaterialToUberShader()
    {
        string projectRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"poiyomi-project-{Guid.NewGuid():N}");
        string assetsRoot = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assetsRoot);

        string shaderPath = Path.Combine(assetsRoot, "_PoiyomiShaders", "Shaders", "9.3", "Toon", "Poiyomi Toon.shader");
        string materialPath = Path.Combine(assetsRoot, "Materials", "Avatar.mat");
        string mainTexPath = CreateUnityAsset(projectRoot, "Assets/Textures/body.png", "11111111111111111111111111111111");
        string normalPath = CreateUnityAsset(projectRoot, "Assets/Textures/body_normal.png", "22222222222222222222222222222222");
        string alphaPath = CreateUnityAsset(projectRoot, "Assets/Textures/body_alpha.png", "33333333333333333333333333333333");
        string emissionPath = CreateUnityAsset(projectRoot, "Assets/Textures/body_emission.png", "44444444444444444444444444444444");
        string matcapPath = CreateUnityAsset(projectRoot, "Assets/Textures/body_matcap.png", "55555555555555555555555555555555");

        Directory.CreateDirectory(Path.GetDirectoryName(shaderPath)!);
        File.WriteAllText(shaderPath, "Shader \".poiyomi/Poiyomi Toon\" { // Poiyomi 9.3 }\n");
        WriteUnityMeta(shaderPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
        File.WriteAllText(materialPath, """
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 8
  m_Name: AvatarBody
  m_Shader: {fileID: 4800000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}
  m_CustomRenderQueue: -1
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs:
    - _MainTex:
        m_Texture: {fileID: 2800000, guid: 11111111111111111111111111111111, type: 3}
        m_Scale: {x: 2, y: 3}
        m_Offset: {x: 0.25, y: 0.5}
    - _BumpMap:
        m_Texture: {fileID: 2800000, guid: 22222222222222222222222222222222, type: 3}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    - _AlphaMask:
        m_Texture: {fileID: 2800000, guid: 33333333333333333333333333333333, type: 3}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    - _EmissionMap:
        m_Texture: {fileID: 2800000, guid: 44444444444444444444444444444444, type: 3}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    - _Matcap:
        m_Texture: {fileID: 2800000, guid: 55555555555555555555555555555555, type: 3}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    m_Ints:
    - _MainTexUV: 1
    - _LightingMode: 2
    m_Floats:
    - _BumpScale: 0.75
    - _MainAlphaMaskMode: 2
    - _AlphaMaskBlendStrength: 0.9
    - _Mode: 1
    - _Cutoff: 0.42
    - _Cull: 0
    - _EnableEmission: 1
    - _EmissionStrength: 2.5
    - _MatcapEnable: 1
    - _MatcapIntensity: 0.6
    - _EnableRimLighting: 1
    - _RimWidth: 0.33
    - _GlitterEnable: 1
    - _SubsurfaceScattering: 1
    - _EnableDissolve: 1
    - _ParallaxInternalMaxDepth: 0.04
    m_Colors:
    - _Color: {r: 0.1, g: 0.2, b: 0.3, a: 0.4}
    - _EmissionColor: {r: 1, g: 0.5, b: 0.25, a: 1}
""");

        UnityMaterialImportResult result = UnityMaterialImporter.ImportWithReport(materialPath, projectRoot);

        result.IsPoiyomiToon.ShouldBeTrue();
        result.ShaderPath.ShouldBe(shaderPath);
        result.Warnings.ShouldBeEmpty();

        XRMaterial material = result.Material.ShouldNotBeNull();
        material.Name.ShouldBe("AvatarBody");
        Path.GetFileName(material.FragmentShaders.Single().Source?.FilePath ?? material.FragmentShaders.Single().FilePath)
            .ShouldBe("UberShader.frag");

        GetTexture(material, "_MainTex").FilePath.ShouldBe(mainTexPath);
        GetTexture(material, "_BumpMap").FilePath.ShouldBe(normalPath);
        GetTexture(material, "_AlphaMask").FilePath.ShouldBe(alphaPath);
        GetTexture(material, "_EmissionMap").FilePath.ShouldBe(emissionPath);
        GetTexture(material, "_Matcap").FilePath.ShouldBe(matcapPath);

        AssertVector4(material, "_Color", new Vector4(0.1f, 0.2f, 0.3f, 0.4f));
        AssertVector4(material, "_MainTex_ST", new Vector4(2.0f, 3.0f, 0.25f, 0.5f));
        material.Parameter<ShaderFloat>("_BumpScale")?.Value.ShouldBe(0.75f, 0.0001f);
        material.Parameter<ShaderInt>("_MainTexUV")?.Value.ShouldBe(1);
        material.Parameter<ShaderInt>("_MainAlphaMaskMode")?.Value.ShouldBe(2);
        material.Parameter<ShaderFloat>("_EmissionStrength")?.Value.ShouldBe(2.5f, 0.0001f);
        material.Parameter<ShaderFloat>("_RimWidth")?.Value.ShouldBe(0.33f, 0.0001f);

        material.TransparencyMode.ShouldBe(ETransparencyMode.Masked);
        material.RenderPass.ShouldBe((int)EDefaultRenderPass.MaskedForward);
        material.RenderOptions.CullMode.ShouldBe(ECullMode.None);
        material.AlphaCutoff.ShouldBe(0.42f, 0.0001f);
        material.Parameter<ShaderInt>("_Mode")?.Value.ShouldBe(1);
        material.Parameter<ShaderFloat>("_AlphaForceOpaque")?.Value.ShouldBe(0.0f, 0.0001f);

        AssertUberFeature(material, "stylized-shading");
        AssertUberFeature(material, "alpha-masks");
        AssertUberFeature(material, "emission");
        AssertUberFeature(material, "matcap");
        AssertUberFeature(material, "rim-lighting");
        AssertUberFeature(material, "glitter");
        AssertUberFeature(material, "subsurface");
        AssertUberFeature(material, "dissolve");
        AssertUberFeature(material, "parallax");
    }

    [Test]
    public void ImportWithReport_LilToonCutout_MapsMaterialToUberShader()
    {
        string projectRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"liltoon-project-{Guid.NewGuid():N}");
        string assetsRoot = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assetsRoot);

        string shaderPath = Path.Combine(assetsRoot, "lilToon", "Shader", "lts_cutout.shader");
        string materialPath = Path.Combine(assetsRoot, "Materials", "LilAvatar.mat");
        string mainTexPath = CreateUnityAsset(projectRoot, "Assets/Textures/lil_body.png", "61111111111111111111111111111111");
        string normalPath = CreateUnityAsset(projectRoot, "Assets/Textures/lil_body_normal.png", "62222222222222222222222222222222");
        string alphaPath = CreateUnityAsset(projectRoot, "Assets/Textures/lil_body_alpha.png", "63333333333333333333333333333333");
        string emissionPath = CreateUnityAsset(projectRoot, "Assets/Textures/lil_body_emission.png", "64444444444444444444444444444444");
        string matcapPath = CreateUnityAsset(projectRoot, "Assets/Textures/lil_body_matcap.png", "65555555555555555555555555555555");
        string rimPath = CreateUnityAsset(projectRoot, "Assets/Textures/lil_body_rim.png", "66666666666666666666666666666666");
        string shadowPath = CreateUnityAsset(projectRoot, "Assets/Textures/lil_body_shadow.png", "67777777777777777777777777777777");
        string parallaxPath = CreateUnityAsset(projectRoot, "Assets/Textures/lil_body_height.png", "68888888888888888888888888888888");

        Directory.CreateDirectory(Path.GetDirectoryName(shaderPath)!);
        File.WriteAllText(shaderPath, "Shader \"Hidden/lilToonCutout\" { _lilToonVersion (\"Version\", Int) = 45 }\n");
        WriteUnityMeta(shaderPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
        File.WriteAllText(materialPath, """
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 8
  m_Name: LilBody
  m_Shader: {fileID: 4800000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb, type: 3}
  m_CustomRenderQueue: 2450
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs:
    - _MainTex:
        m_Texture: {fileID: 2800000, guid: 61111111111111111111111111111111, type: 3}
        m_Scale: {x: 4, y: 2}
        m_Offset: {x: 0.125, y: 0.25}
    - _BumpMap:
        m_Texture: {fileID: 2800000, guid: 62222222222222222222222222222222, type: 3}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    - _AlphaMask:
        m_Texture: {fileID: 2800000, guid: 63333333333333333333333333333333, type: 3}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    - _EmissionMap:
        m_Texture: {fileID: 2800000, guid: 64444444444444444444444444444444, type: 3}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    - _MatCapTex:
        m_Texture: {fileID: 2800000, guid: 65555555555555555555555555555555, type: 3}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    - _RimColorTex:
        m_Texture: {fileID: 2800000, guid: 66666666666666666666666666666666, type: 3}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    - _ShadowColorTex:
        m_Texture: {fileID: 2800000, guid: 67777777777777777777777777777777, type: 3}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    - _ParallaxMap:
        m_Texture: {fileID: 2800000, guid: 68888888888888888888888888888888, type: 3}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    m_Ints:
    - _lilToonVersion: 45
    - _UseBumpMap: 1
    - _AlphaMaskMode: 2
    - _UseShadow: 1
    - _UseEmission: 1
    - _UseMatCap: 1
    - _UseRim: 1
    - _UseParallax: 1
    m_Floats:
    - _BumpScale: 0.8
    - _AlphaMaskScale: 0.7
    - _Cutoff: 0.37
    - _Cull: 0
    - _ShadowStrength: 0.6
    - _ShadowBorder: 0.42
    - _EmissionBlend: 0.9
    - _MatCapBlend: 0.55
    - _RimBorder: 0.25
    - _Parallax: 0.04
    m_Colors:
    - _Color: {r: 0.7, g: 0.6, b: 0.5, a: 0.8}
    - _EmissionColor: {r: 0.25, g: 0.5, b: 1, a: 1}
    - _MatCapColor: {r: 1, g: 0.8, b: 0.6, a: 1}
    - _RimColor: {r: 0.2, g: 0.3, b: 1, a: 1}
    - _ShadowColor: {r: 0.05, g: 0.06, b: 0.07, a: 1}
    - _MainTexHSVG: {r: 0, g: 1.25, b: 1.15, a: 1}
""");

        UnityMaterialImportResult result = UnityMaterialImporter.ImportWithReport(materialPath, projectRoot);

        result.IsPoiyomiToon.ShouldBeFalse();
        result.IsLilToon.ShouldBeTrue();
        result.ShaderPath.ShouldBe(shaderPath);
        result.Warnings.ShouldBeEmpty();

        XRMaterial material = result.Material.ShouldNotBeNull();
        material.Name.ShouldBe("LilBody");
        Path.GetFileName(material.FragmentShaders.Single().Source?.FilePath ?? material.FragmentShaders.Single().FilePath)
            .ShouldBe("UberShader.frag");

        GetTexture(material, "_MainTex").FilePath.ShouldBe(mainTexPath);
        GetTexture(material, "_BumpMap").FilePath.ShouldBe(normalPath);
        GetTexture(material, "_AlphaMask").FilePath.ShouldBe(alphaPath);
        GetTexture(material, "_EmissionMap").FilePath.ShouldBe(emissionPath);
        GetTexture(material, "_Matcap").FilePath.ShouldBe(matcapPath);
        GetTexture(material, "_RimMask").FilePath.ShouldBe(rimPath);
        GetTexture(material, "_ShadowColorTex").FilePath.ShouldBe(shadowPath);
        GetTexture(material, "_ParallaxMap").FilePath.ShouldBe(parallaxPath);

        AssertVector4(material, "_Color", new Vector4(0.7f, 0.6f, 0.5f, 0.8f));
        AssertVector4(material, "_MainTex_ST", new Vector4(4.0f, 2.0f, 0.125f, 0.25f));
        material.Parameter<ShaderFloat>("_BumpScale")?.Value.ShouldBe(0.8f, 0.0001f);
        material.Parameter<ShaderInt>("_MainAlphaMaskMode")?.Value.ShouldBe(2);
        material.Parameter<ShaderFloat>("_AlphaMaskBlendStrength")?.Value.ShouldBe(0.7f, 0.0001f);
        material.Parameter<ShaderFloat>("_EmissionStrength")?.Value.ShouldBe(0.9f, 0.0001f);
        material.Parameter<ShaderFloat>("_MatcapIntensity")?.Value.ShouldBe(0.55f, 0.0001f);
        material.Parameter<ShaderFloat>("_RimWidth")?.Value.ShouldBe(0.25f, 0.0001f);
        material.Parameter<ShaderFloat>("_ParallaxStrength")?.Value.ShouldBe(0.04f, 0.0001f);
        material.Parameter<ShaderFloat>("_Saturation")?.Value.ShouldBe(0.25f, 0.0001f);
        material.Parameter<ShaderFloat>("_MainBrightness")?.Value.ShouldBe(0.15f, 0.0001f);

        material.TransparencyMode.ShouldBe(ETransparencyMode.Masked);
        material.RenderPass.ShouldBe((int)EDefaultRenderPass.MaskedForward);
        material.RenderOptions.CullMode.ShouldBe(ECullMode.None);
        material.AlphaCutoff.ShouldBe(0.37f, 0.0001f);

        AssertUberFeature(material, "stylized-shading");
        AssertUberFeature(material, "alpha-masks");
        AssertUberFeature(material, "color-adjustments");
        AssertUberFeature(material, "emission");
        AssertUberFeature(material, "matcap");
        AssertUberFeature(material, "rim-lighting");
        AssertUberFeature(material, "parallax");
    }

    private static string CreateUnityAsset(string projectRoot, string relativePath, string guid)
    {
        string path = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        WriteUnityMeta(path, guid);
        return path;
    }

    private static void WriteUnityMeta(string assetPath, string guid)
        => File.WriteAllText($"{assetPath}.meta", $"fileFormatVersion: 2{Environment.NewLine}guid: {guid}{Environment.NewLine}");

    private static XRTexture2D GetTexture(XRMaterial material, string samplerName)
        => material.Textures
            .OfType<XRTexture2D>()
            .Single(texture => string.Equals(texture.SamplerName, samplerName, StringComparison.Ordinal));

    private static void AssertVector4(XRMaterial material, string parameterName, Vector4 expected)
    {
        ShaderVector4 parameter = material.Parameter<ShaderVector4>(parameterName).ShouldNotBeNull();
        parameter.Value.X.ShouldBe(expected.X, 0.0001f);
        parameter.Value.Y.ShouldBe(expected.Y, 0.0001f);
        parameter.Value.Z.ShouldBe(expected.Z, 0.0001f);
        parameter.Value.W.ShouldBe(expected.W, 0.0001f);
    }

    private static void AssertUberFeature(XRMaterial material, string featureId)
    {
        UberMaterialFeatureState feature = material.UberAuthoredState.GetFeature(featureId).ShouldNotBeNull();
        feature.Enabled.ShouldBeTrue(featureId);
        feature.ExplicitlyAuthored.ShouldBeTrue(featureId);
    }

    private sealed class FileBackedRuntimeShaderServices : IRuntimeShaderServices
    {
        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
            => new T();

        public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => CreateEngineAsset<T>(relativePath);

        public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => Task.FromResult(CreateEngineAsset<T>(relativePath));

        public void LogWarning(string message)
        {
        }

        private static T CreateEngineAsset<T>(string relativePath) where T : XRAsset, new()
        {
            if (typeof(T) != typeof(XRShader))
                return new T();

            string fullPath = ResolveWorkspacePath(Path.Combine("Build", "CommonAssets", "Shaders", relativePath));
            TextFile source = new(fullPath)
            {
                Text = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "void main() {}\n",
            };

            XRShader shader = new(XRShader.ResolveType(Path.GetExtension(relativePath)), source)
            {
                FilePath = fullPath,
                Name = Path.GetFileName(relativePath),
            };

            return (T)(XRAsset)shader;
        }

        private static string ResolveWorkspacePath(string relativePath)
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            return relativePath;
        }
    }
}
