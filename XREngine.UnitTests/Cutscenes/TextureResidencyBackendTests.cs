using NUnit.Framework;
using Shouldly;
using XREngine.Cutscenes;
using XREngine.Rendering;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
[NonParallelizable]
public sealed class TextureResidencyBackendTests
{
    private TextureResidencyTestHost _host = null!;
    private readonly Dictionary<Guid, XRTexture2D> _textures = [];

    [SetUp]
    public void SetUp()
    {
        _host = new TextureResidencyTestHost();
        _textures.Clear();
    }

    [TearDown]
    public void TearDown()
        => _host.Dispose();

    [Test]
    public void Backend_SelectsCutsceneProfileUntilDispose()
    {
        TextureResidencyBackend backend = CreateBackend();
        _host.Manager.BudgetProfile.ShouldBe(ETextureStreamingBudgetProfile.Cutscene);

        backend.Dispose();

        _host.Manager.BudgetProfile.ShouldBe(ETextureStreamingBudgetProfile.Default);
    }

    [Test]
    public void Backends_ShareTheCutsceneProfile_UntilTheLastDispose()
    {
        TextureResidencyBackend first = CreateBackend();
        TextureResidencyBackend second = CreateBackend();
        TextureResidencyBackend.GetBudgetProfileOwnerCount(_host.Manager).ShouldBe(2);

        first.Dispose();
        first.Dispose();
        _host.Manager.BudgetProfile.ShouldBe(ETextureStreamingBudgetProfile.Cutscene);
        TextureResidencyBackend.GetBudgetProfileOwnerCount(_host.Manager).ShouldBe(1);

        second.Dispose();
        _host.Manager.BudgetProfile.ShouldBe(ETextureStreamingBudgetProfile.Default);
        TextureResidencyBackend.GetBudgetProfileOwnerCount(_host.Manager).ShouldBe(0);
    }

    [Test]
    public void Levels_MapToPreviewCoarseAndFullMips()
    {
        using TextureResidencyBackend backend = CreateBackend();
        Guid coarseAsset = AddTexture("coarse", 2048u);
        Guid targetAsset = AddTexture("target", 2048u);

        backend.Request(new ResidencyRequest(coarseAsset, (int)EResidencyLevel.Coarse, CutsceneTime.Zero, 0.1f, 0)).IsValid.ShouldBeTrue();
        _host.Manager.Evaluate(102);
        _host.Backend.Loads.ShouldHaveSingleItem().MaxResidentDimension.ShouldBe(512u);

        backend.Request(new ResidencyRequest(targetAsset, (int)EResidencyLevel.Target, CutsceneTime.Zero, 0.1f, 0)).IsValid.ShouldBeTrue();
        _host.Manager.Evaluate(105);
        _host.Backend.Loads[^1].MaxResidentDimension.ShouldBe(2048u);
    }

    [Test]
    public void Request_RejectsAssetThatIsNotLoadedTexture()
    {
        using TextureResidencyBackend backend = CreateBackend();

        backend.Request(new ResidencyRequest(Guid.NewGuid(), 0, CutsceneTime.Zero, 0.0f, 0)).ShouldBe(ResidencyTicket.None);
        backend.RejectedRequestCount.ShouldBe(1);
    }

    [Test]
    public void AssetResolver_FindsModelTexturesThatAreNotAssetsOfTheManager()
    {
        // A model import creates its textures at runtime. They are live objects, but not assets of the manager.
        AssetManager assets = new() { MonitorGameAssetsForChanges = false };
        XRTexture2D modelTexture = new() { Name = "model texture" };
        try
        {
            Resolve(assets, new TextureResidencyKeyIndex(), modelTexture.ID).ShouldBe([modelTexture]);
            Resolve(assets, new TextureResidencyKeyIndex(), Guid.NewGuid()).ShouldBeEmpty();

            // A texture whose destroy is queued is about to go, so it is not found.
            modelTexture.Destroy();
            Resolve(assets, new TextureResidencyKeyIndex(), modelTexture.ID).ShouldBeEmpty();
        }
        finally
        {
            modelTexture.Destroy(now: true);
            assets.Dispose();
        }
    }

