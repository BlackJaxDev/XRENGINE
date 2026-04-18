using Assimp;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Gltf;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class NativeGltfImporterTests
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousServices;

    [Test]
    public void Import_AutoBackend_MatchesCommittedGoldenSummaries_ForCheckedInCorpus()
    {
        GltfCorpusManifest manifest = GltfImportTestUtilities.LoadManifest();
        GltfCorpusEntry[] entries = manifest.Entries
            .Where(static entry => entry.Availability == GltfCorpusAvailability.CheckedIn && entry.ExpectedImportSuccess)
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToArray();

        foreach (GltfCorpusEntry entry in entries)
        {
            GltfGoldenSummary actual = GltfImportTestUtilities.ImportAndSummarize(entry, GltfImportBackend.Auto);
            GltfGoldenSummary expected = GltfImportTestUtilities.LoadExpectedSummary(entry);
            AssertSummaryMatches(expected, actual, entry.Id);
        }
    }

    [Test]
    public void Import_AssimpLegacy_RemainsAvailable_AsCompatibilityEscapeHatch()
    {
        GltfCorpusManifest manifest = GltfImportTestUtilities.LoadManifest();
        GltfCorpusEntry staticEntry = manifest.Entries.Single(static entry => entry.Id == "external-static-scene");
        GltfCorpusEntry animatedEntry = manifest.Entries.Single(static entry => entry.Id == "skinned-morph-animated");

        GltfImportTestUtilities.ImportAndSummarize(staticEntry, GltfImportBackend.Assimp).MeshCount.ShouldBeGreaterThan(0);

        GltfImportTestUtilities.ImportAndSummarize(animatedEntry, GltfImportBackend.Assimp).ShouldSatisfyAllConditions(
            summary => summary.MeshCount.ShouldBeGreaterThan(0),
            summary => summary.MaterialCount.ShouldBeGreaterThan(0));
    }

    [Test]
    public void Import_AutoBackend_FallsBackToAssimp_WhenNativeRejectsUnsupportedBasisuExtension()
    {
        string tempDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"gltf-basisu-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        string sourceAssetPath = GltfImportTestUtilities.ResolveCorpusAssetPath(GltfImportTestUtilities.LoadManifest().Entries.Single(static entry => entry.Id == "external-static-scene"));
        string originalJson = File.ReadAllText(sourceAssetPath);
        string compatibilityJson = originalJson
            .Replace("\"extensionsUsed\": [", "\"extensionsUsed\": [\n    \"KHR_texture_basisu\", ", StringComparison.Ordinal)
            .Replace("\"source\": 0", "\"source\": 0,\n      \"extensions\": {\n        \"KHR_texture_basisu\": {\n          \"source\": 0\n        }\n      }", StringComparison.Ordinal);

        string assetPath = Path.Combine(tempDirectory, "basisu-compat.gltf");
        File.WriteAllText(assetPath, compatibilityJson);
        File.Copy(Path.Combine(Path.GetDirectoryName(sourceAssetPath).ShouldNotBeNull(), "external-static-scene.bin"), Path.Combine(tempDirectory, "external-static-scene.bin"), overwrite: true);
        File.Copy(Path.Combine(Path.GetDirectoryName(sourceAssetPath).ShouldNotBeNull(), "checker.png"), Path.Combine(tempDirectory, "checker.png"), overwrite: true);

        try
        {
            GltfImportTestUtilities.ImportedSceneSummary summary = GltfImportTestUtilities.ImportAndSummarize(assetPath, GltfImportBackend.Auto);
            summary.MeshCount.ShouldBeGreaterThan(0);
            summary.MaterialCount.ShouldBeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Import_TextureAndMaterialRemaps_AreHonoredByNativeGltfPath()
    {
        GltfCorpusEntry entry = GltfImportTestUtilities.LoadManifest().Entries.Single(static entry => entry.Id == "external-static-scene");
        string assetPath = GltfImportTestUtilities.ResolveCorpusAssetPath(entry);

        XRTexture2D replacementTexture = new()
        {
            Name = "ReplacementTexture",
            FilePath = "replacement-texture.png",
        };

        XRMaterial replacementMaterial = new()
        {
            Name = "ReplacementMaterial",
        };

        using ModelImporter importer = GltfImportTestUtilities.CreateImporter(assetPath, GltfImportBackend.Auto);
        importer.ImportOptions = new ModelImportOptions
        {
            GltfBackend = GltfImportBackend.Auto,
            GenerateMeshRenderersAsync = false,
            ProcessMeshesAsynchronously = false,
            TextureRemap = new Dictionary<string, XRTexture2D?>
            {
                ["Checker Base Color"] = replacementTexture,
            },
            MaterialRemap = new Dictionary<string, XRMaterial?>
            {
                ["Checker Material"] = replacementMaterial,
            },
        };

        SceneNode? rootNode = importer.Import(PostProcessSteps.None, cancellationToken: default, onProgress: null);
        rootNode.ShouldNotBeNull();

        ModelComponent modelComponent = rootNode!
            .FindDescendantByName("MeshNode")
            .ShouldNotBeNull()
            .GetComponent<ModelComponent>()
            .ShouldNotBeNull();

        XRMaterial material = modelComponent.Model.ShouldNotBeNull().Meshes[0].LODs.Min.ShouldNotBeNull().Material.ShouldNotBeNull();
        material.ShouldBeSameAs(replacementMaterial);
        material.Textures.Count.ShouldBe(0);
    }

    [Test]
    public void XRPrefabSource_Import3rdParty_PreseedsGltfTextureAndMaterialKeys()
    {
        GltfCorpusEntry entry = GltfImportTestUtilities.LoadManifest().Entries.Single(static entry => entry.Id == "external-static-scene");
        string assetPath = GltfImportTestUtilities.ResolveCorpusAssetPath(entry);

        XRPrefabSource prefabSource = new();
        ModelImportOptions options = new()
        {
            GltfBackend = GltfImportBackend.Auto,
            GenerateMeshRenderersAsync = false,
            ProcessMeshesAsynchronously = false,
        };

        prefabSource.Import3rdParty(assetPath, options).ShouldBeTrue();
        options.TextureRemap.ShouldNotBeNull();
        options.MaterialRemap.ShouldNotBeNull();
        options.TextureRemap!.ContainsKey("Checker Base Color").ShouldBeTrue();
        options.MaterialRemap!.ContainsKey("Checker Material").ShouldBeTrue();
    }

    private static void AssertSummaryMatches(GltfGoldenSummary expected, GltfGoldenSummary actual, string entryId)
    {
        actual.AssetId.ShouldBe(expected.AssetId, $"AssetId mismatch for corpus entry '{entryId}'.");
        actual.ImportSucceeded.ShouldBe(expected.ImportSucceeded, $"ImportSucceeded mismatch for corpus entry '{entryId}'.");
        actual.Container.ShouldBe(expected.Container, $"Container mismatch for corpus entry '{entryId}'.");
        actual.FileSizeBytes.ShouldBe(expected.FileSizeBytes, $"FileSizeBytes mismatch for corpus entry '{entryId}'.");
        actual.NodeCount.ShouldBe(expected.NodeCount, $"NodeCount mismatch for corpus entry '{entryId}'.");
        actual.MeshCount.ShouldBe(expected.MeshCount, $"MeshCount mismatch for corpus entry '{entryId}'.");
        actual.MaterialCount.ShouldBe(expected.MaterialCount, $"MaterialCount mismatch for corpus entry '{entryId}'.");
        actual.AnimationCount.ShouldBe(expected.AnimationCount, $"AnimationCount mismatch for corpus entry '{entryId}'.");
        actual.SkinCount.ShouldBe(expected.SkinCount, $"SkinCount mismatch for corpus entry '{entryId}'.");
        actual.BoneCount.ShouldBe(expected.BoneCount, $"BoneCount mismatch for corpus entry '{entryId}'.");
        actual.MorphTargetCount.ShouldBe(expected.MorphTargetCount, $"MorphTargetCount mismatch for corpus entry '{entryId}'.");
        actual.TotalVertices.ShouldBe(expected.TotalVertices, $"TotalVertices mismatch for corpus entry '{entryId}'.");
        actual.TotalTriangles.ShouldBe(expected.TotalTriangles, $"TotalTriangles mismatch for corpus entry '{entryId}'.");
        actual.MaxHierarchyDepth.ShouldBe(expected.MaxHierarchyDepth, $"MaxHierarchyDepth mismatch for corpus entry '{entryId}'.");
        actual.TextureCount.ShouldBe(expected.TextureCount, $"TextureCount mismatch for corpus entry '{entryId}'.");
        actual.UsedExtensions.ShouldBe(expected.UsedExtensions, $"UsedExtensions mismatch for corpus entry '{entryId}'.");
        actual.Notes.ShouldBe(expected.Notes, $"Notes mismatch for corpus entry '{entryId}'.");
    }
}