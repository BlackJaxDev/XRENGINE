using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Cutscenes;
using XREngine.Editor.Publishing;
using XREngine.Rendering;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture, NonParallelizable]
public sealed class CookedSourcePathPolicyTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "cutscene-cook-policy", "Assets");

    /// <summary>An asset whose graph has an object array that is too large for the graph refresh, so the refresh throws.</summary>
    private sealed class OversizedGraphAsset : XRAsset
    {
        public XRTexture2D?[] Items { get; set; } = [];
    }

    /// <summary>A cooked asset that holds an embedded texture and a reference to a live texture.</summary>
    private sealed class TextureHolderAsset : XRAsset
    {
        public XRTexture2D? Embedded { get; set; }
        public XRTexture2D? Live { get; set; }
    }

    [Test]
    public void Cook_WritesPortableSourcePaths_SoThatTheTextureKeyIsTheSameAsInTheEditor()
    {
        string inside = Path.Combine(Root, "Models", "floor.png");
        string outside = Path.Combine(Path.GetTempPath(), "cutscene-cook-policy", "Elsewhere", "wall.png");
        XRTexture2D cooked = new() { Name = "floor", OriginalPath = inside, SamplerName = "_MainTex" };
        XRTexture2D external = new() { Name = "wall", OriginalPath = outside, SamplerName = "_MainTex" };
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(Root));
            Guid editorKey = CutsceneResidencyAssetKeys.ForTexture(cooked);

            CookedSourcePathPolicy.MakeSourcePathsPortable(cooked).ShouldBe(1);
            CookedSourcePathPolicy.MakeSourcePathsPortable(external).ShouldBe(0);

            cooked.OriginalPath.ShouldBe("game://Models/floor.png");
            external.OriginalPath.ShouldBe(outside);
            CutsceneResidencyAssetKeys.ForTexture(cooked).ShouldBe(editorKey);
        }
        finally
        {
            cooked.Destroy(now: true);
            external.Destroy(now: true);
        }
    }

    [Test]
    public void Cook_WritesTheEmbeddedPaths_AndDoesNotClaimOrChangeALiveAsset()
    {
        string embeddedPath = Path.Combine(Root, "Models", "embedded.png");
        string livePath = Path.Combine(Root, "Models", "live.png");
        XRTexture2D embedded = new() { Name = "embedded", OriginalPath = embeddedPath, SamplerName = "_MainTex" };
        XRTexture2D live = new() { Name = "live", OriginalPath = livePath, SamplerName = "_DetailTex" };
        TextureHolderAsset holder = new() { Name = "cooked holder", Embedded = embedded, Live = live };
        try
        {
            FakeAssetRootServices services = new(Root);
            services.LiveAssets.Add(live.ID, live);
            using IDisposable lease = AssetSerializationServices.Install(services);

            CookedSourcePathPolicy.MakeSourcePathsPortable(holder).ShouldBe(1);

            embedded.OriginalPath.ShouldBe("game://Models/embedded.png");
            embedded.SourceAsset.ShouldBeSameAs(holder);
            live.OriginalPath.ShouldBe(livePath);
            live.SourceAsset.ShouldBeSameAs(live);
            holder.EmbeddedAssets.ShouldContain(embedded);
            holder.EmbeddedAssets.ShouldNotContain(live);
        }
        finally
        {
            holder.Destroy(now: true);
            embedded.Destroy(now: true);
            live.Destroy(now: true);
        }
    }

    [Test]
    public void Cook_WhenTheGraphRefreshThrows_WritesOnlyTheRoot_AndDoesNotFail()
    {
        OversizedGraphAsset asset = new()
        {
            Name = "oversized",
            OriginalPath = Path.Combine(Root, "Data", "oversized.bin"),
            Items = new XRTexture2D?[1001],
        };
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(Root));
            Should.Throw<InvalidDataException>(() => XRAssetGraphUtility.RefreshAssetGraph(asset));

            Should.NotThrow(() => CookedSourcePathPolicy.MakeSourcePathsPortable(asset)).ShouldBe(1);
            asset.OriginalPath.ShouldBe("game://Data/oversized.bin");
        }
        finally
        {
            asset.Destroy(now: true);
        }
    }
}
