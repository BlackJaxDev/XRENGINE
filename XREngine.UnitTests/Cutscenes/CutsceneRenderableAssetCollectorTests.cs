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
            assets.ShouldContain(asset => asset.AssetId == fileTexture.ID && asset.Kind == EResidencyAssetKind.Texture);
            assets.ShouldContain(asset => asset.AssetId == subMesh.ID && asset.Kind == EResidencyAssetKind.Mesh);
            assets.ShouldNotContain(asset => asset.AssetId == generated.ID);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }
}
