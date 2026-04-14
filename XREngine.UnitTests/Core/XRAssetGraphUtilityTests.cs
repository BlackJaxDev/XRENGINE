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
}