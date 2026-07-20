using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Physics;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Joints;
using XREngine.Scene.Physics.Jolt;

namespace XREngine.UnitTests.Physics;

[TestFixture]
[NonParallelizable]
public sealed class RuntimeDistanceConstraintOwnerTests
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
    public void BindConfigureRebindAndRelease_OwnsOneTrackedConstraint()
    {
        JoltDynamicRigidBody bodyA = CreateDynamicSphere(new Vector3(-1.0f, 0.0f, 0.0f));
        JoltDynamicRigidBody bodyB = CreateDynamicSphere(new Vector3(1.0f, 0.0f, 0.0f));
        using RuntimeDistanceConstraintOwner owner = new();
        RuntimeDistanceConstraintSettings initial = new(
            MinDistance: 0.25f,
            MaxDistance: 2.0f,
            EnableMinDistance: true,
            EnableMaxDistance: true,
            Stiffness: 8.0f,
            Damping: 0.4f,
            Tolerance: 0.05f,
            EnableCollision: true,
            EnablePreprocessing: false,
            BreakForce: 50.0f,
            BreakTorque: 25.0f);

        IAbstractDistanceJoint first = owner.Bind(
            _scene!,
            bodyA,
            JointAnchor.Identity,
            bodyB,
            JointAnchor.Identity,
            initial);

        owner.IsBound.ShouldBeTrue();
        owner.Constraint.ShouldBeSameAs(first);
        AssertSettings(first, initial);
        _scene!.GetDiagnostics().JointCount.ShouldBe(1);

        RuntimeDistanceConstraintSettings updated = initial with
        {
            MinDistance = 0.5f,
            MaxDistance = 1.5f,
            Stiffness = 12.0f,
            Damping = 0.2f,
        };
        owner.Configure(updated);
        AssertSettings(first, updated);

        IAbstractDistanceJoint second = owner.Bind(
            _scene,
            bodyA,
            new JointAnchor(Vector3.UnitX, Quaternion.Identity),
            bodyB,
            JointAnchor.Identity,
            updated);

        second.ShouldNotBeSameAs(first);
        owner.Constraint.ShouldBeSameAs(second);
        _scene.GetDiagnostics().JointCount.ShouldBe(1);
        owner.Release().ShouldBeTrue();
        owner.Release().ShouldBeFalse();
        owner.Constraint.ShouldBeNull();
        _scene.GetDiagnostics().JointCount.ShouldBe(0);
    }

    [Test]
    public void Configure_WhenUnbound_FailsExplicitly()
    {
        using RuntimeDistanceConstraintOwner owner = new();
        RuntimeDistanceConstraintSettings settings = new(
            MinDistance: 0.0f,
            MaxDistance: 0.0f,
            EnableMinDistance: false,
            EnableMaxDistance: true,
            Stiffness: 1.0f,
            Damping: 0.1f,
            Tolerance: 0.01f);

        Should.Throw<InvalidOperationException>(() => owner.Configure(settings));
    }

    [Test]
    public void TransientGameplayConsumers_DoNotCreateOrReleaseSceneJointsDirectly()
    {
        string transformTool = ReadWorkspaceFile(
            "XREngine.Editor/Scene/Components/Editing/TransformTool3D.cs");
        string vrInput = ReadWorkspaceFile(
            "XRENGINE/Scene/Components/Pawns/VRPlayerInputSet.cs");
        string owner = ReadWorkspaceFile(
            "XREngine.Runtime.Core/Scene/Components/Physics/Joints/RuntimeDistanceConstraintOwner.cs");

        transformTool.ShouldContain("_dragConstraintOwner.Bind(");
        transformTool.ShouldContain("private void MouseDownTranslation()");
        transformTool.ShouldContain("private void MouseUpTranslation()");
        transformTool.ShouldNotContain("CreateDistanceJoint(");
        transformTool.ShouldNotContain("_dragJoint.Release(");
        vrInput.ShouldContain("RuntimeDistanceConstraintOwner _leftHandConstraintOwner");
        vrInput.ShouldContain("RuntimeDistanceConstraintOwner _rightHandConstraintOwner");
        vrInput.ShouldContain("LeftHandGrabbed?.Invoke(this, itemRB);");
        vrInput.ShouldContain("LeftHandReleased?.Invoke(this, item);");
        vrInput.ShouldContain("Release(left: true);");
        vrInput.ShouldContain("Release(left: false);");
        vrInput.ShouldNotContain("CreateDistanceJoint(");
        vrInput.ShouldNotContain("HandConstraint?.Release(");
        owner.ShouldContain("scene.CreateDistanceJoint(");
        owner.ShouldContain("scene.RemoveJoint(constraint);");
    }

    private JoltDynamicRigidBody CreateDynamicSphere(Vector3 position)
    {
        JoltDynamicRigidBody? body = _scene!.CreateDynamicRigidBody(
            new IPhysicsGeometry.Sphere(0.25f),
            (position, Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            new LayerMask(1 << 2));
        return body.ShouldNotBeNull();
    }

    private static void AssertSettings(
        IAbstractDistanceJoint joint,
        in RuntimeDistanceConstraintSettings expected)
    {
        joint.MinDistance.ShouldBe(expected.MinDistance);
        joint.MaxDistance.ShouldBe(expected.MaxDistance);
        joint.EnableMinDistance.ShouldBe(expected.EnableMinDistance);
        joint.EnableMaxDistance.ShouldBe(expected.EnableMaxDistance);
        joint.Stiffness.ShouldBe(expected.Stiffness);
        joint.Damping.ShouldBe(expected.Damping);
        joint.Tolerance.ShouldBe(expected.Tolerance);
        joint.EnableCollision.ShouldBe(expected.EnableCollision);
        joint.EnablePreprocessing.ShouldBe(expected.EnablePreprocessing);
        joint.BreakForce.ShouldBe(expected.BreakForce);
        joint.BreakTorque.ShouldBe(expected.BreakTorque);
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = TestContext.CurrentContext.WorkDirectory;
        while (!File.Exists(Path.Combine(root, "XRENGINE.slnx")))
        {
            DirectoryInfo? parent = Directory.GetParent(root);
            parent.ShouldNotBeNull($"Unable to locate workspace root while reading {relativePath}.");
            root = parent.FullName;
        }

        return File.ReadAllText(Path.Combine(root, relativePath)).Replace("\r\n", "\n");
    }
}
