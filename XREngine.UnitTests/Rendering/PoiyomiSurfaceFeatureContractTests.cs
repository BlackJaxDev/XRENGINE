using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PoiyomiSurfaceFeatureContractTests
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new PoiyomiRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousServices;

    [Test]
    public void CanonicalShader_ExecutesEverySurfaceFamily()
    {
        string source = ReadShader("UberShader.frag");

        source.ShouldContain("poiSampleMainTexture(mesh)");
        source.ShouldContain("poiResolveThemeColor(_ColorThemeIndex, _Color.rgb)");
        source.ShouldContain("poiResolveThemeColor(_EmissionColorThemeIndex, _EmissionColor.rgb)");
        source.ShouldContain("poiApplyDecals(fragData, mesh)");
        source.ShouldContain("poiApplyColorMask(");
        source.ShouldContain("poiApplyPbrParity(");
        source.ShouldContain("poiApplyMatcapSlots(");
        source.ShouldContain("poiApplyDepthRim(mesh)");
        source.ShouldContain("poiApplyEmissionSlots(fragData, mesh)");
        source.ShouldContain("poiApplyFlipbookArray(fragData, mesh)");
    }

    [Test]
    public void GlobalTheme_ValueAdjustmentMatchesPoiyomiClamping()
    {
        string source = ReadShader("poiyomi_surface_features.glsl");

        source.ShouldContain("hsv.z = saturate(hsv.z + adjustment.z);");
        source.ShouldNotContain("hsv.z = max(0.0, hsv.z + adjustment.z);");
    }

    [Test]
    public void SurfaceManifest_DeclaresNativeArrayAndDceFeatureFamilies()
    {
        XRShader shader = ShaderHelper.UberFragForward();
        ShaderUiManifest manifest = shader.GetUiManifest();

        string[] expectedFeatures =
        [
            "poiyomi-surface",
            "poiyomi-masks-themes",
            "poiyomi-lighting-parity",
            "poiyomi-pbr-parity",
            "poiyomi-matcap-rim-slots",
            "poiyomi-decals",
            "poiyomi-emission-slots",
            "poiyomi-flipbook-array",
        ];
        foreach (string feature in expectedFeatures)
            manifest.Features.ShouldContain(item => item.Id == feature);

        ShaderUiProperty flipbook = manifest.Properties.Single(property => property.Name == "_FlipbookTexArray");
        flipbook.GlslType.ShouldBe("sampler2DArray");
        flipbook.FeatureId.ShouldBe("poiyomi-flipbook-array");
    }

    [Test]
    public void UberResourceProvisioning_UsesNativeTextureTargets()
    {
        XRShader shader = ShaderHelper.UberFragForward();
        XRMaterial material = new()
        {
            Parameters = ModelImporter.CreateDefaultForwardPlusUberShaderParameters(),
            RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions(),
        };
        material.Shaders.Clear();
        material.Shaders.Add(shader);
        material.EnsureUberStateInitialized();
        material.SetUberFeatureEnabled("poiyomi-flipbook-array", true);
        material.SetUberFeatureEnabled("poiyomi-pbr-parity", true);

        material.Textures.Single(texture => texture?.SamplerName == "_FlipbookTexArray")
            .ShouldBeOfType<XRTexture2DArray>();
        material.Textures.Single(texture => texture?.SamplerName == "_CubeMap")
            .ShouldBeOfType<XRTextureCube>();
    }

    [Test]
    public void RepeatedFamilies_AreIndependentlySpecializedOut()
    {
        XRShader shader = ShaderHelper.UberFragForward();
        XRMaterial material = new()
        {
            Parameters = ModelImporter.CreateDefaultForwardPlusUberShaderParameters(),
            RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions(),
        };
        material.Shaders.Clear();
        material.Shaders.Add(shader);
        material.EnsureUberStateInitialized();

        string[] phaseFeatures =
        [
            "poiyomi-surface",
            "poiyomi-masks-themes",
            "poiyomi-lighting-parity",
            "poiyomi-pbr-parity",
            "poiyomi-matcap-rim-slots",
            "poiyomi-decals",
            "poiyomi-emission-slots",
            "poiyomi-flipbook-array",
        ];
        foreach (string feature in phaseFeatures)
            material.SetUberFeatureEnabled(feature, feature == "poiyomi-decals").ShouldBeTrue();

        material.TryGetUberMaterialState(out XRShader? canonicalShader, out ShaderUiManifest manifest)
            .ShouldBeTrue();
        UberShaderVariantBuilder.PreparedUberVariant prepared =
            UberShaderVariantBuilder.PrepareVariant(material, canonicalShader!, manifest);
        string source = prepared.FragmentShader.Source.Text ?? string.Empty;

        source.ShouldContain("uniform sampler2D _DecalTexture3;");
        source.ShouldNotContain("uniform sampler2D _Emission3Tex;");
        source.ShouldNotContain("uniform sampler2DArray _FlipbookTexArray;");
        source.ShouldNotContain("uniform sampler2D _Matcap3Tex;");
        prepared.BindingState.EnabledFeatures.ShouldContain("poiyomi-decals");
        prepared.BindingState.EnabledFeatures.ShouldNotContain("poiyomi-emission-slots");
    }

    [Test]
    public void TextureImporter_ParsesFlipbookGridMetadata()
    {
        UnityTextureImportDocument document = UnityTextureImportDocumentParser.Parse("""
TextureImporter:
  serializedVersion: 13
  textureShape: 4
  flipbookRows: 3
  flipbookColumns: 5
  textureSettings:
    wrapU: 1
    wrapV: 2
""");

        document.Shape.ShouldBe(UnityTextureShape.Texture2DArray);
        document.FlipbookRows.ShouldBe(3);
        document.FlipbookColumns.ShouldBe(5);
    }

    private static string ReadShader(string fileName)
    {
        string root = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(root, "XRENGINE.slnx")))
            root = Directory.GetParent(root)?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate repository root.");

        return File.ReadAllText(Path.Combine(root, "Build", "CommonAssets", "Shaders", "Uber", fileName)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
