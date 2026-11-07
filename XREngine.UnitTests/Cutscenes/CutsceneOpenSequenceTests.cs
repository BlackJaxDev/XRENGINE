using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Cutscenes;
using XREngine.Editor.Cutscenes;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Residency;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture, NonParallelizable]
public sealed class CutsceneOpenSequenceTests
{
    private static readonly Guid ShotA = Guid.NewGuid();
    private static readonly Guid ShotB = Guid.NewGuid();
    private static readonly Vector3 OriginalPosition = new(7, 8, 9);

    private sealed class FakeResolver(SceneNode node) : ICutsceneBindingResolver
    {
        public SceneNode? FindNodeById(Guid id) => id == node.ID ? node : null;

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
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

    /// <summary>A program prewarmer whose states the test sets.</summary>
    private sealed class FakePrewarmer : ICutsceneProgramPrewarmer
    {
        public Dictionary<XRMaterial, (ECutsceneProgramState State, string? Reason)> States { get; } = new(ReferenceEqualityComparer.Instance);

        public List<XRMaterial> Requested { get; } = [];

        public void Request(XRMaterial material)
            => Requested.Add(material);

        public ECutsceneProgramState GetState(XRMaterial material, out string? failureReason)
        {
            if (States.TryGetValue(material, out (ECutsceneProgramState State, string? Reason) value))
            {
                failureReason = value.Reason;
                return value.State;
            }
            failureReason = null;
            return ECutsceneProgramState.Pending;
        }
    }

    private sealed class RecordingProgress : IProgress<float>
    {
        public List<float> Values { get; } = [];

        public void Report(float value) => Values.Add(value);
    }

    private sealed class Rig
    {
        public required SceneNode Node { get; init; }
        public required CutsceneDirectorComponent Director { get; init; }
        public required FakeResidencyBackend Backend { get; init; }
        public required FakePrewarmer Prewarmer { get; init; }
        public required XRMaterial DetailMaterial { get; init; }
        public required XRMaterial ProxyMaterial { get; init; }
        public required CutsceneAsset Asset { get; init; }
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static Rig MakeRig()
    {
        SceneNode node = new("Shot", new Transform());
        ((Transform)node.Transform).Translation = OriginalPosition;

        XRMaterial detail = new() { Name = "DetailMaterial" };
        XRMaterial proxy = new() { Name = "ProxyMaterial" };
        ModelComponent model = node.AddComponent<ModelComponent>()!;
        model.Model = new Model(new SubMesh(
            new SubMeshLOD(detail, XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY), 10.0f),
            new SubMeshLOD(proxy, XRMesh.CreateTriangles(Vector3.UnitZ, Vector3.UnitZ + Vector3.UnitX, Vector3.UnitZ + Vector3.UnitY), 1000.0f))
        {
            Name = "ShotMesh",
        });

        CutsceneAsset asset = new() { Name = "Open", Duration = T(4) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        TransformTrack track = new() { Name = "Shot", BindingSlot = 0, WhenFinished = ECutsceneFinishBehavior.RestoreState };
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

        SceneNode host = new("Director", new Transform());
        host.SetWorldContext(new TestWorld());
        CutsceneDirectorComponent director = host.AddComponent<CutsceneDirectorComponent>()!;
        director.Asset = asset;
        director.BindingResolver = new FakeResolver(node);
        FakeResidencyBackend backend = new();
        director.ResidencyBackendFactory = () => backend;

        return new Rig
        {
            Node = node,
            Director = director,
            Backend = backend,
            Prewarmer = new FakePrewarmer(),
            DetailMaterial = detail,
            ProxyMaterial = proxy,
            Asset = asset,
        };
    }

    private static Task<bool> Start(Rig rig, CutsceneOpenSequence sequence, IProgress<float>? progress, CancellationToken token)
    {
        Task<bool> task = sequence.RunAsync(progress, token);
        if (rig.Director.Residency is { } residency)
            rig.Backend.Planner = residency.Planner;
        return task;
    }

    private static void CompleteProxies(FakeResidencyBackend backend)
    {
        foreach ((ResidencyTicket ticket, ResidencyRequest request) in backend.Requests.ToArray())
            if (request.TargetLevel == (int)EResidencyLevel.Proxy)
                backend.Complete(ticket);
    }

    private static float X(SceneNode node) => ((Transform)node.Transform).Translation.X;

    [Test]
    public void ReadyForInstantSeek_IsFalse_UntilT0AndItsProgramsAreReady()
    {
        Rig rig = MakeRig();
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer);
        RecordingProgress progress = new();

        Task<bool> task = Start(rig, sequence, progress, CancellationToken.None);

