using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Cutscenes;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Animation;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class AnimationClipTrackTests
{
    private static readonly Vector3 OriginalPosition = new(7, 8, 9);
    private static readonly Quaternion OriginalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f);

    private sealed class FakeResolver : ICutsceneBindingResolver
    {
        public readonly Dictionary<Guid, SceneNode> ById = [];

        public SceneNode? FindNodeById(Guid id) => ById.GetValueOrDefault(id);

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    private sealed class Rig : IDisposable
    {
        public required CutscenePlayer Player { get; init; }
        public required CutsceneBindingTable Table { get; init; }
        public required SceneNode Node { get; init; }

        public Transform Root => (Transform)Node.Transform;

        public void SeekTo(double seconds)
        {
            Player.Seek(T(seconds), ESeekIntent.Jump);
            Player.Tick(0);
        }

        public void Dispose() => Table.Dispose();
    }

    /// <summary>An IK solver that records the state of the target at each solve.</summary>
    private sealed class RecordingIKSolver : HumanoidIKSolverComponent
    {
        public int SolveCount;
        public Vector3 RootAtSolve;
        public Quaternion ArmAtSolve;
        public bool PoseAcceptedAtSolve;
        public Transform? RootTransform;
        public Transform? ArmTransform;
        public HumanoidComponent? Target;

        protected override void UpdateSolver()
        {
            SolveCount++;
            RootAtSolve = RootTransform!.Translation;
            ArmAtSolve = ArmTransform!.Rotation;
            PoseAcceptedAtSolve = Target!.WasLastNativeFrameAccepted;
        }
    }

    /// <summary>A component with a value that only a method sets.</summary>
    private sealed class LevelComponent : XRComponent
    {
        public float Level { get; private set; }

        public void SetLevel(float level)
            => Level = level;
    }

    /// <summary>A transform with a discrete property whose setter throws.</summary>
    private sealed class ThrowingTransform : Transform
    {
        public string Boom
        {
            get => "start";
            set => throw new InvalidOperationException("Setter failure.");
        }
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    // ── Clips ───────────────────────────────────────────────────────────

    private static AnimationClip PositionClip(float seconds, Vector3 from, Vector3 to, Quaternion? fromRotation = null, Quaternion? toRotation = null)
    {
        PropAnimVector3 position = new(seconds, false, true);
        position.Keyframes.Add(new Vector3Keyframe(0, from, Vector3.Zero, EVectorInterpType.Linear));
        position.Keyframes.Add(new Vector3Keyframe(seconds, to, Vector3.Zero, EVectorInterpType.Linear));

        PropAnimQuaternion rotation = new(seconds, false, true);
        rotation.Keyframes.Add(new QuaternionKeyframe(0, fromRotation ?? Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, ERadialInterpType.Linear) { InterpolationTypeIn = ERadialInterpType.Linear });
        rotation.Keyframes.Add(new QuaternionKeyframe(seconds, toRotation ?? Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, ERadialInterpType.Linear) { InterpolationTypeIn = ERadialInterpType.Linear });

        AnimationMember transform = new("Transform", EAnimationMemberType.Property);
        transform.Children.Add(new AnimationMember("Translation", EAnimationMemberType.Property, position));
        transform.Children.Add(new AnimationMember("Rotation", EAnimationMemberType.Property, rotation));
        AnimationMember sceneNode = new("SceneNode", EAnimationMemberType.Property);
        sceneNode.Children.Add(transform);
        return new AnimationClip(sceneNode) { LengthInSeconds = seconds };
    }

    private static AnimationClip HumanoidClip(float muscleFrom = 0.1f, float muscleTo = 0.6f, float rootFrom = 0f, float rootTo = 2f)
    {
        string yaml = $"""
AnimationClip:
  serializedVersion: 7
  m_Name: CutsceneWalk
  m_SampleRate: 30
  m_AnimationClipSettings:
    m_StartTime: 0
    m_StopTime: 2
    m_LoopTime: 0
  m_FloatCurves:
    - path: ''
      attribute: Left Arm Down-Up
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: {muscleFrom}
          - time: 2
            value: {muscleTo}
    - path: ''
      attribute: RootT.x
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0
    - path: ''
      attribute: RootT.y
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 1
    - path: ''
      attribute: RootT.z
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: {rootFrom}
          - time: 2
            value: {rootTo}
    - path: ''
      attribute: RootQ.x
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0
    - path: ''
      attribute: RootQ.y
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0
    - path: ''
      attribute: RootQ.z
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0
    - path: ''
      attribute: RootQ.w
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 1
""";
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "AnimationClipTrackTests", $"{Guid.NewGuid():N}.anim");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
        AnimationClip clip = AnimYamlImporter.Import(path);
        clip.HasRootMotion.ShouldBeTrue();
        return clip;
    }

    private static AnimationClipTrackClip Entry(AnimationClip? clip, double start, double duration, double blendIn = 0, double blendOut = 0)
        => new() { Clip = clip, Start = T(start), Duration = T(duration), BlendIn = T(blendIn), BlendOut = T(blendOut) };

    // ── Rigs ────────────────────────────────────────────────────────────

    private static Rig MakeRig(SceneNode node, double duration, Action<AnimationClipTrack>? configure, params CutsceneClip[] clips)
    {
        FakeResolver resolver = new();
        resolver.ById[node.ID] = node;

        CutsceneAsset asset = new() { Duration = T(duration) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        AnimationClipTrack track = new() { Name = "Anim", BindingSlot = 0 };
        track.Clips.AddRange(clips);
        configure?.Invoke(track);
        asset.Tracks.Add(track);

        List<CutsceneCompileDiagnostic> compile = [];
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, compile);
        CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, resolver);
        return new Rig { Player = new CutscenePlayer(compiled, table, new CutsceneStateSnapshot(table)), Table = table, Node = node };
    }

    private static SceneNode PlainNode()
    {
        SceneNode node = new("Prop", new Transform());
        ((Transform)node.Transform).Translation = OriginalPosition;
        ((Transform)node.Transform).Rotation = OriginalRotation;
        return node;
    }

    private static (SceneNode Root, HumanoidComponent Humanoid) HumanoidNode()
    {
        SceneNode root = new("Avatar", new Transform());
        AnimationClipComponentTests.AddPlaybackSkeleton(root);
        HumanoidComponent humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();
        humanoid.TryValidateAvatarDefinitionForPlayback(out string diagnostic).ShouldBeTrue(diagnostic);
        ((Transform)root.Transform).Translation = OriginalPosition;
        ((Transform)root.Transform).Rotation = OriginalRotation;
        return (root, humanoid);
    }

    private static Transform Bone(SceneNode root, string name)
        => root.FindDescendantByName(name, StringComparison.InvariantCultureIgnoreCase)!.GetTransformAs<Transform>(true)!;

    private static void ShouldBeNear(Vector3 actual, Vector3 expected, float tolerance = 1e-4f)
        => Vector3.Distance(actual, expected).ShouldBeLessThan(tolerance, $"actual {actual}, expected {expected}");

    private static void ShouldBeNear(Quaternion actual, Quaternion expected, float tolerance = 1e-4f)
        => (1f - MathF.Abs(Quaternion.Dot(Quaternion.Normalize(actual), Quaternion.Normalize(expected)))).ShouldBeLessThan(tolerance, $"actual {actual}, expected {expected}");

    // ── Blend ───────────────────────────────────────────────────────────

    private static Rig CrossfadeRig(Quaternion? secondRotation = null)
        => MakeRig(PlainNode(), 10, null,
            Entry(PositionClip(4, Vector3.Zero, Vector3.Zero, Quaternion.Identity, Quaternion.Identity), 0, 4, blendOut: 2),
            Entry(PositionClip(4, new Vector3(10, 0, 0), new Vector3(10, 0, 0), secondRotation, secondRotation), 2, 4, blendIn: 2));

    [Test]
    public void Crossfade_AtFiftyPercent_GivesMidpoint()
    {
        using Rig rig = CrossfadeRig();
        rig.SeekTo(3);
        ShouldBeNear(rig.Root.Translation, new Vector3(5, 0, 0));
    }

    [Test]
    public void Crossfade_OfQuaternions_TakesShortPath()
    {
        // The second value is the negative of a turn of 60 degrees. The short path passes through 30 degrees.
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 3f);
        using Rig rig = CrossfadeRig(Quaternion.Negate(turn));
        rig.SeekTo(3);
        ShouldBeNear(rig.Root.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6f));
    }

    [Test]
    public void ZeroWeight_LeavesOriginalValue()
    {
        using Rig rig = MakeRig(PlainNode(), 10, null, Entry(PositionClip(4, new Vector3(1, 1, 1), new Vector3(1, 1, 1)), 2, 4, blendIn: 2));
        rig.SeekTo(2);
        rig.Root.Translation.ShouldBe(OriginalPosition);
        rig.Root.Rotation.ShouldBe(OriginalRotation);
    }

    [Test]
    public void WeightBelowOne_BlendsFromOriginalValue()
    {
        Vector3 clipValue = new(1, 1, 1);
        using Rig rig = MakeRig(PlainNode(), 10, null, Entry(PositionClip(4, clipValue, clipValue), 2, 4, blendIn: 2));
        rig.SeekTo(3);
        ShouldBeNear(rig.Root.Translation, Vector3.Lerp(OriginalPosition, clipValue, 0.5f));
    }

    // ── Root placement ──────────────────────────────────────────────────

    private static Action<AnimationClipTrack> Offset(Vector3 position, Quaternion rotation)
        => track =>
        {
            track.RootOffsetPosition = position;
            track.RootOffsetRotation = rotation;
        };

    [Test]
    public void RepeatedSeeks_DoNotDriftRoot()
    {
        (SceneNode root, HumanoidComponent _) = HumanoidNode();
        using Rig rig = MakeRig(root, 10, null, Entry(HumanoidClip(), 0, 2));

        rig.SeekTo(1);
        Vector3 first = rig.Root.Translation;
        Quaternion firstRotation = rig.Root.Rotation;
        Vector3.Distance(first, OriginalPosition).ShouldBeGreaterThan(0.01f);

        for (int i = 0; i < 25; i++)
        {
            rig.Player.Seek(T(0.2 * (i % 7)), ESeekIntent.Jump);
            rig.Player.Tick(0);
            rig.SeekTo(1);
            ShouldBeNear(rig.Root.Translation, first, 1e-5f);
            ShouldBeNear(rig.Root.Rotation, firstRotation, 1e-6f);
        }
    }

    [Test]
    public void RootPlacement_UsesClipStartTransformAndTrackOffset()
    {
        Vector3 offset = new(1, 0, 0);
        Quaternion offsetTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        (SceneNode plainRoot, _) = HumanoidNode();
        using Rig plain = MakeRig(plainRoot, 10, null, Entry(HumanoidClip(), 0, 2));
        (SceneNode offsetRoot, _) = HumanoidNode();
        using Rig placed = MakeRig(offsetRoot, 10, Offset(offset, offsetTurn), Entry(HumanoidClip(), 0, 2));

        plain.SeekTo(1);
        placed.SeekTo(1);

        // Remove the start transform from the plain result to get the motion in the start frame.
        Quaternion inverseStart = Quaternion.Inverse(OriginalRotation);
        Vector3 motion = Vector3.Transform(plain.Root.Translation - OriginalPosition, inverseStart);
        Vector3 expected = OriginalPosition + Vector3.Transform(offset + Vector3.Transform(motion, offsetTurn), OriginalRotation);
        ShouldBeNear(placed.Root.Translation, expected);
        ShouldBeNear(placed.Root.Rotation, OriginalRotation * offsetTurn * inverseStart * plain.Root.Rotation);
    }

    [Test]
    public void RootPlacement_MatchesClipComponentOnExplicitTarget()
    {
        (SceneNode directRoot, HumanoidComponent directHumanoid) = HumanoidNode();
        AnimationClipComponent player = directRoot.AddComponent<AnimationClipComponent>()!;
        player.Animation = HumanoidClip();
        player.RootMotionApplicationMode = EHumanoidRootMotionApplicationMode.ApplyToExplicitTarget;
        player.RootMotionTarget = (Transform)directRoot.Transform;
        player.EvaluateAtTime(1f);
        directHumanoid.WasLastNativeFrameAccepted.ShouldBeTrue();

        (SceneNode root, _) = HumanoidNode();
        using Rig rig = MakeRig(root, 10, null, Entry(HumanoidClip(), 0, 2));
        rig.SeekTo(1);

        ShouldBeNear(rig.Root.Translation, ((Transform)directRoot.Transform).Translation);
        ShouldBeNear(rig.Root.Rotation, ((Transform)directRoot.Transform).Rotation);
        ShouldBeNear(Bone(root, "LeftArm").Rotation, Bone(directRoot, "LeftArm").Rotation);
        ShouldBeNear(Bone(root, "Hips").Translation, Bone(directRoot, "Hips").Translation);
    }

    [Test]
    public void GapAfterRootPlacement_ReturnsRootToStartTransform()
    {
        (SceneNode root, _) = HumanoidNode();
        using Rig rig = MakeRig(root, 10, null, Entry(HumanoidClip(), 0, 2));
        rig.SeekTo(1);
        rig.SeekTo(5);
        rig.Root.Translation.ShouldBe(OriginalPosition);
        rig.Root.Rotation.ShouldBe(OriginalRotation);
    }

    // ── Solve order ─────────────────────────────────────────────────────

    [Test]
    public void SolveOrder_IsStoreThenHumanoidThenRootThenOneIKSolve()
    {
        (SceneNode root, HumanoidComponent humanoid) = HumanoidNode();
        Transform arm = Bone(root, "LeftArm");
        RecordingIKSolver solver = root.AddComponent<RecordingIKSolver>()!;
        solver.RootTransform = (Transform)root.Transform;
        solver.ArmTransform = arm;
        solver.Target = humanoid;
        using Rig rig = MakeRig(root, 10, null, Entry(HumanoidClip(), 0, 2));

        rig.SeekTo(1);

        solver.SolveCount.ShouldBe(1);
        solver.PoseAcceptedAtSolve.ShouldBeTrue();

        // The solver saw the final root and the final bone: the store write, the humanoid solve, and the placement came first.
        humanoid.TryGetMuscleValue(EHumanoidValue.LeftArmDownUp, out float muscle).ShouldBeTrue();
        muscle.ShouldBe(0.35f, 1e-4f);
        Vector3.Distance(solver.RootAtSolve, OriginalPosition).ShouldBeGreaterThan(0.01f);
        solver.RootAtSolve.ShouldBe(rig.Root.Translation);
        solver.ArmAtSolve.ShouldBe(arm.Rotation);
        ShouldBeNotBindPose(arm);

        rig.SeekTo(1.5);
        solver.SolveCount.ShouldBe(2);
    }

    private static void ShouldBeNotBindPose(Transform bone)
        => MathF.Abs(Quaternion.Dot(Quaternion.Normalize(bone.Rotation), Quaternion.Normalize(bone.BindState.Rotation))).ShouldBeLessThan(0.9999f);

    [Test]
    public void HumanoidGate_OutsideAnimatedPosePreview_WritesNothingAndReportsOnce()
    {
        (SceneNode root, HumanoidComponent humanoid) = HumanoidNode();
        humanoid.PosePreviewMode = EHumanoidPosePreviewMode.MeshBindPose;
        Transform arm = Bone(root, "LeftArm");
        root.Transform.RecalculateMatrixHierarchyImmediate();
        Quaternion armBefore = arm.Rotation;
        Vector3 rootBefore = ((Transform)root.Transform).Translation;
        using Rig rig = MakeRig(root, 10, null, Entry(HumanoidClip(), 0, 2));

        rig.SeekTo(1);
        rig.SeekTo(1.5);

        arm.Rotation.ShouldBe(armBefore);
        rig.Root.Translation.ShouldBe(rootBefore);
        rig.Player.Diagnostics.Count(d => d.Message.Contains("preview", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    // ── Fade from the live pose ─────────────────────────────────────────

    [Test]
    public void Fade_OfPosedHumanoid_BlendsFromLiveMuscles()
    {
        (SceneNode root, HumanoidComponent humanoid) = HumanoidNode();
        humanoid.SetValue(EHumanoidValue.LeftArmDownUp, 0.8f);
        humanoid.SetValue(EHumanoidValue.SpineFrontBack, 0.3f);
        humanoid.ApplyCurrentMusclePose();
        Transform arm = Bone(root, "LeftArm");
        Quaternion armBefore = arm.Rotation;

        using Rig rig = MakeRig(root, 10, null, Entry(HumanoidClip(0.2f, 0.2f), 2, 4, blendIn: 2));

        rig.SeekTo(3);
        humanoid.TryGetRawHumanoidValue(EHumanoidValue.LeftArmDownUp, out float blended).ShouldBeTrue();
        blended.ShouldBe(0.5f, 1e-3f);
        humanoid.TryGetRawHumanoidValue(EHumanoidValue.SpineFrontBack, out float kept).ShouldBeTrue();
        kept.ShouldBe(0.3f, 1e-5f);

        // The first frame of the fade has almost no clip weight, so the arm must not pop to neutral.
        rig.SeekTo(2.002);
        ShouldBeNear(arm.Rotation, armBefore, 1e-3f);

        rig.SeekTo(8);
        humanoid.TryGetRawHumanoidValue(EHumanoidValue.LeftArmDownUp, out float restored).ShouldBeTrue();
        restored.ShouldBe(0.8f, 1e-5f);
        ShouldBeNear(arm.Rotation, armBefore, 1e-5f);
    }

    [Test]
    public void Fade_OfHumanoidPosedOnlyByBones_AddsDiagnostic()
    {
        (SceneNode root, _) = HumanoidNode();
        Bone(root, "LeftArm").Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
        using Rig rig = MakeRig(root, 10, null, Entry(HumanoidClip(0.2f, 0.2f), 2, 4, blendIn: 2));

        rig.SeekTo(3);
        rig.SeekTo(3.5);

        rig.Player.Diagnostics.Count(d => d.Message.Contains("no readable muscle", StringComparison.Ordinal)).ShouldBe(1);
    }

    // ── Exit paths ──────────────────────────────────────────────────────

    [Test]
    public void MemberOnHumanoidTarget_BackwardSeekBeforeClip_EqualsFreshEvaluation()
    {
        (SceneNode root, _) = HumanoidNode();
        using Rig rig = MakeRig(root, 10, null, Entry(PositionClip(2, new Vector3(3, 3, 3), new Vector3(3, 3, 3)), 1, 2));
        (SceneNode freshRoot, _) = HumanoidNode();
        using Rig fresh = MakeRig(freshRoot, 10, null, Entry(PositionClip(2, new Vector3(3, 3, 3), new Vector3(3, 3, 3)), 1, 2));

        rig.SeekTo(2);
        ShouldBeNear(rig.Root.Translation, new Vector3(3, 3, 3));

        rig.SeekTo(0.5);
        fresh.SeekTo(0.5);
        rig.Root.Translation.ShouldBe(fresh.Root.Translation);
        rig.Root.Rotation.ShouldBe(fresh.Root.Rotation);

        rig.SeekTo(2);
        rig.Player.Stop();
        rig.Root.Translation.ShouldBe(OriginalPosition);
    }

    [Test]
    public void ThrowingSetter_LeavesHumanoidUsableAfterStop()
    {
        SceneNode root = new("Avatar", new ThrowingTransform());
        AnimationClipComponentTests.AddPlaybackSkeleton(root);
        HumanoidComponent humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();
        humanoid.TryValidateAvatarDefinitionForPlayback(out string avatarProblem).ShouldBeTrue(avatarProblem);

        PropAnimObject boom = new(2f, false, true);
        boom.Keyframes.Add(new ObjectKeyframe { Second = 0f, Value = "next" });
        AnimationMember transform = new("Transform", EAnimationMemberType.Property);
        transform.Children.Add(new AnimationMember(nameof(ThrowingTransform.Boom), EAnimationMemberType.Property, boom));
        AnimationMember sceneNode = new("SceneNode", EAnimationMemberType.Property);
        sceneNode.Children.Add(transform);
        AnimationClip clip = new(sceneNode) { LengthInSeconds = 2f };

        using Rig rig = MakeRig(root, 10, null, Entry(clip, 0, 2));
        rig.SeekTo(1);
        rig.SeekTo(1.5);
        rig.Player.Diagnostics.Count(d => d.Message.Contains("Setter failure", StringComparison.Ordinal)).ShouldBe(1);

        rig.Player.Stop();

        AnimationClipComponent player = root.AddComponent<AnimationClipComponent>()!;
        player.Animation = HumanoidClip();
        player.EvaluateAtTime(1f);
        humanoid.WasLastNativeFrameAccepted.ShouldBeTrue();
    }

    [Test]
    public void KeepState_NextRunStartsFromTheKeptRoot()
    {
        (SceneNode root, _) = HumanoidNode();
        using Rig rig = MakeRig(root, 10, track => track.WhenFinished = ECutsceneFinishBehavior.KeepState, Entry(HumanoidClip(), 0, 2));

        rig.SeekTo(1);
        rig.Player.Stop();
        Vector3 kept = rig.Root.Translation;
        Vector3.Distance(kept, OriginalPosition).ShouldBeGreaterThan(0.01f);

        rig.SeekTo(1);
        rig.SeekTo(5);
        ShouldBeNear(rig.Root.Translation, kept, 1e-5f);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Restore_SharedSlot_RestoresWhenAnyTrackRestores(bool keepTrackFirst)
    {
        SceneNode node = PlainNode();
        FakeResolver resolver = new();
        resolver.ById[node.ID] = node;

        CutsceneAsset asset = new() { Duration = T(10) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        CutsceneTrack keep = new TransformTrack { Name = "Keep", BindingSlot = 0, WhenFinished = ECutsceneFinishBehavior.KeepState };
        CutsceneTrack restore = new AnimationClipTrack { Name = "Restore", BindingSlot = 0, WhenFinished = ECutsceneFinishBehavior.RestoreState };
        asset.Tracks.Add(keepTrackFirst ? keep : restore);
        asset.Tracks.Add(keepTrackFirst ? restore : keep);

        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        using CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, resolver);
        CutsceneStateSnapshot snapshot = new(table);
        snapshot.CaptureTransform(0);
        ((Transform)node.Transform).Translation = new Vector3(1, 2, 3);

        snapshot.Restore(forceAll: false, default);

        ((Transform)node.Transform).Translation.ShouldBe(OriginalPosition);
    }

    [Test]
    public void Restore_KeepStateOnly_ForgetsRecord()
    {
        SceneNode node = PlainNode();
        FakeResolver resolver = new();
        resolver.ById[node.ID] = node;

        CutsceneAsset asset = new() { Duration = T(10) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = node.ID });
        asset.Tracks.Add(new TransformTrack { Name = "Keep", BindingSlot = 0, WhenFinished = ECutsceneFinishBehavior.KeepState });

        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        using CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, resolver);
        CutsceneStateSnapshot snapshot = new(table);
        snapshot.CaptureTransform(0);
        Vector3 kept = new(1, 2, 3);
        ((Transform)node.Transform).Translation = kept;

        snapshot.Restore(forceAll: false, default);

        ((Transform)node.Transform).Translation.ShouldBe(kept);
        snapshot.TryGetTransform(0, out _, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void ReturnToOriginal_RestoresOtherMethodMembersOnHumanoid()
    {
        (SceneNode root, _) = HumanoidNode();
        LevelComponent level = root.AddComponent<LevelComponent>()!;

        // A method member on a component that is not the humanoid or the IK solver.
        PropAnimFloat value = new(2f, false, true);
        value.Keyframes.Add(new FloatKeyframe(0f, 5f, 0f, EVectorInterpType.Linear));
        AnimationMember find = new("GetComponentInHierarchy", EAnimationMemberType.Method)
        {
            MethodArguments = [nameof(LevelComponent)],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        find.Children.Add(new AnimationMember(nameof(LevelComponent.SetLevel), EAnimationMemberType.Method, value)
        {
            MethodArguments = [0f],
            AnimatedMethodArgumentIndex = 0,
        });
        AnimationMember sceneNode = new("SceneNode", EAnimationMemberType.Property);
        sceneNode.Children.Add(find);
        AnimationClip clip = new(sceneNode) { LengthInSeconds = 2f };

        using Rig rig = MakeRig(root, 10, null, Entry(clip, 1, 2));
        rig.SeekTo(2);
        level.Level.ShouldBe(5f);

        // A method has no getter, so its original value is its default argument.
        rig.SeekTo(5);
        level.Level.ShouldBe(0f);
    }

    [Test]
    public void Fade_OfHumanoidPosedOnlyByBones_ReportsAtFirstWrite()
    {
        (SceneNode root, _) = HumanoidNode();
        Bone(root, "LeftArm").Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);

        // The first write has full weight, so a report that waits for a weight below one never comes.
        using Rig rig = MakeRig(root, 10, null, Entry(HumanoidClip(0.2f, 0.2f), 0, 4));
        rig.SeekTo(1);

        rig.Player.Diagnostics.Count(d => d.Message.Contains("no readable muscle", StringComparison.Ordinal)).ShouldBe(1);
    }

    // ── Lease ───────────────────────────────────────────────────────────

    private static AnimStateMachineComponent AddStateMachine(SceneNode node, Vector3 value)
    {
        AnimStateMachineComponent component = node.AddComponent<AnimStateMachineComponent>()!;
        AnimationClip clip = PositionClip(4, value, value);
        component.StateMachine = new AnimStateMachine();
        component.StateMachine.Layers.Add(new AnimLayer { States = [new AnimState(clip)], InitialStateIndex = 0 });
        return component;
    }

    [Test]
    public void Lease_StopsStateMachineWrites_AndEndsAtClipExit()
    {
        SceneNode node = PlainNode();
        Vector3 machineValue = new(50, 50, 50);
        AnimStateMachineComponent machine = AddStateMachine(node, machineValue);
        machine.EvaluateAtTime(0.5f);
        ShouldBeNear(((Transform)node.Transform).Translation, machineValue);
        ((Transform)node.Transform).Translation = OriginalPosition;

        using Rig rig = MakeRig(node, 10, null, Entry(PositionClip(2, Vector3.One, Vector3.One), 1, 2));

        rig.SeekTo(2);
        machine.EvaluationSuspended.ShouldBeTrue();
        machine.EvaluationTick();
        ShouldBeNear(rig.Root.Translation, Vector3.One);

        rig.SeekTo(5);
        machine.EvaluationSuspended.ShouldBeFalse();
        rig.Root.Translation.ShouldBe(OriginalPosition);
    }

    [Test]
    public void Lease_EndsAtClose()
    {
        SceneNode node = PlainNode();
        AnimStateMachineComponent machine = AddStateMachine(node, Vector3.One);
        using Rig rig = MakeRig(node, 10, null, Entry(PositionClip(2, Vector3.One, Vector3.One), 1, 2));

        rig.SeekTo(2);
        machine.EvaluationSuspended.ShouldBeTrue();

        rig.Player.Stop();
        machine.EvaluationSuspended.ShouldBeFalse();
        rig.Root.Translation.ShouldBe(OriginalPosition);
    }

    [Test]
    public void Lease_RestoresEarlierSuspension()
    {
        SceneNode node = PlainNode();
        AnimStateMachineComponent machine = AddStateMachine(node, Vector3.One);
        machine.SetEvaluationSuspended(true);
        using Rig rig = MakeRig(node, 10, null, Entry(PositionClip(2, Vector3.One, Vector3.One), 1, 2));

        rig.SeekTo(2);
        rig.SeekTo(5);

        machine.EvaluationSuspended.ShouldBeTrue();
    }

    [Test]
    public void Lease_KeepsMachineSuspended_WhenSiblingClipStopsDuringLease()
    {
        SceneNode node = PlainNode();
        Vector3 machineValue = new(50, 50, 50);
        AnimStateMachineComponent machine = AddStateMachine(node, machineValue);
        machine.EvaluateAtTime(0.5f);
        ((Transform)node.Transform).Translation = OriginalPosition;
        machine.SetSuspendedByClip(true);
        using Rig rig = MakeRig(node, 10, null, Entry(PositionClip(2, Vector3.One, Vector3.One), 1, 2));

        rig.SeekTo(2);

        // A sibling clip component gives its suspension back at its stop.
        machine.SetSuspendedByClip(false);
        machine.EvaluationTick();
        ShouldBeNear(rig.Root.Translation, Vector3.One);

        rig.SeekTo(5);
        machine.EvaluationSuspended.ShouldBeFalse();
        machine.SuspendedByClip.ShouldBeFalse();
    }

    [Test]
    public void Lease_SuspendsClipComponentWithoutChangingItsPlaybackState()
    {
        SceneNode node = PlainNode();
        AnimationClipComponent player = node.AddComponent<AnimationClipComponent>()!;
        player.Animation = PositionClip(4, new Vector3(50, 0, 0), new Vector3(50, 0, 0));
        player.Play();
        player.IsPlaying.ShouldBeTrue();
        ((Transform)node.Transform).Translation = OriginalPosition;

        using Rig rig = MakeRig(node, 10, null, Entry(PositionClip(2, Vector3.One, Vector3.One), 1, 2));

        rig.SeekTo(2);
        player.EvaluationSuspended.ShouldBeTrue();
        typeof(AnimationClipComponent).GetMethod("TickAnimation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(player, null);
        ShouldBeNear(rig.Root.Translation, Vector3.One);
        player.IsPlaying.ShouldBeTrue();
        player.IsPaused.ShouldBeFalse();

        rig.SeekTo(5);
        player.EvaluationSuspended.ShouldBeFalse();
        player.IsPlaying.ShouldBeTrue();
        player.IsPaused.ShouldBeFalse();
    }

    // ── Original values ─────────────────────────────────────────────────

    [Test]
    public void TargetWithNoClip_ReturnsAllBonesToSnapshotPose()
    {
        (SceneNode root, HumanoidComponent humanoid) = HumanoidNode();
        Transform arm = Bone(root, "LeftArm");
        Transform knee = Bone(root, "LeftKnee");

        // The snapshot pose is not the bind pose, so a reset to the bind pose fails this test.
        Quaternion armOriginal = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
        Quaternion kneeOriginal = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.2f);
        arm.Rotation = armOriginal;
        knee.Rotation = kneeOriginal;

        using Rig rig = MakeRig(root, 10, null, Entry(HumanoidClip(), 1, 2));
        rig.SeekTo(2);
        MathF.Abs(Quaternion.Dot(Quaternion.Normalize(arm.Rotation), armOriginal)).ShouldBeLessThan(0.9999f);

        rig.SeekTo(5);

        arm.Rotation.ShouldBe(armOriginal);
        knee.Rotation.ShouldBe(kneeOriginal);
        humanoid.TryGetMuscleValue(EHumanoidValue.LeftArmDownUp, out _).ShouldBeFalse();

        // A later scene tick must not solve a stale humanoid pose over the restored bones.
        typeof(HumanoidComponent).GetMethod("TickMusclePose", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(humanoid, null);
        arm.Rotation.ShouldBe(armOriginal);
    }

    [Test]
    public void SharedSlot_KeepStateFirst_RestoreStateSecond_RestoresTheBonePose()
    {
        (SceneNode root, _) = HumanoidNode();
        Transform arm = Bone(root, "LeftArm");
        Quaternion armBefore = arm.Rotation;
        FakeResolver resolver = new();
        resolver.ById[root.ID] = root;

        CutsceneAsset asset = new() { Duration = T(10) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = root.ID });
        AnimationClipTrack keep = new() { Name = "Keep", BindingSlot = 0, WhenFinished = ECutsceneFinishBehavior.KeepState };
        keep.Clips.Add(Entry(HumanoidClip(), 0, 2));
        asset.Tracks.Add(keep);
        asset.Tracks.Add(new AnimationClipTrack { Name = "Restore", BindingSlot = 0, WhenFinished = ECutsceneFinishBehavior.RestoreState });

        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        using CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, resolver);
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table));

        player.Seek(T(1), ESeekIntent.Jump);
        player.Tick(0);
        ShouldBeNotBindPose(arm);

        player.Stop();

        ShouldBeNear(arm.Rotation, armBefore);
        ((Transform)root.Transform).Translation.ShouldBe(OriginalPosition);
        player.Diagnostics.ShouldContain(d => d.Message.Contains("if any of the tracks", StringComparison.Ordinal));
    }

    [Test]
    public void KeepState_LeavesPoseAtStop_AndReleasesLease()
    {
        SceneNode node = PlainNode();
        AnimStateMachineComponent machine = AddStateMachine(node, Vector3.One);
        Vector3 clipValue = new(3, 3, 3);
        using Rig rig = MakeRig(node, 10, track => track.WhenFinished = ECutsceneFinishBehavior.KeepState, Entry(PositionClip(2, clipValue, clipValue), 1, 2));

        rig.SeekTo(2);
        rig.Player.Stop();

        ShouldBeNear(rig.Root.Translation, clipValue);
        machine.EvaluationSuspended.ShouldBeFalse();
    }

    // ── Diagnostics at open ─────────────────────────────────────────────

    [Test]
    public void Open_ReportsAdditiveClip()
    {
        AnimationClip clip = HumanoidClip();
        clip.ImportedHumanoidRootMotionSettings!.HasAdditiveReferencePose = true;
        (SceneNode root, _) = HumanoidNode();
        using Rig rig = MakeRig(root, 10, null, Entry(clip, 0, 2));
        rig.Player.Diagnostics.ShouldContain(d => d.Message.Contains("additive", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void Open_ReportsClipComponentRemaps()
    {
        (SceneNode root, _) = HumanoidNode();
        AnimationClipComponent player = root.AddComponent<AnimationClipComponent>()!;
        player.FlipIKPositionLeftRight = true;
        using Rig rig = MakeRig(root, 10, null, Entry(HumanoidClip(), 0, 2));
        rig.Player.Diagnostics.ShouldContain(d => d.Message.Contains("remap", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void Open_ReportsMissingClipAndMissingMember()
    {
        AnimationMember missing = new("NoSuchMember", EAnimationMemberType.Property, new PropAnimFloat(1f, false, true));
        AnimationClip bad = new(missing) { LengthInSeconds = 1f };
        using Rig rig = MakeRig(PlainNode(), 10, null, Entry(null, 0, 1), Entry(bad, 2, 1));
        rig.Player.Diagnostics.ShouldContain(d => d.Message.Contains("no animation clip", StringComparison.OrdinalIgnoreCase));
        rig.Player.Diagnostics.ShouldContain(d => d.Message.Contains("NoSuchMember", StringComparison.Ordinal));
    }

    [Test]
    public void Open_ReportsHumanoidClipOnTargetWithoutHumanoid()
    {
        using Rig rig = MakeRig(PlainNode(), 10, null, Entry(HumanoidClip(), 0, 2));
        rig.Player.Diagnostics.ShouldContain(d => d.Message.Contains("HumanoidComponent", StringComparison.Ordinal));
    }

    [Test]
    public void Open_ReportsTrackOffsetOnTargetWithoutHumanoid()
    {
        using Rig rig = MakeRig(PlainNode(), 10, Offset(Vector3.One, Quaternion.Identity), Entry(PositionClip(2, Vector3.Zero, Vector3.Zero), 0, 2));
        rig.Player.Diagnostics.ShouldContain(d => d.Message.Contains("offset", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void BindingChangedAfterOpen_WritesNothingAndReportsOnce()
    {
        const string SlotName = "Actor";
        SceneNode first = PlainNode();
        SceneNode second = PlainNode();

        CutsceneAsset asset = new() { Duration = T(10) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = SlotName });
        AnimationClipTrack track = new() { Name = "Anim", BindingSlot = 0 };
        track.Clips.Add(Entry(PositionClip(2, Vector3.One, Vector3.One), 0, 2));
        asset.Tracks.Add(track);

        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        using CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, new FakeResolver());
        table.SetRuntimeSlot(SlotName, first);
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table));

        table.SetRuntimeSlot(SlotName, second);
        player.Seek(T(1), ESeekIntent.Jump);
        player.Tick(0);
        player.Seek(T(1.5), ESeekIntent.Jump);
        player.Tick(0);

        ((Transform)second.Transform).Translation.ShouldBe(OriginalPosition);
        player.Diagnostics.Count(d => d.Message.Contains("another node", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Test]
    public void Open_ReportsTrackWithoutNode()
    {
        CutsceneAsset asset = new() { Duration = T(10) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = "Actor" });
        AnimationClipTrack track = new() { Name = "Anim", BindingSlot = 0 };
        track.Clips.Add(Entry(PositionClip(2, Vector3.One, Vector3.One), 0, 2));
        asset.Tracks.Add(track);

        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        using CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, new FakeResolver());
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table));

        player.Diagnostics.ShouldContain(d => d.Message.Contains("no node when the cutscene opens", StringComparison.Ordinal));
    }

    [Test]
    public void Instancer_MakesIndependentCopy()
    {
        AnimationClip source = HumanoidClip();
        AnimationClip first = CutsceneClipInstancer.CreateInstance(source);
        AnimationClip second = CutsceneClipInstancer.CreateInstance(source);

        first.ShouldNotBeSameAs(source);
        first.ShouldNotBeSameAs(second);
        first.RootMember.ShouldNotBeSameAs(source.RootMember);
        first.HasRootMotion.ShouldBeTrue();
        first.LengthInSeconds.ShouldBe(source.LengthInSeconds);
        first.ImportedHumanoidRootMotionSettings.ShouldNotBeNull();
    }

    [Test]
    public void Instancer_CopiesQuaternionTracks()
    {
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
        AnimationClip source = PositionClip(2, Vector3.Zero, Vector3.One, turn, turn);
        AnimationClip copy = CutsceneClipInstancer.CreateInstance(source);

        using Rig sourceRig = MakeRig(PlainNode(), 10, null, Entry(source, 0, 2));
        sourceRig.SeekTo(0.5);
        ShouldBeNear(sourceRig.Root.Rotation, turn);

        AnimationValueStore store = new();
        source = PositionClip(2, Vector3.Zero, Vector3.One, turn, turn);
        AnimationSlotLayout layout = source.PrepareSampling(store);
        source.SampleToStore(0.5f, store);
        ShouldBeNear((Quaternion)store.GetValue(layout.SamplingApplyMembers[1].Slot)!, turn);

        AnimationValueStore copyStore = new();
        layout = copy.PrepareSampling(copyStore);
        copy.SampleToStore(0.5f, copyStore);
        ShouldBeNear((Quaternion)copyStore.GetValue(layout.SamplingApplyMembers[1].Slot)!, turn);
        ShouldBeNear((Vector3)copyStore.GetValue(layout.SamplingApplyMembers[0].Slot)!, new Vector3(0.25f));
    }

    [Test]
    public void SourceClip_IsNotPreparedByOpen()
    {
        AnimationClip source = PositionClip(2, Vector3.One, Vector3.One);
        using Rig first = MakeRig(PlainNode(), 10, null, Entry(source, 0, 2));
        using Rig second = MakeRig(PlainNode(), 10, null, Entry(source, 0, 2));
        source.SlotLayout.ShouldBeNull();

        first.SeekTo(1);
        second.SeekTo(1);
        ShouldBeNear(first.Root.Translation, Vector3.One);
        ShouldBeNear(second.Root.Translation, Vector3.One);
    }

    // ── Seek equivalence ────────────────────────────────────────────────

    private static CutsceneClip[] PlainTimeline()
    {
        Quaternion a = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f);
        Quaternion b = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.1f);
        return
        [
            Entry(PositionClip(4, new Vector3(0, 0, 0), new Vector3(4, 0, 0), a, b), 1, 4, blendIn: 1, blendOut: 2),
            Entry(PositionClip(4, new Vector3(9, 2, 0), new Vector3(1, 7, 3), b, Quaternion.Negate(a)), 3, 4, blendIn: 2, blendOut: 1),
            Entry(PositionClip(3, new Vector3(2, 2, 2), new Vector3(5, 5, 5)), 10, 3),
        ];
    }

    private static readonly double[] SeekTimes = [0.5, 1.5, 3.5, 4.0, 6.5, 8.0, 11.5, 2.25, 9.0, 3.5, 3.5, 12.5, 0.0, 5.0, 14.0, 4.5];

    [Test]
    public void SeekEquivalence_AnimationClips_MatchDirectEvaluation()
    {
        using Rig seeking = MakeRig(PlainNode(), 15, null, PlainTimeline());
        foreach (double time in SeekTimes)
        {
            seeking.SeekTo(time);
            using Rig direct = MakeRig(PlainNode(), 15, null, PlainTimeline());
            direct.SeekTo(time);
            ShouldBeNear(seeking.Root.Translation, direct.Root.Translation, 1e-5f);
            ShouldBeNear(seeking.Root.Rotation, direct.Root.Rotation, 1e-6f);
        }
    }

    private static CutsceneClip[] HumanoidTimeline()
        =>
        [
            Entry(HumanoidClip(0.1f, 0.6f, 0f, 2f), 1, 4, blendOut: 2),
            Entry(HumanoidClip(-0.4f, 0.2f, 1f, -1f), 3, 4, blendIn: 2),
            Entry(HumanoidClip(0.5f, 0.5f, 3f, 3f), 10, 2),
        ];

    [Test]
    public void SeekEquivalence_Humanoid_MatchesDirectEvaluation()
    {
        (SceneNode seekingRoot, _) = HumanoidNode();
        using Rig seeking = MakeRig(seekingRoot, 15, null, HumanoidTimeline());
        Transform seekingArm = Bone(seekingRoot, "LeftArm");
        Transform seekingHips = Bone(seekingRoot, "Hips");

        foreach (double time in SeekTimes)
        {
            seeking.SeekTo(time);
            (SceneNode directRoot, _) = HumanoidNode();
            using Rig direct = MakeRig(directRoot, 15, null, HumanoidTimeline());
            direct.SeekTo(time);

            string at = $"time {time}";
            TestContext.Out.WriteLine(at);
            ShouldBeNear(seeking.Root.Translation, direct.Root.Translation, 1e-4f);
            ShouldBeNear(seeking.Root.Rotation, direct.Root.Rotation, 1e-5f);
            ShouldBeNear(seekingArm.Rotation, Bone(directRoot, "LeftArm").Rotation, 1e-5f);
            ShouldBeNear(seekingHips.Translation, Bone(directRoot, "Hips").Translation, 1e-4f);
        }
    }

    // ── Allocation ──────────────────────────────────────────────────────

    [Test]
    public void TransformClips_AllocateNothingPerFrame()
    {
        using Rig rig = MakeRig(PlainNode(), 15, null, PlainTimeline());
        for (int i = 0; i < 300; i++)
        {
            rig.SeekTo((i % 150) * 0.1);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            rig.Player.Seek(T((i % 150) * 0.1), ESeekIntent.Scrub);
            rig.Player.Tick(0);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
    }

    [Test]
    public void HumanoidCrossfade_AllocatesNothingPerFrame()
    {
        (SceneNode root, _) = HumanoidNode();
        using Rig rig = MakeRig(root, 15, null, HumanoidTimeline());
        for (int i = 0; i < 300; i++)
        {
            rig.SeekTo(1.0 + (i % 100) * 0.1);
        }

        const int Frames = 1000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Frames; i++)
        {
            rig.Player.Seek(T(1.0 + (i % 100) * 0.1), ESeekIntent.Scrub);
            rig.Player.Tick(0);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
    }

    [Test]
    public void ImportedWalkWithoutIK_AllocatesNothingPerFrame()
    {
        (long bytes, _) = MeasureWalk(withIK: false);
        bytes.ShouldBe(0);
    }

    [Test]
    [Explicit("The IK rig allocates in engine code. This test only prints the number.")]
    public void ImportedWalkWithIK_PrintsAllocationPerFrame()
    {
        (long bytes, int frames) = MeasureWalk(withIK: true);
        TestContext.Progress.WriteLine($"Imported walk with IK: {bytes / (double)frames:F1} bytes per frame.");
    }

    private static (long Bytes, int Frames) MeasureWalk(bool withIK)
    {
        (SceneNode root, _) = HumanoidNode();
        if (withIK)
            root.AddComponent<HumanoidIKSolverComponent>();
        AnimationClip walk = AnimYamlImporter.Import(Path.Combine(FindRepositoryRoot(), "Assets", "Walks", "Sexy Walk.anim"));
        double length = walk.LengthInSeconds;
        using Rig rig = MakeRig(root, length, null, Entry(walk, 0, length));
        for (int i = 0; i < 100; i++)
            rig.SeekTo(length * (i % 50) / 50.0);

        const int Frames = 500;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Frames; i++)
        {
            rig.Player.Seek(T(length * (i % 50) / 50.0), ESeekIntent.Scrub);
            rig.Player.Tick(0);
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before, Frames);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.sln")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    // ── Serialization ───────────────────────────────────────────────────

    [Test]
    public void Track_RoundTripsThroughYaml()
    {
        CutsceneAsset asset = new() { Duration = T(4) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = Guid.NewGuid() });
        AnimationClipTrack track = new() { Name = "Anim", RootOffsetPosition = new Vector3(1, 2, 3), RootOffsetRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f) };
        track.Clips.Add(new AnimationClipTrackClip { Start = T(1), Duration = T(2), Clip = PositionClip(2, Vector3.One, Vector3.One) });
        asset.Tracks.Add(track);

        string yaml = AssetManager.Serializer.Serialize(asset, typeof(CutsceneAsset));
        CutsceneAsset clone = AssetManager.Deserializer.Deserialize<CutsceneAsset>(yaml)!;

        AnimationClipTrack cloneTrack = clone.Tracks[0].ShouldBeOfType<AnimationClipTrack>();
        cloneTrack.RootOffsetPosition.ShouldBe(track.RootOffsetPosition);
        cloneTrack.RootOffsetRotation.ShouldBe(track.RootOffsetRotation);
        cloneTrack.Clips[0].ShouldBeOfType<AnimationClipTrackClip>().Clip.ShouldNotBeNull();
    }
}
