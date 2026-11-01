using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Cutscenes;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutscenePlayerTests
{
    private sealed class FakeResolver : ICutsceneBindingResolver
    {
        public readonly Dictionary<Guid, SceneNode> ById = [];

        public SceneNode? FindNodeById(Guid id) => ById.GetValueOrDefault(id);

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    /// <summary>A player and the objects that it acts on.</summary>
    private sealed class Rig : IDisposable
    {
        public required CutscenePlayer Player { get; init; }
        public required CutsceneBindingTable Table { get; init; }
        public required SceneNode[] Nodes { get; init; }

        public Transform TransformOf(int slot) => (Transform)Nodes[slot].Transform;

        public void Dispose() => Table.Dispose();
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static readonly Vector3 OriginalPosition = new(7, 8, 9);

    private static TransformTrackClip Move(double start, double duration, Vector3 from, Vector3 to,
        ECutsceneClipExtrapolation extrapolation = ECutsceneClipExtrapolation.None)
        => new()
        {
            Start = T(start),
            Duration = T(duration),
            Extrapolation = extrapolation,
            Position = new CutsceneVector3Curve { Times = [T(0), T(duration)], Values = [from, to] },
            Rotation = new CutsceneQuaternionCurve
            {
                Times = [T(0), T(duration)],
                Values = [Quaternion.CreateFromAxisAngle(Vector3.UnitY, from.X * 0.1f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, to.X * 0.1f)],
            },
            Scale = new CutsceneVector3Curve { Times = [T(0), T(duration)], Values = [Vector3.One + from * 0.01f, Vector3.One + to * 0.01f] },
        };

    private static ActivationClip Show(double start, double duration)
        => new() { Start = T(start), Duration = T(duration) };

    /// <summary>
    /// Builds a cutscene with one node. Track 0 moves the node, track 1 toggles it.
    /// Both tracks bind slot 0.
    /// </summary>
    private static Rig MakeRig(double duration, IEnumerable<CutsceneClip> moveClips, IEnumerable<CutsceneClip>? showClips = null, bool bindNode = true)
    {
        SceneNode node = new("Hero", new Transform());
        ((Transform)node.Transform).Translation = OriginalPosition;
        FakeResolver resolver = new();
        if (bindNode)
            resolver.ById[node.ID] = node;

        CutsceneAsset asset = new() { Duration = T(duration) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        TransformTrack move = new() { Name = "Move", BindingSlot = 0 };
        move.Clips.AddRange(moveClips);
        asset.Tracks.Add(move);
        ActivationTrack toggle = new() { Name = "Toggle", BindingSlot = 0 };
        if (showClips is not null)
            toggle.Clips.AddRange(showClips);
        asset.Tracks.Add(toggle);

        List<CutsceneCompileDiagnostic> compile = [];
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, compile);
        CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, resolver);
        CutsceneStateSnapshot snapshot = new(table);
        return new Rig { Player = new CutscenePlayer(compiled, table, snapshot), Table = table, Nodes = [node] };
    }

    private static Rig StandardRig()
        => MakeRig(
            20,
            [
                Move(1, 4, new Vector3(0, 0, 0), new Vector3(4, 0, 0)),
                Move(5, 4, new Vector3(4, 0, 0), new Vector3(4, 8, 0), ECutsceneClipExtrapolation.Hold),
                Move(12, 3, new Vector3(0, 5, 0), new Vector3(3, 5, 0)),
            ],
            [Show(2, 3), Show(10, 5)]);

    private static void SeekAndTick(CutscenePlayer player, double seconds)
    {
        player.Seek(T(seconds), ESeekIntent.Jump);
        player.Tick(0);
    }

    private static void ShouldMatch(Rig actual, Rig expected)
    {
        Transform a = actual.TransformOf(0);
        Transform e = expected.TransformOf(0);
        (a.Translation - e.Translation).Length().ShouldBeLessThan(1e-4f);
        (a.Scale - e.Scale).Length().ShouldBeLessThan(1e-4f);
        Quaternion.Dot(a.Rotation, e.Rotation).ShouldBeGreaterThan(1f - 1e-4f);
        actual.Nodes[0].IsActiveSelf.ShouldBe(expected.Nodes[0].IsActiveSelf);
    }

    [Test]
    public void SeekEquivalence_RandomSeeksMatchDirectEvaluation()
    {
        using Rig sequence = StandardRig();
        Random random = new(12345);

        for (int i = 0; i < 200; i++)
        {
            double target = random.NextDouble() * 22.0 - 1.0;
            SeekAndTick(sequence.Player, target);

            using Rig direct = StandardRig();
            SeekAndTick(direct.Player, target);

            ShouldMatch(sequence, direct);
        }
    }

    [Test]
    public void TwoSeeksInOneFrame_EvaluateOnceAtTheLastValue()
    {
        using Rig rig = StandardRig();
        int discontinuities = 0;
        rig.Player.Discontinuity += _ => discontinuities++;

        rig.Player.Seek(T(2), ESeekIntent.Scrub);
        rig.Player.Seek(T(3), ESeekIntent.Scrub);
        rig.Player.Tick(0);

        discontinuities.ShouldBe(1);
        rig.Player.Position.ShouldBe(T(3));
        rig.TransformOf(0).Translation.X.ShouldBe(2f, 1e-4f);
    }

    [TestCase(-5.0, 0.0)]
    [TestCase(99.0, 20.0)]
    public void Seek_ClampsToTheCutsceneRange(double target, double expected)
    {
        using Rig rig = StandardRig();

        SeekAndTick(rig.Player, target);

        rig.Player.Position.ShouldBe(T(expected));
    }

    [Test]
    public void NegativeRate_PlaysBackward()
    {
        using Rig rig = StandardRig();
        SeekAndTick(rig.Player, 3);
        rig.Player.SetRate(-1.0);
        rig.Player.Play();

        rig.Player.Tick(0.5);

        rig.Player.Position.ShouldBe(T(2.5));
        rig.TransformOf(0).Translation.X.ShouldBe(1.5f, 1e-4f);
    }

    [Test]
    public void Seek_RaisesOneDiscontinuity_AndNormalPlayRaisesNone()
    {
        using Rig rig = StandardRig();
        List<CutsceneDiscontinuity> raised = [];
        rig.Player.Discontinuity += raised.Add;

        rig.Player.Play();
        for (int i = 0; i < 10; i++)
            rig.Player.Tick(0.016);
        raised.Count.ShouldBe(0);

        rig.Player.Seek(T(12), ESeekIntent.Jump);
        rig.Player.Tick(0.016);
        rig.Player.Tick(0.016);

        raised.Count.ShouldBe(1);
        raised[0].To.ShouldBe(T(12));
        raised[0].Intent.ShouldBe(ESeekIntent.Jump);
    }

    [TestCase(ECutsceneClipExtrapolation.Hold)]
    [TestCase(ECutsceneClipExtrapolation.None)]
    public void SeekToDuration_ShowsTheLastPoseOfAClipThatEndsThere(ECutsceneClipExtrapolation extrapolation)
    {
        using Rig rig = MakeRig(10, [Move(6, 4, new Vector3(0, 0, 0), new Vector3(5, 0, 0), extrapolation)]);

        SeekAndTick(rig.Player, 10);

        rig.Player.Position.ShouldBe(T(10));
        rig.TransformOf(0).Translation.X.ShouldBe(5f, 1e-3f);
    }

    [Test]
    public void TargetThatHadAClipAndHasNone_ReturnsToItsOriginalValue()
    {
        using Rig rig = MakeRig(10, [Move(2, 2, new Vector3(1, 1, 1), new Vector3(2, 2, 2))], [Show(2, 2)]);
        rig.Nodes[0].IsActiveSelf = false;

        SeekAndTick(rig.Player, 3);
        rig.TransformOf(0).Translation.ShouldNotBe(OriginalPosition);
        rig.Nodes[0].IsActiveSelf.ShouldBeTrue();

        SeekAndTick(rig.Player, 8);
        rig.TransformOf(0).Translation.ShouldBe(OriginalPosition);
        rig.Nodes[0].IsActiveSelf.ShouldBeFalse();
    }

    [Test]
    public void Crossfade_BlendsPositionBySummedWeights()
    {
        TransformTrackClip first = Move(0, 2, new Vector3(2, 0, 0), new Vector3(2, 0, 0), ECutsceneClipExtrapolation.Hold);
        TransformTrackClip second = Move(2, 4, new Vector3(10, 0, 0), new Vector3(10, 0, 0));
        second.BlendIn = T(2);
        using Rig rig = MakeRig(10, [first, second]);

        SeekAndTick(rig.Player, 3);

        // The incoming clip has weight 0.5 and the outgoing clip has weight 0.5.
        rig.TransformOf(0).Translation.X.ShouldBe(0.5f * 10f + 0.5f * 2f, 1e-4f);
    }

    [Test]
    public void WrongClipSubtype_GivesOneDiagnostic_AndIsSkipped()
    {
        using Rig rig = MakeRig(10, [Show(1, 5)]);

        for (int i = 0; i < 5; i++)
            SeekAndTick(rig.Player, 2 + i * 0.1);

        rig.Player.Diagnostics.Count.ShouldBe(1);
        rig.TransformOf(0).Translation.ShouldBe(OriginalPosition);
    }

    [Test]
    public void UnboundTrack_IsSkipped_WithoutPerFrameDiagnostics()
    {
        using Rig rig = MakeRig(10, [Move(0, 5, Vector3.Zero, Vector3.One)], null, bindNode: false);

        for (int i = 0; i < 5; i++)
            SeekAndTick(rig.Player, 1 + i);

        rig.TransformOf(0).Translation.ShouldBe(OriginalPosition);
        rig.Player.Diagnostics.Count.ShouldBe(0);
        rig.Table.Diagnostics.Count.ShouldBe(1);
    }

    [Test]
    public void Stop_RestoresOriginalState_AndResetsThePosition()
    {
        using Rig rig = StandardRig();
        SeekAndTick(rig.Player, 3);

        rig.Player.Stop();

        rig.Player.Position.ShouldBe(CutsceneTime.Zero);
        rig.TransformOf(0).Translation.ShouldBe(OriginalPosition);
    }

    [Test]
    public void Tick_AllocatesNothing()
    {
        using Rig rig = StandardRig();
        rig.Player.Play();
        for (int i = 0; i < 50; i++)
            rig.Player.Tick(0.001);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            rig.Player.Tick(0.001);
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
    }

    [Test]
    public void SeekAndTick_AllocateNothing()
    {
        using Rig rig = StandardRig();
        for (int i = 0; i < 50; i++)
            SeekAndTick(rig.Player, i * 0.3);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            rig.Player.Seek(CutsceneTime.FromSeconds((i % 200) * 0.1), ESeekIntent.Scrub);
            rig.Player.Tick(0);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
    }
}
