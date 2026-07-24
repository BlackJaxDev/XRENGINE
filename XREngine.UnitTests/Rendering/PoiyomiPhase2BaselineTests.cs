using System.Numerics;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Generator;
using XREngine.Rendering.Vulkan;
using XREngine.Scene.Importers;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class PoiyomiPhase2BaselineTests
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
        => RuntimeShaderServices.Current = _previousServices;

    [Test]
    public void Importer_MapsIndependentPhase2SamplerFamiliesAndPackedChannels()
    {
        (string projectRoot, string materialPath) = CreateProject("phase2-mappings");
        (string Property, string Guid, string Path)[] textures =
        [
            CreateTexture(projectRoot, "_MainTex", 1),
            CreateTexture(projectRoot, "_ToonRamp", 2, srgb: true),
            CreateTexture(projectRoot, "_1st_ShadeMap", 3),
            CreateTexture(projectRoot, "_2nd_ShadeMap", 4),
            CreateTexture(projectRoot, "_MochieMetallicMaps", 5, srgb: false),
            CreateTexture(projectRoot, "_RGBASmoothnessMaps", 6, srgb: false),
            CreateTexture(projectRoot, "_RimColorTex", 7),
            CreateTexture(projectRoot, "_RimMask", 8, srgb: false),
            CreateTexture(projectRoot, "_DissolveNoiseTexture", 9, srgb: false),
            CreateTexture(projectRoot, "_DissolveDetailNoise", 10, srgb: false),
            CreateTexture(projectRoot, "_DissolveMask", 11, srgb: false),
            CreateTexture(projectRoot, "_DissolveEdgeGradient", 12),
            CreateTexture(projectRoot, "_DissolveToTexture", 13),
            CreateTexture(projectRoot, "_BumpMap", 14, srgb: false, normalMap: true),
            CreateTexture(projectRoot, "_EmissionMap", 15),
        ];

        WriteMaterial(
            materialPath,
            textures,
            ints:
            [
                ("_ToonRampUVSelector", 3),
                ("_1st_ShadeMapUV", 1),
                ("_2nd_ShadeMapUV", 2),
                ("_MochieMetallicMapsUV", 2),
                ("_RGBASmoothnessMapsUV", 3),
                ("_MochieMetallicMapsMetallicChannel", 2),
                ("_RimColorTexUV", 1),
                ("_RimMaskUV", 2),
                ("_DissolveNoiseTextureUV", 0),
                ("_DissolveDetailNoiseUV", 1),
                ("_DissolveMaskUV", 2),
                ("_DissolveToTextureUV", 3),
            ],
            floats:
            [
                ("_ShadingEnabled", 1.0f),
                ("_EnableRimLighting", 1.0f),
                ("_MochieBRDF", 1.0f),
                ("_EnableDissolve", 1.0f),
                ("_EnableEmission", 1.0f),
                ("_DissolveDetailStrength", 0.35f),
                ("_DissolveMaskInvert", 1.0f),
                ("_Smoothness", 0.7f),
            ]);

        UnityMaterialImportResult result = UnityMaterialImporter.ImportWithReport(materialPath, projectRoot);
        XRMaterial material = result.Material.ShouldNotBeNull();

        AssertTexturePath(material, "_ToonRamp", textures[1].Path);
        AssertTexturePath(material, "_FirstShadeMap", textures[2].Path);
        AssertTexturePath(material, "_SecondShadeMap", textures[3].Path);
        AssertTexturePath(material, "_PBRMetallicMaps", textures[4].Path);
        AssertTexturePath(material, "_PBRSmoothnessMaps", textures[5].Path);
        AssertTexturePath(material, "_RimColorTexture", textures[6].Path);
        AssertTexturePath(material, "_RimMask", textures[7].Path);
        AssertTexturePath(material, "_DissolveNoiseTexture", textures[8].Path);
        AssertTexturePath(material, "_DissolveDetailNoise", textures[9].Path);
        AssertTexturePath(material, "_DissolveMask", textures[10].Path);
        AssertTexturePath(material, "_DissolveEdgeGradient", textures[11].Path);
        AssertTexturePath(material, "_DissolveEdgeTexture", textures[12].Path);

        material.Parameter<ShaderInt>("_ToonRampUV")?.Value.ShouldBe(3);
        material.Parameter<ShaderInt>("_FirstShadeMapUV")?.Value.ShouldBe(1);
        material.Parameter<ShaderInt>("_SecondShadeMapUV")?.Value.ShouldBe(2);
        material.Parameter<ShaderInt>("_PBRMetallicMapsUV")?.Value.ShouldBe(2);
        material.Parameter<ShaderInt>("_PBRSmoothnessMapsUV")?.Value.ShouldBe(3);
        material.Parameter<ShaderInt>("_PBRMetallicMapsMetallicChannel")?.Value.ShouldBe(2);
        material.Parameter<ShaderInt>("_PBRSmoothnessMapsChannel")?.Value.ShouldBe(0);
        material.Parameter<ShaderFloat>("_DissolveDetailStrength")?.Value.ShouldBe(0.35f, 0.0001f);
        material.Parameter<ShaderFloat>("_DissolveMaskInvert")?.Value.ShouldBe(1.0f, 0.0001f);

        GetTexture(material, "_ToonRamp").ImportedColorSpace.ShouldBe(ETextureColorSpace.Srgb);
        GetTexture(material, "_ToonRamp").ImportedUsage.ShouldBe(ETextureImportUsage.Color);
        GetTexture(material, "_PBRMetallicMaps").ImportedUsage.ShouldBe(ETextureImportUsage.Data);
        GetTexture(material, "_PBRSmoothnessMaps").ImportedUsage.ShouldBe(ETextureImportUsage.Data);
        GetTexture(material, "_BumpMap").ImportedUsage.ShouldBe(ETextureImportUsage.Normal);
        GetTexture(material, "_BumpMap").ImportedNormalMapFlipGreen.ShouldBeTrue();

        material.IsUberFeatureEnabled("stylized-shading", defaultEnabled: false).ShouldBeTrue();
        material.IsUberFeatureEnabled("advanced-specular", defaultEnabled: false).ShouldBeTrue();
        material.IsUberFeatureEnabled("rim-lighting", defaultEnabled: false).ShouldBeTrue();
        material.IsUberFeatureEnabled("dissolve", defaultEnabled: false).ShouldBeTrue();
        material.IsUberFeatureEnabled("normal-map", defaultEnabled: false).ShouldBeTrue();
        material.IsUberFeatureEnabled("emission", defaultEnabled: false).ShouldBeTrue();

        byte[] spirv = VulkanShaderCompiler.Compile(
            material.FragmentShaders.Single(),
            out string entryPoint,
            out _,
            out _);
        entryPoint.ShouldBe("main");
        spirv.Length.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Importer_PropagatesUnitySamplerMetadataAndUsesSemanticDefaults()
    {
        (string projectRoot, string materialPath) = CreateProject("phase2-metadata");
        (string Property, string Guid, string Path) main =
            CreateTexture(projectRoot, "_MainTex", 21, srgb: false, detailedSampler: true);
        WriteMaterial(
            materialPath,
            [main],
            floats:
            [
                ("_ShadingEnabled", 1.0f),
                ("_MochieBRDF", 1.0f),
            ]);

        XRMaterial material = UnityMaterialImporter.ImportWithReport(materialPath, projectRoot).Material.ShouldNotBeNull();
        XRTexture2D mainTexture = GetTexture(material, "_MainTex");

        mainTexture.ImportedColorSpace.ShouldBe(ETextureColorSpace.Linear);
        mainTexture.AutoGenerateMipmaps.ShouldBeTrue();
        mainTexture.LodBias.ShouldBe(-0.5f);
        mainTexture.MaxAnisotropy.ShouldBe(8.0f);
        mainTexture.UWrap.ShouldBe(ETexWrapMode.MirroredRepeat);
        mainTexture.VWrap.ShouldBe(ETexWrapMode.ClampToEdge);
        mainTexture.MinFilter.ShouldBe(ETexMinFilter.LinearMipmapLinear);
        mainTexture.MagFilter.ShouldBe(ETexMagFilter.Linear);
        mainTexture.AlphaAsTransparency.ShouldBeTrue();

        XRTexture2D ramp = GetTexture(material, "_ToonRamp");
        ramp.Width.ShouldBe(2u);
        ramp.ImportedUsage.ShouldBe(ETextureImportUsage.Color);
        ramp.ImportedColorSpace.ShouldBe(ETextureColorSpace.Srgb);
        ramp.UWrap.ShouldBe(ETexWrapMode.ClampToEdge);

        XRTexture2D metallic = GetTexture(material, "_PBRMetallicMaps");
        metallic.ImportedUsage.ShouldBe(ETextureImportUsage.Data);
        metallic.ImportedColorSpace.ShouldBe(ETextureColorSpace.Linear);
        metallic.AlphaAsTransparency.ShouldBeFalse();
    }

    [Test]
    public void Importer_ExplicitlyDisabledSectionDoesNotEnableVariant()
    {
        (string projectRoot, string materialPath) = CreateProject("phase2-disabled");
        (string Property, string Guid, string Path) main = CreateTexture(projectRoot, "_MainTex", 31);
        (string Property, string Guid, string Path) emission = CreateTexture(projectRoot, "_EmissionMap", 32);
        WriteMaterial(
            materialPath,
            [main, emission],
            floats: [("_EnableEmission", 0.0f)]);

        XRMaterial material = UnityMaterialImporter.ImportWithReport(materialPath, projectRoot).Material.ShouldNotBeNull();

        AssertTexturePath(material, "_EmissionMap", emission.Path);
        material.IsUberFeatureEnabled("emission", defaultEnabled: true).ShouldBeFalse();
    }

    [Test]
    public void Importer_ReportsUnsupportedFeaturesInvalidEnumsAndMissingUvChannels()
    {
        (string projectRoot, string materialPath) = CreateProject("phase2-diagnostics");
        (string Property, string Guid, string Path) main = CreateTexture(projectRoot, "_MainTex", 41);
        WriteMaterial(
            materialPath,
            [main],
            ints:
            [
                ("_MainTexUV", 3),
                ("_RimStyle", 99),
            ],
            floats:
            [
                ("_EnableOutlines", 1.0f),
                ("_LTCGIEnabled", 1.0f),
            ]);

        UnityMaterialImportResult result = UnityMaterialImporter.ImportWithReport(materialPath, projectRoot);
        XRMaterial material = result.Material.ShouldNotBeNull();

        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == MaterialConversionDiagnosticCodes.EnumValueOutOfRange &&
            diagnostic.SourceProperty == "_RimStyle");
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == MaterialConversionDiagnosticCodes.IntentionalNativeDifference &&
            diagnostic.SourceProperty == "_EnableOutlines");
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == MaterialConversionDiagnosticCodes.IntegrationUnavailable &&
            diagnostic.SourceProperty == "_LTCGIEnabled");
        material.IsUberFeatureEnabled("outline", defaultEnabled: true).ShouldBeFalse();

        UnityMaterialImporter.ValidateMeshCompatibility(material, 2)
            .ShouldContain(diagnostic =>
                diagnostic.Code == MaterialConversionDiagnosticCodes.RequestedUvChannelMissing &&
                diagnostic.SourceProperty == "UV3");
        UnityMaterialImporter.ValidateMeshCompatibility(material, 4).ShouldBeEmpty();
    }

    [TestCase(0, ETransparencyMode.Opaque)]
    [TestCase(1, ETransparencyMode.Masked)]
    [TestCase(2, ETransparencyMode.WeightedBlendedOit)]
    [TestCase(3, ETransparencyMode.WeightedBlendedOit)]
    public void Importer_MapsBaselineTransparencyFixtures(int sourceMode, ETransparencyMode expected)
    {
        (string projectRoot, string materialPath) = CreateProject($"phase2-mode-{sourceMode}");
        (string Property, string Guid, string Path) main =
            CreateTexture(projectRoot, "_MainTex", 50 + sourceMode);
        WriteMaterial(materialPath, [main], floats: [("_Mode", sourceMode)]);

        UnityMaterialImporter.ImportWithReport(materialPath, projectRoot)
            .Material.ShouldNotBeNull()
            .TransparencyMode.ShouldBe(expected);
    }

    [Test]
    public void FourUvMeshAndAllVertexPaths_PreserveDistinctChannels()
    {
        Vector2[] expected =
        [
            new(0.1f, 0.2f),
            new(0.3f, 0.4f),
            new(0.5f, 0.6f),
            new(0.7f, 0.8f),
        ];
        Vertex[] vertices =
        [
            CreateVertex(Vector3.Zero, expected),
            CreateVertex(Vector3.UnitX, expected),
            CreateVertex(Vector3.UnitY, expected),
        ];
        XRMesh mesh = new(vertices, [0, 1, 2]);

        mesh.TexCoordCount.ShouldBe(4u);
        for (uint channel = 0; channel < 4; channel++)
            mesh.GetTexCoord(0, channel).ShouldBe(expected[channel]);

        string generated = new DefaultVertexShaderGenerator(mesh).Generate();
        for (int channel = 0; channel < 4; channel++)
        {
            generated.ShouldContain($"layout (location = {4 + channel}) out vec2 FragUV{channel};");
            generated.ShouldContain($"FragUV{channel} = TexCoord{channel};");
        }

        foreach (string shaderName in new[] { "UberShader.vert", "UberShader_OVR.vert", "UberShader_NV.vert" })
        {
            string source = ReadWorkspaceFile($"Build/CommonAssets/Shaders/Uber/{shaderName}");
            for (int channel = 0; channel < 4; channel++)
            {
                source.ShouldContain($"layout(location = {3 + channel}) in vec2 TexCoord{channel};");
                source.ShouldContain($"layout(location = {4 + channel}) out vec2 FragUV{channel};");
                source.ShouldContain($"FragUV{channel} = TexCoord{channel};");
            }
        }

        string oneUvGenerated = new DefaultVertexShaderGenerator(
            new XRMesh(
                [
                    new Vertex(Vector3.Zero, Vector2.Zero),
                    new Vertex(Vector3.UnitX, Vector2.UnitX),
                    new Vertex(Vector3.UnitY, Vector2.UnitY),
                ],
                [0, 1, 2]))
            .Generate();
        oneUvGenerated.ShouldContain("FragUV1 = FragUV0;");
        oneUvGenerated.ShouldContain("FragUV2 = FragUV0;");
        oneUvGenerated.ShouldContain("FragUV3 = FragUV0;");
    }

    [Test]
    public void UberShader_ConsumesCorrectedSamplerFamiliesIndependently()
    {
        string fragment = ReadWorkspaceFile("Build/CommonAssets/Shaders/Uber/UberShader.frag");
        string dissolve = ReadWorkspaceFile("Build/CommonAssets/Shaders/Uber/dissolve.glsl");

        fragment.ShouldContain("texture(_ToonRamp, rampCoord)");
        fragment.ShouldContain("texture(_FirstShadeMap, firstUV)");
        fragment.ShouldContain("texture(_SecondShadeMap, secondUV)");
        fragment.ShouldContain("texture(_PBRMetallicMaps, metallicUV)");
        fragment.ShouldContain("texture(_PBRSmoothnessMaps, smoothnessUV)");
        fragment.ShouldContain("texture(_RimMask, maskUV)");
        fragment.ShouldContain("texture(_RimColorTexture, colorUV)");
        fragment.ShouldContain("getUV(_DissolveNoiseTextureUV, mesh)");
        fragment.ShouldContain("getUV(_DissolveDetailNoiseUV, mesh)");
        fragment.ShouldContain("getUV(_DissolveMaskUV, mesh)");
        fragment.ShouldContain("getUV(_DissolveEdgeTextureUV, mesh)");

        dissolve.ShouldContain("texture(_DissolveDetailNoise, detailUV)");
        dissolve.ShouldContain("texture(_DissolveMask, maskUV)");
        dissolve.ShouldContain("texture(_DissolveEdgeGradient, gradientUV)");
        dissolve.ShouldContain("texture(_DissolveEdgeTexture, edgeUV)");
    }

    private static Vertex CreateVertex(Vector3 position, IEnumerable<Vector2> texCoords)
        => new(position)
        {
            Normal = Vector3.UnitZ,
            Tangent = Vector3.UnitX,
            TextureCoordinateSets = [.. texCoords],
        };

    private static (string ProjectRoot, string MaterialPath) CreateProject(string suffix)
    {
        string projectRoot = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"{suffix}-{Guid.NewGuid():N}");
        string shaderPath = Path.Combine(
            projectRoot,
            "Assets",
            "_PoiyomiShaders",
            "Shaders",
            "9.3",
            "Toon",
            "Poiyomi Toon.shader");
        string materialPath = Path.Combine(projectRoot, "Assets", "Materials", "Fixture.mat");

        Directory.CreateDirectory(Path.GetDirectoryName(shaderPath)!);
        File.WriteAllText(shaderPath, $"Shader \".poiyomi/Poiyomi Toon\" {{ // Poiyomi {PoiyomiToon93Catalog.VersionText} }}");
        WriteMeta(shaderPath, PoiyomiToon93Catalog.ShaderGuid, srgb: true);
        Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
        return (projectRoot, materialPath);
    }

    private static (string Property, string Guid, string Path) CreateTexture(
        string projectRoot,
        string property,
        int id,
        bool srgb = true,
        bool normalMap = false,
        bool detailedSampler = false)
    {
        string guid = id.ToString("x32");
        string path = Path.Combine(projectRoot, "Assets", "Textures", $"{id:D3}-{property.TrimStart('_')}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        WriteMeta(path, guid, srgb, normalMap, detailedSampler);
        return (property, guid, path);
    }

    private static void WriteMeta(
        string assetPath,
        string guid,
        bool srgb,
        bool normalMap = false,
        bool detailedSampler = false)
    {
        string metadata = $$"""
        fileFormatVersion: 2
        guid: {{guid}}
        TextureImporter:
          serializedVersion: 13
          mipmaps:
            enableMipMap: {{(detailedSampler ? 1 : 0)}}
            sRGBTexture: {{(srgb ? 1 : 0)}}
          bumpmap:
            convertToNormalMap: {{(normalMap ? 1 : 0)}}
            normalMapFilter: 1
            flipGreenChannel: {{(normalMap ? 1 : 0)}}
          textureSettings:
            serializedVersion: 2
            filterMode: {{(detailedSampler ? 2 : 1)}}
            aniso: {{(detailedSampler ? 8 : 1)}}
            mipBias: {{(detailedSampler ? "-0.5" : "0")}}
            wrapU: {{(detailedSampler ? 2 : 0)}}
            wrapV: {{(detailedSampler ? 1 : 0)}}
            wrapW: 0
          alphaSource: 1
          alphaIsTransparency: {{(detailedSampler ? 1 : 0)}}
          textureType: {{(normalMap ? 1 : 0)}}
          textureShape: 1
        """;
        File.WriteAllText($"{assetPath}.meta", metadata);
    }

    private static void WriteMaterial(
        string materialPath,
        IReadOnlyList<(string Property, string Guid, string Path)> textures,
        IReadOnlyList<(string Property, int Value)>? ints = null,
        IReadOnlyList<(string Property, float Value)>? floats = null)
    {
        StringBuilder yaml = new();
        yaml.AppendLine("%YAML 1.1");
        yaml.AppendLine("%TAG !u! tag:unity3d.com,2011:");
        yaml.AppendLine("--- !u!21 &2100000");
        yaml.AppendLine("Material:");
        yaml.AppendLine("  serializedVersion: 8");
        yaml.AppendLine("  m_Name: Phase2Fixture");
        yaml.AppendLine($"  m_Shader: {{fileID: 4800000, guid: {PoiyomiToon93Catalog.ShaderGuid}, type: 3}}");
        yaml.AppendLine("  m_CustomRenderQueue: -1");
        yaml.AppendLine("  m_SavedProperties:");
        yaml.AppendLine("    serializedVersion: 3");
        yaml.AppendLine("    m_TexEnvs:");
        foreach ((string property, string guid, _) in textures)
        {
            yaml.AppendLine($"    - {property}:");
            yaml.AppendLine($"        m_Texture: {{fileID: 2800000, guid: {guid}, type: 3}}");
            yaml.AppendLine("        m_Scale: {x: 2, y: 3}");
            yaml.AppendLine("        m_Offset: {x: 0.25, y: 0.5}");
        }

        yaml.AppendLine("    m_Ints:");
        foreach ((string property, int value) in ints ?? [])
            yaml.AppendLine($"    - {property}: {value}");

        yaml.AppendLine("    m_Floats:");
        foreach ((string property, float value) in floats ?? [])
            yaml.AppendLine($"    - {property}: {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        File.WriteAllText(materialPath, yaml.ToString());
    }

    private static XRTexture2D GetTexture(XRMaterial material, string samplerName)
        => material.Textures
            .OfType<XRTexture2D>()
            .Single(texture => string.Equals(texture.SamplerName, samplerName, StringComparison.Ordinal));

    private static void AssertTexturePath(XRMaterial material, string samplerName, string expectedPath)
        => GetTexture(material, samplerName).FilePath.ShouldBe(expectedPath);

    private static string ReadWorkspaceFile(string relativePath)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return File.ReadAllText(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace file '{relativePath}'.");
    }

    private sealed class FileBackedRuntimeShaderServices : IRuntimeShaderServices
    {
        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
            => new T();

        public T LoadEngineAsset<T>(
            JobPriority priority,
            bool bypassJobThread,
            string assetRoot,
            string relativePath) where T : XRAsset, new()
            => CreateEngineAsset<T>(relativePath);

        public Task<T> LoadEngineAssetAsync<T>(
            JobPriority priority,
            bool bypassJobThread,
            string assetRoot,
            string relativePath) where T : XRAsset, new()
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
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }

            return relativePath;
        }
    }
}