    [Test]
    public void AssetResolver_FindsAModelTextureByItsSourceKey_AfterANewImportChangesItsId()
    {
        // Each model import makes a new texture object with a new runtime ID for the same source file.
        AssetManager assets = new() { MonitorGameAssetsForChanges = false };
        TextureResidencyKeyIndex keys = new();
        string source = Path.Combine(Path.GetTempPath(), "cutscene-residency-key", "floor_diff.png");
        XRTexture2D first = new() { Name = "floor_diff", OriginalPath = source, SamplerName = "_MainTex" };
        XRTexture2D? second = null;
        try
        {
            Guid key = CutsceneResidencyAssetKeys.ForTexture(first);
            key.ShouldNotBe(first.ID);
            Resolve(assets, keys, key).ShouldBe([first]);

            first.Destroy(now: true);
            second = new XRTexture2D { Name = "floor_diff", OriginalPath = source, SamplerName = "_MainTex" };
            second.ID.ShouldNotBe(first.ID);
            CutsceneResidencyAssetKeys.ForTexture(second).ShouldBe(key);

            // A new selection lets the index build again.
            keys.AllowRebuild();
            Resolve(assets, keys, key).ShouldBe([second]);
            Resolve(assets, keys, first.ID).ShouldBeEmpty();
        }
        finally
        {
            first.Destroy(now: true);
            second?.Destroy(now: true);
            assets.Dispose();
        }
    }

    [Test]
    public void KeyIndex_BuildsOneTimeForEachSelection_AndFindsALaterTextureAfterTheNextSelection()
    {
        TextureResidencyKeyIndex keys = new();
        string source = Path.Combine(Path.GetTempPath(), "cutscene-residency-key", "later.png");
        Guid key = CutsceneResidencyAssetKeys.ForTexturePath(source, "_MainTex");
        List<XRTexture2D> found = [];

        keys.Find(key, found).ShouldBe(0);
        keys.Find(CutsceneResidencyAssetKeys.ForTexturePath(source, "_Other"), found).ShouldBe(0);
        keys.BuildCount.ShouldBe(1);

        XRTexture2D later = new() { Name = "later", OriginalPath = source, SamplerName = "_MainTex" };
        try
        {
            // The texture came after the build of this selection, so the selection does not scan again.
            keys.Find(key, found).ShouldBe(0);
            keys.BuildCount.ShouldBe(1);

            keys.AllowRebuild();
            keys.Find(key, found).ShouldBe(1);
            found.ShouldBe([later]);
            keys.BuildCount.ShouldBe(2);
        }
        finally
        {
            later.Destroy(now: true);
        }
    }

    [Test]
    public void TextureKeys_AreEqualForTwoAssetRoots_AndForTheCookedPortableReference()
    {
        string firstRoot = Path.Combine(Path.GetTempPath(), "cutscene-key-roots", "machine-a", "Assets");
        string secondRoot = Path.Combine(Path.GetTempPath(), "cutscene-key-roots", "other-folder", "Game", "Assets");
        string relative = Path.Combine("Models", "Sponza", "textures", "Floor_Diff.png");
        XRTexture2D bakedTexture = new() { Name = "floor", OriginalPath = Path.Combine(firstRoot, relative), SamplerName = "_MainTex" };
        XRTexture2D playedTexture = new() { Name = "floor", OriginalPath = Path.Combine(secondRoot, relative.ToLowerInvariant()), SamplerName = "_MainTex" };
        XRTexture2D cookedTexture = new() { Name = "floor", OriginalPath = "game://Models/Sponza/textures/Floor_Diff.png", SamplerName = "_MainTex" };
        try
        {
            Guid baked;
            bool bakedMachineDependent;
            using (AssetSerializationServices.Install(new FakeAssetRootServices(firstRoot)))
                baked = CutsceneResidencyAssetKeys.ForTexture(bakedTexture, out bakedMachineDependent);

            Guid played;
            Guid cooked;
            using (AssetSerializationServices.Install(new FakeAssetRootServices(secondRoot)))
            {
                played = CutsceneResidencyAssetKeys.ForTexture(playedTexture);
                cooked = CutsceneResidencyAssetKeys.ForTexture(cookedTexture);
            }

            bakedMachineDependent.ShouldBeFalse();
            played.ShouldBe(baked);
            cooked.ShouldBe(baked);

            // Outside the asset folders the key uses the full path, and the bake can report it.
            using (AssetSerializationServices.Install(new FakeAssetRootServices(secondRoot)))
            {
                CutsceneResidencyAssetKeys.ForTexture(bakedTexture, out bool outside).ShouldNotBe(baked);
                outside.ShouldBeTrue();
            }
        }
        finally
        {
            bakedTexture.Destroy(now: true);
            playedTexture.Destroy(now: true);
            cookedTexture.Destroy(now: true);
        }
    }

