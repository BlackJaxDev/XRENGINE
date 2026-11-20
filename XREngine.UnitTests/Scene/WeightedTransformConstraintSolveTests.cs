using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class WeightedTransformConstraintSolveTests
{
    private const float Tolerance = 1e-4f;

    [Test]
    public void ParentMode_PlacesTargetAsChildOfSourceWithSourceSpaceOffset()
    {
        var root = new SceneNode("Root", new Transform());
        var source = new SceneNode(root, "Source", new Transform(new Vector3(1.0f, 2.0f, 3.0f)));
        var sourceTransform = (Transform)source.Transform;
        sourceTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f);
        sourceTransform.Scale = new Vector3(2.0f);
        var target = new SceneNode(root, "Target", new Transform());
        WeightedTransformConstraintComponent constraint = target.AddComponent<WeightedTransformConstraintComponent>()!;
        constraint.Mode = TransformConstraintMode.Parent;
        constraint.Channels = TransformConstraintChannels.Parent;
        constraint.Locked = true;
        constraint.Sources =
        [
            new TransformConstraintSource
            {
                SourceTransform = source.Transform,
                Weight = 1.0f,
                PositionOffset = new Vector3(1.0f, 0.0f, 0.0f),
                RotationOffset = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI * 0.5f),
            },
        ];

        Solve(root, constraint);

        // The source scale (2) and rotation (+90 degrees around Y maps +X to -Z) move the offset.
        target.Transform.WorldTranslation.ShouldBeNear(new Vector3(1.0f, 2.0f, 1.0f), Tolerance);
        Quaternion expectedRotation = sourceTransform.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI * 0.5f);
        AssertSameRotation(target.Transform.WorldRotation, expectedRotation);
    }

    [Test]
    public void ParentMode_RebakeKeepsCurrentPose()
    {
        var root = new SceneNode("Root", new Transform());
        var source = new SceneNode(root, "Source", new Transform(new Vector3(0.0f, 1.0f, 0.0f)));
        ((Transform)source.Transform).Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        var target = new SceneNode(root, "Target", new Transform(new Vector3(0.5f, -0.25f, 2.0f)));
        ((Transform)target.Transform).Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        WeightedTransformConstraintComponent constraint = target.AddComponent<WeightedTransformConstraintComponent>()!;
        constraint.Mode = TransformConstraintMode.Parent;
        constraint.Channels = TransformConstraintChannels.Parent;
        constraint.Locked = true;
        constraint.Sources = [new TransformConstraintSource { SourceTransform = source.Transform, Weight = 1.0f }];
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
        Vector3 position = target.Transform.WorldTranslation;
        Quaternion rotation = target.Transform.WorldRotation;

        constraint.RebakeOffsets();
        Solve(root, constraint);

        target.Transform.WorldTranslation.ShouldBeNear(position, Tolerance);
        AssertSameRotation(target.Transform.WorldRotation, rotation);
    }

    [Test]
    public void MatchMode_MultipliesScaleOffsetAndUsesRestScaleWithoutSources()
    {
        var root = new SceneNode("Root", new Transform());
        var source = new SceneNode(root, "Source", new Transform());
        ((Transform)source.Transform).Scale = new Vector3(2.0f, 3.0f, 4.0f);
        var target = new SceneNode(root, "Target", new Transform());
        WeightedTransformConstraintComponent constraint = target.AddComponent<WeightedTransformConstraintComponent>()!;
        constraint.Channels = TransformConstraintChannels.Scale;
        constraint.SolveInLocalSpace = true;
        constraint.Locked = true;
        constraint.HasRestPose = true;
        constraint.ScaleAtRest = new Vector3(0.5f);
        constraint.ScaleOffset = new Vector3(0.5f, 1.0f, 2.0f);
        var weightedSource = new TransformConstraintSource { SourceTransform = source.Transform, Weight = 1.0f };
        constraint.Sources = [weightedSource];

        Solve(root, constraint);
        ((Transform)target.Transform).Scale.ShouldBeNear(new Vector3(1.0f, 3.0f, 8.0f), Tolerance);

        weightedSource.Weight = 0.0f;
        Solve(root, constraint);
        ((Transform)target.Transform).Scale.ShouldBeNear(new Vector3(0.5f), Tolerance);
    }

    [Test]
    public void AimMode_PointsAimAxisAtSourceAndUpAxisTowardUpObject()
    {
        var root = new SceneNode("Root", new Transform());
        var source = new SceneNode(root, "Source", new Transform(new Vector3(5.0f, 0.0f, 0.0f)));
        var up = new SceneNode(root, "Up", new Transform(new Vector3(0.0f, 0.0f, 3.0f)));
        var target = new SceneNode(root, "Target", new Transform());
        WeightedTransformConstraintComponent constraint = target.AddComponent<WeightedTransformConstraintComponent>()!;
        constraint.Mode = TransformConstraintMode.Aim;
        constraint.Channels = TransformConstraintChannels.All;
        constraint.Locked = true;
        constraint.AimAxis = -Vector3.UnitZ;
        constraint.UpAxis = Vector3.UnitY;
        constraint.WorldUpType = TransformConstraintWorldUpType.ObjectUp;
        constraint.WorldUpTransform = up.Transform;
        constraint.Sources = [new TransformConstraintSource { SourceTransform = source.Transform, Weight = 1.0f }];

        Solve(root, constraint);

        Quaternion rotation = target.Transform.WorldRotation;
        Vector3.Transform(-Vector3.UnitZ, rotation).ShouldBeNear(Vector3.UnitX, Tolerance);
        Vector3.Transform(Vector3.UnitY, rotation).ShouldBeNear(Vector3.UnitZ, Tolerance);
        target.Transform.WorldTranslation.ShouldBeNear(Vector3.Zero, Tolerance);
    }

    [Test]
    public void AimMode_RollTurnsAroundAimAxis()
    {
        var root = new SceneNode("Root", new Transform());
        var source = new SceneNode(root, "Source", new Transform(new Vector3(0.0f, 0.0f, -4.0f)));
        var target = new SceneNode(root, "Target", new Transform());
        WeightedTransformConstraintComponent constraint = target.AddComponent<WeightedTransformConstraintComponent>()!;
        constraint.Mode = TransformConstraintMode.Aim;
        constraint.Channels = TransformConstraintChannels.Rotation;
        constraint.Locked = true;
        constraint.Roll = 90.0f;
        constraint.Sources = [new TransformConstraintSource { SourceTransform = source.Transform, Weight = 1.0f }];

        Solve(root, constraint);

        // Right-handed 90 degrees around -Z turns +Y toward +X.
        Vector3.Transform(Vector3.UnitY, target.Transform.WorldRotation).ShouldBeNear(Vector3.UnitX, Tolerance);
    }

    [Test]
    public void ExecutionOrder_PutsReadersAfterWritersAndIgnoresCycles()
    {
        var root = new SceneNode("Root", new Transform());
        var a = new SceneNode(root, "A", new Transform());
        var aChild = new SceneNode(a, "A Child", new Transform());
        var b = new SceneNode(root, "B", new Transform());
        var c = new SceneNode(root, "C", new Transform());
        var source = new SceneNode(root, "Source", new Transform());

        WeightedTransformConstraintComponent first = CreateConstraint(a, source.Transform);
        WeightedTransformConstraintComponent second = CreateConstraint(b, aChild.Transform);
        WeightedTransformConstraintComponent third = CreateConstraint(c, b.Transform);

        TransformConstraintExecutionOrder.Assign([third, second, first]).ShouldBe(0);
        first.ExecutionOrder.ShouldBe(0);
        second.ExecutionOrder.ShouldBe(1);
        third.ExecutionOrder.ShouldBe(2);
        first.HasDependents.ShouldBeTrue();
        second.HasDependents.ShouldBeTrue();
        third.HasDependents.ShouldBeFalse();

        first.Sources = [new TransformConstraintSource { SourceTransform = c.Transform, Weight = 1.0f }];
        TransformConstraintExecutionOrder.Assign([first, second, third]).ShouldBe(1);
    }

    [Test]
    public void ExecutionOrder_LocalSolveIgnoresMovedAncestors()
    {
        var root = new SceneNode("Root", new Transform());
        var a = new SceneNode(root, "A", new Transform());
        var aChild = new SceneNode(a, "A Child", new Transform());
        var b = new SceneNode(root, "B", new Transform());
        var source = new SceneNode(root, "Source", new Transform());

        WeightedTransformConstraintComponent writer = CreateConstraint(a, source.Transform);
        WeightedTransformConstraintComponent reader = CreateConstraint(b, aChild.Transform);
        reader.SolveInLocalSpace = true;

        TransformConstraintExecutionOrder.Assign([reader, writer]);
        reader.ExecutionOrder.ShouldBe(0);
        writer.HasDependents.ShouldBeFalse();
    }

    private static WeightedTransformConstraintComponent CreateConstraint(SceneNode node, TransformBase source)
    {
        WeightedTransformConstraintComponent constraint = node.AddComponent<WeightedTransformConstraintComponent>()!;
        constraint.Sources = [new TransformConstraintSource { SourceTransform = source, Weight = 1.0f }];
        return constraint;
    }

    private static void Solve(SceneNode root, WeightedTransformConstraintComponent constraint)
    {
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
        constraint.Evaluate();
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
    }

    private static void AssertSameRotation(Quaternion actual, Quaternion expected)
        => MathF.Abs(Quaternion.Dot(Quaternion.Normalize(actual), Quaternion.Normalize(expected))).ShouldBe(1.0f, Tolerance);
}
