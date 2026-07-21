using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainColliderSetTests
{
    [Test]
    public void IdenticalShapeStreamsDeduplicateWithinAWorld()
    {
        var world = new TestWorldContext();
        PhysicsChainComponent component = CreateRegisteredComponent(world);
        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        PhysicsChainWorld activeScheduler = scheduler!;

        PhysicsChainColliderShape[] shapes =
        [
            PhysicsChainColliderShape.Sphere(new Vector3(1.0f, 2.0f, 3.0f), 0.5f),
            PhysicsChainColliderShape.Capsule(Vector3.Zero, Vector3.UnitY * 2.0f, 0.25f),
        ];
        PhysicsChainColliderSet first = activeScheduler.GetOrCreateColliderSet(shapes);
        PhysicsChainColliderSet second = activeScheduler.GetOrCreateColliderSet(shapes.AsSpan());

        second.ShouldBeSameAs(first);
        activeScheduler.GetUniqueColliderSetCount().ShouldBe(1);
        first.StableId.ShouldBeGreaterThan(0L);
        component.RuntimeHandle.IsValid.ShouldBeTrue();
    }

    [Test]
    public void ShapeAndPoseVersionsAreIndependentAndDirtyRangesAreBounded()
    {
        var poses = new PhysicsChainColliderPoseBuffer(4);
        poses.ClearDirtyRange();
        uint originalVersion = poses.PoseVersion;

        poses.TrySetPose(2, Matrix4x4.CreateTranslation(1.0f, 2.0f, 3.0f)).ShouldBeTrue();
        poses.DirtyStart.ShouldBe(2);
        poses.DirtyCount.ShouldBe(1);
        poses.PoseVersion.ShouldNotBe(originalVersion);

        poses.TrySetPose(0, Matrix4x4.CreateScale(2.0f)).ShouldBeTrue();
        poses.DirtyStart.ShouldBe(0);
        poses.DirtyCount.ShouldBe(3);
        poses.TrySetPose(4, Matrix4x4.Identity).ShouldBeFalse();
    }

    [Test]
    public void DegenerateOrNonFiniteShapesAreRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PhysicsChainColliderShape.Capsule(Vector3.Zero, Vector3.Zero, 1.0f));
        Should.Throw<ArgumentOutOfRangeException>(() => PhysicsChainColliderShape.Sphere(Vector3.Zero, -1.0f));
        Should.Throw<ArgumentOutOfRangeException>(() => PhysicsChainColliderShape.Box(Vector3.Zero, new Vector3(float.NaN)));
    }

    private static PhysicsChainComponent CreateRegisteredComponent(TestWorldContext world)
    {
        var node = new SceneNode();
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        component.World = world;
        world.Run(ETickGroup.PostPhysics);
        return component;
    }
}
