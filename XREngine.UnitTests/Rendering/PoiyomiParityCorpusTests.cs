using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class PoiyomiParityCorpusTests
{
    private static readonly string[] ConvertedUberFeatureIds =
    [
        "surface-extensions",
        "global-masks-themes",
        "advanced-stylized-lighting",
        "advanced-pbr",
        "layered-matcap-rim",
        "layered-decals",
        "layered-emission",
        "texture-array-flipbook",
        "extended-effects",
        "vertex-effects",
        "audiolink",
        "environment-lighting",
        "view-context",
    ];

    private JsonDocument _corpus = null!;

    [OneTimeSetUp]
    public void LoadCorpus()
        => _corpus = JsonDocument.Parse(File.ReadAllText(CorpusPath));

    [OneTimeTearDown]
    public void DisposeCorpus()
        => _corpus.Dispose();

    [Test]
    public void Corpus_MetadataAndLicense_ArePinnedForEveryMaterial()
    {
        JsonElement root = _corpus.RootElement;
        root.GetProperty("formatVersion").GetInt32().ShouldBe(1);
        root.GetProperty("poiyomiVersion").GetString().ShouldBe(PoiyomiToon93Catalog.VersionText);
        root.GetProperty("poiyomiCommit").GetString().ShouldBe(PoiyomiToon93Catalog.RepositoryCommit);
        root.GetProperty("unityVersion").GetString().ShouldBe("2022.3.22f1");
        root.GetProperty("license").GetString().ShouldBe("CC0-1.0");
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(CorpusPath)!, "..", "LICENSE.txt"))
            .ShouldContain("SPDX-License-Identifier: CC0-1.0");

        foreach (JsonElement material in root.GetProperty("materials").EnumerateArray())
        {
            material.GetProperty("id").GetString().ShouldNotBeNullOrWhiteSpace();
            material.GetProperty("locked").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
            material.GetProperty("renderPreset").GetString().ShouldNotBeNullOrWhiteSpace();
            material.GetProperty("features").GetArrayLength().ShouldBeGreaterThan(0);
            material.GetProperty("sourceValues").EnumerateObject().ShouldNotBeEmpty();
        }
    }

    [Test]
    public void Corpus_HasUnlockedLockedFocusedPresetAndMaximalMaterials()
    {
        JsonElement[] materials = [.. _corpus.RootElement.GetProperty("materials").EnumerateArray()];
        materials.ShouldContain(material => !material.GetProperty("locked").GetBoolean());
        materials.ShouldContain(material => material.GetProperty("locked").GetBoolean());
        materials.Count(material => material.GetProperty("id").GetString()!.StartsWith(
            "maximal-practical",
            StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(2);

        HashSet<string> corpusFeatures = materials
            .SelectMany(static material => material.GetProperty("features").EnumerateArray())
            .Select(static feature => feature.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        IRuntimeShaderServices? previous = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new UberRuntimeShaderServices();
        try
        {
            ShaderUiManifest manifest = ShaderHelper.UberFragForward().GetUiManifest();
            foreach (string featureId in ConvertedUberFeatureIds)
            {
                manifest.FeatureLookup.ShouldContainKey(featureId);
                corpusFeatures.ShouldContain(featureId);
            }
        }
        finally
        {
            RuntimeShaderServices.Current = previous;
        }

        HashSet<string> usedPresets = materials
            .Select(static material => material.GetProperty("renderPreset").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (JsonElement preset in _corpus.RootElement.GetProperty("renderPresets").EnumerateArray())
            usedPresets.ShouldContain(preset.GetString()!);
    }

    [Test]
    public void Corpus_MeshesCoverAllGeometryAndTransformInputs()
    {
        HashSet<string> attributes = _corpus.RootElement.GetProperty("meshes")
            .EnumerateArray()
            .SelectMany(static mesh => mesh.GetProperty("attributes").EnumerateArray())
            .Select(static attribute => attribute.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string required in new[]
                 {
                     "UV0", "UV1", "UV2", "UV3", "Color", "Tangent", "Skinning",
                     "MorphTargets", "MirroredTransform", "NonUniformScale",
                 })
            attributes.ShouldContain(required);
    }

    [Test]
    public void Corpus_TexturesCoverColorDataNormalCubeAndArrayRoles()
    {
        JsonElement[] textures = [.. _corpus.RootElement.GetProperty("textures").EnumerateArray()];
        textures.ShouldContain(texture => texture.GetProperty("colorSpace").GetString() == "sRGB");
        textures.ShouldContain(texture => texture.GetProperty("role").GetString() == "Normal");
        textures.ShouldContain(texture => texture.GetProperty("role").GetString() == "Mask");
        textures.ShouldContain(texture => texture.GetProperty("role").GetString() == "PackedData");
        textures.ShouldContain(texture => texture.GetProperty("kind").GetString() == "Cube");
        textures.ShouldContain(texture => texture.GetProperty("kind").GetString() == "2DArray");
    }

    [Test]
    public void Corpus_AnimationsCoverAllBindingShapes()
    {
        HashSet<string> kinds = _corpus.RootElement.GetProperty("animations")
            .EnumerateArray()
            .Select(static animation => animation.GetProperty("valueKind").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        kinds.ShouldBe(["Float", "Vector", "Color", "Texture"], ignoreOrder: true);
        JsonElement[] animations = [.. _corpus.RootElement.GetProperty("animations").EnumerateArray()];
        animations.ShouldContain(animation => animation.GetProperty("id").GetString() == "repeated-slot");
        animations.ShouldContain(animation => animation.GetProperty("id").GetString() == "renamed-locked");
    }

    [Test]
    public void Corpus_SchemaInventorySeparatesActiveAndInactiveLookalikes()
    {
        JsonElement schema = _corpus.RootElement.GetProperty("schemaFixture");
        schema.GetProperty("activeKinds").GetArrayLength().ShouldBe(3);
        string[] active = schema.GetProperty("activeAnnotations").EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        string[] inactive = schema.GetProperty("inactiveLookalikes").EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        active.ShouldContain("ThryRGBAPacker");
        active.ShouldContain("condition_show");
        inactive.ShouldAllBe(static value =>
            value.Contains("//", StringComparison.Ordinal) ||
            value.Contains("/*", StringComparison.Ordinal) ||
            value.Contains("#if 0", StringComparison.Ordinal) ||
            value.Contains("Malformed", StringComparison.Ordinal));
        active.Intersect(inactive, StringComparer.Ordinal).ShouldBeEmpty();
    }

    [Test]
    public void Corpus_AuthoringAndMultiMaterialFixturesAreVersionedAndComplete()
    {
        JsonElement[] authoring =
            [.. _corpus.RootElement.GetProperty("authoringFixtures").EnumerateArray()];
        authoring.Length.ShouldBe(8);
        authoring.ShouldAllBe(fixture => fixture.GetProperty("version").GetInt32() > 0);
        string[] expected =
        [
            "locale", "preset", "clipboard", "packing-recipe", "texture-array",
            "material-link", "cross-shader", "user-override",
        ];
        authoring.Select(static fixture => fixture.GetProperty("kind").GetString())
            .ShouldBe(expected, ignoreOrder: true);

        string[] relationships = _corpus.RootElement.GetProperty("multiMaterialFixtures")
            .EnumerateArray()
            .Select(static fixture => fixture.GetProperty("relationship").GetString()!)
            .ToArray();
        relationships.ShouldBe(
            ["Compatible", "MixedValues", "MissingSemantic", "PackedSplitEquivalent", "IntentionallyIncompatible"],
            ignoreOrder: true);
    }

    [Test]
    public void PinnedCatalogSnapshot_HasStableHashAndEveryPropertyIsClassified()
    {
        string catalogPath = FindRepositoryFile(
            "XREngine.Editor",
            "Importers",
            "Poiyomi",
            "Catalogs",
            "poiyomi-toon-9.3.64.json");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant();
        hash.ShouldBe("d9ce662fed87308e0a841906d4e523032b24c629e66d2aa0de730755783fc663");

        using JsonDocument catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
        JsonElement[] properties = [.. catalog.RootElement.GetProperty("properties").EnumerateArray()];
        properties.Length.ShouldBe(3736);
        properties.ShouldAllBe(property =>
            !string.IsNullOrWhiteSpace(property.GetProperty("classification").GetString()));
        properties.Select(static property => property.GetProperty("name").GetString())
            .ShouldBeUnique();
        properties.Select(static property => property.GetProperty("sourceLine").GetInt32())
            .ShouldBeInOrder(SortDirection.Ascending);
        catalog.RootElement.GetProperty("annotations").GetArrayLength().ShouldBe(41);
    }

    [Test]
    public void ReferenceImages_AreLicensedDecodableAndNonEmpty()
    {
        foreach (string fileName in new[]
                 {
                     "inspector-reference-collapsed.ppm",
                     "inspector-reference-tools.ppm",
                     "render-reference-atlas.ppm",
                 })
        {
            string path = Path.Combine(Path.GetDirectoryName(CorpusPath)!, fileName);
            string source = File.ReadAllText(path);
            source.ShouldStartWith("P3");
            source.ShouldContain("CC0-1.0");
            using ImageMagick.MagickImage image = new(path);
            image.Width.ShouldBeGreaterThan(0u);
            image.Height.ShouldBeGreaterThan(0u);
        }
    }

    [Test]
    public void UnityReferenceImages_ArePinnedLicensedAndMatchManifestCameraCount()
    {
        string fixtureRoot = Path.Combine(Path.GetDirectoryName(CorpusPath)!, "UnityReferences");
        using JsonDocument metadata = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(fixtureRoot, "capture-metadata.json")));
        JsonElement root = metadata.RootElement;

        root.GetProperty("poiyomiVersion").GetString().ShouldBe(PoiyomiToon93Catalog.VersionText);
        root.GetProperty("poiyomiCommit").GetString().ShouldBe(PoiyomiToon93Catalog.RepositoryCommit);
        root.GetProperty("unityVersion").GetString().ShouldBe("2022.3.22f1");
        root.GetProperty("shaderName").GetString().ShouldBe(".poiyomi/Poiyomi Toon");
        root.GetProperty("license").GetString().ShouldBe("CC0-1.0");
        root.GetProperty("sourceShaderLicense").GetString().ShouldBe("MIT");

        int expectedCameraCount = _corpus.RootElement
            .GetProperty("visualValidation")
            .GetProperty("cameraPositions")
            .GetArrayLength();
        JsonElement[] cameraPositions = [.. root.GetProperty("cameraPositions").EnumerateArray()];
        cameraPositions.Length.ShouldBe(expectedCameraCount);

        for (int index = 0; index < cameraPositions.Length; index++)
        {
            string path = Path.Combine(fixtureRoot, $"poiyomi-unity-reference-camera-{index}.png");
            new FileInfo(path).Length.ShouldBeGreaterThan(1024);
            using ImageMagick.MagickImage image = new(path);
            image.Width.ShouldBe(640u);
            image.Height.ShouldBe(360u);
        }
    }

    private static string CorpusPath
        => FindRepositoryFile(
            "XREngine.UnitTests",
            "TestData",
            "Poiyomi",
            "ParityCorpus",
            "corpus-manifest.json");

    internal static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Could not find repository file '{Path.Combine(segments)}'.");
    }
}
