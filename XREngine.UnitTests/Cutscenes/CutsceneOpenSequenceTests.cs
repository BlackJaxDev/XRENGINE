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

    /// <summary>A program prewarmer whose backend states the test sets. A material with no state is not started.</summary>
    private sealed class FakePrewarmer : ICutsceneProgramPrewarmer
    {
        public Dictionary<XRMaterial, (ECutsceneProgramState State, string? Reason)> States { get; } = new(ReferenceEqualityComparer.Instance);

        public List<XRMaterial> Requested { get; } = [];

        public List<XRMeshRenderer?> RequestedRenderers { get; } = [];

        public List<XRMeshRenderer?> QueriedRenderers { get; } = [];

        public int StopCount { get; private set; }

        public int ResetCount { get; private set; }

        public XRRenderPipelineInstance? View { get; set; }

        public void Request(XRMaterial material, XRMeshRenderer? renderer)
        {
            Requested.Add(material);
            RequestedRenderers.Add(renderer);
        }

        public ECutsceneProgramState GetState(XRMaterial material, XRMeshRenderer? renderer, out string? reason)
        {
            QueriedRenderers.Add(renderer);
            if (States.TryGetValue(material, out (ECutsceneProgramState State, string? Reason) value))
            {
                reason = value.Reason;
                return value.State;
            }
            reason = null;
            return ECutsceneProgramState.NotStarted;
        }

        public void Stop()
            => StopCount++;

        public void Reset()
            => ResetCount++;
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

    private static readonly XRMaterial SecondProxyMaterial = new() { Name = "SecondProxyMaterial" };

    private static Rig MakeRig(bool withSecondMesh = false)
    {
        SceneNode node = new("Shot", new Transform());
        ((Transform)node.Transform).Translation = OriginalPosition;

        XRMaterial detail = new() { Name = "DetailMaterial" };
        XRMaterial proxy = new() { Name = "ProxyMaterial" };
        ModelComponent model = node.AddComponent<ModelComponent>()!;
        SubMesh shotMesh = new(
            new SubMeshLOD(detail, XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY), 10.0f),
            new SubMeshLOD(proxy, XRMesh.CreateTriangles(Vector3.UnitZ, Vector3.UnitZ + Vector3.UnitX, Vector3.UnitZ + Vector3.UnitY), 1000.0f))
        {
            Name = "ShotMesh",
        };
        model.Model = withSecondMesh
            ? new Model(shotMesh, new SubMesh(new SubMeshLOD(SecondProxyMaterial, XRMesh.CreateTriangles(Vector3.UnitY, Vector3.UnitY + Vector3.UnitX, Vector3.UnitY + Vector3.UnitZ), 1000.0f)) { Name = "SecondMesh" })
            : new Model(shotMesh);

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
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Compiling, null);
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

    [Test]
    public void ProxyDraw_IsQueriedOnTheRendererOfTheLowestLod()
    {
        Rig rig = MakeRig();
        RenderableMesh renderable = rig.Node.GetComponent<ModelComponent>().ShouldNotBeNull().Meshes.Single();
        XRMeshRenderer lowest = renderable.LowestLodRenderer.ShouldNotBeNull();
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer);

        Start(rig, sequence, null, CancellationToken.None);

        sequence.ProxyDrawCount.ShouldBe(1);
        rig.Prewarmer.RequestedRenderers.ShouldBe([lowest]);
        rig.Prewarmer.QueriedRenderers.ShouldAllBe(r => ReferenceEquals(r, lowest));
        lowest.Material.ShouldBeSameAs(rig.ProxyMaterial);
        rig.Director.Close();
    }

    [Test]
    public void ReadyForInstantSeek_NeverComesBeforeEveryProxyDrawIsReady()
    {
        Rig rig = MakeRig(withSecondMesh: true);
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer);
        Task<bool> task = Start(rig, sequence, null, CancellationToken.None);
        CompleteProxies(rig.Backend);
        CutscenePlayer player = rig.Director.Player.ShouldNotBeNull();
        sequence.ProxyDrawCount.ShouldBe(2);

        // Each step leaves at least one draw that is not ready. The flag must stay false in each one.
        (ECutsceneProgramState First, ECutsceneProgramState Second)[] steps =
        [
            (ECutsceneProgramState.NotStarted, ECutsceneProgramState.NotStarted),
            (ECutsceneProgramState.Compiling, ECutsceneProgramState.NotStarted),
            (ECutsceneProgramState.Ready, ECutsceneProgramState.NotStarted),
            (ECutsceneProgramState.Ready, ECutsceneProgramState.Compiling),
            (ECutsceneProgramState.Compiling, ECutsceneProgramState.Ready),
        ];
        foreach ((ECutsceneProgramState first, ECutsceneProgramState second) in steps)
        {
            rig.Prewarmer.States[rig.ProxyMaterial] = (first, null);
            rig.Prewarmer.States[SecondProxyMaterial] = (second, null);
            rig.Director.AdvanceFrame(0);

            player.ReadyForInstantSeek.ShouldBeFalse($"first={first} second={second}");
            task.IsCompleted.ShouldBeFalse();
            sequence.PendingProgramCount.ShouldBeGreaterThan(0);
            sequence.NotStartedProgramCount.ShouldBe((first == ECutsceneProgramState.NotStarted ? 1 : 0) + (second == ECutsceneProgramState.NotStarted ? 1 : 0));
            sequence.CompilingProgramCount.ShouldBe((first == ECutsceneProgramState.Compiling ? 1 : 0) + (second == ECutsceneProgramState.Compiling ? 1 : 0));
        }

        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Ready, null);
        rig.Prewarmer.States[SecondProxyMaterial] = (ECutsceneProgramState.Ready, null);
        rig.Director.AdvanceFrame(0);

        player.ReadyForInstantSeek.ShouldBeTrue();
        task.Result.ShouldBeTrue();
        sequence.ReadyProgramCount.ShouldBe(2);
        sequence.PendingProgramCount.ShouldBe(0);
        rig.Prewarmer.StopCount.ShouldBe(1);
        rig.Director.Close();
    }

    [Test]
    public void NotSupportedBackend_ReportsADiagnostic_AndIsNeverReady()
    {
        Rig rig = MakeRig(withSecondMesh: true);
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Ready, null);
        rig.Prewarmer.States[SecondProxyMaterial] = (ECutsceneProgramState.NotSupported, "the render backend does not report draw readiness");
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer);
        Task<bool> task = Start(rig, sequence, null, CancellationToken.None);
        CompleteProxies(rig.Backend);

        rig.Director.AdvanceFrame(0);
        rig.Director.AdvanceFrame(0);

        task.IsCompletedSuccessfully.ShouldBeTrue();
        task.Result.ShouldBeFalse();
        rig.Director.Player.ShouldNotBeNull().ReadyForInstantSeek.ShouldBeFalse();
        sequence.UnsupportedProgramCount.ShouldBe(1);
        sequence.ReadyProgramCount.ShouldBe(1);
        rig.Director.Diagnostics.Count(d =>
            d.Message.Contains("SecondProxyMaterial", StringComparison.Ordinal) &&
            d.Message.Contains("does not report draw readiness", StringComparison.Ordinal)).ShouldBe(1);
        rig.Prewarmer.StopCount.ShouldBe(1);
        rig.Director.Close();
    }

    [Test]
    public void FailedDraw_WaitsForThePendingDraws_ThenFails()
    {
        Rig rig = MakeRig(withSecondMesh: true);
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Failed, "pipeline creation failed");
        rig.Prewarmer.States[SecondProxyMaterial] = (ECutsceneProgramState.Compiling, null);
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer);
        Task<bool> task = Start(rig, sequence, null, CancellationToken.None);
        CompleteProxies(rig.Backend);

        rig.Director.AdvanceFrame(0);
        task.IsCompleted.ShouldBeFalse();
        sequence.FailedProgramCount.ShouldBe(1);
        rig.Director.Diagnostics.ShouldContain(d => d.Message.Contains("pipeline creation failed", StringComparison.Ordinal));

        rig.Prewarmer.States[SecondProxyMaterial] = (ECutsceneProgramState.Ready, null);
        rig.Director.AdvanceFrame(0);

        task.Result.ShouldBeFalse();
        rig.Director.Player.ShouldNotBeNull().ReadyForInstantSeek.ShouldBeFalse();
        rig.Director.Close();
    }

    [Test]
    public void ADrawThatNeverBecomesReady_AddsOneTimeoutDiagnostic_AndTheOpenKeepsWaiting()
    {
        Rig rig = MakeRig(withSecondMesh: true);
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.NotStarted, "no frame drew the renderer");
        rig.Prewarmer.States[SecondProxyMaterial] = (ECutsceneProgramState.Ready, null);
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer) { ProgramWaitWarningFrames = 3 };
        Task<bool> task = Start(rig, sequence, null, CancellationToken.None);
        CompleteProxies(rig.Backend);

        for (int i = 0; i < 6; i++)
            rig.Director.AdvanceFrame(0);

        task.IsCompleted.ShouldBeFalse();
        rig.Director.Player.ShouldNotBeNull().ReadyForInstantSeek.ShouldBeFalse();
        rig.Director.Diagnostics.Count(d =>
            d.Message.Contains("ProxyMaterial", StringComparison.Ordinal) &&
            d.Message.Contains("still NotStarted", StringComparison.Ordinal) &&
            d.Message.Contains("no frame drew the renderer", StringComparison.Ordinal) &&
            d.Message.Contains("mesh submission strategy", StringComparison.Ordinal)).ShouldBe(1);
        rig.Director.Diagnostics.ShouldNotContain(d => d.Message.Contains("SecondProxyMaterial", StringComparison.Ordinal));

        // The wait still ends when the draw becomes ready.
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Ready, null);
        rig.Director.AdvanceFrame(0);
        task.Result.ShouldBeTrue();
        rig.Director.Close();
    }

    [Test]
    public void ACompilingDraw_AlsoGetsTheTimeoutDiagnostic()
    {
        Rig rig = MakeRig();
        rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Compiling, "graphics pipeline compile is queued");
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer) { ProgramWaitWarningFrames = 2 };
        Task<bool> task = Start(rig, sequence, null, CancellationToken.None);
        CompleteProxies(rig.Backend);

        for (int i = 0; i < 4; i++)
            rig.Director.AdvanceFrame(0);

        task.IsCompleted.ShouldBeFalse();
        rig.Director.Diagnostics.Count(d => d.Message.Contains("still Compiling", StringComparison.Ordinal)).ShouldBe(1);
        rig.Director.Close();
    }

    [Test]
    public void PreviewRecompile_DuringTheOpen_CollectsTheDrawsAgain_FromTheNewInstances()
    {
        Rig rig = MakeRig();
        CutscenePreviewHost host = new(new FakeResolver(rig.Node), residencyBackendFactory: () => rig.Backend);
        Task<bool> task = host.BeginWithOpenSequence(rig.Asset, out CutsceneOpenSequence sequence, rig.Prewarmer);
        try
        {
            rig.Backend.Planner = host.Residency.ShouldNotBeNull().Planner;
            host.Tick(0);
            int requestsBefore = rig.Prewarmer.Requested.Count;
            rig.Prewarmer.ResetCount.ShouldBe(0);

            host.NotifyAssetEdited();
            host.Tick(0);

            rig.Prewarmer.ResetCount.ShouldBe(1);
            rig.Prewarmer.Requested.Count.ShouldBeGreaterThan(requestsBefore);
            sequence.ProxyDrawCount.ShouldBe(1);
            sequence.ProxyMaterials.ShouldBe([rig.ProxyMaterial]);
            task.IsCompleted.ShouldBeFalse();

            if (host.Residency is { } residency)
                rig.Backend.Planner = residency.Planner;
            rig.Prewarmer.States[rig.ProxyMaterial] = (ECutsceneProgramState.Ready, null);
            CompleteProxies(rig.Backend);
            host.Tick(0);
            host.Tick(0);

            task.Result.ShouldBeTrue();
            host.Player.ShouldNotBeNull().ReadyForInstantSeek.ShouldBeTrue();
        }
        finally
        {
            host.End();
        }
    }

    [Test]
    public void TheOpen_SetsTheViewOfThePrewarmer_FromTheHostViewport()
    {
        Rig rig = MakeRig();
        CutsceneOpenSequence sequence = new(rig.Director, rig.Prewarmer);

        Start(rig, sequence, null, CancellationToken.None);
        rig.Director.AdvanceFrame(0);

        // The test director has no target viewport, so no view filter is set.
        rig.Prewarmer.View.ShouldBeNull();
        rig.Director.Close();
    }
}
