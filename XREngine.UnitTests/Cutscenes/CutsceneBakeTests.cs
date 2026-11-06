using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Cutscenes;
using XREngine.Data;
using XREngine.Editor.Cutscenes;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture, NonParallelizable]
public sealed class CutsceneBakeTests
{
    private const long ProxyBytes = 1000;

    private sealed class FakeResolver : ICutsceneBindingResolver
    {
        public readonly Dictionary<Guid, SceneNode> ById = [];
        public readonly Dictionary<Guid, Func<SceneNode>> Prefabs = [];

        public SceneNode? FindNodeById(Guid id) => ById.GetValueOrDefault(id);

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => Prefabs.TryGetValue(prefabAssetId, out Func<SceneNode>? create) ? create() : null;
    }

    /// <summary>Gives each registered node one texture asset with a sphere at the node position.</summary>
    private sealed class FakeCollector : ICutsceneResidencyAssetCollector
    {
        private readonly Dictionary<string, (Guid Id, float Radius, long Bytes)> _byName = [];

        public Guid Register(string nodeName, float radius = 1.0f, long bytes = ProxyBytes)
        {
            Guid id = Guid.NewGuid();
            _byName[nodeName] = (id, radius, bytes);
            return id;
        }

        public void Collect(SceneNode node, List<CutsceneResidencyAsset> destination)
        {
            if (_byName.TryGetValue(node.Name ?? string.Empty, out (Guid Id, float Radius, long Bytes) item))
                destination.Add(new CutsceneResidencyAsset(item.Id, EResidencyAssetKind.Texture, node.Transform.WorldTranslation, item.Radius, item.Bytes));
        }
    }

    private sealed class Rig
    {
        public required CutsceneAsset Asset { get; init; }
        public required FakeResolver Resolver { get; init; }
        public required FakeCollector Collector { get; init; }
        public required SceneNode Camera { get; init; }
        public List<SceneNode> Roots { get; } = [];

        public CutsceneBakeOptions Options(long budget = CutsceneBakeOptions.DefaultT0BudgetBytes, bool compress = true)
            => new(Resolver) { Collector = Collector, SceneRoots = Roots, T0BudgetBytes = budget, CompressManifest = compress };

        public int Bind(SceneNode node)
        {
            Resolver.ById[node.ID] = node;
            Asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
            return Asset.Bindings.Count - 1;
        }
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static CameraComponent CreateCamera(SceneNode node)
    {
        CameraComponent component = node.AddComponent<CameraComponent>()!;
        component.CameraParameters = new XRPerspectiveCameraParameters(60f, 1f, 0.1f, 100f);
        return component;
    }

    private static SceneNode CreateNode(string name, Vector3 position)
    {
        SceneNode node = new(name, new Transform());
        ((Transform)node.Transform).Translation = position;
        return node;
    }

    /// <summary>A 4 second cutscene with one camera at the origin that looks along -Z for the whole time.</summary>
    private static Rig MakeRig()
    {
        FakeResolver resolver = new();
        CutsceneAsset asset = new() { Name = "Bake", Duration = T(4) };
        SceneNode cameraNode = CreateNode("Camera", Vector3.Zero);
        CreateCamera(cameraNode);

        Rig rig = new() { Asset = asset, Resolver = resolver, Collector = new FakeCollector(), Camera = cameraNode };
        int cameraSlot = rig.Bind(cameraNode);
        CameraCutTrack cuts = new() { Name = "Cuts" };
        cuts.Clips.Add(new CameraCutClip { Start = T(0), Duration = T(4), CameraBindingSlot = cameraSlot });
        asset.Tracks.Add(cuts);
        return rig;
    }

    private static TransformTrackClip StepClip(double start, double duration, double offset, (double Time, Vector3 Position)[] keys, Quaternion[]? rotations = null, double[]? rotationTimes = null)
        => new()
        {
            Start = T(start),
            Duration = T(duration),
            ClipOffset = T(offset),
            Position = new CutsceneVector3Curve
            {
                Interpolation = ECutsceneCurveInterpolation.Step,
                Times = [.. keys.Select(k => T(k.Time))],
                Values = [.. keys.Select(k => k.Position)],
            },
            Rotation = rotations is null
                ? new CutsceneQuaternionCurve { Times = [T(0)], Values = [Quaternion.Identity] }
                : new CutsceneQuaternionCurve { Interpolation = ECutsceneCurveInterpolation.Step, Times = [.. rotationTimes!.Select(T)], Values = rotations },
            Scale = new CutsceneVector3Curve { Times = [T(0)], Values = [Vector3.One] },
        };

    private static float CoverageAt(CutsceneResidencyManifest manifest, Guid asset, double seconds)
    {
        int index = Array.IndexOf(manifest.Assets, asset);
        if (index < 0)
            return 0.0f;

        int bucket = FindBucket(manifest, T(seconds));
        if (bucket < 0)
            return 0.0f;

        CutsceneResidencyBucketCursor cursor = new(manifest);
        cursor.MoveTo(bucket);
        foreach (int member in cursor.Members.ToArray())
            if (member == index)
                return cursor.GetCoverage(member);
        return 0.0f;
    }

    private static int FindBucket(CutsceneResidencyManifest manifest, CutsceneTime time)
    {
        for (int b = 0; b < manifest.Buckets.Length; b++)
            if (time >= manifest.Buckets[b].Start && time < manifest.Buckets[b].End)
                return b;
        return -1;
    }