    [Test]
    public void StoredTextureAsset_UsesItsPersistedId()
    {
        XRTexture2D stored = new() { Name = "stored", FilePath = Path.Combine(Path.GetTempPath(), "cutscene-key", $"stored.{AssetSerializationServices.Current.AssetExtension}"), OriginalPath = Path.Combine(Path.GetTempPath(), "cutscene-key", "stored.png") };
        try
        {
            CutsceneResidencyAssetKeys.ForTexture(stored).ShouldBe(stored.ID);
        }
        finally
        {
            stored.Destroy(now: true);
        }
    }

    [Test]
    public void Request_GoesToEachLiveTextureOfAKey_AndIsResidentOnlyWhenEachIsResident()
    {
        _host.Settings.TextureStreamingBudgetProfileFadeFrames = 0;
        AssetManager assets = new() { MonitorGameAssetsForChanges = false };
        string source = Path.Combine(Path.GetTempPath(), "cutscene-residency-fanout", "detail.png");
        (XRTexture2D eager, _) = _host.AddTexture("fanout-eager", 1024u);
        (XRTexture2D deferred, _) = _host.AddTexture("fanout-deferred", 1024u);
        eager.OriginalPath = source;
        eager.SamplerName = "_DetailTex";
        deferred.OriginalPath = source;
        deferred.SamplerName = "_DetailTex";
        Guid key = CutsceneResidencyAssetKeys.ForTexture(eager);
        CutsceneResidencyAssetKeys.ForTexture(deferred).ShouldBe(key);

        try
        {
            using TextureResidencyBackend backend = new(_host.Manager, assets, new TextureResidencyKeyIndex(), useCutsceneBudgetProfile: true);
            backend.CanResolve(key).ShouldBeTrue();
            ResidencyTicket ticket = backend.Request(new ResidencyRequest(key, (int)EResidencyLevel.Target, CutsceneTime.Zero, 0.5f, 0));
            ticket.IsValid.ShouldBeTrue();

            _host.Manager.Evaluate(102);
            _host.Backend.Loads.Select(load => load.Target).ShouldBe([eager, deferred], ignoreOrder: true);

            FakeTextureStreamingResidencyBackend.ScheduledLoad first = _host.Backend.Loads[0];
            first.OnFinished?.Invoke(first.Target);
            _host.Manager.Evaluate(103);
            backend.GetLevel(key).ShouldBe(EResidencyLevel.None);

            _host.Backend.CompleteLast().ShouldBeTrue();
            _host.Manager.Evaluate(104);
            backend.GetLevel(key).ShouldBe(EResidencyLevel.Target);

            backend.Cancel(ticket);
            _host.Manager.ActiveResidencyRequestCount.ShouldBe(0);
        }
        finally
        {
            assets.Dispose();
        }
    }

    [Test]
    public void Request_FindsATextureThatWasDestroyedAndMadeAgain_InTheSameSelection()
    {
        _host.Settings.TextureStreamingBudgetProfileFadeFrames = 0;
        AssetManager assets = new() { MonitorGameAssetsForChanges = false };
        string source = Path.Combine(Path.GetTempPath(), "cutscene-residency-reimport", "wall.png");
        (XRTexture2D first, _) = _host.AddTexture("reimport-first", 1024u);
        first.OriginalPath = source;
        first.SamplerName = "_MainTex";
        Guid key = CutsceneResidencyAssetKeys.ForTexture(first);
        try
        {
            using TextureResidencyBackend backend = new(_host.Manager, assets, new TextureResidencyKeyIndex(), useCutsceneBudgetProfile: true);
            backend.BeginSelection();
            backend.CanResolve(key).ShouldBeTrue();

            // A reimport while the preview is open destroys the texture and makes a new one for the same source.
            first.Destroy(now: true);
            (XRTexture2D second, _) = _host.AddTexture("reimport-second", 1024u);
            second.OriginalPath = source;
            second.SamplerName = "_MainTex";

            backend.Request(new ResidencyRequest(key, (int)EResidencyLevel.Target, CutsceneTime.Zero, 0.5f, 0)).IsValid.ShouldBeTrue();
            _host.Manager.Evaluate(102);
            _host.Backend.Loads.ShouldHaveSingleItem().Target.ShouldBeSameAs(second);
            backend.RejectedRequestCount.ShouldBe(0);
        }
        finally
        {
            assets.Dispose();
        }
    }

