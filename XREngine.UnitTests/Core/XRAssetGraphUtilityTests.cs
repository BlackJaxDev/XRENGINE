using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using ModelSubMesh = XREngine.Rendering.Models.SubMesh;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class XRAssetGraphUtilityTests
{
    [Test]
    public void SourceSubMeshAsset_RuntimeBacklink_DoesNotBecomeEmbeddedAsset()
    {
        var renderer = new XRMeshRenderer();
        var sourceSubMesh = new ModelSubMesh
        {
            Name = "SourceSubMesh"
        };

        renderer.EmbeddedAssets.Count.ShouldBe(0);

        renderer.SourceSubMeshAsset = sourceSubMesh;

        renderer.EmbeddedAssets.Count.ShouldBe(0);
        sourceSubMesh.SourceAsset.ShouldBeSameAs(sourceSubMesh);

        XRAssetGraphUtility.RefreshAssetGraph(renderer);

        renderer.EmbeddedAssets.Count.ShouldBe(0);
        sourceSubMesh.SourceAsset.ShouldBeSameAs(sourceSubMesh);
    }

    [Test]
    public void VisitReachableAssets_EntersAMaterialTextureList_AndAFileBackedTexture_WithoutClaims()
    {
        string imagePath = Path.Combine(Path.GetTempPath(), "XRAssetGraphUtilityTests", Guid.NewGuid().ToString("N"), "backed.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        XRTexture2D listed = new() { Name = "listed" };
        XRTexture2D backed = new() { Name = "backed", FilePath = imagePath };
        XRTexture2D foreign = new() { Name = "foreign" };
        XRMaterial material = new([listed, backed, foreign]) { Name = "material" };
        try
        {
            List<XRAsset> visited = [];

            XRAssetGraphUtility.VisitReachableAssets(material, asset => ReferenceEquals(asset, foreign), visited.Add).ShouldBeTrue();

            visited.ShouldContain(listed);
            visited.ShouldContain(backed);
            visited.ShouldNotContain(foreign);
            visited.ShouldNotContain(material);
            backed.SourceAsset.ShouldBeSameAs(backed);
            listed.SourceAsset.ShouldBeSameAs(listed);
        }
        finally
        {
            material.Destroy(now: true);
            listed.Destroy(now: true);
            backed.Destroy(now: true);
            foreign.Destroy(now: true);
            Directory.Delete(Path.GetDirectoryName(imagePath)!, recursive: true);
        }
    }
}