    /// <summary>The full entries of the bucket at a time, as (asset ID, level, coverage), in asset ID order.</summary>
    private static List<(Guid Asset, int Level, float Coverage)> EntriesAt(CutsceneResidencyManifest manifest, CutsceneResidencyBucketCursor cursor, CutsceneTime time)
    {
        List<(Guid, int, float)> result = [];
        int bucket = FindBucket(manifest, time);
        if (bucket < 0)
            return result;

        cursor.MoveTo(bucket);
        for (int k = 0; k < cursor.Count; k++)
        {
            ResidencyEntry entry = cursor.GetEntry(k);
            result.Add((manifest.Assets[entry.AssetIndex], entry.TargetLevel, entry.ScreenCoverage));
        }
        result.Sort((left, right) => left.Item1.CompareTo(right.Item1));
        return result;
    }

    [Test]
    public void Bake_SamplesFourTimesASecondAndAtEachCut()
    {
        Rig rig = MakeRig();
        CameraCutTrack cuts = (CameraCutTrack)rig.Asset.Tracks[0];
        cuts.Clips[0].Duration = T(1.1);
        cuts.Clips.Add(new CameraCutClip { Start = T(1.1), Duration = T(2.9), CameraBindingSlot = 0 });

        CutsceneResidencyManifest manifest = CutsceneBakeTool.Bake(rig.Asset, rig.Options(compress: false));

        manifest.Buckets.Length.ShouldBe(17);
        manifest.Buckets.Count(b => b.StartsAtCut).ShouldBe(1);
        manifest.Buckets.Single(b => b.StartsAtCut).Start.ShouldBe(T(1.1));
        manifest.Buckets[^1].End.ShouldBe(T(4));

        // The buckets have no entries, so compression merges them into one bucket before the cut and one from the cut.
        CutsceneResidencyManifest compressed = CutsceneBakeTool.Bake(rig.Asset, rig.Options());
        compressed.Buckets.Length.ShouldBe(2);
        compressed.Buckets[1].StartsAtCut.ShouldBeTrue();
        compressed.Buckets[1].IsKey.ShouldBeTrue();
    }

    [Test]
    public void Bake_NodeBehindCamera_HasLowerCoverageThanNodeInFront()
    {
        Rig rig = MakeRig();
        SceneNode front = CreateNode("Front", new Vector3(0, 0, -10));
        SceneNode behind = CreateNode("Behind", new Vector3(0, 0, 10));
        rig.Roots.Add(front);
        rig.Roots.Add(behind);
        Guid frontAsset = rig.Collector.Register("Front");
        Guid behindAsset = rig.Collector.Register("Behind");

        // At 2 s the camera turns around, so the real evaluator must give the other orientation from then on.
        TransformTrack turn = new() { Name = "Turn", BindingSlot = 0 };
        turn.Clips.Add(StepClip(0, 4, 0, [(0, Vector3.Zero)], [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI)], [0, 2]));
        rig.Asset.Tracks.Add(turn);

        CutsceneResidencyManifest manifest = CutsceneBakeTool.Bake(rig.Asset, rig.Options());

        float frontBefore = CoverageAt(manifest, frontAsset, 1.0);
        float behindBefore = CoverageAt(manifest, behindAsset, 1.0);
        frontBefore.ShouldBeGreaterThan(0.0f);
        behindBefore.ShouldBeLessThan(frontBefore);
        behindBefore.ShouldBe(0.0f);

        CoverageAt(manifest, behindAsset, 3.0).ShouldBeGreaterThan(0.0f);
        CoverageAt(manifest, frontAsset, 3.0).ShouldBe(0.0f);
    }

    [Test]
    public void Bake_ClipStartAndOffset_AreInTheResult()
    {
        Rig rig = MakeRig();
        SceneNode actor = CreateNode("Actor", new Vector3(0, 0, 10));
        Guid actorAsset = rig.Collector.Register("Actor");
        int slot = rig.Bind(actor);

        // The clip starts at 1 s with an offset of 0.5 s. Its key at local 1 s moves the actor in front of the camera at 1.5 s.
        TransformTrack move = new() { Name = "Move", BindingSlot = slot };
        move.Clips.Add(StepClip(1, 3, 0.5, [(0, new Vector3(0, 0, 10)), (1, new Vector3(0, 0, -10))]));
        rig.Asset.Tracks.Add(move);

        CutsceneResidencyManifest manifest = CutsceneBakeTool.Bake(rig.Asset, rig.Options());

        CoverageAt(manifest, actorAsset, 0.5).ShouldBe(0.0f);
        CoverageAt(manifest, actorAsset, 1.25).ShouldBe(0.0f);
        CoverageAt(manifest, actorAsset, 1.5).ShouldBeGreaterThan(0.0f);
        CoverageAt(manifest, actorAsset, 3.75).ShouldBeGreaterThan(0.0f);

        // The bake restores the bound nodes.
        ((Transform)actor.Transform).Translation.ShouldBe(new Vector3(0, 0, 10));
    }

