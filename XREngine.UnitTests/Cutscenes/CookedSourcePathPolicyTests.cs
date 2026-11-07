using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Cutscenes;
using XREngine.Editor;
using XREngine.Editor.Publishing;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;

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
    public void Cook_WhenTheGraphRefreshThrows_StillWritesTheReachedPaths_AndDoesNotFail()
    {
        XRTexture2D item = new() { Name = "item", OriginalPath = Path.Combine(Root, "Data", "item.png"), SamplerName = "_MainTex" };
        OversizedGraphAsset asset = new()
        {
            Name = "oversized",
            OriginalPath = Path.Combine(Root, "Data", "oversized.bin"),
            Items = new XRTexture2D?[1001],
        };
        asset.Items[1000] = item;
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(Root));
            Should.Throw<InvalidDataException>(() => XRAssetGraphUtility.RefreshAssetGraph(asset));

            Should.NotThrow(() => CookedSourcePathPolicy.MakeSourcePathsPortable(asset)).ShouldBe(2);
            asset.OriginalPath.ShouldBe("game://Data/oversized.bin");
            item.OriginalPath.ShouldBe("game://Data/item.png");
        }
        finally
        {
            asset.Destroy(now: true);
            item.Destroy(now: true);
        }
    }

    [Test]
    public void Cook_WritesThePathOfAFileBackedTexture_AndDoesNotClaimIt()
    {
        string imagePath = Path.Combine(Root, "Models", "backed.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        XRTexture2D backed = new() { Name = "backed", FilePath = imagePath, OriginalPath = imagePath, SamplerName = "_MainTex" };
        TextureHolderAsset holder = new() { Name = "cooked holder", Embedded = backed };
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(Root));
            Guid editorKey = CutsceneResidencyAssetKeys.ForTexture(backed);

            CookedSourcePathPolicy.MakeSourcePathsPortable(holder).ShouldBe(1);

            backed.OriginalPath.ShouldBe("game://Models/backed.png");
            backed.SourceAsset.ShouldBeSameAs(backed);
            holder.EmbeddedAssets.ShouldNotContain(backed);
            CutsceneResidencyAssetKeys.ForTexture(backed).ShouldBe(editorKey);
        }
        finally
        {
            holder.Destroy(now: true);
            backed.Destroy(now: true);
            File.Delete(imagePath);
        }
    }

    [Test]
    public void ContentCook_OfAMaterialWithASourceImageTexture_WritesAPortablePath_ThatKeepsTheKeyAndResolves()
    {
        string tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "CookedSourcePathPolicy", Guid.NewGuid().ToString("N"));
        string assets = Path.Combine(tempRoot, "Assets");
        string imagePath = Path.Combine(assets, "Textures", "floor.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4E, 0x47]);

        // An imported material texture: a source image backs it. The second texture has only the source path.
        XRTexture2D backed = new() { Name = "floor", FilePath = imagePath, OriginalPath = imagePath, SamplerName = "_MainTex" };
        XRTexture2D sourceOnly = new() { Name = "floor detail", OriginalPath = imagePath, SamplerName = "_DetailTex" };
        XRMaterial material = new([backed, sourceOnly]) { Name = "FloorMaterial" };
        XRMaterial? cooked = null;
        IRuntimeRenderingHostServices? previousRendering = RuntimeRenderingHostServices.Current;
        try
        {
            Guid backedKey;
            Guid sourceOnlyKey;
            using (AssetSerializationServices.Install(new FakeAssetRootServices(assets)))
            {
                backedKey = CutsceneResidencyAssetKeys.ForTexture(backed);
                sourceOnlyKey = CutsceneResidencyAssetKeys.ForTexture(sourceOnly);
                string yaml = AssetManager.Serializer.Serialize(material);
                if (!yaml.Contains("__assetType:", StringComparison.Ordinal))
                    yaml = $"__assetType: {typeof(XRMaterial).FullName}{Environment.NewLine}{yaml}";
                File.WriteAllText(Path.Combine(assets, "FloorMaterial.asset"), yaml);

                string cookedDirectory = ProjectBuilder.PrepareCookedContentDirectoryForTests(assets, Path.Combine(tempRoot, "Intermediate"));
                cooked = CookedAssetReader.LoadAsset<XRMaterial>(File.ReadAllBytes(Path.Combine(cookedDirectory, "FloorMaterial.asset"))).ShouldNotBeNull();
            }

            XRTexture2D cookedBacked = cooked.Textures[0].ShouldBeOfType<XRTexture2D>();
            XRTexture2D cookedSourceOnly = cooked.Textures[1].ShouldBeOfType<XRTexture2D>();
            cookedBacked.OriginalPath.ShouldBe("game://Textures/floor.png");
            cookedSourceOnly.OriginalPath.ShouldBe("game://Textures/floor.png");

            // Another machine: other asset roots. The baked keys still match.
            using (AssetSerializationServices.Install(new FakeAssetRootServices(Path.Combine(tempRoot, "OtherMachine", "Assets"))))
            {
                CutsceneResidencyAssetKeys.ForTexture(cookedBacked, out bool backedMachineDependent).ShouldBe(backedKey);
                CutsceneResidencyAssetKeys.ForTexture(cookedSourceOnly, out bool sourceOnlyMachineDependent).ShouldBe(sourceOnlyKey);
                backedMachineDependent.ShouldBeFalse();
                sourceOnlyMachineDependent.ShouldBeFalse();
            }

            // The cook machine: the portable path resolves to the source image, and the texture registers its streaming source.
            RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
            using RendererBackendCatalog catalog = new();
            using IDisposable registrations = BuiltInRendererBackendModules.RegisterAll(catalog);
            using IDisposable cookMachine = AssetSerializationServices.Install(new FakeAssetRootServices(assets));
            AssetReferencePath.TryResolveFileSystemPath(cookedSourceOnly.OriginalPath!, out string? resolved).ShouldBeTrue();
            resolved.ShouldBe(Path.GetFullPath(imagePath));
            File.Exists(resolved).ShouldBeTrue();
            cookedSourceOnly.TryRestoreImportedTextureStreamingSource().ShouldBeTrue();
            cookedSourceOnly.FilePath.ShouldBe(Path.GetFullPath(imagePath));
        }
        finally
        {
            RuntimeRenderingHostServices.Current = previousRendering!;
            if (cooked is not null)
            {
                foreach (XRTexture? texture in cooked.Textures)
                    texture?.Destroy(now: true);
                cooked.Destroy(now: true);
            }
            material.Destroy(now: true);
            backed.Destroy(now: true);
            sourceOnly.Destroy(now: true);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
