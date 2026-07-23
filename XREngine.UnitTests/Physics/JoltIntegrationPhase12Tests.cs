using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Scene.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Joints;
using XREngine.Scene.Physics.Jolt;
using JoltPhysicsSharp;

namespace XREngine.UnitTests.Physics;

[TestFixture]
[NonParallelizable]
public unsafe class JoltIntegrationPhase12Tests
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

    private JoltDynamicRigidBody CreateDynamicSphere(Vector3 position, int layerBit)
    {
        var body = _scene!.CreateDynamicRigidBody(
            new IPhysicsGeometry.Sphere(0.25f),
            (position, Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            new LayerMask(1 << layerBit));

        body.ShouldNotBeNull();
        return body!;
    }

    private JoltStaticRigidBody CreateStaticSphere(Vector3 position, int layerBit)
    {
        var body = _scene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Sphere(0.3f),
            (position, Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            new LayerMask(1 << layerBit));

        body.ShouldNotBeNull();
        return body!;
    }

    [Test]
    public void LayerMask_AsJoltObjectLayer_UsesLowestSetBitAsGroup()
    {
        LayerMask mask = new(1 << 5);

        ObjectLayer layer = mask.AsJoltObjectLayer();

        ObjectLayerPairFilterMask.GetGroup(layer).ShouldBe(1u << 5);
        ObjectLayerPairFilterMask.GetMask(layer).ShouldBe((uint)(1 << 5));
    }

    [Test]
    public void LayerMask_AsJoltObjectLayer_ZeroMaskUsesAllMaskBits()
    {
        LayerMask mask = new(0);

        ObjectLayer layer = mask.AsJoltObjectLayer();

        ObjectLayerPairFilterMask.GetGroup(layer).ShouldBe(1u);
        ObjectLayerPairFilterMask.GetMask(layer).ShouldBe(0xFFFFu);
    }

    [TestCase("Fixed")]
    [TestCase("Distance")]
    [TestCase("Hinge")]
    [TestCase("Prismatic")]
    [TestCase("Spherical")]
    [TestCase("D6")]
    public void JoltScene_AllJointFactories_CanCreateConfigureStepAndRelease(string jointKind)
    {
        var scene = _scene!;
        var bodyA = CreateDynamicSphere(new Vector3(-1.0f, 0.0f, 0.0f), 1);
        var bodyB = CreateDynamicSphere(new Vector3(1.0f, 0.0f, 0.0f), 1);

        IAbstractJoint joint = jointKind switch
        {
            "Fixed" => scene.CreateFixedJoint(bodyA, JointAnchor.Identity, bodyB, JointAnchor.Identity),
            "Distance" => scene.CreateDistanceJoint(bodyA, JointAnchor.Identity, bodyB, JointAnchor.Identity),
            "Hinge" => scene.CreateHingeJoint(bodyA, JointAnchor.Identity, bodyB, JointAnchor.Identity),
            "Prismatic" => scene.CreatePrismaticJoint(bodyA, JointAnchor.Identity, bodyB, JointAnchor.Identity),
            "Spherical" => scene.CreateSphericalJoint(bodyA, JointAnchor.Identity, bodyB, JointAnchor.Identity),
            "D6" => scene.CreateD6Joint(bodyA, JointAnchor.Identity, bodyB, JointAnchor.Identity),
            _ => throw new ArgumentOutOfRangeException(nameof(jointKind), jointKind, null),
        };

        joint.ShouldNotBeNull();
        ConfigureJoint(joint);
        Should.NotThrow(() => scene.StepSimulation());
        Should.NotThrow(() => joint.Release());
    }

    [Test]
    public void JoltScene_DynamicBodyFallsOntoStaticFloorWithoutTunneling()
    {
        _scene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Box(new Vector3(5.0f, 0.5f, 5.0f)),
            (new Vector3(0.0f, -0.5f, 0.0f), Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            new LayerMask(1 << 1)).ShouldNotBeNull();
        JoltDynamicRigidBody body = CreateDynamicSphere(new Vector3(0.0f, 4.0f, 0.0f), 1);

        for (int step = 0; step < 180; step++)
            _scene.StepSimulation();

        body.Transform.position.Y.ShouldBeGreaterThan(0.15f);
        body.Transform.position.Y.ShouldBeLessThan(0.6f);
        MathF.Abs(body.LinearVelocity.Y).ShouldBeLessThan(0.5f);
    }

    private static void ConfigureJoint(IAbstractJoint joint)
    {
        switch (joint)
        {
            case IAbstractDistanceJoint distance:
                distance.EnableMinDistance = true;
                distance.EnableMaxDistance = true;
                distance.MinDistance = 1.0f;
                distance.MaxDistance = 3.0f;
                distance.Stiffness = 10.0f;
                distance.Damping = 1.0f;
                break;
            case IAbstractHingeJoint hinge:
                hinge.EnableLimit = true;
                hinge.Limit = new JointAngularLimitPair(-0.5f, 0.5f);
                hinge.EnableDrive = true;
                hinge.DriveVelocity = 0.5f;
                hinge.DriveForceLimit = 10.0f;
                break;
            case IAbstractPrismaticJoint prismatic:
                prismatic.EnableLimit = true;
                prismatic.Limit = new JointLinearLimitPair(-1.0f, 1.0f);
                break;
            case IAbstractSphericalJoint spherical:
                spherical.EnableLimitCone = true;
                spherical.LimitCone = new JointLimitCone(0.75f, 0.75f);
                break;
            case IAbstractD6Joint d6:
                d6.SetMotion(JointD6Axis.X, JointMotion.Limited);
                d6.SetLinearLimit(JointD6Axis.X, new JointLinearLimitPair(-1.0f, 1.0f));
                d6.SetDrive(JointD6DriveType.X, new JointDrive(10.0f, 1.0f));
                break;
        }
    }

    [Test]
    public void JoltScene_RaycastAny_RespectsLayerMaskFiltering()
    {
        CreateStaticSphere(new Vector3(2.5f, 0.0f, 0.0f), 5);
        Segment ray = new(new Vector3(2.0f, 0.0f, 0.0f), new Vector3(3.0f, 0.0f, 0.0f));

        bool included = _scene!.RaycastAny(ray, new LayerMask(1 << 5), null, out _);
        bool excluded = _scene.RaycastAny(ray, new LayerMask(1 << 2), null, out _);

        included.ShouldBeTrue();
        excluded.ShouldBeFalse();
    }
}
