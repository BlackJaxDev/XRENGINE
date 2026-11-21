using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Editor.Cutscenes;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Rendering.Residency;

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
        public int DisposeCount { get; private set; }
        public bool ThrowOnRequest { get; set; }

        public ResidencyTicket Request(in ResidencyRequest request)
            => ThrowOnRequest ? throw new InvalidOperationException("The test backend failed.") : Inner.Request(in request);

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
            DisposeCount++;
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
        session.PlaybackWaitEnabled = true;
        session.PlaybackWaitMaximumSeconds = maximumWait;
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
        session.PlaybackWaitEnabled = false;
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
    public void MemoryPressure_ReleasesHeldLevelsOutsideTheWindow_OneTimeForEachEvaluation_WithNoAllocation()
    {
        const double frame = 1.0 / 60.0;
        (SceneNode node, CutsceneAsset asset) = MakeAsset();
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
        CutscenePlayer player = new(compiled, bindings, new CutsceneStateSnapshot(bindings));
        FakeResidencyBackend backend = new();
        List<CutsceneCompileDiagnostic> diagnostics = [];
        using CutsceneResidencySession session = CutsceneResidencySession.Create(compiled, bindings, backend, new NoAssets(), diagnostics);
        backend.Planner = session.Planner;
        session.Open(player);

        // Load both shots to T2. Each update applies the completions and requests the next level.
        int completed = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            while (completed < backend.Requests.Count)
                backend.Complete(backend.Requests[completed++].Ticket);
            session.UpdateIdle(player, frame);
        }
        ResidencyTicket heldA = backend.Requests.Single(r => r.Request.AssetId == ShotA && r.Request.TargetLevel == (int)EResidencyLevel.Target).Ticket;
        ResidencyTicket heldB = backend.Requests.Single(r => r.Request.AssetId == ShotB && r.Request.TargetLevel == (int)EResidencyLevel.Target).Ticket;

        // Pressure at 0 s: both shots are in the window, so nothing leaves. The session records the evaluation.
        backend.IsUnderMemoryPressure = true;
        backend.MemoryPressureFrameId = 5;
        session.UpdateIdle(player, frame);
        session.PressureEvictionCount.ShouldBe(0);

        // At 3 s the window starts at 2.5 s, so shot A (0 to 2 s) is outside it. The flag is from the same evaluation
        // (no viewport collected since then), so it is stale and releases nothing. No pressure releases nothing too.
        player.Seek(T(3), ESeekIntent.Jump);
        Step(player, session, 0.0);
        session.UpdateIdle(player, frame);
        int cancelled = backend.Cancelled.Count;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
            session.UpdateIdle(player, frame);
        backend.IsUnderMemoryPressure = false;
        for (int i = 0; i < 10; i++)
            session.UpdateIdle(player, frame);
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
        backend.Cancelled.Count.ShouldBe(cancelled);
        session.PressureEvictionCount.ShouldBe(0);

        // A new evaluation under pressure releases the hold outside the window, one time, with no allocation.
        // The hold in the window and the T0 data stay.
        backend.IsUnderMemoryPressure = true;
        backend.MemoryPressureFrameId = 6;

        // The fake records each cancel in a list. Room in the list keeps that record from allocating in the measure.
        backend.Cancelled.Capacity = backend.Cancelled.Count + 16;
        before = GC.GetAllocatedBytesForCurrentThread();
        session.UpdateIdle(player, frame);
        session.UpdateIdle(player, frame);
        after = GC.GetAllocatedBytesForCurrentThread();

        session.PressureEvictionCount.ShouldBe(1);
        backend.Cancelled.ShouldContain(heldA);
        backend.Cancelled.ShouldNotContain(heldB);
        backend.Cancelled.ShouldNotContain(t => backend.Requests.Any(r => r.Ticket == t && r.Request.TargetLevel == (int)EResidencyLevel.Proxy));
        backend.GetLevel(ShotA).ShouldBe(EResidencyLevel.Proxy);
        (after - before).ShouldBe(0);
    }

    [Test]
    public void SelectionAfterALoad_WaitsWhileAHoldIsActive()
    {
        // The baked manifest also names a texture that is still loading, so the session keeps it and selects again later.
        Guid loading = Guid.NewGuid();
        (SceneNode node, CutsceneAsset asset) = MakeAsset();
        asset.Manifest = new CutsceneResidencyManifest(
            [ShotA, ShotB, loading],
            [
                new ResidencyBucket(T(0), T(2), [new ResidencyEntry(0, (int)EResidencyLevel.Target, 0.5f), new ResidencyEntry(2, (int)EResidencyLevel.Target, 0.5f)]),
                new ResidencyBucket(T(2), T(4), [new ResidencyEntry(1, (int)EResidencyLevel.Target, 0.5f)], startsAtCut: true),
            ]);
        asset.ManifestContentHash = CutsceneManifestHash.Compute(asset);
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
        CutscenePlayer player = new(compiled, bindings, new CutsceneStateSnapshot(bindings));
        FakeResidencyBackend backend = new() { Resolvable = [ShotA, ShotB], AreAssetsLoading = true };
        List<CutsceneCompileDiagnostic> diagnostics = [];
        using CutsceneResidencySession session = CutsceneResidencySession.Create(compiled, bindings, backend, new NoAssets(), diagnostics);
        backend.Planner = session.Planner;
        session.PlaybackWaitEnabled = true;
        session.PlaybackWaitMaximumSeconds = 10.0;
        session.Open(player);
        session.IsWaitingForAssets.ShouldBeTrue();
        backend.SetLevel(ShotA, EResidencyLevel.Target);

        player.Play();
        Step(player, session, 1.9);
        Step(player, session, 0.2);
        session.Planner.IsHoldingPlayback.ShouldBeTrue();
        CutsceneResidencyPlanner heldPlanner = session.Planner;
        int cancelled = backend.Cancelled.Count;

        // The load ends during the hold. The selection waits, so the hold and the held tickets stay.
        backend.AreAssetsLoading = false;
        Step(player, session, 0.1);
        Step(player, session, 0.1);
        session.RefreshCount.ShouldBe(0);
        session.Planner.ShouldBeSameAs(heldPlanner);
        session.Planner.IsHoldingPlayback.ShouldBeTrue();
        player.Position.ShouldBeLessThan(T(2));
        backend.Cancelled.Count.ShouldBe(cancelled);

        // The next shot is resident, so the hold ends. Then the selection runs one time.
        backend.SetLevel(ShotB, EResidencyLevel.Target);
        Step(player, session, 0.1);
        Step(player, session, 0.1);
        session.RefreshCount.ShouldBe(1);
        session.IsWaitingForAssets.ShouldBeFalse();
        X(node).ShouldBe(100f);
        session.Close();
        bindings.Dispose();
    }

    [Test]
    public void SelectionAfterALoad_WaitsForTheHostRefresh_AfterANewPlayer()
    {
        Guid loading = Guid.NewGuid();
        (SceneNode node, CutsceneAsset asset) = MakeAsset();
        asset.Manifest = new CutsceneResidencyManifest(
            [ShotA, loading],
            [new ResidencyBucket(T(0), T(4), [new ResidencyEntry(0, (int)EResidencyLevel.Target, 0.5f), new ResidencyEntry(1, (int)EResidencyLevel.Target, 0.5f)])]);
        asset.ManifestContentHash = CutsceneManifestHash.Compute(asset);
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
        CutscenePlayer player = new(compiled, bindings, new CutsceneStateSnapshot(bindings));
        FakeResidencyBackend backend = new() { Resolvable = [ShotA], AreAssetsLoading = true };
        List<CutsceneCompileDiagnostic> diagnostics = [];
        using CutsceneResidencySession session = CutsceneResidencySession.Create(compiled, bindings, backend, new NoAssets(), diagnostics);
        session.Open(player);

        // A preview recompile gives a new player. The old inputs are not used for the selection after the load.
        CutscenePlayer recompiled = new(compiled, bindings, new CutsceneStateSnapshot(bindings));
        session.AttachPlayer(recompiled);
        backend.AreAssetsLoading = false;
        session.UpdateIdle(recompiled, 1.0 / 60.0);
        session.RefreshCount.ShouldBe(0);
        session.IsWaitingForAssets.ShouldBeTrue();

        // The refresh of the host gives the new inputs and selects with the load ended.
        session.Refresh(compiled, bindings, new NoAssets(), diagnostics);
        session.RefreshCount.ShouldBe(1);
        session.IsWaitingForAssets.ShouldBeFalse();
        session.ManifestState.ShouldBe(ECutsceneManifestState.Unmatched);
        session.Close();
        bindings.Dispose();
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

    [Test]
    public void StopThenPlay_BeforeATick_IsReportedAsAJump()
    {
        (CutscenePlayer player, CutsceneResidencySession session, _, _) = MakePlayback();
        player.Play();
        Step(player, session, 1.0);
        int generation = session.Planner.Generation;

        player.Stop();
        player.Play();
        Step(player, session, 0.1);

        player.LastTickSeek.ShouldBe(ESeekIntent.Jump);
        session.Planner.Generation.ShouldBe(generation + 1);

        // A stop on its own is also a jump at the next tick.
        player.Stop();
        Step(player, session, 0.0);
        player.LastTickSeek.ShouldBe(ESeekIntent.Jump);
        session.Planner.Generation.ShouldBe(generation + 2);
        session.Close();
    }

    [Test]
    public void Hold_DefersTheEventAtTheCut_AndSilencesAudioUntilTheSameTickAsTheRelease()
    {
        (SceneNode node, CutsceneAsset asset) = MakeAsset();
        EventTrack events = new() { Name = "Events" };
        events.Events.Add(new CutsceneEvent { Time = T(2), Key = "cut" });
        asset.Tracks.Add(events);
        asset.ManifestContentHash = CutsceneManifestHash.Compute(asset);
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        CutsceneBindingTable bindings = CutsceneBindingTable.Resolve(compiled, new FakeResolver(node));
        CutscenePlayer player = new(compiled, bindings, new CutsceneStateSnapshot(bindings));
        List<string> fired = [];
        player.EventFired += e => fired.Add(e.Event.Key);
        TrackingBackend backend = new();
        CutsceneResidencySession session = CutsceneResidencySession.Create(compiled, bindings, backend, new NoAssets(), []);
        session.PlaybackWaitEnabled = true;
        session.Open(player);

        player.Play();
        Step(player, session, 1.9);
        player.IsAudioMuted.ShouldBeFalse();
        Step(player, session, 0.2);
        player.IsHeldByGate.ShouldBeTrue();
        player.IsAudioMuted.ShouldBeTrue();
        fired.ShouldBeEmpty();

        Step(player, session, 0.1);
        player.IsAudioMuted.ShouldBeTrue();
        fired.ShouldBeEmpty();

        backend.Inner.SetLevel(ShotB, EResidencyLevel.Target);
        Step(player, session, 0.1);
        player.IsHeldByGate.ShouldBeFalse();
        player.IsAudioMuted.ShouldBeFalse();
        fired.ShouldBe(["cut"]);
        session.Close();
    }

    [Test]
    public void Pause_DuringAHold_KeepsTheHold()
    {
        (CutscenePlayer player, CutsceneResidencySession session, _, SceneNode node) = MakePlayback(maximumWait: 0.5);
        player.Play();
        Step(player, session, 1.9);
        Step(player, session, 0.2);
        session.Planner.IsHoldingPlayback.ShouldBeTrue();

        player.Pause();
        Step(player, session, 2.0);
        session.Planner.IsHoldingPlayback.ShouldBeTrue();

        // The paused time does not count toward the maximum wait, so a resume still holds before the cut.
        player.Play();
        Step(player, session, 0.1);
        session.Planner.IsHoldingPlayback.ShouldBeTrue();
        player.IsHeldByGate.ShouldBeTrue();
        X(node).ShouldBe(0f);
        session.Planner.PlaybackWaitTimeoutCount.ShouldBe(0);
        session.Close();
    }

    [Test]
    public void Director_ClosesResidency_AtEndPlay_AndAtDeactivation()
    {
        foreach (bool endPlay in new[] { true, false })
        {
            (SceneNode node, CutsceneAsset asset) = MakeAsset();
            SceneNode host = new("Director", new Transform());
            host.SetWorldContext(new TestWorld());
            CutsceneDirectorComponent director = host.AddComponent<CutsceneDirectorComponent>()!;
            director.Asset = asset;
            director.BindingResolver = new FakeResolver(node);
            TrackingBackend backend = new();
            director.ResidencyBackendFactory = () => backend;
            director.OpenAsync().IsCompletedSuccessfully.ShouldBeTrue();
            CutsceneResidencySession residency = director.Residency.ShouldNotBeNull();

            if (endPlay)
                director.NotifyEndPlay();
            else
                director.NotifyComponentDeactivated();

            director.IsOpen.ShouldBeFalse();
            residency.IsClosed.ShouldBeTrue();
            backend.DisposeCount.ShouldBe(1);
        }
    }

    [Test]
    public void ResidencyFailure_LeavesAnOpenCutsceneWithoutResidency()
    {
        (SceneNode node, CutsceneAsset asset) = MakeAsset();
        TrackingBackend previewBackend = new() { ThrowOnRequest = true };
        CutscenePreviewHost preview = new(new FakeResolver(node), residencyBackendFactory: () => previewBackend);

        preview.Begin(asset);

        preview.IsActive.ShouldBeTrue();
        preview.Residency.ShouldBeNull();
        preview.Player!.PlaybackGate.ShouldBeNull();
        previewBackend.DisposeCount.ShouldBe(1);
        List<CutsceneCompileDiagnostic> diagnostics = [];
        preview.CopyDiagnostics(diagnostics);
        diagnostics.Count(d => d.Message.Contains("Residency failed to open", StringComparison.Ordinal)).ShouldBe(1);
        preview.Seek(T(3), ESeekIntent.Jump);
        preview.Tick(0.0);
        X(node).ShouldBe(100f);
        preview.End();

        SceneNode host = new("Director", new Transform());
        host.SetWorldContext(new TestWorld());
        CutsceneDirectorComponent director = host.AddComponent<CutsceneDirectorComponent>()!;
        director.Asset = asset;
        director.BindingResolver = new FakeResolver(node);
        director.ResidencyBackendFactory = () => throw new InvalidOperationException("The factory failed.");

        director.OpenAsync().IsCompletedSuccessfully.ShouldBeTrue();

        director.IsOpen.ShouldBeTrue();
        director.Residency.ShouldBeNull();
        director.Diagnostics.Count(d => d.Message.Contains("Residency failed to open", StringComparison.Ordinal)).ShouldBe(1);
        director.Player!.Play();
        director.AdvanceFrame(3.0);
        X(node).ShouldBe(100f);
        director.Close();
    }

    [Test]
    public void PreviewRecompiles_KeepTheBackend_AndReportTheStaleManifestOnce()
    {
        (SceneNode node, CutsceneAsset asset) = MakeAsset();
        TrackingBackend backend = new();
        CutscenePreviewHost host = new(new FakeResolver(node), residencyBackendFactory: () => backend);
        host.Begin(asset);
        host.Tick(0.0);
        CutsceneResidencySession residency = host.Residency.ShouldNotBeNull();
        residency.ManifestState.ShouldBe(ECutsceneManifestState.Current);

        TransformTrackClip clip = (TransformTrackClip)asset.Tracks[0].Clips[0];
        for (int i = 0; i < 10; i++)
        {
            clip.Duration = T(4) - new CutsceneTime(i + 1);
            host.NotifyAssetEdited();
            host.Tick(1.0 / 60.0);
            host.Residency.ShouldBeSameAs(residency);
            host.Player!.PlaybackGate.ShouldBeSameAs(residency);
        }

        host.CompileCount.ShouldBe(11);
        residency.RefreshCount.ShouldBe(0);
        backend.DisposeCount.ShouldBe(0);

        // The edits stopped, so the session selects the manifest one time.
        host.Tick(CutscenePreviewHost.ResidencyRefreshDelaySeconds);
        residency.RefreshCount.ShouldBe(1);
        residency.ManifestState.ShouldBe(ECutsceneManifestState.Stale);

        // A later edit in the same stale state gives no new warning.
        clip.Duration = T(3);
        host.NotifyAssetEdited();
        host.Tick(0.0);
        host.Tick(CutscenePreviewHost.ResidencyRefreshDelaySeconds);
        residency.RefreshCount.ShouldBe(2);

        backend.DisposeCount.ShouldBe(0);
        List<CutsceneCompileDiagnostic> diagnostics = [];
        host.CopyDiagnostics(diagnostics);
        diagnostics.Count(d => d.Message.Contains("stale", StringComparison.Ordinal)).ShouldBe(1);

        host.End();
        backend.DisposeCount.ShouldBe(1);
    }

    [Test]
    public void Director_OnAHostWithoutResidency_OpensWithoutAWarning()
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
            using (CutsceneResidencyRegistration.InstallUnavailable())
            {
                director.OpenAsync().IsCompletedSuccessfully.ShouldBeTrue();
                director.IsOpen.ShouldBeTrue();
                director.Residency.ShouldBeNull();
                director.Diagnostics.ShouldNotContain(d => d.Message.Contains("Residency", StringComparison.Ordinal));
                director.Close();
            }

            CutsceneResidencyRegistration.IsUnavailableOnHost.ShouldBeFalse();
            director.OpenAsync().IsCompletedSuccessfully.ShouldBeTrue();
            director.Diagnostics.ShouldContain(d => d.Message.Contains("Residency is off", StringComparison.Ordinal));
        }
        finally
        {
            CutsceneDirectorComponent.DefaultResidencyBackendFactory = previous;
            director.Close();
        }
    }
}