        CutscenePlayer player = rig.Director.Player.ShouldNotBeNull();
        player.ReadyForInstantSeek.ShouldBeFalse();
        task.IsCompleted.ShouldBeFalse();
        sequence.ProxyMaterials.ShouldBe([rig.ProxyMaterial]);
        rig.Prewarmer.Requested.ShouldContain(rig.ProxyMaterial);
        rig.Prewarmer.Requested.ShouldNotContain(rig.DetailMaterial);

        // The programs are ready, but T0 is not resident.
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Ready, null);
        rig.Director.AdvanceFrame(0);
        player.ReadyForInstantSeek.ShouldBeFalse();
        task.IsCompleted.ShouldBeFalse();

        // T0 is resident, but the program is not ready.
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Pending, null);
        CompleteProxies(rig.Backend);
        rig.Director.AdvanceFrame(0);
        player.ReadyForInstantSeek.ShouldBeFalse();
        task.IsCompleted.ShouldBeFalse();

        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Ready, null);
        rig.Director.AdvanceFrame(0);
        player.ReadyForInstantSeek.ShouldBeTrue();
        task.IsCompletedSuccessfully.ShouldBeTrue();
        task.Result.ShouldBeTrue();
        progress.Values[^1].ShouldBe(1.0f);

        // The detail programs start as a lower-priority request after T0.
        rig.Prewarmer.Requested.ShouldContain(rig.DetailMaterial);
        rig.Director.Close();
    }

    [Test]
    public void AfterReady_ASeekIssuesNoLoadForT0Data()
    {
        Rig rig = MakeRig();
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Ready, null);
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer);
        Task<bool> task = Start(rig, sequence, null, CancellationToken.None);
        CompleteProxies(rig.Backend);
        rig.Director.AdvanceFrame(0);
        task.Result.ShouldBeTrue();
        CutscenePlayer player = rig.Director.Player.ShouldNotBeNull();
        player.ReadyForInstantSeek.ShouldBeTrue();

        int proxyRequests = rig.Backend.Requests.Count(r => r.Request.TargetLevel == (int)EResidencyLevel.Proxy);
        int cancels = rig.Backend.Cancelled.Count;
        player.Seek(T(3), ESeekIntent.Jump);
        rig.Director.AdvanceFrame(0);
        player.Seek(T(0.5), ESeekIntent.Jump);
        rig.Director.AdvanceFrame(0);

        rig.Backend.Requests.Count(r => r.Request.TargetLevel == (int)EResidencyLevel.Proxy).ShouldBe(proxyRequests);
        HashSet<long> proxyTickets = [.. rig.Backend.Requests.Where(r => r.Request.TargetLevel == (int)EResidencyLevel.Proxy).Select(r => r.Ticket.Id)];
        rig.Backend.Cancelled.Skip(cancels).ShouldNotContain(t => proxyTickets.Contains(t.Id));
        player.ReadyForInstantSeek.ShouldBeTrue();
        rig.Director.Close();
    }

    [Test]
    public void CancelDuringOpen_ClosesTheCutscene_AndRestoresTheScene()
    {
        Rig rig = MakeRig();
        using CancellationTokenSource cancel = new();
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer);
        Task<bool> task = Start(rig, sequence, null, cancel.Token);
        CutscenePlayer player = rig.Director.Player.ShouldNotBeNull();
        CutsceneResidencySession residency = rig.Director.Residency.ShouldNotBeNull();

        player.Seek(T(3), ESeekIntent.Jump);
        rig.Director.AdvanceFrame(0);
        X(rig.Node).ShouldBe(100f);

        cancel.Cancel();
        rig.Director.AdvanceFrame(0);

        task.IsCanceled.ShouldBeTrue();
        rig.Director.IsOpen.ShouldBeFalse();
        residency.IsClosed.ShouldBeTrue();
        player.ReadyForInstantSeek.ShouldBeFalse();
        ((Transform)rig.Node.Transform).Translation.ShouldBe(OriginalPosition);
    }

    [Test]
    public void CancelBeforeTheOpen_LeavesTheDirectorClosed()
    {
        Rig rig = MakeRig();
        using CancellationTokenSource cancel = new();
        cancel.Cancel();

        Task<bool> task = new CutsceneOpenSequence(rig.Director, rig.Prewarmer).RunAsync(null, cancel.Token);

        task.IsCanceled.ShouldBeTrue();
        rig.Director.IsOpen.ShouldBeFalse();
        ((Transform)rig.Node.Transform).Translation.ShouldBe(OriginalPosition);
    }

    [Test]
    public void ShaderFailure_ReportsADiagnostic_AndIsNotHidden()
    {
        Rig rig = MakeRig();
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Failed, "fragment compile failed at line 12");
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer);
        Task<bool> task = Start(rig, sequence, null, CancellationToken.None);
        CompleteProxies(rig.Backend);

        rig.Director.AdvanceFrame(0);

        task.IsCompletedSuccessfully.ShouldBeTrue();
        task.Result.ShouldBeFalse();
        rig.Director.Player.ShouldNotBeNull().ReadyForInstantSeek.ShouldBeFalse();
        rig.Director.IsOpen.ShouldBeTrue();
        rig.Director.Diagnostics.ShouldContain(d =>
            d.Message.Contains("ProxyMaterial", StringComparison.Ordinal) &&
            d.Message.Contains("fragment compile failed at line 12", StringComparison.Ordinal));
        sequence.FailedProgramCount.ShouldBe(1);
        rig.Director.Close();
    }

    [Test]
    public void PreviewHost_OpenSequence_BecomesReady_AfterT0AndItsPrograms()
    {
        Rig rig = MakeRig();
        CutscenePreviewHost host = new(new FakeResolver(rig.Node), residencyBackendFactory: () => rig.Backend);
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Ready, null);

        Task<bool> task = host.BeginWithOpenSequence(rig.Asset, out CutsceneOpenSequence sequence, rig.Prewarmer);
        try
        {
            host.IsActive.ShouldBeTrue();
            rig.Backend.Planner = host.Residency.ShouldNotBeNull().Planner;
            sequence.ProxyMaterials.ShouldBe([rig.ProxyMaterial]);

            host.Tick(0);
            task.IsCompleted.ShouldBeFalse();
            sequence.PendingProxyCount.ShouldBeGreaterThan(0);
            host.Player.ShouldNotBeNull().ReadyForInstantSeek.ShouldBeFalse();

            CompleteProxies(rig.Backend);
            host.Tick(0);

            task.IsCompletedSuccessfully.ShouldBeTrue();
            task.Result.ShouldBeTrue();
            sequence.PendingProxyCount.ShouldBe(0);
            host.Player.ShouldNotBeNull().ReadyForInstantSeek.ShouldBeTrue();
        }
        finally
        {
            host.End();
        }
    }

    [Test]
    public void PreviewHost_EndDuringOpen_CancelsTheTask_AndRestoresTheScene()
    {
        Rig rig = MakeRig();
        CutscenePreviewHost host = new(new FakeResolver(rig.Node), residencyBackendFactory: () => rig.Backend);
        Task<bool> task = host.BeginWithOpenSequence(rig.Asset, out _, rig.Prewarmer);
        host.Seek(T(3), ESeekIntent.Jump);
        host.Tick(0);
        X(rig.Node).ShouldBe(100f);

        host.End();

        task.IsCanceled.ShouldBeTrue();
        host.IsActive.ShouldBeFalse();
        ((Transform)rig.Node.Transform).Translation.ShouldBe(OriginalPosition);
    }

    [Test]
    public void CloseByAnotherCaller_CancelsTheTask()
    {
        Rig rig = MakeRig();
        Task<bool> task = Start(rig, new CutsceneOpenSequence(rig.Director, rig.Prewarmer), null, CancellationToken.None);

        rig.Director.Close();

        task.IsCanceled.ShouldBeTrue();
        ((Transform)rig.Node.Transform).Translation.ShouldBe(OriginalPosition);
    }

    [Test]
    public void WithoutAResidencySession_ReadyForInstantSeekStaysFalse_AndADiagnosticSaysWhy()
    {
        Rig rig = MakeRig();
        rig.Director.ResidencyEnabled = false;
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Ready, null);

        Task<bool> task = new CutsceneOpenSequence(rig.Director, rig.Prewarmer).RunAsync(null, CancellationToken.None);

        rig.Director.IsOpen.ShouldBeTrue();
        rig.Director.Residency.ShouldBeNull();
        task.IsCompletedSuccessfully.ShouldBeTrue();
        task.Result.ShouldBeFalse();
        rig.Director.Player.ShouldNotBeNull().ReadyForInstantSeek.ShouldBeFalse();
        rig.Director.Diagnostics.ShouldContain(d => d.Message.Contains("No residency session", StringComparison.Ordinal));
        rig.Director.Close();
    }

    [Test]
    public void MaterialOverride_IsTheProxyMaterial()
    {
        Rig rig = MakeRig();
        XRMaterial overrideMaterial = new() { Name = "OverrideMaterial" };
        rig.Node.GetComponent<ModelComponent>().ShouldNotBeNull().Meshes.Single().MaterialOverride = overrideMaterial;
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer);

        Start(rig, sequence, null, CancellationToken.None);

        sequence.ProxyMaterials.ShouldBe([overrideMaterial]);
        sequence.DetailMaterials.ShouldBeEmpty();
        rig.Prewarmer.Requested.ShouldContain(overrideMaterial);
        rig.Director.Close();
    }
}