    [Test]
    public void Request_FindsANewDuplicate_WhenOneOfTwoKeptTexturesIsMadeAgain()
    {
        _host.Settings.TextureStreamingBudgetProfileFadeFrames = 0;
        AssetManager assets = new() { MonitorGameAssetsForChanges = false };
        string source = Path.Combine(Path.GetTempPath(), "cutscene-residency-partly-dead", "trim.png");
        (XRTexture2D survivor, _) = _host.AddTexture("partly-dead-survivor", 1024u);
        (XRTexture2D replaced, _) = _host.AddTexture("partly-dead-replaced", 1024u);
        foreach (XRTexture2D texture in new[] { survivor, replaced })
        {
            texture.OriginalPath = source;
            texture.SamplerName = "_MainTex";
        }
        Guid key = CutsceneResidencyAssetKeys.ForTexture(survivor);
        try
        {
            using TextureResidencyBackend backend = new(_host.Manager, assets, new TextureResidencyKeyIndex(), useCutsceneBudgetProfile: true);
            backend.BeginSelection();
            backend.CanResolve(key).ShouldBeTrue();

            // A reimport makes one of the two textures again. The other one stays alive.
            replaced.Destroy(now: true);
            (XRTexture2D replacement, _) = _host.AddTexture("partly-dead-replacement", 1024u);
            replacement.OriginalPath = source;
            replacement.SamplerName = "_MainTex";

            backend.Request(new ResidencyRequest(key, (int)EResidencyLevel.Target, CutsceneTime.Zero, 0.5f, 0)).IsValid.ShouldBeTrue();
            _host.Manager.Evaluate(102);
            _host.Backend.Loads.Select(load => load.Target).ShouldBe([survivor, replacement], ignoreOrder: true);
        }
        finally
        {
            assets.Dispose();
        }
    }

    [Test]
    public void KeyIndex_AddsANewDuplicateOfAKeyThatHits_AtTheNextSelection()
    {
        TextureResidencyKeyIndex keys = new();
        string source = Path.Combine(Path.GetTempPath(), "cutscene-residency-key", "duplicate.png");
        XRTexture2D eager = new() { Name = "eager", OriginalPath = source, SamplerName = "_MainTex" };
        XRTexture2D? deferred = null;
        try
        {
            Guid key = CutsceneResidencyAssetKeys.ForTexture(eager);
            List<XRTexture2D> found = [];
            keys.Find(key, found).ShouldBe(1);

            deferred = new XRTexture2D { Name = "deferred", OriginalPath = source, SamplerName = "_MainTex" };
            found.Clear();
            keys.Find(key, found).ShouldBe(1);

            keys.AllowRebuild();
            found.Clear();
            keys.Find(key, found).ShouldBe(2);
            found.ShouldBe([eager, deferred], ignoreOrder: true);
        }
        finally
        {
            eager.Destroy(now: true);
            deferred?.Destroy(now: true);
        }
    }

    [Test]
    public void Request_CountsATextureThatStreamingDoesNotAccept_AndGoesOnWithTheOthers()
    {
        AssetManager assets = new() { MonitorGameAssetsForChanges = false };
        string source = Path.Combine(Path.GetTempPath(), "cutscene-residency-partial", "trim.png");
        (XRTexture2D tracked, _) = _host.AddTexture("partial-tracked", 1024u);
        tracked.OriginalPath = source;
        tracked.SamplerName = "_MainTex";
        XRTexture2D untracked = new() { Name = "partial-untracked", OriginalPath = source, SamplerName = "_MainTex" };
        Guid key = CutsceneResidencyAssetKeys.ForTexture(tracked);
        try
        {
            using TextureResidencyBackend backend = new(_host.Manager, assets, new TextureResidencyKeyIndex(), useCutsceneBudgetProfile: false);

            backend.Request(new ResidencyRequest(key, (int)EResidencyLevel.Proxy, CutsceneTime.Zero, 0.5f, 0)).IsValid.ShouldBeTrue();

            backend.PartialRejectedTextureCount.ShouldBe(1);
            backend.RejectedRequestCount.ShouldBe(0);
        }
        finally
        {
            untracked.Destroy(now: true);
            assets.Dispose();
        }
    }

