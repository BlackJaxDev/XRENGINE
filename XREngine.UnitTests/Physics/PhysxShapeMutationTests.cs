using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Physics;

namespace XREngine.UnitTests.Physics;

[TestFixture]
[NonParallelizable]
public unsafe sealed class PhysxShapeMutationTests
{
    private PhysxScene? _scene;

    [SetUp]
    public void SetUp()
    {
        _scene = new PhysxScene();
        _scene.Initialize();
    }

    [TearDown]
    public void TearDown()
    {
        _scene?.Destroy();
        _scene = null;
    }

    [Test]
    public void ReplaceCollisionShapes_PreservesNativeActorAndInstallsEntireCompound()
    {
        PhysicsRigidBodyCreateInfo initial = new(
            [new PhysicsColliderShape { Geometry = new IPhysicsGeometry.Sphere(0.5f) }],
            FallbackGeometry: null,
            RuntimeMaterial: null,
            MaterialDefinition: new PhysicsMaterialDefinition(),
            Pose: (Vector3.Zero, Quaternion.Identity),
            FallbackShapeOffsetTranslation: Vector3.Zero,
            FallbackShapeOffsetRotation: Quaternion.Identity,
            Density: 1.0f,
            LayerMask: new LayerMask(1));
        PhysxDynamicRigidBody body = _scene!.BackendService
            .CreateDynamicRigidBody(initial)
            .ShouldBeOfType<PhysxDynamicRigidBody>();
        _scene.AddActor(body);

        nint originalActor = (nint)body.ActorPtr;
        body.ShapeCount.ShouldBe(1u);
        PhysicsRigidBodyCreateInfo replacement = initial with
        {
            ColliderShapes =
            [
                new PhysicsColliderShape
                {
                    Geometry = new IPhysicsGeometry.Box(new Vector3(0.4f)),
                    LocalPosition = -Vector3.UnitX,
                },
                new PhysicsColliderShape
                {
                    Geometry = new IPhysicsGeometry.Sphere(0.25f),
                    LocalPosition = Vector3.UnitX,
                },
            ],
        };

        _scene.BackendService.TryReplaceCollisionShapes(body, replacement).ShouldBeTrue();

        ((nint)body.ActorPtr).ShouldBe(originalActor);
        body.ShapeCount.ShouldBe(2u);
        body.GetShapes().Select(static shape => shape.LocalPose.position.X)
            .Order()
            .ShouldBe([-1.0f, 1.0f]);
    }
}
