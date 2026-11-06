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
            TextureResidencyBackend.ResolveLoadedTexture(assets, modelTexture.ID).ShouldBeSameAs(modelTexture);
            TextureResidencyBackend.ResolveLoadedTexture(assets, Guid.NewGuid()).ShouldBeNull();
        }
        finally
        {
            modelTexture.Destroy();
            assets.Dispose();
        }
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
