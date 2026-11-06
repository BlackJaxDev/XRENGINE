using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Editor.Cutscenes;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture, NonParallelizable]
public sealed class CutsceneResidencyWiringTests
{
    private static readonly Guid ShotA = Guid.NewGuid();
    private static readonly Guid ShotB = Guid.NewGuid();

    private sealed class FakeResolver(SceneNode node) : ICutsceneBindingResolver
    {
        public SceneNode? FindNodeById(Guid id) => id == node.ID ? node : null;

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    /// <summary>A fake backend that records the planner contract calls and its disposal.</summary>
    private sealed class TrackingBackend : IResidencyBackend, IDisposable
    {
        public FakeResidencyBackend Inner { get; } = new();
        public List<string> Calls { get; } = [];
        public bool Disposed { get; private set; }

        public ResidencyTicket Request(in ResidencyRequest request) => Inner.Request(in request);

        public void Cancel(ResidencyTicket ticket) => Inner.Cancel(ticket);

        public EResidencyLevel GetLevel(Guid assetId) => Inner.GetLevel(assetId);

        public void AttachPlanner(CutsceneResidencyPlanner planner)
        {
            Calls.Add(planner.IsOpen ? "attach-after-open" : "attach");
            Inner.Planner = planner;
        }

        public void SetPlayhead(CutsceneTime position, double rate)
            => Calls.Add("playhead");

        public void Dispose()
        {
            Calls.Add(Inner.Planner is { IsClosed: true } ? "dispose-after-close" : "dispose");
            Disposed = true;
        }
    }

    private sealed class TestWorld : IRuntimeWorldContext
    {
        public bool IsPlaySessionActive => false;

        public void RegisterTick(ETickGroup group, int order, WorldTick tick)
        {
        }

