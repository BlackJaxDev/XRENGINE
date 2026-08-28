using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class UberSurfaceFeatureContractTests
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new UberRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousServices;

    [Test]
    public void CanonicalShader_ExecutesEverySurfaceFamily()
    {
        string source = ReadShader("UberShader.frag");

        source.ShouldContain("uberSampleMainTexture(mesh)");
        source.ShouldContain("uberResolveThemeColor(_ColorThemeIndex, _Color.rgb)");
        source.ShouldContain("uberResolveThemeColor(_EmissionColorThemeIndex, _EmissionColor.rgb)");
        source.ShouldContain("uberApplyDecals(fragData, mesh)");
        source.ShouldContain("uberApplyColorMask(");
        source.ShouldContain("uberApplyAdvancedPbr(");
        source.ShouldContain("uberApplyMatcapSlots(");
        source.ShouldContain("uberApplyDepthRim(mesh)");
        source.ShouldContain("uberApplyEmissionSlots(fragData, mesh)");
        source.ShouldContain("uberApplyFlipbookArray(fragData, mesh)");
    }

    [Test]
    public void GlobalTheme_ValueAdjustmentUsesSaturatedRange()
    {
        string source = ReadShader("surface_extensions.glsl");

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
            "surface-extensions",
            "global-masks-themes",
            "advanced-stylized-lighting",
            "advanced-pbr",
            "layered-matcap-rim",
            "layered-decals",
            "layered-emission",
            "texture-array-flipbook",
        ];
        foreach (string feature in expectedFeatures)
            manifest.Features.ShouldContain(item => item.Id == feature);

        ShaderUiProperty flipbook = manifest.Properties.Single(property => property.Name == "_FlipbookTexArray");
        flipbook.GlslType.ShouldBe("sampler2DArray");
        flipbook.FeatureId.ShouldBe("texture-array-flipbook");
    }

    [Test]
    public void UberResourceProvisioning_UsesNativeTextureTargets()
    {
        XRShader shader = ShaderHelper.UberFragForward();
        XRMaterial material = new()
        {
            Parameters = ModelAssetImporter.CreateDefaultForwardPlusUberShaderParameters(),
            RenderOptions = ModelAssetImporter.CreateForwardPlusUberShaderRenderOptions(),
        };
        material.Shaders.Clear();
        material.Shaders.Add(shader);
        material.EnsureUberStateInitialized();
        material.SetUberFeatureEnabled("texture-array-flipbook", true);
        material.SetUberFeatureEnabled("advanced-pbr", true);

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
            Parameters = ModelAssetImporter.CreateDefaultForwardPlusUberShaderParameters(),
            RenderOptions = ModelAssetImporter.CreateForwardPlusUberShaderRenderOptions(),
        };
        material.Shaders.Clear();
        material.Shaders.Add(shader);
        material.EnsureUberStateInitialized();

        string[] phaseFeatures =
        [
            "surface-extensions",
            "global-masks-themes",
            "advanced-stylized-lighting",
            "advanced-pbr",
            "layered-matcap-rim",
            "layered-decals",
            "layered-emission",
            "texture-array-flipbook",
        ];
        foreach (string feature in phaseFeatures)
            material.SetUberFeatureEnabled(feature, feature == "layered-decals").ShouldBeTrue();

        material.TryGetUberMaterialState(out XRShader? canonicalShader, out ShaderUiManifest manifest)
            .ShouldBeTrue();
        UberShaderVariantBuilder.PreparedUberVariant prepared =
            UberShaderVariantBuilder.PrepareVariant(material, canonicalShader!, manifest);
        string source = prepared.FragmentShader.Source.Text ?? string.Empty;

        source.ShouldContain("uniform sampler2D _DecalTexture3;");
        source.ShouldNotContain("uniform sampler2D _Emission3Tex;");
        source.ShouldNotContain("uniform sampler2DArray _FlipbookTexArray;");
        source.ShouldNotContain("uniform sampler2D _Matcap3Tex;");
        prepared.BindingState.EnabledFeatures.ShouldContain("layered-decals");
        prepared.BindingState.EnabledFeatures.ShouldNotContain("layered-emission");
    }

    [Test]
    public void TextureImporter_ParsesFlipbookGridMetadata()
    {
        SerializedTextureImportDocument document = SerializedTextureImportDocumentParser.Parse("""
TextureImporter:
  serializedVersion: 13
  textureShape: 4
  flipbookRows: 3
  flipbookColumns: 5
  textureSettings:
    wrapU: 1
    wrapV: 2
""");

        document.Shape.ShouldBe(SerializedTextureShape.Texture2DArray);
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
