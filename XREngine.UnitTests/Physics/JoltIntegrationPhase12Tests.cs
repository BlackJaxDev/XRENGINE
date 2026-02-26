using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
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

        ObjectLayerPairFilterMask.GetGroup(layer).ShouldBe((uint)5);
        ObjectLayerPairFilterMask.GetMask(layer).ShouldBe((uint)(1 << 5));
    }

    [Test]
    public void LayerMask_AsJoltObjectLayer_ZeroMaskUsesAllMaskBits()
    {
        LayerMask mask = new(0);

        ObjectLayer layer = mask.AsJoltObjectLayer();

        ObjectLayerPairFilterMask.GetGroup(layer).ShouldBe(0u);
        ObjectLayerPairFilterMask.GetMask(layer).ShouldBe(0xFFFFu);
    }

    [Test]
    public void JoltScene_FixedJoint_CanCreateStepAndRelease()
    {
        var scene = _scene!;
        var bodyA = CreateDynamicSphere(new Vector3(-1.0f, 0.0f, 0.0f), 1);
        var bodyB = CreateDynamicSphere(new Vector3(1.0f, 0.0f, 0.0f), 1);

        var joint = scene.CreateFixedJoint(bodyA, JointAnchor.Identity, bodyB, JointAnchor.Identity);

        joint.ShouldNotBeNull();
        Should.NotThrow(() => scene.StepSimulation());
        Should.NotThrow(() => joint.Release());
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