    [Test]
    public void MemoryPressure_FollowsTheVramBudgetFitOfTheManager()
    {
        _host.Settings.TextureStreamingBudgetProfileFadeFrames = 0;
        using TextureResidencyBackend backend = CreateBackend();
        Guid asset = AddTexture("pressure", 1024u);
        backend.Request(new ResidencyRequest(asset, (int)EResidencyLevel.Target, CutsceneTime.Zero, 0.5f, 0)).IsValid.ShouldBeTrue();

        _host.Manager.Evaluate(102);
        backend.IsUnderMemoryPressure.ShouldBeFalse();
        backend.MemoryPressureFrameId.ShouldBe(102L);

        // The budget holds a 256 texture, so the fit gives the requested texture less than its target.
        _host.Host.TrackedVramBytes = FakeTextureStreamingResidencyBackend.BytesFor(64u);
        _host.Host.TrackedVramBudgetBytes = FakeTextureStreamingResidencyBackend.BytesFor(256u) + 1024L;
        _host.Manager.Evaluate(103);
        _host.Manager.IsUnderBudgetPressure.ShouldBeTrue();
        backend.IsUnderMemoryPressure.ShouldBeTrue();
        backend.MemoryPressureFrameId.ShouldBe(103L);

        _host.Host.TrackedVramBudgetBytes = long.MaxValue;
        _host.Manager.Evaluate(104);
        backend.IsUnderMemoryPressure.ShouldBeFalse();
    }

    [Test]
    public void MemoryPressure_IsSetWhenTheStreamedTexturesUseMoreThanTheBudget()
    {
        using TextureResidencyBackend backend = CreateBackend();
        AddTexture("over-budget", 1024u);

        // The preview alone uses more than the budget, so the manager stops all promotions.
        _host.Host.TrackedVramBytes = FakeTextureStreamingResidencyBackend.BytesFor(64u);
        _host.Host.TrackedVramBudgetBytes = FakeTextureStreamingResidencyBackend.BytesFor(64u) - 1L;
        _host.Manager.Evaluate(102);

        backend.IsUnderMemoryPressure.ShouldBeTrue();
    }

    private static List<XRTexture2D> Resolve(AssetManager assets, TextureResidencyKeyIndex keys, Guid assetId)
    {
        List<XRTexture2D> found = [];
        TextureResidencyBackend.ResolveLoadedTextures(assets, keys, assetId, found);
        return found;
    }

    [Test]
    public void Planner_ReceivesCompletionsAfterLevelIsSet()
    {
        using TextureResidencyBackend backend = CreateBackend();
        Guid asset = AddTexture("planned", 1024u);
        CutsceneResidencyManifest manifest = new(
            [asset],
            [new ResidencyBucket(CutsceneTime.Zero, CutsceneTime.FromSeconds(10.0), [new ResidencyEntry(0, (int)EResidencyLevel.Coarse, 0.5f)])]);
        CutsceneResidencyPlanner planner = new(manifest, backend);
        backend.AttachPlanner(planner);
        List<(Guid Asset, int Level)> loads = [];
        planner.LoadCompleted += (id, level) =>
        {
            backend.GetLevel(id).ShouldBe((EResidencyLevel)level);
            loads.Add((id, level));
        };

        planner.Open();
        planner.PendingProxyCount.ShouldBe(1);

        // The preview is resident already, so the next evaluation completes T0.
        _host.Manager.Evaluate(102);
        backend.SetPlayhead(CutsceneTime.Zero, 1.0);
        planner.Update(CutsceneTime.Zero, 1.0, null, 1.0 / 60.0);
        planner.PendingProxyCount.ShouldBe(0);

        // The update planned T1. The texture promotes, publishes, and the planner gets the completion.
        _host.Manager.Evaluate(103);
        _host.Backend.Loads.ShouldHaveSingleItem().MaxResidentDimension.ShouldBe(512u);
        _host.Backend.CompleteLast().ShouldBeTrue();
        _host.Manager.Evaluate(104);
        planner.Update(CutsceneTime.FromSeconds(1.0 / 60.0), 1.0, null, 1.0 / 60.0);

        loads.ShouldBe([(asset, (int)EResidencyLevel.Proxy), (asset, (int)EResidencyLevel.Coarse)]);
        backend.GetLevel(asset).ShouldBe(EResidencyLevel.Coarse);

        planner.Close();
        backend.GetLevel(asset).ShouldBe(EResidencyLevel.None);
        _host.Manager.ActiveResidencyRequestCount.ShouldBe(0);
    }

    private TextureResidencyBackend CreateBackend()
        => new(_host.Manager, id => _textures.GetValueOrDefault(id), useCutsceneBudgetProfile: true);

    private Guid AddTexture(string name, uint sourceSize)
    {
        (XRTexture2D texture, _) = _host.AddTexture(name, sourceSize);
        Guid id = Guid.NewGuid();
        _textures.Add(id, texture);
        return id;
    }
}
