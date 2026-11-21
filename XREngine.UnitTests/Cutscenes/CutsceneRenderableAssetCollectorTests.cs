using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Cutscenes;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutsceneRenderableAssetCollectorTests
{
    [Test]
    public void Collect_SkipsTexturesThatHaveNoFile()
    {
        // A model import gives a file texture, and a material default is a generated 1 x 1 texture.
        XRTexture2D fileTexture = new() { Name = "diffuse", FilePath = "Textures/diffuse.png" };
        XRTexture2D generated = new(1u, 1u, ColorF4.White) { Name = "_MainTex" };
        XRMaterial material = new() { Name = "Material" };
        material.Textures.Add(fileTexture);
        material.Textures.Add(generated);
        SubMesh subMesh = new(new SubMeshLOD(material, mesh: null, maxVisibleDistance: 0.0f));
        SceneNode node = new("Model");
        ModelComponent component = node.AddComponent<ModelComponent>()!;
        component.Model = new Model(subMesh);
        List<CutsceneResidencyAsset> assets = [];

        try
        {
            CutsceneRenderableAssetCollector.Instance.Collect(node, assets);

            assets.Count.ShouldBe(2);
            assets.ShouldContain(asset => asset.AssetId == CutsceneResidencyAssetKeys.ForTexture(fileTexture) && asset.Kind == EResidencyAssetKind.Texture);
            assets.ShouldContain(asset => asset.Kind == EResidencyAssetKind.Mesh);
            assets.ShouldNotContain(asset => asset.AssetId == generated.ID);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void Collect_GivesTheSameKeys_AfterANewImportMakesNewIds()
    {
        // Two imports of one model make new texture and sub-mesh objects with new runtime IDs.
        (SceneNode firstRoot, XRTexture2D firstTexture, SubMesh firstMesh) = Import();
        (SceneNode secondRoot, XRTexture2D secondTexture, SubMesh secondMesh) = Import();
        List<CutsceneResidencyAsset> first = [];
        List<CutsceneResidencyAsset> second = [];

        try
        {
            secondTexture.ID.ShouldNotBe(firstTexture.ID);
            secondMesh.ID.ShouldNotBe(firstMesh.ID);

            CutsceneResidencySceneWalker.Collect(firstRoot, CutsceneRenderableAssetCollector.Instance, first, shownOnly: false);
            CutsceneResidencySceneWalker.Collect(secondRoot, CutsceneRenderableAssetCollector.Instance, second, shownOnly: false);

            first.Count.ShouldBe(2);
            second.Select(asset => asset.AssetId).ShouldBe(first.Select(asset => asset.AssetId));
            first.ShouldNotContain(asset => asset.AssetId == firstTexture.ID || asset.AssetId == firstMesh.ID);
            first.ShouldContain(asset => asset.AssetId == CutsceneResidencyAssetKeys.ForTexture(secondTexture) && asset.Kind == EResidencyAssetKind.Texture);
        }
        finally
        {
            firstRoot.Destroy(now: true);
            secondRoot.Destroy(now: true);
        }
    }

    [Test]
    public void Collect_GivesTwoKeys_ForOneFileInTwoSamplerSlots()
    {
        // The importer makes one texture for each source file and sampler name, so one file in two slots is two textures.
        string source = Path.Combine(Path.GetTempPath(), "cutscene-collector", "shared.png");
        XRTexture2D mainTexture = new() { Name = "shared", OriginalPath = source, SamplerName = "_MainTex" };
        XRTexture2D emissionTexture = new() { Name = "shared", OriginalPath = source, SamplerName = "_EmissionMap" };
        XRMaterial material = new() { Name = "shared" };
        material.Textures.Add(mainTexture);
        material.Textures.Add(emissionTexture);
        SceneNode node = new("Model");
        node.AddComponent<ModelComponent>()!.Model = new Model(new SubMesh(new SubMeshLOD(material, mesh: null, maxVisibleDistance: 0.0f)));
        List<CutsceneResidencyAsset> assets = [];

        try
        {
            CutsceneRenderableAssetCollector.Instance.Collect(node, assets);

            Guid[] textureKeys = [.. assets.Where(asset => asset.Kind == EResidencyAssetKind.Texture).Select(asset => asset.AssetId)];
            textureKeys.Length.ShouldBe(2);
            textureKeys.ShouldBeUnique();
            textureKeys.ShouldContain(CutsceneResidencyAssetKeys.ForTexture(mainTexture));
            textureKeys.ShouldContain(CutsceneResidencyAssetKeys.ForTexture(emissionTexture));
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void SubMeshKeys_DifferForRootsWithTheSameName_AndForScenes()
    {
        SceneNode first = new("sponza");
        SceneNode second = new("sponza");
        XRScene scene = new("Level", first, second);
        XRScene otherScene = new("Other", first);

        try
        {
            Guid firstKey = CutsceneResidencyAssetKeys.ForSubMesh(first, 0, 0, scene);
            Guid secondKey = CutsceneResidencyAssetKeys.ForSubMesh(second, 0, 0, scene);

            secondKey.ShouldNotBe(firstKey);
            CutsceneResidencyAssetKeys.ForSubMesh(first, 0, 0, scene).ShouldBe(firstKey);
            CutsceneResidencyAssetKeys.ForSubMesh(first, 0, 0, otherScene).ShouldNotBe(firstKey);
        }
        finally
        {
            first.Destroy(now: true);
            second.Destroy(now: true);
        }
    }

    private static (SceneNode Root, XRTexture2D Texture, SubMesh Mesh) Import()
    {
        XRTexture2D texture = new() { Name = "floor_diff", OriginalPath = Path.Combine(Path.GetTempPath(), "cutscene-collector", "floor_diff.png") };
        XRMaterial material = new() { Name = "floor" };
        material.Textures.Add(texture);
        SubMesh subMesh = new(new SubMeshLOD(material, mesh: null, maxVisibleDistance: 0.0f));
        SceneNode root = new("sponza");
        SceneNode merged = new(root, "$MergedNode_0");
        merged.AddComponent<ModelComponent>()!.Model = new Model(subMesh);
        return (root, texture, subMesh);
    }
}
