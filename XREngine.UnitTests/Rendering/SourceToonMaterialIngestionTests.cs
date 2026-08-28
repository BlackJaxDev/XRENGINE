using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Scene.Importers;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SourceToonMaterialIngestionTests
{
    [Test]
    public void MaterialParser_OldAndNewSourceLayouts_RoundTripLosslessly()
    {
        string oldYaml = File.ReadAllText(GetFixturePath("material-old-unity.yaml"));
        string newYaml = File.ReadAllText(GetFixturePath("material-new-unity.yaml"));

        SerializedMaterialDocument oldDocument = SerializedMaterialDocumentParser.Parse(oldYaml, "legacy.mat");
        SerializedMaterialDocument newDocument = SerializedMaterialDocumentParser.Parse(newYaml, "modern.mat");

        oldDocument.SerializeDiagnosticRoundTrip().ShouldBe(oldYaml);
        oldDocument.SerializedVersion.ShouldBe(6);
        oldDocument.SavedPropertiesSerializedVersion.ShouldBe(2);
        oldDocument.ValidKeywords.ShouldBe(["_ALPHATEST_ON", "_NORMALMAP"], ignoreOrder: true);
        oldDocument.DisabledShaderPasses.ShouldContain("Always");
        oldDocument.OverrideTags["RenderType"].ShouldBe("TransparentCutout");
        oldDocument.Textures["_MainTex"].Scale.ShouldBe(new Vector2(2.0f, 3.0f));
        oldDocument.UnknownSerializedFields.ShouldContainKey("legacyUnknownRoot");
        oldDocument.UnknownSavedProperties.ShouldContainKey("legacyUnknownSaved");

        newDocument.SerializeDiagnosticRoundTrip().ShouldBe(newYaml);
        newDocument.SerializedVersion.ShouldBe(8);
        newDocument.ValidKeywords.ShouldContain("_EMISSION");
        newDocument.InvalidKeywords.ShouldContain("_OBSOLETE_KEYWORD");
        newDocument.DisabledShaderPasses.ShouldContain("ShadowCaster");
        newDocument.Ints["_Mode"].ShouldBe(3);
        newDocument.Strings["_GeneratedShaderSource"].ShouldBe("Assets/Generated/Locked.shader");
        newDocument.OverrideTags["OriginalShaderGUID"].ShouldBe(SourceToon93Catalog.ShaderGuid);
    }

    [Test]
    public void TextureParser_PreservesColorNormalAlphaSamplerMipAndShapeSemantics()
    {
        string yaml = File.ReadAllText(GetFixturePath("texture-import-modern.meta"));

        SerializedTextureImportDocument document = SerializedTextureImportDocumentParser.Parse(yaml);

        document.SerializeDiagnosticRoundTrip().ShouldBe(yaml);
        document.SerializedVersion.ShouldBe(13);
        document.IsSrgb.ShouldBeFalse();
        document.IsNormalMap.ShouldBeTrue();
        document.FlipGreenChannel.ShouldBeTrue();
        document.NormalMapChannel.ShouldBe(1);
        document.AlphaSource.ShouldBe(1);
        document.AlphaIsTransparency.ShouldBeTrue();
        document.WrapU.ShouldBe(2);
        document.WrapV.ShouldBe(1);
        document.WrapW.ShouldBe(0);
        document.FilterMode.ShouldBe(2);
        document.GenerateMipMaps.ShouldBeTrue();
        document.MipBias.ShouldBe(-0.5f);
        document.Anisotropy.ShouldBe(8);
        document.Shape.ShouldBe(SerializedTextureShape.Texture2DArray);
        document.UnknownSerializedFields.ShouldContainKey("legacyUnknownTextureSetting");
    }

    [Test]
    public void Descriptor_LockedAndUnlockedMaterials_NormalizeToEquivalentSemantics()
    {
        string projectRoot = CreateProjectWithArrayTexture();
        const string textureGuid = "50000000000000000000000000000005";
        const string generatedShaderGuid = "60000000000000000000000000000006";

        SerializedMaterialDocument unlocked = SerializedMaterialDocumentParser.Parse(
            CreateMaterialYaml(
                SourceToon93Catalog.ShaderGuid,
                textureGuid,
                "_MainTex",
                "_RimWidth",
                "_Color",
                "_GeneratedShaderSource",
                string.Empty));
        SerializedMaterialDocument locked = SerializedMaterialDocumentParser.Parse(
            CreateMaterialYaml(
                generatedShaderGuid,
                textureGuid,
                "_MainTex_Body",
                "_RimWidth_Body",
                "_Color_Body",
                "_GeneratedShaderSource_Body",
                $$"""
                  m_StringTagMap:
                    OriginalShaderGUID: {{SourceToon93Catalog.ShaderGuid}}
                    thry_rename_suffix: Body
                    _MainTexAnimated: 2
                    _RimWidthAnimated: 2
                    _ColorAnimated: 2
                    _GeneratedShaderSourceAnimated: 2
                """ + Environment.NewLine));

        SourceToonShaderMatchResult unlockedMatch = Match(unlocked, null);
        SourceToonShaderMatchResult lockedMatch = Match(locked, "Shader \"Hidden/Locked/Fixture\" { OPTIMIZER_ENABLED }");
        var resolver = new SourceAssetResolver(projectRoot);
        List<MaterialConversionDiagnostic> diagnostics = [];

        SourceToonMaterialDescriptor unlockedDescriptor =
            SourceToonMaterialDescriptorFactory.Create(unlocked, resolver, unlockedMatch, diagnostics);
        SourceToonMaterialDescriptor lockedDescriptor =
            SourceToonMaterialDescriptorFactory.Create(locked, resolver, lockedMatch, diagnostics);

        unlockedDescriptor.IsLocked.ShouldBeFalse();
        lockedDescriptor.IsLocked.ShouldBeTrue();
        lockedDescriptor.Floats.ShouldBe(unlockedDescriptor.Floats);
        lockedDescriptor.Vectors.ShouldBe(unlockedDescriptor.Vectors);
        lockedDescriptor.Strings.ShouldBe(unlockedDescriptor.Strings);
        lockedDescriptor.Textures.Keys.ShouldBe(unlockedDescriptor.Textures.Keys);
        lockedDescriptor.Textures["_MainTex"].Scale.ShouldBe(unlockedDescriptor.Textures["_MainTex"].Scale);
        lockedDescriptor.Textures["_MainTex"].ImportSettings!.Shape.ShouldBe(SerializedTextureShape.Texture2DArray);
        lockedDescriptor.PropertyBindings["_RimWidth_Body"].SemanticName.ShouldBe("_RimWidth");
        lockedDescriptor.PropertyBindings["_RimWidth_Body"].IsAnimated.ShouldBeTrue();
        lockedDescriptor.PropertyBindings["_RimWidth_Body"].IsRenamed.ShouldBeTrue();
        diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == MaterialConversionDiagnosticCodes.UnsupportedTextureAsset);
    }

    [Test]
    public void Descriptor_MissingTextureReference_IsPreservedAndReported()
    {
        SerializedMaterialDocument document = SerializedMaterialDocumentParser.Parse(
            CreateMaterialYaml(
                SourceToon93Catalog.ShaderGuid,
                "ffffffffffffffffffffffffffffffff",
                "_MainTex",
                "_RimWidth",
                "_Color",
                "_GeneratedShaderSource",
                string.Empty));
        SourceToonShaderMatchResult match = Match(document, null);
        List<MaterialConversionDiagnostic> diagnostics = [];
        string projectRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"poiyomi-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));

        SourceToonMaterialDescriptor descriptor =
            SourceToonMaterialDescriptorFactory.Create(document, new SourceAssetResolver(projectRoot), match, diagnostics);

        descriptor.Textures["_MainTex"].IsMissing.ShouldBeTrue();
        descriptor.Textures["_MainTex"].Reference.Guid.ShouldBe("ffffffffffffffffffffffffffffffff");
        diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == MaterialConversionDiagnosticCodes.AssetReferenceMissing);
    }

    private static SourceToonShaderMatchResult Match(SerializedMaterialDocument document, string? shaderSource)
        => SourceToonShaderMatcher.Match(new SourceToonShaderMatchInput
        {
            ShaderGuid = document.Shader.Guid,
            ShaderSource = shaderSource,
            PropertyNames = document.GetPropertyNames(),
            OverrideTags = document.OverrideTags,
        });

    private static string CreateProjectWithArrayTexture()
    {
        string projectRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"poiyomi-ingestion-{Guid.NewGuid():N}");
        string texturePath = Path.Combine(projectRoot, "Assets", "Textures", "fixture.asset");
        Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
        File.WriteAllBytes(texturePath, []);
        File.Copy(GetFixturePath("texture-import-modern.meta"), texturePath + ".meta");
        return projectRoot;
    }

    private static string CreateMaterialYaml(
        string shaderGuid,
        string textureGuid,
        string textureProperty,
        string floatProperty,
        string vectorProperty,
        string stringProperty,
        string tagBlock)
        => $$"""
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!21 &2100000
        Material:
          serializedVersion: 8
          m_Name: Fixture
          m_Shader: {fileID: 4800000, guid: {{shaderGuid}}, type: 3}
        {{tagBlock}}  m_SavedProperties:
            serializedVersion: 3
            m_TexEnvs:
            - {{textureProperty}}:
                m_Texture: {fileID: 2800000, guid: {{textureGuid}}, type: 3}
                m_Scale: {x: 2, y: 3}
                m_Offset: {x: 0.25, y: 0.5}
            m_Floats:
            - {{floatProperty}}: 0.35
            m_Colors:
            - {{vectorProperty}}: {r: 0.8, g: 0.7, b: 0.6, a: 0.5}
            m_Strings:
            - {{stringProperty}}: Assets/Generated/Locked.shader
        """;

    private static string GetFixturePath(string fileName)
        => Path.Combine(ResolveWorkspaceRoot(), "XREngine.UnitTests", "TestData", "Poiyomi", fileName);

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XRENGINE workspace root.");
    }
}
