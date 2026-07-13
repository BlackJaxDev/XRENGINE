using System.Numerics;
using JoltPhysicsSharp;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Joints;
using XREngine.Scene.Physics.Jolt;

namespace XREngine.UnitTests.Physics;

[TestFixture]
[NonParallelizable]
public sealed class JoltProductionHardeningTests
{
    private JoltScene? _scene;

    [SetUp]
    public void SetUp()
    {
        _scene = new JoltScene();
        _scene.Initialize();
    }

    [TearDown]
    public void TearDown()
    {
        _scene?.Destroy();
        _scene = null;
    }

    [Test]
    public void Destroy_ReleasesActorsControllersAndConstraintsBeforeNativeSystem()
    {
        JoltDynamicRigidBody bodyA = CreateDynamic(new Vector3(-1.0f, 1.0f, 0.0f));
        JoltDynamicRigidBody bodyB = CreateDynamic(new Vector3(1.0f, 1.0f, 0.0f));
        _scene!.CreateFixedJoint(bodyA, JointAnchor.Identity, bodyB, JointAnchor.Identity);
        _ = new JoltCharacterVirtualController(_scene, new Vector3(0.0f, 1.0f, 0.0f))
        {
            Radius = 0.25f,
            TotalHeight = 1.0f,
        };

        _scene.GetDiagnostics().ShouldBe(new JoltPhysicsDiagnostics(2, 2, 0, 2, 1, 1));

        _scene.Destroy();

        _scene.GetDiagnostics().ShouldBe(default);
        _scene.PhysicsSystem.ShouldBeNull();
        _scene.JobSystem.ShouldBeNull();
    }

    [Test]
    public void RemoveReactivateAndTeleport_PreserveStableNativeBodyIdentifier()
    {
        JoltDynamicRigidBody body = CreateDynamic(Vector3.Zero);
        BodyID originalId = body.BodyID;

        _scene!.RemoveActor(body);
        _scene.GetDiagnostics().ActorCount.ShouldBe(0);
        _scene.AddActor(body);
        body.BodyID.ShouldBe(originalId);

        Vector3 teleportedPosition = new(4.0f, 3.0f, -2.0f);
        Quaternion teleportedRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);
        body.SetTransform(teleportedPosition, teleportedRotation);

        Vector3.Distance(body.Transform.position, teleportedPosition).ShouldBeLessThan(0.0001f);
        MathF.Abs(Quaternion.Dot(body.Transform.rotation, teleportedRotation)).ShouldBeGreaterThan(0.9999f);
    }

    [Test]
    public void SetShapeInPlace_PreservesBodyIdentifierAndUpdatesQueryBounds()
    {
        JoltDynamicRigidBody body = CreateDynamic(new Vector3(3.0f, 0.0f, 0.0f));
        BodyID originalId = body.BodyID;
        using BoxShape replacement = new(new Vector3(1.0f, 0.5f, 0.5f));

        _scene!.TrySetShapeInPlace(body, replacement).ShouldBeTrue();

        body.BodyID.ShouldBe(originalId);
        _scene.GetDiagnostics().ActorCount.ShouldBe(1);
        Segment ray = new(Vector3.Zero, new Vector3(5.0f, 0.0f, 0.0f));
        _scene.RaycastAny(ray, LayerMask.Everything, null, out _).ShouldBeTrue();
    }

    [Test]
    public void DebugExtraction_DrawsNativeShapesConstraintsAndCapturedContacts()
    {
        PhysicsVisualizeSettings previous = Engine.Rendering.Settings.PhysicsVisualizeSettings;
        PhysicsVisualizeSettings settings = new();
        settings.SetAllTrue();
        Engine.Rendering.Settings.PhysicsVisualizeSettings = settings;
        try
        {
            _scene!.CreateStaticRigidBody(
                new IPhysicsGeometry.Box(new Vector3(2.0f, 0.5f, 2.0f)),
                (new Vector3(0.0f, -0.5f, 0.0f), Quaternion.Identity),
                Vector3.Zero,
                Quaternion.Identity,
                LayerMask.Everything).ShouldNotBeNull();
            JoltDynamicRigidBody bodyA = CreateDynamic(new Vector3(0.0f, 0.25f, 0.0f));
            JoltDynamicRigidBody bodyB = CreateDynamic(new Vector3(0.8f, 0.25f, 0.0f));
            _scene.CreateDistanceJoint(bodyA, JointAnchor.Identity, bodyB, JointAnchor.Identity);

            _scene.StepSimulation();
            Should.NotThrow(_scene.DebugRenderCollect);

            JoltDebugRenderSnapshot snapshot = _scene.GetDebugRenderSnapshot();
            snapshot.BodyCount.ShouldBe(3);
            snapshot.JointCount.ShouldBe(1);
            snapshot.ContactCount.ShouldBeGreaterThan(0);
        }
        finally
        {
            Engine.Rendering.Settings.PhysicsVisualizeSettings = previous;
        }
    }

    [Test]
    public void RepeatedInitializeCreateDestroyCycles_ReturnEveryDiagnosticCountToZero()
    {
        _scene!.Destroy();

        for (int cycle = 0; cycle < 5; cycle++)
        {
            _scene.Initialize();
            JoltDynamicRigidBody bodyA = CreateDynamic(new Vector3(-0.5f, 0.0f, 0.0f));
            JoltDynamicRigidBody bodyB = CreateDynamic(new Vector3(0.5f, 0.0f, 0.0f));
            _scene.CreateHingeJoint(bodyA, JointAnchor.Identity, bodyB, JointAnchor.Identity);
            _ = new JoltCharacterVirtualController(_scene, Vector3.UnitY);
            _scene.StepSimulation();

            _scene.Destroy();
            _scene.GetDiagnostics().ShouldBe(default);
        }
    }

    private JoltDynamicRigidBody CreateDynamic(Vector3 position)
    {
        JoltDynamicRigidBody? body = _scene!.CreateDynamicRigidBody(
            new IPhysicsGeometry.Sphere(0.25f),
            (position, Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            LayerMask.Everything);
        body.ShouldNotBeNull();
        return body!;
    }
}