    [Test]
    public void Bake_SpawnedObject_IsInTheResult()
    {
        Rig rig = MakeRig();
        Guid prefab = Guid.NewGuid();
        SceneNode? spawned = null;
        rig.Resolver.Prefabs[prefab] = () => spawned = CreateNode("Spawned", new Vector3(0, 0, -5));
        Guid spawnedAsset = rig.Collector.Register("Spawned");
        rig.Asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.Spawned, PrefabAssetId = prefab });
        int slot = rig.Asset.Bindings.Count - 1;

        ActivationTrack show = new() { Name = "Show", BindingSlot = slot };
        show.Clips.Add(new ActivationClip { Start = T(1), Duration = T(1) });
        rig.Asset.Tracks.Add(show);

        CutsceneResidencyManifest manifest = CutsceneBakeTool.Bake(rig.Asset, rig.Options());

        spawned.ShouldNotBeNull();
        manifest.Assets.ShouldContain(spawnedAsset);
        CoverageAt(manifest, spawnedAsset, 0.5).ShouldBe(0.0f);
        CoverageAt(manifest, spawnedAsset, 1.25).ShouldBeGreaterThan(0.0f);
        CoverageAt(manifest, spawnedAsset, 2.5).ShouldBe(0.0f);
    }

    [Test]
    public void Bake_AboveT0Budget_FailsAndListsTheLargestAssets()
    {
        Rig rig = MakeRig();
        SceneNode small = CreateNode("Small", new Vector3(-1, 0, -10));
        SceneNode large = CreateNode("Large", new Vector3(1, 0, -10));
        rig.Roots.Add(small);
        rig.Roots.Add(large);
        rig.Collector.Register("Small", bytes: 1000);
        Guid largeAsset = rig.Collector.Register("Large", bytes: 5000);

        CutsceneBakeTool.Bake(rig.Asset, rig.Options(budget: 6000), out CutsceneT0Report report);
        report.TotalBytes.ShouldBe(6000);
        report.IsOverBudget.ShouldBeFalse();

        CutsceneBakeException failure = Should.Throw<CutsceneBakeException>(() => CutsceneBakeTool.Bake(rig.Asset, rig.Options(budget: 5999)));
        failure.Report.IsOverBudget.ShouldBeTrue();
        failure.Report.LargestAssets[0].AssetId.ShouldBe(largeAsset);
        failure.Message.ShouldContain(largeAsset.ToString());
    }

    [Test]
    public void ContentHash_ChangesWithTheAsset_AndMakesTheManifestStale()
    {
        Rig rig = MakeRig();
        ulong hash = CutsceneManifestHash.Compute(rig.Asset);
        CutsceneManifestHash.Compute(rig.Asset).ShouldBe(hash);
        CutsceneManifestHash.GetState(rig.Asset).ShouldBe(ECutsceneManifestState.Missing);

        rig.Asset.Manifest = CutsceneBakeTool.Bake(rig.Asset, rig.Options());
        rig.Asset.ManifestContentHash = CutsceneManifestHash.Compute(rig.Asset);
        rig.Asset.ManifestContentHash.ShouldBe(hash);
        CutsceneManifestHash.GetState(rig.Asset).ShouldBe(ECutsceneManifestState.Current);

        rig.Asset.Tracks[0].Clips[0].Duration = T(3);

        CutsceneManifestHash.Compute(rig.Asset).ShouldNotBe(hash);
        CutsceneManifestHash.GetState(rig.Asset).ShouldBe(ECutsceneManifestState.Stale);
    }

    [Test]
    public void StaleManifest_UsesTheApproximatePath_WithOneDiagnostic()
    {
        Rig rig = MakeRig();
        SceneNode actor = CreateNode("Actor", new Vector3(0, 0, -10));
        Guid actorAsset = rig.Collector.Register("Actor");
        int slot = rig.Bind(actor);
        TransformTrack move = new() { Name = "Move", BindingSlot = slot };
        move.Clips.Add(StepClip(0, 4, 0, [(0, new Vector3(0, 0, -10))]));
        rig.Asset.Tracks.Add(move);

        rig.Asset.Manifest = CutsceneBakeTool.Bake(rig.Asset, rig.Options());
        rig.Asset.ManifestContentHash = CutsceneManifestHash.Compute(rig.Asset);

        // A current manifest is used as it is, with no residency diagnostic.
        using (CutsceneResidencySession current = CreateSession(rig, out List<CutsceneCompileDiagnostic> currentDiagnostics))
        {
            current.IsApproximate.ShouldBeFalse();
            current.Manifest.Buckets.Length.ShouldBe(rig.Asset.Manifest.Buckets.Length);
            currentDiagnostics.ShouldBeEmpty();
        }

        move.Clips[0].Duration = T(3);

        using CutsceneResidencySession stale = CreateSession(rig, out List<CutsceneCompileDiagnostic> diagnostics);
        stale.IsApproximate.ShouldBeTrue();
        stale.ManifestState.ShouldBe(ECutsceneManifestState.Stale);
        diagnostics.Count(d => d.Message.Contains("stale", StringComparison.Ordinal)).ShouldBe(1);
        diagnostics.Count.ShouldBe(1);
        stale.Manifest.Assets.ShouldContain(actorAsset);
        stale.Manifest.Buckets[0].Start.ShouldBe(CutsceneTime.Zero);
        stale.Manifest.Buckets[0].Entries.ShouldContain(e => stale.Manifest.Assets[e.AssetIndex] == actorAsset);
    }

    [Test]
    public void CurrentManifest_WhoseTexturesDoNotResolve_IsUnmatched_AndUsesTheApproximatePath_WithOneDiagnostic()
    {
        (Rig rig, Guid bakedAsset) = MakeActorRig();

        // Other asset keys in the scene. The content hash does not change, but no baked texture resolves.
        Guid liveAsset = rig.Collector.Register("Actor");
        CutsceneManifestHash.GetState(rig.Asset).ShouldBe(ECutsceneManifestState.Current);
        FakeResidencyBackend backend = new() { Resolvable = [liveAsset] };

        using CutsceneResidencySession session = CreateSession(rig, backend, out List<CutsceneCompileDiagnostic> diagnostics);

        session.IsApproximate.ShouldBeTrue();
        session.ManifestState.ShouldBe(ECutsceneManifestState.Unmatched);
        string message = diagnostics.ShouldHaveSingleItem().Message;
        message.ShouldContain("1 of 1 baked textures");
        message.ShouldNotContain("stale");
        session.UnresolvedTextureCount.ShouldBe(0);
        session.Manifest.Assets.ShouldBe([liveAsset]);
        session.Manifest.Assets.ShouldNotContain(bakedAsset);

        // One selection resolves each texture of the baked and of the approximate manifest one time.
        backend.SelectionCount.ShouldBe(1);
        backend.CanResolveCalls.ShouldBe(2);
    }

    [TestCase(2, false)]
    [TestCase(3, true)]
    public void CurrentManifest_IsUnmatched_OnlyAboveTheFraction(int missing, bool unmatched)
    {
        (Rig rig, List<Guid> assets) = MakePropRig(20);
        FakeResidencyBackend backend = new() { Resolvable = [.. assets.Skip(missing)] };

        using CutsceneResidencySession session = CreateSession(rig, backend, out List<CutsceneCompileDiagnostic> diagnostics);

        session.ManifestState.ShouldBe(unmatched ? ECutsceneManifestState.Unmatched : ECutsceneManifestState.Current);
        diagnostics.Count.ShouldBe(1);
        if (unmatched)
            diagnostics[0].Message.ShouldContain($"{missing} of 20 baked textures");
        else
        {
            session.UnresolvedTextureCount.ShouldBe(missing);
            diagnostics[0].Message.ShouldContain($"{missing} of 20 texture assets");
            session.Manifest.Assets.Length.ShouldBe(20 - missing);
        }
    }

    [Test]
    public void CurrentManifest_DuringALoad_StaysUntilTheLoadEnds_ThenTheCheckRunsOneTime()
    {
        (Rig rig, Guid bakedAsset) = MakeActorRig();
        FakeResidencyBackend backend = new() { Resolvable = [], AreAssetsLoading = true };
        CompiledCutscene compiled = CutsceneCompiler.Compile(rig.Asset, []);
        CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, rig.Resolver);
        List<CutsceneCompileDiagnostic> diagnostics = [];
        using CutsceneResidencySession session = CutsceneResidencySession.Create(compiled, bindings, backend, rig.Collector, diagnostics);
        backend.Planner = session.Planner;
        CutscenePlayer player = new(compiled, bindings, new CutsceneStateSnapshot(bindings));
        session.Open(player);

        // The texture can still load, so the baked manifest stays, and nothing says that it does not match.
        session.ManifestState.ShouldBe(ECutsceneManifestState.Current);
        session.IsWaitingForAssets.ShouldBeTrue();
        diagnostics.ShouldNotContain(d => d.Message.Contains("baked textures", StringComparison.Ordinal));
        session.UpdateIdle(player, 1.0 / 60.0);
        session.RefreshCount.ShouldBe(0);

        // The load ends with the texture still missing. The next update selects again, one time.
        backend.AreAssetsLoading = false;
        session.UpdateIdle(player, 1.0 / 60.0);
        session.UpdateIdle(player, 1.0 / 60.0);

        session.RefreshCount.ShouldBe(1);
        session.IsWaitingForAssets.ShouldBeFalse();
        session.ManifestState.ShouldBe(ECutsceneManifestState.Unmatched);
        diagnostics.Count(d => d.Message.Contains("1 of 1 baked textures", StringComparison.Ordinal)).ShouldBe(1);
        session.Manifest.Assets.ShouldNotContain(bakedAsset);
        bindings.Dispose();
    }

    [Test]
    public void ManifestDiagnostic_IsReportedAgain_OnlyWhenTheReasonChanges()
    {
        (Rig rig, _) = MakeActorRig();
        FakeResidencyBackend backend = new() { Resolvable = [] };
        CompiledCutscene compiled = CutsceneCompiler.Compile(rig.Asset, []);
        using CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, rig.Resolver);
        List<CutsceneCompileDiagnostic> diagnostics = [];
        using CutsceneResidencySession session = CutsceneResidencySession.Create(compiled, bindings, backend, rig.Collector, diagnostics);
        session.ManifestState.ShouldBe(ECutsceneManifestState.Unmatched);
        int count = diagnostics.Count;

        session.Refresh(compiled, bindings, rig.Collector, diagnostics);
        diagnostics.Count.ShouldBe(count);

        // An edit makes the manifest stale: a new reason, so a new diagnostic.
        rig.Asset.Duration = T(5);
        CompiledCutscene edited = CutsceneCompiler.Compile(rig.Asset, []);
        session.Refresh(edited, bindings, rig.Collector, diagnostics);
        session.ManifestState.ShouldBe(ECutsceneManifestState.Stale);
        diagnostics.Count(d => d.Message.Contains("stale", StringComparison.Ordinal)).ShouldBe(1);
    }

    /// <summary>A rig with one bound actor and a current baked manifest that holds its texture.</summary>
    private static (Rig Rig, Guid BakedAsset) MakeActorRig()
    {
        Rig rig = MakeRig();
        SceneNode actor = CreateNode("Actor", new Vector3(0, 0, -10));
        Guid bakedAsset = rig.Collector.Register("Actor");
        int slot = rig.Bind(actor);
        TransformTrack move = new() { Name = "Move", BindingSlot = slot };
        move.Clips.Add(StepClip(0, 4, 0, [(0, new Vector3(0, 0, -10))]));
        rig.Asset.Tracks.Add(move);
        rig.Asset.Manifest = CutsceneBakeTool.Bake(rig.Asset, rig.Options());
        rig.Asset.ManifestContentHash = CutsceneManifestHash.Compute(rig.Asset);
        return (rig, bakedAsset);
    }

    /// <summary>A rig with a current baked manifest of one texture for each of a number of props in the camera view.</summary>
    private static (Rig Rig, List<Guid> Assets) MakePropRig(int count)
    {
        Rig rig = MakeRig();
        List<Guid> assets = [];
        for (int i = 0; i < count; i++)
        {
            string name = $"Prop{i}";
            SceneNode prop = CreateNode(name, new Vector3((i - (count / 2)) * 0.5f, 0, -10));
            assets.Add(rig.Collector.Register(name));
            rig.Roots.Add(prop);
        }

        rig.Asset.Manifest = CutsceneBakeTool.Bake(rig.Asset, rig.Options());
        rig.Asset.ManifestContentHash = CutsceneManifestHash.Compute(rig.Asset);
        rig.Asset.Manifest.Assets.Length.ShouldBe(count);
        return (rig, assets);
    }

    [Test]
    public void ShippingCook_RejectsAMissingOrStaleManifest()
    {
        Rig rig = MakeRig();

        CutsceneCookCheck.GetRejection(rig.Asset, shippingCook: true).ShouldNotBeNull().ShouldContain("no residency manifest");
        CutsceneCookCheck.GetRejection(rig.Asset, shippingCook: false).ShouldBeNull();

        rig.Asset.Manifest = CutsceneBakeTool.Bake(rig.Asset, rig.Options());
        rig.Asset.ManifestContentHash = CutsceneManifestHash.Compute(rig.Asset);
        CutsceneCookCheck.GetRejection(rig.Asset, shippingCook: true).ShouldBeNull();

        rig.Asset.Duration = T(5);
        CutsceneCookCheck.GetRejection(rig.Asset, shippingCook: true).ShouldNotBeNull().ShouldContain("stale");
    }

    [Test]
    public void Manifest_RoundTripsWithTheAsset_AndDoesNotChangeTheHash()
    {
        Rig rig = MakeRig();
        Guid texture = Guid.NewGuid();
        Guid mesh = Guid.NewGuid();
        rig.Asset.Manifest = new CutsceneResidencyManifest(
            [texture, mesh],
            [EResidencyAssetKind.Texture, EResidencyAssetKind.Mesh],
            [
                new ResidencyBucket(T(0), T(1), [new ResidencyEntry(0, 2, 0.5f)]),
                new ResidencyBucket(T(1), T(3), [new ResidencyEntry(0, 1, 0.25f), new ResidencyEntry(1, 2, 0.75f)], startsAtCut: true),
                ResidencyBucket.CreateDelta(T(3), T(4), [new ResidencyEntry(0, 2, 0.25f)], [1]),
            ]);
        rig.Asset.ManifestContentHash = 0x0123456789ABCDEFUL;

        string yaml = AssetManager.Serializer.Serialize(rig.Asset, typeof(CutsceneAsset));
        CutsceneAsset? fromYaml = AssetManager.Deserializer.Deserialize<CutsceneAsset>(yaml);
        CutsceneAsset? fromCooked = CookedBinarySerializer.Deserialize(typeof(CutsceneAsset), CookedBinarySerializer.Serialize(rig.Asset)) as CutsceneAsset;

        foreach (CutsceneAsset? clone in new[] { fromYaml, fromCooked })
        {
            clone.ShouldNotBeNull();
            clone!.ManifestContentHash.ShouldBe(0x0123456789ABCDEFUL);
            CutsceneResidencyManifest manifest = clone.Manifest.ShouldNotBeNull();
            manifest.Validate();
            manifest.Assets.ShouldBe([texture, mesh]);
            manifest.AssetKinds.ShouldBe([EResidencyAssetKind.Texture, EResidencyAssetKind.Mesh]);
            manifest.Buckets.Length.ShouldBe(3);
            manifest.Buckets[1].StartsAtCut.ShouldBeTrue();
            manifest.Buckets[1].Start.ShouldBe(T(1));
            manifest.Buckets[1].Entries.ShouldBe([new ResidencyEntry(0, 1, 0.25f), new ResidencyEntry(1, 2, 0.75f)]);
            manifest.Buckets[2].IsKey.ShouldBeFalse();
            manifest.Buckets[2].Removed.ShouldBe([1]);
            manifest.Buckets[2].Entries.ShouldBe([new ResidencyEntry(0, 2, 0.25f)]);
            CutsceneManifestHash.Compute(clone).ShouldBe(CutsceneManifestHash.Compute(rig.Asset));
        }
    }

    [Test]
    public void SelectKind_KeepsBucketsAndRemapsEntries()
    {
        Guid texture = Guid.NewGuid();
        Guid mesh = Guid.NewGuid();
        CutsceneResidencyManifest manifest = new(
            [mesh, texture],
            [EResidencyAssetKind.Mesh, EResidencyAssetKind.Texture],
            [new ResidencyBucket(T(0), T(1), [new ResidencyEntry(0, 2, 0.5f), new ResidencyEntry(1, 1, 0.25f)], startsAtCut: true)]);

        CutsceneResidencyManifest textures = manifest.SelectKind(EResidencyAssetKind.Texture);

        textures.Assets.ShouldBe([texture]);
        textures.Buckets.ShouldHaveSingleItem().StartsAtCut.ShouldBeTrue();
        textures.Buckets[0].Entries.ShouldBe([new ResidencyEntry(0, 1, 0.25f)]);
    }

    [Test]
    public void Compression_DecodesToTheUncompressedBake()
    {
        Rig rig = MakeRig();
        SceneNode front = CreateNode("Front", new Vector3(0, 0, -10));
        SceneNode behind = CreateNode("Behind", new Vector3(0, 0, 10));
        rig.Roots.Add(front);
        rig.Roots.Add(behind);
        rig.Collector.Register("Front");
        rig.Collector.Register("Behind");
        SceneNode actor = CreateNode("Actor", new Vector3(0, 0, -20));
        rig.Collector.Register("Actor", radius: 3.0f);
        int slot = rig.Bind(actor);

        TransformTrack turn = new() { Name = "Turn", BindingSlot = 0 };
        turn.Clips.Add(StepClip(0, 4, 0, [(0, Vector3.Zero)], [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI)], [0, 2.5]));
        rig.Asset.Tracks.Add(turn);
        TransformTrack move = new() { Name = "Move", BindingSlot = slot };
        move.Clips.Add(StepClip(0, 4, 0, [(0, new Vector3(0, 0, -20)), (0.5, new Vector3(0, 0, -8)), (1.0, new Vector3(0, 0, -4)), (3.0, new Vector3(0, 0, 6))]));
        rig.Asset.Tracks.Add(move);

        CutsceneResidencyManifest full = CutsceneBakeTool.Bake(rig.Asset, rig.Options(compress: false));
        CutsceneResidencyManifest compressed = CutsceneBakeTool.Bake(rig.Asset, rig.Options());

        compressed.Buckets.Length.ShouldBeLessThan(full.Buckets.Length);
        compressed.Buckets.ShouldContain(b => !b.IsKey);
        CutsceneResidencyBucketCursor fullCursor = new(full);
        CutsceneResidencyBucketCursor compressedCursor = new(compressed);
        for (long flicks = 0; flicks < T(4).Flicks; flicks += T(0.125).Flicks)
            EntriesAt(compressed, compressedCursor, new CutsceneTime(flicks)).ShouldBe(EntriesAt(full, fullCursor, new CutsceneTime(flicks)));
    }

    [Test]
    public void Compression_SeekDecodeAtRandomTimes_MatchesAndDoesNotAllocate()
    {
        (CutsceneResidencyManifest full, CutsceneResidencyManifest compressed) = BuildLongManifests(seconds: 120);
        CutsceneResidencyBucketCursor fullCursor = new(full);
        CutsceneResidencyBucketCursor compressedCursor = new(compressed);
        Random random = new(1234);
        for (int i = 0; i < 300; i++)
        {
            CutsceneTime time = new(random.NextInt64(0, T(120).Flicks));
            EntriesAt(compressed, compressedCursor, time).ShouldBe(EntriesAt(full, fullCursor, time));
        }

        int[] targets = new int[200];
        for (int i = 0; i < targets.Length; i++)
            targets[i] = random.Next(compressed.Buckets.Length);
        compressedCursor.MoveTo(0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < targets.Length; i++)
            compressedCursor.MoveTo(targets[i]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBe(0);
    }

    [Test]
    public void Compression_ShrinksALongCutscene_AndKeepsKeysAtTheInterval()
    {
        (CutsceneResidencyManifest full, CutsceneResidencyManifest compressed) = BuildLongManifests(seconds: 600);

        full.Buckets.Length.ShouldBe(2400);
        int fullEntries = full.CountStoredEntries();
        int compressedEntries = compressed.CountStoredEntries();
        TestContext.Out.WriteLine($"Stored entries: {fullEntries} uncompressed, {compressedEntries} compressed, {compressed.Buckets.Length} buckets.");
        compressedEntries.ShouldBeLessThan(fullEntries / 5);

        CutsceneTime lastKey = CutsceneTime.Zero;
        foreach (ResidencyBucket bucket in compressed.Buckets)
        {
            if (bucket.StartsAtCut)
                bucket.IsKey.ShouldBeTrue();
            if (bucket.IsKey)
                lastKey = bucket.Start;
            else
                (bucket.Start - lastKey).ShouldBeLessThan(CutsceneResidencyManifestBuilder.DefaultKeyInterval);
        }
    }

    [Test]
    public void InvalidManifest_GivesOneDiagnostic()
    {
        Rig rig = MakeRig();
        Guid asset = Guid.NewGuid();
        rig.Asset.Manifest = new CutsceneResidencyManifest
        {
            Assets = [asset],
            AssetKinds = [EResidencyAssetKind.Texture],
            Buckets = [ResidencyBucket.CreateDelta(T(0), T(4), [new ResidencyEntry(0, 2, 0.5f)], [])],
        };
        rig.Asset.ManifestContentHash = CutsceneManifestHash.Compute(rig.Asset);
        Should.Throw<ArgumentException>(() => rig.Asset.Manifest.Validate());

        using CutsceneResidencySession session = CreateSession(rig, out List<CutsceneCompileDiagnostic> diagnostics);

        session.ManifestState.ShouldBe(ECutsceneManifestState.Invalid);
        session.IsApproximate.ShouldBeTrue();
        diagnostics.ShouldHaveSingleItem().Message.ShouldContain("not valid");
    }

    /// <summary>
    /// A long synthetic bake: 4 samples each second, 300 assets in 30 groups, a camera cut every 10 s. Each group shows
    /// for a few seconds at a time, and a few assets change their coverage in each sample, as moving objects do.
    /// </summary>
    private static (CutsceneResidencyManifest Full, CutsceneResidencyManifest Compressed) BuildLongManifests(int seconds)
    {
        CutsceneResidencyManifestBuilder full = new() { Compress = false };
        CutsceneResidencyManifestBuilder compressed = new();
        const int AssetCount = 300;
        Guid[] ids = new Guid[AssetCount];
        for (int i = 0; i < AssetCount; i++)
        {
            ids[i] = new Guid(i + 1, 0, 0, new byte[8]);
            full.AddAsset(ids[i], EResidencyAssetKind.Texture, ProxyBytes);
            compressed.AddAsset(ids[i], EResidencyAssetKind.Texture, ProxyBytes);
        }

        int samples = seconds * CutsceneBakeTool.SamplesPerSecond;
        long step = CutsceneTime.FlicksPerSecond / CutsceneBakeTool.SamplesPerSecond;
        foreach (CutsceneResidencyManifestBuilder builder in new[] { full, compressed })
        {
            for (int s = 0; s < samples; s++)
            {
                CutsceneTime start = new(s * step);
                bool cut = s > 0 && s % (10 * CutsceneBakeTool.SamplesPerSecond) == 0;
                builder.BeginBucket(start, new CutsceneTime((s + 1) * step), cut);
                int shot = s / (10 * CutsceneBakeTool.SamplesPerSecond);
                for (int group = 0; group < 30; group++)
                {
                    if ((group + shot) % 3 != 0)
                        continue;
                    for (int k = 0; k < 10; k++)
                    {
                        int asset = (group * 10) + k;
                        bool moving = k == 0;
                        float coverage = moving ? 0.05f + (0.002f * (s % 40)) : 0.02f + (0.01f * k);
                        builder.AddEntry(asset, CutsceneResidencyManifestBuilder.SelectLevel(coverage, CutsceneResidencyManifestBuilder.DefaultTargetCoverage), coverage);
                    }
                }
                builder.EndBucket();
            }
        }

        return (full.Build(), compressed.Build());
    }

    private sealed class DictionaryClip : CutsceneClip
    {
        public Dictionary<string, int> Values { get; set; } = new() { ["a"] = 1 };
    }

    private sealed class DecimalClip : CutsceneClip
    {
        public decimal Amount { get; set; } = 1.5m;
    }

    private sealed class TimeSpanClip : CutsceneClip
    {
        public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);
    }

    [Test]
    public void ContentHash_RejectsMemberTypesThatItCannotHashExactly()
    {
        foreach (CutsceneClip clip in new CutsceneClip[] { new DictionaryClip(), new DecimalClip(), new TimeSpanClip() })
        {
            Rig rig = MakeRig();
            TransformTrack track = new() { Name = "Unsupported" };
            clip.Duration = T(1);
            track.Clips.Add(clip);
            rig.Asset.Tracks.Add(track);

            Should.Throw<NotSupportedException>(() => CutsceneManifestHash.Compute(rig.Asset)).Message.ShouldContain(clip.GetType().Name);
        }
    }

    [Test]
    public void ContentHash_Exception_GivesTheInvalidStateWithOneDiagnostic()
    {
        Rig rig = MakeRig();
        rig.Asset.Manifest = CutsceneBakeTool.Bake(rig.Asset, rig.Options());
        rig.Asset.ManifestContentHash = CutsceneManifestHash.Compute(rig.Asset);
        TransformTrack track = new() { Name = "Unsupported" };
        track.Clips.Add(new TimeSpanClip { Duration = T(1) });
        rig.Asset.Tracks.Add(track);

        using CutsceneResidencySession session = CreateSession(rig, out List<CutsceneCompileDiagnostic> diagnostics);

        session.ManifestState.ShouldBe(ECutsceneManifestState.Invalid);
        diagnostics.ShouldHaveSingleItem().Message.ShouldContain("content hash");
    }

    [Test]
    public void ContentHash_SurvivesTheRoundTripOfFloatCurves()
    {
        Rig rig = MakeRig();
        float[] awkward = [0.1f, 1.0f / 3.0f, 1e-7f, 123456.79f, -2.5e-12f, MathF.PI, 16777217.0f, float.Epsilon];
        TransformTrack move = new() { Name = "Move" };
        move.Clips.Add(new TransformTrackClip
        {
            Start = T(0.1),
            Duration = T(1.0 / 3.0),
            Speed = 1.0 / 3.0,
            Position = new CutsceneVector3Curve
            {
                Times = [T(0), T(0.123456789), T(1.0 / 7.0)],
                Values = [new Vector3(awkward[0], awkward[1], awkward[2]), new Vector3(awkward[3], awkward[4], awkward[5]), new Vector3(awkward[6], awkward[7], -awkward[0])],
                DefaultValue = new Vector3(awkward[1], awkward[3], awkward[5]),
            },
            Rotation = new CutsceneQuaternionCurve
            {
                Times = [T(0), T(0.2)],
                Values = [Quaternion.CreateFromYawPitchRoll(0.1f, 0.7f, 1.3f), Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 2.2f)],
            },
            Scale = new CutsceneVector3Curve { Times = [T(0)], Values = [new Vector3(awkward[2], awkward[6], awkward[1])] },
        });
        rig.Asset.Tracks.Add(move);

        PropertyTrack member = new() { Name = "Member", ComponentType = "LightComponent", MemberPath = "Intensity" };
        member.Clips.Add(new PropertyTrackClip
        {
            Duration = T(1),
            FloatCurve = new CutsceneFloatCurve { Times = [T(0), T(0.3)], Values = [awkward[0], awkward[4]], DefaultValue = awkward[7] },
        });
        rig.Asset.Tracks.Add(member);

        CameraPropertyTrack lens = new() { Name = "Lens" };
        lens.Clips.Add(new CameraPropertyClip
        {
            Duration = T(1),
            FieldOfView = new CutsceneFloatCurve { Times = [T(0)], Values = [awkward[1] * 100.0f], DefaultValue = awkward[3] },
        });
        rig.Asset.Tracks.Add(lens);

        ulong hash = CutsceneManifestHash.Compute(rig.Asset);
        string yaml = AssetManager.Serializer.Serialize(rig.Asset, typeof(CutsceneAsset));
        CutsceneAsset fromYaml = AssetManager.Deserializer.Deserialize<CutsceneAsset>(yaml).ShouldNotBeNull();
        CutsceneAsset fromCooked = (CookedBinarySerializer.Deserialize(typeof(CutsceneAsset), CookedBinarySerializer.Serialize(rig.Asset)) as CutsceneAsset).ShouldNotBeNull();

        CutsceneManifestHash.Compute(fromYaml).ShouldBe(hash);
        CutsceneManifestHash.Compute(fromCooked).ShouldBe(hash);

        // The test must see a change of one float bit.
        ((TransformTrackClip)fromYaml.Tracks[^3].Clips[0]).Position.Values[0].X = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(awkward[0]) + 1);
        CutsceneManifestHash.Compute(fromYaml).ShouldNotBe(hash);
    }

    [Test]
    public void Bake_MakesOneAudioProxyForEachSource_CountsItInT0_AndReusesACurrentProxy()
    {
        string root = Path.Combine(Path.GetTempPath(), "xre-audio-proxy-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "xre-audio-outside-" + Guid.NewGuid().ToString("N"), "Far.wav");
        Directory.CreateDirectory(Path.Combine(root, "Audio"));
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(root));
            string wavPath = Path.Combine(root, "Audio", "Line.wav");
            File.WriteAllBytes(wavPath, BuildMonoWav(44100, 44100, 0.25));

            Rig rig = MakeRig();
            AudioTrack voice = new() { Name = "Voice", BindingSlot = rig.Bind(CreateNode("Speaker", new Vector3(0, 0, -5))) };
            voice.Clips.Add(new AudioTrackClip { Start = T(0), Duration = T(1), SourcePath = wavPath });
            voice.Clips.Add(new AudioTrackClip { Start = T(2), Duration = T(1), SourcePath = wavPath });
            voice.Clips.Add(new AudioTrackClip { Start = T(3), Duration = T(1), SourcePath = outside });
            rig.Asset.Tracks.Add(voice);

            List<CutsceneCompileDiagnostic> diagnostics = [];
            CutsceneBakeOptions options = new(rig.Resolver) { Collector = rig.Collector, SceneRoots = rig.Roots, Diagnostics = diagnostics };
            CutsceneResidencyManifest manifest = CutsceneBakeTool.Bake(rig.Asset, options, out CutsceneT0Report report, out List<AudioProxyAsset> proxies);

            // Two clips share one source, so they share one proxy. The source outside the asset folders gets none.
            proxies.Count.ShouldBe(1);
            AudioProxyAsset proxy = proxies[0];
            proxy.SourceReference.ShouldBe("game://Audio/Line.wav");
            proxy.SourceReference.ShouldNotContain(root);
            proxy.SampleRate.ShouldBe(ImaAdpcmDecoder.ProxySampleRate);
            proxy.FrameCount.ShouldBe(22050);
            proxy.SourceContentHash.ShouldNotBe(0UL);
            diagnostics.Count(d => d.Message.Contains("gets no audio proxy")).ShouldBe(1);

            int index = Array.IndexOf(manifest.Assets, CutsceneResidencyAssetKeys.ForAudioSource(proxy.SourceReference));
            index.ShouldBeGreaterThanOrEqualTo(0);
            manifest.AssetKinds[index].ShouldBe(EResidencyAssetKind.Audio);
            report.AssetCount.ShouldBe(1);
            report.TotalBytes.ShouldBe(proxy.EncodedData.Length);
            report.LargestAssets[0].Kind.ShouldBe(EResidencyAssetKind.Audio);
            report.Describe().ShouldNotContain("not counted");

            // A proxy of the same source and content is used again. A changed source gives a new proxy.
            rig.Asset.AudioProxies = proxies;
            CutsceneBakeTool.Bake(rig.Asset, options, out _, out List<AudioProxyAsset> again);
            again.Count.ShouldBe(1);
            again[0].ShouldBeSameAs(proxy);

            File.WriteAllBytes(wavPath, BuildMonoWav(44100, 88200, 0.5));
            CutsceneBakeTool.Bake(rig.Asset, options, out CutsceneT0Report changedReport, out List<AudioProxyAsset> changed);
            changed[0].ShouldNotBeSameAs(proxy);
            changed[0].FrameCount.ShouldBe(44100);
            changedReport.TotalBytes.ShouldBe(changed[0].EncodedData.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A 16-bit mono WAV file of a 440 Hz sine.</summary>
    private static byte[] BuildMonoWav(int sampleRate, int frames, double amplitude)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(frames * 2);
        for (int f = 0; f < frames; f++)
            writer.Write((short)Math.Round(amplitude * 32767.0 * Math.Sin(2.0 * Math.PI * 440.0 * f / sampleRate)));
        writer.Flush();
        return stream.ToArray();
    }

    private static CutsceneResidencySession CreateSession(Rig rig, out List<CutsceneCompileDiagnostic> diagnostics)
        => CreateSession(rig, new FakeResidencyBackend(), out diagnostics);

    private static CutsceneResidencySession CreateSession(Rig rig, FakeResidencyBackend backend, out List<CutsceneCompileDiagnostic> diagnostics)
    {
        diagnostics = [];
        CompiledCutscene compiled = CutsceneCompiler.Compile(rig.Asset, []);
        using CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, rig.Resolver);
        return CutsceneResidencySession.Create(compiled, bindings, backend, rig.Collector, diagnostics);
    }
}