        public void UnregisterTick(ETickGroup group, int order, WorldTick tick)
        {
        }

        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject)
        {
        }

        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix)
        {
        }
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    /// <summary>
    /// A 4 second cutscene with a camera cut at 2 s. The node moves from x = 0 to x = 100 at the cut, so a frame
    /// that shows x = 100 shows the new shot. The baked manifest needs shot B at T2 from the cut.
    /// </summary>
    private static (SceneNode Node, CutsceneAsset Asset) MakeAsset()
    {
        SceneNode node = new("Shot", new Transform());
        CutsceneAsset asset = new() { Name = "Wait", Duration = T(4) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        TransformTrack track = new() { Name = "Shot", BindingSlot = 0 };
        track.Clips.Add(new TransformTrackClip
        {
            Start = T(0),
            Duration = T(4),
            Position = new CutsceneVector3Curve
            {
                Interpolation = ECutsceneCurveInterpolation.Step,
                Times = [T(0), T(2)],
                Values = [Vector3.Zero, new Vector3(100, 0, 0)],
            },
            Rotation = new CutsceneQuaternionCurve { Times = [T(0)], Values = [Quaternion.Identity] },
            Scale = new CutsceneVector3Curve { Times = [T(0)], Values = [Vector3.One] },
        });
        asset.Tracks.Add(track);

        asset.Manifest = new CutsceneResidencyManifest(
            [ShotA, ShotB],
            [
                new ResidencyBucket(T(0), T(2), [new ResidencyEntry(0, (int)EResidencyLevel.Target, 0.5f)]),
                new ResidencyBucket(T(2), T(4), [new ResidencyEntry(1, (int)EResidencyLevel.Target, 0.5f)], startsAtCut: true),
            ]);
        asset.ManifestContentHash = CutsceneManifestHash.Compute(asset);
        return (node, asset);
    }

    private static (CutscenePlayer Player, CutsceneResidencySession Session, TrackingBackend Backend, SceneNode Node) MakePlayback(double maximumWait = 1.0)
    {
        (SceneNode node, CutsceneAsset asset) = MakeAsset();
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
        CutscenePlayer player = new(compiled, bindings, new CutsceneStateSnapshot(bindings));
        TrackingBackend backend = new();
        List<CutsceneCompileDiagnostic> diagnostics = [];
        CutsceneResidencySession session = CutsceneResidencySession.Create(compiled, bindings, backend, new NoAssets(), diagnostics);
        diagnostics.ShouldBeEmpty();
        session.Planner.PlaybackWaitEnabled = true;
        session.Planner.PlaybackWaitMaximumSeconds = maximumWait;
        session.Open(player);
        return (player, session, backend, node);
    }

    private sealed class NoAssets : ICutsceneResidencyAssetCollector
    {
        public void Collect(SceneNode node, List<CutsceneResidencyAsset> destination)
        {
        }
    }

    private static void Step(CutscenePlayer player, CutsceneResidencySession session, double seconds)
    {
        session.BeginTick();
        player.Tick(seconds);
        session.CompleteTick(player, seconds);
    }

    private static float X(SceneNode node) => ((Transform)node.Transform).Translation.X;

    [Test]
    public void PlaybackWait_HoldsBeforeTheCut_UntilTheNextShotIsResident()
    {
        (CutscenePlayer player, CutsceneResidencySession session, TrackingBackend backend, SceneNode node) = MakePlayback();
        backend.Inner.SetLevel(ShotA, EResidencyLevel.Target);
        player.Play();
        Step(player, session, 1.9);
        X(node).ShouldBe(0f);

        // The step crosses the cut. The planner holds before the evaluation, so the frame shows the old shot.
        Step(player, session, 0.2);
        session.Planner.IsHoldingPlayback.ShouldBeTrue();
        player.IsHeldByGate.ShouldBeTrue();
        player.Position.ShouldBeLessThan(T(2));
        X(node).ShouldBe(0f);

        Step(player, session, 0.1);
        player.Position.ShouldBe(new CutsceneTime(T(2).Flicks - 1));
        X(node).ShouldBe(0f);

        backend.Inner.SetLevel(ShotB, EResidencyLevel.Target);
        Step(player, session, 0.1);
        session.Planner.IsHoldingPlayback.ShouldBeFalse();
        player.IsHeldByGate.ShouldBeFalse();
        player.Position.ShouldBeGreaterThanOrEqualTo(T(2));
        X(node).ShouldBe(100f);
        session.Planner.PlaybackWaitTimeoutCount.ShouldBe(0);
        session.Close();
    }

    [Test]
    public void PlaybackWait_EndsAtTheMaximumWait()
    {
        (CutscenePlayer player, CutsceneResidencySession session, _, SceneNode node) = MakePlayback(maximumWait: 0.5);
        player.Play();
        Step(player, session, 1.9);
        Step(player, session, 0.2);
        session.Planner.IsHoldingPlayback.ShouldBeTrue();

        Step(player, session, 0.3);
        X(node).ShouldBe(0f);
        Step(player, session, 0.3);

        session.Planner.PlaybackWaitTimeoutCount.ShouldBe(1);
        session.Planner.IsHoldingPlayback.ShouldBeFalse();
        player.Position.ShouldBeGreaterThanOrEqualTo(T(2));
        X(node).ShouldBe(100f);
        session.Close();
    }

    [Test]
    public void PlaybackWait_Off_DoesNotHold()
    {
        (CutscenePlayer player, CutsceneResidencySession session, _, SceneNode node) = MakePlayback();
        session.Planner.PlaybackWaitEnabled = false;
        player.Play();
        Step(player, session, 1.9);
        Step(player, session, 0.2);

        player.IsHeldByGate.ShouldBeFalse();
        X(node).ShouldBe(100f);
        session.Close();
    }

    [Test]
    public void Session_FollowsTheBackendContract()
    {
        (CutscenePlayer player, CutsceneResidencySession session, TrackingBackend backend, _) = MakePlayback();
        backend.Calls[0].ShouldBe("attach");
        session.Planner.IsOpen.ShouldBeTrue();
        player.PlaybackGate.ShouldBeSameAs(session);

        Step(player, session, 0.0);
        backend.Calls[^1].ShouldBe("playhead");

        session.Close();
        backend.Calls[^1].ShouldBe("dispose-after-close");
        player.PlaybackGate.ShouldBeNull();
        backend.Inner.Cancelled.Count.ShouldBe(backend.Inner.Requests.Count);
    }

    [Test]
    public void Seek_IsReportedToThePlannerWithItsIntent()
    {
        (CutscenePlayer player, CutsceneResidencySession session, _, _) = MakePlayback();
        Step(player, session, 0.0);
        int generation = session.Planner.Generation;

        player.Seek(T(3), ESeekIntent.Jump);
        Step(player, session, 0.0);
        player.LastTickSeek.ShouldBe(ESeekIntent.Jump);
        session.Planner.Generation.ShouldBe(generation + 1);

        // A tick with no seek reports nothing.
        Step(player, session, 0.0);
        player.LastTickSeek.ShouldBeNull();
        session.Planner.Generation.ShouldBe(generation + 1);

        player.Seek(T(1), ESeekIntent.Scrub);
        Step(player, session, 0.0);
        player.LastTickSeek.ShouldBe(ESeekIntent.Scrub);
        session.Planner.Generation.ShouldBe(generation + 1);
        session.Close();
    }

    [Test]
    public void Director_OwnsTheResidencyLifetime()
    {
        (SceneNode node, CutsceneAsset asset) = MakeAsset();
        SceneNode host = new("Director", new Transform());
        host.SetWorldContext(new TestWorld());
        CutsceneDirectorComponent director = host.AddComponent<CutsceneDirectorComponent>()!;
        director.Asset = asset;
        director.BindingResolver = new FakeResolver(node);
        director.PlaybackWaitEnabled.ShouldBeFalse();
        TrackingBackend backend = new();
        director.ResidencyBackendFactory = () => backend;
        director.PlaybackWaitEnabled = true;

        director.OpenAsync().IsCompletedSuccessfully.ShouldBeTrue();

        CutsceneResidencySession residency = director.Residency.ShouldNotBeNull();
        residency.IsApproximate.ShouldBeFalse();
        residency.Planner.IsOpen.ShouldBeTrue();
        residency.Planner.PlaybackWaitEnabled.ShouldBeTrue();
        backend.Inner.Requests.Count.ShouldBe(2);

        backend.Inner.SetLevel(ShotA, EResidencyLevel.Target);
        director.Player!.Play();
        director.AdvanceFrame(1.9);
        director.AdvanceFrame(0.2);
        residency.Planner.IsHoldingPlayback.ShouldBeTrue();
        X(node).ShouldBe(0f);

        director.Close();
        director.Residency.ShouldBeNull();
        residency.Planner.IsClosed.ShouldBeTrue();
        backend.Disposed.ShouldBeTrue();
        backend.Calls[^1].ShouldBe("dispose-after-close");
    }

    [Test]
    public void Director_WithoutABackend_ReportsThatResidencyIsOff()
    {
        (SceneNode node, CutsceneAsset asset) = MakeAsset();
        SceneNode host = new("Director", new Transform());
        host.SetWorldContext(new TestWorld());
        CutsceneDirectorComponent director = host.AddComponent<CutsceneDirectorComponent>()!;
        director.Asset = asset;
        director.BindingResolver = new FakeResolver(node);
        Func<CutsceneDirectorComponent, IResidencyBackend?>? previous = CutsceneDirectorComponent.DefaultResidencyBackendFactory;
        try
        {
            CutsceneDirectorComponent.DefaultResidencyBackendFactory = null;
            director.OpenAsync().IsCompletedSuccessfully.ShouldBeTrue();

            director.IsOpen.ShouldBeTrue();
            director.Residency.ShouldBeNull();
            director.Diagnostics.ShouldContain(d => d.Message.Contains("Residency is off", StringComparison.Ordinal));
        }
        finally
        {
            CutsceneDirectorComponent.DefaultResidencyBackendFactory = previous;
            director.Close();
        }
    }

    [Test]
    public void PreviewHost_ReportsALoopWrapAsAJump_AndDoesNotWaitByDefault()
    {
        (SceneNode node, CutsceneAsset asset) = MakeAsset();
        TrackingBackend backend = new();
        CutscenePreviewHost host = new(new FakeResolver(node), residencyBackendFactory: () => backend);
        host.PlaybackWaitEnabled.ShouldBeFalse();

        host.Begin(asset);
        CutsceneResidencySession residency = host.Residency.ShouldNotBeNull();
        residency.Planner.PlaybackWaitEnabled.ShouldBeFalse();
        host.Tick(0.0);
        int generation = residency.Planner.Generation;

        host.SetLoopRange(T(0), T(1));
        host.Play();
        host.Tick(0.6);
        host.Tick(0.6);
        residency.Planner.Generation.ShouldBe(generation);

        // The wrap is a jump seek that the next tick resolves.
        host.Tick(0.0);
        residency.Planner.Generation.ShouldBe(generation + 1);

        host.End();
        residency.Planner.IsClosed.ShouldBeTrue();
        backend.Disposed.ShouldBeTrue();
    }

    [Test]
    public void PreviewHost_PausedFrames_StillUpdateThePlanner()
    {
        (SceneNode node, CutsceneAsset asset) = MakeAsset();
        TrackingBackend backend = new();
        CutscenePreviewHost host = new(new FakeResolver(node), residencyBackendFactory: () => backend);
        host.Begin(asset);
        host.Tick(0.0);
        int playheads = backend.Calls.Count(c => c == "playhead");

        host.Tick(0.1);

        backend.Calls.Count(c => c == "playhead").ShouldBe(playheads + 1);
        host.End();
    }
}
