using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Gltf;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class GltfAssetDocumentTests
{
    [Test]
    public void Open_PreservesExtrasAndUnknownExtensions_AndReadsTypedAccessors()
    {
        GltfCorpusManifest manifest = GltfImportTestUtilities.LoadManifest();
        GltfCorpusEntry animatedEntry = manifest.Entries.Single(static entry => entry.Id == "skinned-morph-animated");
        GltfCorpusEntry morphEntry = manifest.Entries.Single(static entry => entry.Id == "morph-sparse-extras");
        GltfCorpusEntry embeddedEntry = manifest.Entries.Single(static entry => entry.Id == "embedded-buffer-view-scene");
        string assetPath = GltfImportTestUtilities.ResolveCorpusAssetPath(animatedEntry);
        string morphAssetPath = GltfImportTestUtilities.ResolveCorpusAssetPath(morphEntry);
        string embeddedAssetPath = GltfImportTestUtilities.ResolveCorpusAssetPath(embeddedEntry);

        using GltfAssetDocument document = GltfAssetDocument.Open(assetPath);
        using GltfAssetDocument morphDocument = GltfAssetDocument.Open(morphAssetPath);
        using GltfAssetDocument embeddedDocument = GltfAssetDocument.Open(embeddedAssetPath);

        morphDocument.Root.Scenes.Count.ShouldBe(2);
        morphDocument.Root.ResolveDefaultSceneIndex().ShouldBe(1);
        morphDocument.Root.Nodes[0].Extras.ShouldNotBeNull();
        morphDocument.Root.Nodes[0].Extras!.Value.TryGetProperty("tag", out var tag).ShouldBeTrue();
        tag.GetString().ShouldBe("RootJoint");

        morphDocument.Root.Materials[0].Extensions.ShouldNotBeNull();
        morphDocument.Root.Materials[0].Extensions!.ContainsKey("XRE_material_metadata").ShouldBeTrue();
        morphDocument.Root.Meshes[0].Extras.ShouldNotBeNull();
        morphDocument.Root.Meshes[0].Extras!.Value.TryGetProperty("targetNames", out var targetNames).ShouldBeTrue();
        targetNames.GetArrayLength().ShouldBe(1);

        Vector3[] positions = morphDocument.ReadVector3Accessor(0);
        positions.Length.ShouldBeGreaterThan(0);
        positions[0].X.ShouldBe(-0.5f, 0.0001f);

        Matrix4x4[] inverseBindMatrices = document.ReadMatrix4Accessor(6);
        inverseBindMatrices.Length.ShouldBe(2);
        inverseBindMatrices[0].M11.ShouldBe(1.0f, 0.0001f);

        embeddedDocument.Root.Scenes.Count.ShouldBe(2);
        embeddedDocument.Root.ResolveDefaultSceneIndex().ShouldBe(1);

        byte[] imageBytes = embeddedDocument.ReadBufferViewBytes(3);
        imageBytes.Length.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Open_RepeatedCycles_RemainDeterministic()
    {
        GltfCorpusEntry entry = GltfImportTestUtilities.LoadManifest().Entries.Single(static entry => entry.Id == "external-static-scene");
        string assetPath = GltfImportTestUtilities.ResolveCorpusAssetPath(entry);

        Vector3[]? baseline = null;
        for (int iteration = 0; iteration < 12; iteration++)
        {
            using GltfAssetDocument document = GltfAssetDocument.Open(assetPath);
            Vector3[] positions = document.ReadVector3Accessor(0);

            if (baseline is null)
            {
                baseline = positions;
                continue;
            }

            positions.Length.ShouldBe(baseline.Length);
            for (int index = 0; index < positions.Length; index++)
                positions[index].ShouldBe(baseline[index]);
        }
    }

    [Test]
    public void Load_MalformedGlb_ThrowsActionableBoundsException()
    {
        GltfCorpusEntry entry = GltfImportTestUtilities.LoadManifest().Entries.Single(static entry => entry.Id == "malformed-truncated-glb");
        string assetPath = GltfImportTestUtilities.ResolveCorpusAssetPath(entry);

        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() => GltfJsonLoader.Load(assetPath));
        ex.Message.ShouldContain("out-of-bounds chunk");
    }
}