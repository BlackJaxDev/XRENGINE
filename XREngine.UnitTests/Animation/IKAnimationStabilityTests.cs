using System.Numerics;
using System.Reflection;
using XREngine.Animation.Importers;
using NUnit.Framework;
using XREngine.Animation.IK;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class IKAnimationStabilityTests
{
    [Test]
    public void SexyWalk_RepeatedExactSamplesProduceTheSameSolvedPose()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Solver.Destroy();
        rig.Humanoid.ClearIKTargets();
        rig.AvatarRoot.Transform.RecalculateMatrixHierarchyImmediate();
        SaveBindPose(rig.AvatarRoot.Transform);
        rig.Humanoid.SetFromNode();
        rig.Humanoid.Settings.IKGoalPolicy = EHumanoidIKGoalPolicy.AlwaysApply;
        var ik = rig.AvatarRoot.AddComponent<HumanoidIKSolverComponent>()!;
        var player = rig.AvatarRoot.AddComponent<AnimationClipComponent>()!;
        string repository = FindRepositoryRoot();
        player.Animation = AnimYamlImporter.Import(Path.Combine(repository, "Assets", "Walks", "Sexy Walk.anim"));
        Assert.That(player.Animation.HasIKGoals, Is.True);
        Assert.That(player.TryValidatePlaybackCapabilities(out string diagnostic), Is.True, diagnostic);
        float maximumRepeatDrift = 0;
        for (int frame = 0; frame < 90; frame++)
        {
            float time = player.Animation.LengthInSeconds * frame / 90f;
            player.EvaluateAtTime(time);
            Assert.That(typeof(HumanoidComponent).GetProperty("WasLastNativeFrameAccepted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Humanoid), Is.True, $"Frame {frame}: native pose rejected");
            rig.AvatarRoot.Transform.RecalculateMatrixHierarchyImmediate();
            Vector3 firstFoot = rig.Humanoid.Left.Foot.Node!.Transform.WorldTranslation;
            player.EvaluateAtTime(time);
            rig.AvatarRoot.Transform.RecalculateMatrixHierarchyImmediate();
            maximumRepeatDrift = Math.Max(maximumRepeatDrift,
                Vector3.Distance(firstFoot, rig.Humanoid.Left.Foot.Node!.Transform.WorldTranslation));
            Assert.That(ik.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.LeftFoot).Status,
                Is.EqualTo(EHumanoidIKGoalApplicationStatus.AppliedAuthored));
        }
        TestContext.Out.WriteLine($"Sexy Walk repeated-sample foot drift: {maximumRepeatDrift:R}");
        Assert.That(maximumRepeatDrift, Is.LessThan(0.0001f));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static void SaveBindPose(TransformBase transform)
    {
        transform.SaveBindState();
        foreach (TransformBase child in transform.Children)
            SaveBindPose(child);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExternalSolve_SuppressesExactlyOneLateSolveRegardlessOfPhysics(bool physicsBetween)
    {
        var root = new SceneNode("Root", new Transform());
        var solver = root.AddComponent<CountingIKSolverComponent>()!;
        solver.UpdateSolverExternal();
        Assert.That(solver.SolveCount, Is.EqualTo(1));
        if (physicsBetween)
            InvokeTick(solver, "FixedUpdate");
        InvokeTick(solver, "LateUpdate");
        Assert.That(solver.SolveCount, Is.EqualTo(1), "External solve must not run twice in its scheduled frame.");
        InvokeTick(solver, "Update");
        InvokeTick(solver, "LateUpdate");
        Assert.That(solver.SolveCount, Is.EqualTo(2), "The next animation frame must not wait for a physics tick.");
    }

    private static void InvokeTick(BaseIKSolverComponent solver, string method)
        => typeof(BaseIKSolverComponent).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(solver, null);

    [Test]
    public void AnalyticLimb_ReachesFixedTargetInOneSolve()
    {
        var root = new SceneNode("Root", new Transform());
        var upper = new SceneNode(root, "Upper", new Transform());
        var lower = new SceneNode(upper, "Lower", new Transform(new Vector3(0, -1, 0.1f)));
        var end = new SceneNode(lower, "End", new Transform(new Vector3(0, -1, 0.1f)));
        var target = new SceneNode(root, "Target", new Transform(new Vector3(0.4f, -1.4f, 0.3f)));
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
        var solver = new IKSolverLimb(ELimbEndEffector.LeftFoot)
        {
            _bendModifier = ELimbBendModifier.Parent,
            IKPositionWeight = 1,
            IKRotationWeight = 0,
        };
        Assert.That(solver.SetChain((Transform)upper.Transform, (Transform)lower.Transform,
            (Transform)end.Transform, (Transform)root.Transform), Is.True);
        solver.TargetIKTransform = target.Transform;
        solver.Update();
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
        float firstError = Vector3.Distance(end.Transform.WorldTranslation, target.Transform.WorldTranslation);
        Vector3 firstPosition = end.Transform.WorldTranslation;
        solver.Update();
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
        float secondMovement = Vector3.Distance(firstPosition, end.Transform.WorldTranslation);
        TestContext.Out.WriteLine($"First-solve error: {firstError:R}; repeated-solve movement: {secondMovement:R}");
        Assert.That(firstError, Is.LessThan(0.0001f));
        Assert.That(secondMovement, Is.LessThan(0.0001f));
    }
}
