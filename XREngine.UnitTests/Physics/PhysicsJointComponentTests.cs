using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Physics;
using XREngine.Scene;
using XREngine.Scene.Physics.Joints;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// Tests for the componentized physics joint system covering:
/// - Default property values for every joint component type
/// - Property mutation round-trips
/// - Gizmo / IRenderable contract
/// - Break notification plumbing
/// - VR grab workflow sketch (DistanceJointComponent)
/// </summary>
[TestFixture]
public sealed class PhysicsJointComponentTests
{
    #region Test Helpers

    private static SceneNode CreateNodeWithJoint<T>(out T joint) where T : PhysicsJointComponent, new()
    {
        var node = new SceneNode($"{typeof(T).Name}TestNode");
        node.SetTransform(new Transform());
        joint = node.AddComponent<T>()!;
        return node;
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────
    // PhysicsJointComponent (base) defaults
    // ─────────────────────────────────────────────────────────────────────

    #region Base Defaults

    [Test]
    public void Base_DefaultAnchorPositionsAreZero()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.AnchorPosition.ShouldBe(Vector3.Zero);
        j.ConnectedAnchorPosition.ShouldBe(Vector3.Zero);
    }

    [Test]
    public void Base_DefaultAnchorRotationsAreIdentity()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.AnchorRotation.ShouldBe(Quaternion.Identity);
        j.ConnectedAnchorRotation.ShouldBe(Quaternion.Identity);
    }

    [Test]
    public void Base_DefaultConnectedBodyIsNull()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.ConnectedBody.ShouldBeNull();
    }

    [Test]
    public void Base_DefaultAutoConfigureConnectedAnchorIsTrue()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.AutoConfigureConnectedAnchor.ShouldBeTrue();
    }

    [Test]
    public void Base_DefaultBreakForceAndTorqueAreMaxValue()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.BreakForce.ShouldBe(float.MaxValue);
        j.BreakTorque.ShouldBe(float.MaxValue);
    }

    [Test]
    public void Base_DefaultEnableCollisionIsFalse()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.EnableCollision.ShouldBeFalse();
    }

    [Test]
    public void Base_DefaultEnablePreprocessingIsTrue()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.EnablePreprocessing.ShouldBeTrue();
    }

    [Test]
    public void Base_DefaultDrawGizmosIsTrue()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.DrawGizmos.ShouldBeTrue();
    }

    [Test]
    public void Base_NativeJointIsNullWhenNotActivated()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.NativeJoint.ShouldBeNull();
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────
    // Base property set / get round-trip
    // ─────────────────────────────────────────────────────────────────────

    #region Base Property Mutation

    [Test]
    public void Base_AnchorPositionRoundTrips()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);
        var v = new Vector3(1, 2, 3);

        j.AnchorPosition = v;

        j.AnchorPosition.ShouldBe(v);
    }

    [Test]
    public void Base_AnchorRotationRoundTrips()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);
        var q = Quaternion.CreateFromYawPitchRoll(0.5f, 0.3f, 0.1f);

        j.AnchorRotation = q;

        j.AnchorRotation.ShouldBe(q);
    }

    [Test]
    public void Base_ConnectedAnchorPositionRoundTrips()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);
        var v = new Vector3(-1, 5, 0);

        j.ConnectedAnchorPosition = v;

        j.ConnectedAnchorPosition.ShouldBe(v);
    }

    [Test]
    public void Base_ConnectedAnchorRotationRoundTrips()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f);

        j.ConnectedAnchorRotation = q;

        j.ConnectedAnchorRotation.ShouldBe(q);
    }

    [Test]
    public void Base_BreakForceRoundTrips()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.BreakForce = 500f;

        j.BreakForce.ShouldBe(500f);
    }

    [Test]
    public void Base_BreakTorqueRoundTrips()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.BreakTorque = 250f;

        j.BreakTorque.ShouldBe(250f);
    }

    [Test]
    public void Base_EnableCollisionRoundTrips()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.EnableCollision = true;

        j.EnableCollision.ShouldBeTrue();
    }

    [Test]
    public void Base_EnablePreprocessingRoundTrips()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.EnablePreprocessing = false;

        j.EnablePreprocessing.ShouldBeFalse();
    }

    [Test]
    public void Base_AutoConfigureConnectedAnchorRoundTrips()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.AutoConfigureConnectedAnchor = false;

        j.AutoConfigureConnectedAnchor.ShouldBeFalse();
    }

    [Test]
    public void Base_DrawGizmosRoundTrips()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.DrawGizmos = false;

        j.DrawGizmos.ShouldBeFalse();
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────
    // IRenderable contract
    // ─────────────────────────────────────────────────────────────────────

    #region Gizmo / IRenderable

    [Test]
    public void Gizmo_RenderedObjectsIsNotNull()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.RenderedObjects.ShouldNotBeNull();
    }

    [Test]
    public void Gizmo_RenderedObjectsContainsExactlyOneEntry()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);

        j.RenderedObjects.Length.ShouldBe(1);
    }

    [Test]
    public void Gizmo_RenderInfoIsNotNull()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);

        j.RenderedObjects[0].ShouldNotBeNull();
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────
    // Break notification
    // ─────────────────────────────────────────────────────────────────────

    #region Break Event

    [Test]
    public void Break_JointBrokenEventIsRaisedByNotify()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);
        bool raised = false;
        j.JointBroken += _ => raised = true;

        // Simulate the internal callback that the physics backend would invoke
        j.NotifyJointBroken();

        raised.ShouldBeTrue();
    }

    [Test]
    public void Break_JointBrokenEventPassesSelfAsArgument()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);
        PhysicsJointComponent? received = null;
        j.JointBroken += c => received = c;

        j.NotifyJointBroken();

        received.ShouldBeSameAs(j);
    }

    #endregion

    // ═════════════════════════════════════════════════════════════════════
    // FixedJointComponent
    // ═════════════════════════════════════════════════════════════════════

    #region Fixed Joint

    [Test]
    public void Fixed_CanInstantiateDirectly()
    {
        var c = new FixedJointComponent();
        c.ShouldNotBeNull();
    }

    [Test]
    public void Fixed_NativeJointNullBeforeActivation()
    {
        CreateNodeWithJoint<FixedJointComponent>(out var j);
        j.NativeJoint.ShouldBeNull();
    }

    #endregion

    // ═════════════════════════════════════════════════════════════════════
    // DistanceJointComponent
    // ═════════════════════════════════════════════════════════════════════

    #region Distance Joint — Defaults

    [Test]
    public void Distance_DefaultMinDistanceIsZero()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.MinDistance.ShouldBe(0f);
    }

    [Test]
    public void Distance_DefaultMaxDistanceIsMaxValue()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.MaxDistance.ShouldBe(float.MaxValue);
    }

    [Test]
    public void Distance_DefaultEnableMinDistanceIsFalse()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.EnableMinDistance.ShouldBeFalse();
    }

    [Test]
    public void Distance_DefaultEnableMaxDistanceIsFalse()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.EnableMaxDistance.ShouldBeFalse();
    }

    [Test]
    public void Distance_DefaultStiffnessIsZero()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.Stiffness.ShouldBe(0f);
    }

    [Test]
    public void Distance_DefaultDampingIsZero()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.Damping.ShouldBe(0f);
    }

    [Test]
    public void Distance_DefaultToleranceIsPointZeroTwoFive()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.Tolerance.ShouldBe(0.025f);
    }

    #endregion

    #region Distance Joint — Property Mutation

    [Test]
    public void Distance_MinDistanceRoundTrips()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.MinDistance = 1.5f;
        j.MinDistance.ShouldBe(1.5f);
    }

    [Test]
    public void Distance_MaxDistanceRoundTrips()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.MaxDistance = 10f;
        j.MaxDistance.ShouldBe(10f);
    }

    [Test]
    public void Distance_EnableMinDistanceRoundTrips()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.EnableMinDistance = true;
        j.EnableMinDistance.ShouldBeTrue();
    }

    [Test]
    public void Distance_EnableMaxDistanceRoundTrips()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.EnableMaxDistance = true;
        j.EnableMaxDistance.ShouldBeTrue();
    }

    [Test]
    public void Distance_StiffnessRoundTrips()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.Stiffness = 100f;
        j.Stiffness.ShouldBe(100f);
    }

    [Test]
    public void Distance_DampingRoundTrips()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.Damping = 5f;
        j.Damping.ShouldBe(5f);
    }

    [Test]
    public void Distance_ToleranceRoundTrips()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);
        j.Tolerance = 0.1f;
        j.Tolerance.ShouldBe(0.1f);
    }

    #endregion

    // ═════════════════════════════════════════════════════════════════════
    // HingeJointComponent
    // ═════════════════════════════════════════════════════════════════════

    #region Hinge Joint — Defaults

    [Test]
    public void Hinge_DefaultEnableLimitIsFalse()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.EnableLimit.ShouldBeFalse();
    }

    [Test]
    public void Hinge_DefaultLowerAngle()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.LowerAngleRadians.ShouldBe(-float.Pi / 4f);
    }

    [Test]
    public void Hinge_DefaultUpperAngle()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.UpperAngleRadians.ShouldBe(float.Pi / 4f);
    }

    [Test]
    public void Hinge_DefaultLimitRestitutionIsZero()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.LimitRestitution.ShouldBe(0f);
    }

    [Test]
    public void Hinge_DefaultLimitBounceThresholdIsZero()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.LimitBounceThreshold.ShouldBe(0f);
    }

    [Test]
    public void Hinge_DefaultLimitStiffnessIsZero()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.LimitStiffness.ShouldBe(0f);
    }

    [Test]
    public void Hinge_DefaultLimitDampingIsZero()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.LimitDamping.ShouldBe(0f);
    }

    [Test]
    public void Hinge_DefaultEnableDriveIsFalse()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.EnableDrive.ShouldBeFalse();
    }

    [Test]
    public void Hinge_DefaultDriveVelocityIsZero()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.DriveVelocity.ShouldBe(0f);
    }

    [Test]
    public void Hinge_DefaultDriveForceLimitIsMaxValue()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.DriveForceLimit.ShouldBe(float.MaxValue);
    }

    [Test]
    public void Hinge_DefaultDriveGearRatioIsOne()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.DriveGearRatio.ShouldBe(1f);
    }

    [Test]
    public void Hinge_DefaultDriveIsFreeSpin()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.DriveIsFreeSpin.ShouldBeFalse();
    }

    #endregion

    #region Hinge Joint — Property Mutation

    [Test]
    public void Hinge_EnableLimitRoundTrips()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.EnableLimit = true;
        j.EnableLimit.ShouldBeTrue();
    }

    [Test]
    public void Hinge_LowerAngleRoundTrips()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.LowerAngleRadians = -1.0f;
        j.LowerAngleRadians.ShouldBe(-1.0f);
    }

    [Test]
    public void Hinge_UpperAngleRoundTrips()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.UpperAngleRadians = 2.0f;
        j.UpperAngleRadians.ShouldBe(2.0f);
    }

    [Test]
    public void Hinge_DriveVelocityRoundTrips()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.DriveVelocity = 3.14f;
        j.DriveVelocity.ShouldBe(3.14f);
    }

    [Test]
    public void Hinge_EnableDriveRoundTrips()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.EnableDrive = true;
        j.EnableDrive.ShouldBeTrue();
    }

    [Test]
    public void Hinge_DriveForceLimitRoundTrips()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.DriveForceLimit = 100f;
        j.DriveForceLimit.ShouldBe(100f);
    }

    [Test]
    public void Hinge_DriveGearRatioRoundTrips()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.DriveGearRatio = 2.5f;
        j.DriveGearRatio.ShouldBe(2.5f);
    }

    [Test]
    public void Hinge_DriveIsFreeSpinRoundTrips()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.DriveIsFreeSpin = true;
        j.DriveIsFreeSpin.ShouldBeTrue();
    }

    [Test]
    public void Hinge_LimitRestitutionRoundTrips()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.LimitRestitution = 0.5f;
        j.LimitRestitution.ShouldBe(0.5f);
    }

    [Test]
    public void Hinge_LimitStiffnessRoundTrips()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.LimitStiffness = 200f;
        j.LimitStiffness.ShouldBe(200f);
    }

    [Test]
    public void Hinge_LimitDampingRoundTrips()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.LimitDamping = 10f;
        j.LimitDamping.ShouldBe(10f);
    }

    [Test]
    public void Hinge_RuntimeAngleZeroWithoutNative()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        // No native joint → returns 0
        j.AngleRadians.ShouldBe(0f);
    }

    [Test]
    public void Hinge_RuntimeVelocityZeroWithoutNative()
    {
        CreateNodeWithJoint<HingeJointComponent>(out var j);
        j.Velocity.ShouldBe(0f);
    }

    #endregion

    // ═════════════════════════════════════════════════════════════════════
    // PrismaticJointComponent
    // ═════════════════════════════════════════════════════════════════════

    #region Prismatic Joint — Defaults

    [Test]
    public void Prismatic_DefaultEnableLimitIsFalse()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.EnableLimit.ShouldBeFalse();
    }

    [Test]
    public void Prismatic_DefaultLowerLimitIsZero()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.LowerLimit.ShouldBe(0f);
    }

    [Test]
    public void Prismatic_DefaultUpperLimitIsZero()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.UpperLimit.ShouldBe(0f);
    }

    [Test]
    public void Prismatic_DefaultLimitRestitutionIsZero()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.LimitRestitution.ShouldBe(0f);
    }

    [Test]
    public void Prismatic_DefaultLimitStiffnessIsZero()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.LimitStiffness.ShouldBe(0f);
    }

    [Test]
    public void Prismatic_DefaultLimitDampingIsZero()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.LimitDamping.ShouldBe(0f);
    }

    #endregion

    #region Prismatic Joint — Property Mutation

    [Test]
    public void Prismatic_EnableLimitRoundTrips()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.EnableLimit = true;
        j.EnableLimit.ShouldBeTrue();
    }

    [Test]
    public void Prismatic_LowerLimitRoundTrips()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.LowerLimit = -5f;
        j.LowerLimit.ShouldBe(-5f);
    }

    [Test]
    public void Prismatic_UpperLimitRoundTrips()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.UpperLimit = 5f;
        j.UpperLimit.ShouldBe(5f);
    }

    [Test]
    public void Prismatic_LimitRestitutionRoundTrips()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.LimitRestitution = 0.3f;
        j.LimitRestitution.ShouldBe(0.3f);
    }

    [Test]
    public void Prismatic_LimitStiffnessRoundTrips()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.LimitStiffness = 50f;
        j.LimitStiffness.ShouldBe(50f);
    }

    [Test]
    public void Prismatic_LimitDampingRoundTrips()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.LimitDamping = 2f;
        j.LimitDamping.ShouldBe(2f);
    }

    [Test]
    public void Prismatic_RuntimePositionZeroWithoutNative()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.Position.ShouldBe(0f);
    }

    [Test]
    public void Prismatic_RuntimeVelocityZeroWithoutNative()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);
        j.Velocity.ShouldBe(0f);
    }

    #endregion

    // ═════════════════════════════════════════════════════════════════════
    // SphericalJointComponent
    // ═════════════════════════════════════════════════════════════════════

    #region Spherical Joint — Defaults

    [Test]
    public void Spherical_DefaultEnableLimitConeIsFalse()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.EnableLimitCone.ShouldBeFalse();
    }

    [Test]
    public void Spherical_DefaultConeYAngle()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.LimitConeYAngleRadians.ShouldBe(float.Pi / 4f);
    }

    [Test]
    public void Spherical_DefaultConeZAngle()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.LimitConeZAngleRadians.ShouldBe(float.Pi / 4f);
    }

    [Test]
    public void Spherical_DefaultLimitRestitutionIsZero()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.LimitRestitution.ShouldBe(0f);
    }

    [Test]
    public void Spherical_DefaultLimitStiffnessIsZero()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.LimitStiffness.ShouldBe(0f);
    }

    [Test]
    public void Spherical_DefaultLimitDampingIsZero()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.LimitDamping.ShouldBe(0f);
    }

    #endregion

    #region Spherical Joint — Property Mutation

    [Test]
    public void Spherical_EnableLimitConeRoundTrips()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.EnableLimitCone = true;
        j.EnableLimitCone.ShouldBeTrue();
    }

    [Test]
    public void Spherical_ConeYAngleRoundTrips()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.LimitConeYAngleRadians = 1.2f;
        j.LimitConeYAngleRadians.ShouldBe(1.2f);
    }

    [Test]
    public void Spherical_ConeZAngleRoundTrips()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.LimitConeZAngleRadians = 0.8f;
        j.LimitConeZAngleRadians.ShouldBe(0.8f);
    }

    [Test]
    public void Spherical_LimitRestitutionRoundTrips()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.LimitRestitution = 0.4f;
        j.LimitRestitution.ShouldBe(0.4f);
    }

    [Test]
    public void Spherical_LimitStiffnessRoundTrips()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.LimitStiffness = 500f;
        j.LimitStiffness.ShouldBe(500f);
    }

    [Test]
    public void Spherical_LimitDampingRoundTrips()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.LimitDamping = 25f;
        j.LimitDamping.ShouldBe(25f);
    }

    [Test]
    public void Spherical_RuntimeSwingAnglesZeroWithoutNative()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);
        j.SwingYAngle.ShouldBe(0f);
        j.SwingZAngle.ShouldBe(0f);
    }

    #endregion

    // ═════════════════════════════════════════════════════════════════════
    // D6JointComponent
    // ═════════════════════════════════════════════════════════════════════

    #region D6 Joint — Defaults

    [Test]
    public void D6_DefaultMotionsAreLocked()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);

        j.MotionX.ShouldBe(JointMotion.Locked);
        j.MotionY.ShouldBe(JointMotion.Locked);
        j.MotionZ.ShouldBe(JointMotion.Locked);
        j.MotionTwist.ShouldBe(JointMotion.Locked);
        j.MotionSwing1.ShouldBe(JointMotion.Locked);
        j.MotionSwing2.ShouldBe(JointMotion.Locked);
    }

    [Test]
    public void D6_DefaultTwistLimits()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);

        j.TwistLowerRadians.ShouldBe(-float.Pi / 4f);
        j.TwistUpperRadians.ShouldBe(float.Pi / 4f);
    }

    [Test]
    public void D6_DefaultSwingLimits()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);

        j.SwingLimitYAngle.ShouldBe(float.Pi / 4f);
        j.SwingLimitZAngle.ShouldBe(float.Pi / 4f);
    }

    [Test]
    public void D6_DefaultDistanceLimitIsMaxValue()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);

        j.DistanceLimitValue.ShouldBe(float.MaxValue);
    }

    [Test]
    public void D6_DefaultDriveTargetPositionIsZero()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);

        j.DriveTargetPosition.ShouldBe(Vector3.Zero);
    }

    [Test]
    public void D6_DefaultDriveTargetRotationIsIdentity()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);

        j.DriveTargetRotation.ShouldBe(Quaternion.Identity);
    }

    [Test]
    public void D6_DefaultDriveLinearVelocityIsZero()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);

        j.DriveLinearVelocity.ShouldBe(Vector3.Zero);
    }

    [Test]
    public void D6_DefaultDriveAngularVelocityIsZero()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);

        j.DriveAngularVelocity.ShouldBe(Vector3.Zero);
    }

    #endregion

    #region D6 Joint — Property Mutation

    [Test]
    public void D6_MotionXRoundTrips()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        j.MotionX = JointMotion.Free;
        j.MotionX.ShouldBe(JointMotion.Free);
    }

    [Test]
    public void D6_MotionYRoundTrips()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        j.MotionY = JointMotion.Limited;
        j.MotionY.ShouldBe(JointMotion.Limited);
    }

    [Test]
    public void D6_MotionTwistRoundTrips()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        j.MotionTwist = JointMotion.Free;
        j.MotionTwist.ShouldBe(JointMotion.Free);
    }

    [Test]
    public void D6_MotionSwing1RoundTrips()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        j.MotionSwing1 = JointMotion.Limited;
        j.MotionSwing1.ShouldBe(JointMotion.Limited);
    }

    [Test]
    public void D6_TwistLimitsRoundTrip()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        j.TwistLowerRadians = -1.0f;
        j.TwistUpperRadians = 1.0f;
        j.TwistLowerRadians.ShouldBe(-1.0f);
        j.TwistUpperRadians.ShouldBe(1.0f);
    }

    [Test]
    public void D6_SwingLimitsRoundTrip()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        j.SwingLimitYAngle = 0.5f;
        j.SwingLimitZAngle = 0.6f;
        j.SwingLimitYAngle.ShouldBe(0.5f);
        j.SwingLimitZAngle.ShouldBe(0.6f);
    }

    [Test]
    public void D6_DistanceLimitRoundTrips()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        j.DistanceLimitValue = 5f;
        j.DistanceLimitValue.ShouldBe(5f);
    }

    [Test]
    public void D6_DriveTargetPositionRoundTrips()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        var v = new Vector3(1, 2, 3);
        j.DriveTargetPosition = v;
        j.DriveTargetPosition.ShouldBe(v);
    }

    [Test]
    public void D6_DriveTargetRotationRoundTrips()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        var q = Quaternion.CreateFromYawPitchRoll(0.5f, 0.3f, 0.1f);
        j.DriveTargetRotation = q;
        j.DriveTargetRotation.ShouldBe(q);
    }

    [Test]
    public void D6_DriveLinearVelocityRoundTrips()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        var v = new Vector3(10, 0, 0);
        j.DriveLinearVelocity = v;
        j.DriveLinearVelocity.ShouldBe(v);
    }

    [Test]
    public void D6_DriveAngularVelocityRoundTrips()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        var v = new Vector3(0, 1, 0);
        j.DriveAngularVelocity = v;
        j.DriveAngularVelocity.ShouldBe(v);
    }

    [Test]
    public void D6_RuntimeAnglesZeroWithoutNative()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);
        j.TwistAngle.ShouldBe(0f);
        j.SwingYAngle.ShouldBe(0f);
        j.SwingZAngle.ShouldBe(0f);
    }

    #endregion

    // ═════════════════════════════════════════════════════════════════════
    // Component lifecycle (create → verify null native → destroy)
    // ═════════════════════════════════════════════════════════════════════

    #region Lifecycle Integration

    [Test]
    public void Lifecycle_AllJointTypes_NativeNullBeforeActivation()
    {
        // Verify that all joint types start with null native joint
        // when added to a node but not activated in a world.
        CreateNodeWithJoint<FixedJointComponent>(out var fixedJ);
        fixedJ.NativeJoint.ShouldBeNull();

        CreateNodeWithJoint<DistanceJointComponent>(out var distJ);
        distJ.NativeJoint.ShouldBeNull();

        CreateNodeWithJoint<HingeJointComponent>(out var hingeJ);
        hingeJ.NativeJoint.ShouldBeNull();

        CreateNodeWithJoint<PrismaticJointComponent>(out var prisJ);
        prisJ.NativeJoint.ShouldBeNull();

        CreateNodeWithJoint<SphericalJointComponent>(out var spherJ);
        spherJ.NativeJoint.ShouldBeNull();

        CreateNodeWithJoint<D6JointComponent>(out var d6J);
        d6J.NativeJoint.ShouldBeNull();
    }

    [Test]
    public void Lifecycle_PropertySetBeforeActivation_NoException()
    {
        // Verify that setting properties before activation doesn't throw,
        // even though no native joint exists to push values to.
        CreateNodeWithJoint<HingeJointComponent>(out var j);

        Should.NotThrow(() =>
        {
            j.EnableLimit = true;
            j.LowerAngleRadians = -1.5f;
            j.UpperAngleRadians = 1.5f;
            j.EnableDrive = true;
            j.DriveVelocity = 10f;
            j.DriveForceLimit = 200f;
            j.AnchorPosition = new Vector3(0, 1, 0);
            j.BreakForce = 1000f;
            j.BreakTorque = 500f;
        });
    }

    [Test]
    public void Lifecycle_PropertySetBeforeActivation_NoException_Distance()
    {
        CreateNodeWithJoint<DistanceJointComponent>(out var j);

        Should.NotThrow(() =>
        {
            j.MinDistance = 0.5f;
            j.MaxDistance = 5f;
            j.EnableMinDistance = true;
            j.EnableMaxDistance = true;
            j.Stiffness = 100f;
            j.Damping = 10f;
            j.Tolerance = 0.05f;
        });
    }

    [Test]
    public void Lifecycle_PropertySetBeforeActivation_NoException_Prismatic()
    {
        CreateNodeWithJoint<PrismaticJointComponent>(out var j);

        Should.NotThrow(() =>
        {
            j.EnableLimit = true;
            j.LowerLimit = -2f;
            j.UpperLimit = 2f;
            j.LimitStiffness = 100f;
            j.LimitDamping = 5f;
        });
    }

    [Test]
    public void Lifecycle_PropertySetBeforeActivation_NoException_Spherical()
    {
        CreateNodeWithJoint<SphericalJointComponent>(out var j);

        Should.NotThrow(() =>
        {
            j.EnableLimitCone = true;
            j.LimitConeYAngleRadians = 1.0f;
            j.LimitConeZAngleRadians = 0.8f;
            j.LimitStiffness = 50f;
            j.LimitDamping = 3f;
        });
    }

    [Test]
    public void Lifecycle_PropertySetBeforeActivation_NoException_D6()
    {
        CreateNodeWithJoint<D6JointComponent>(out var j);

        Should.NotThrow(() =>
        {
            j.MotionX = JointMotion.Free;
            j.MotionY = JointMotion.Limited;
            j.MotionZ = JointMotion.Locked;
            j.MotionTwist = JointMotion.Free;
            j.MotionSwing1 = JointMotion.Limited;
            j.MotionSwing2 = JointMotion.Locked;
            j.TwistLowerRadians = -1.0f;
            j.TwistUpperRadians = 1.0f;
            j.SwingLimitYAngle = 0.5f;
            j.SwingLimitZAngle = 0.6f;
            j.DistanceLimitValue = 10f;
            j.DriveTargetPosition = new Vector3(1, 2, 3);
            j.DriveTargetRotation = Quaternion.Identity;
            j.DriveLinearVelocity = new Vector3(1, 0, 0);
            j.DriveAngularVelocity = new Vector3(0, 1, 0);
        });
    }

    #endregion

    // ═════════════════════════════════════════════════════════════════════
    // VR Grab Workflow — componentized DistanceJointComponent sketch
    // ═════════════════════════════════════════════════════════════════════

    #region VR Grab Workflow (Distance Joint)

    /// <summary>
    /// Verifies the workflow of setting up a distance joint for VR grab:
    /// create node → add distance joint → configure for grab → verify state.
    /// This exercises the API path a VR grab system would use to replace
    /// direct PhysX calls with the componentized joint system.
    /// </summary>
    [Test]
    public void VRGrab_SetupDistanceJointForGrab()
    {
        // Simulate two bodies: hand and grabbed object
        var handNode = new SceneNode("VRHand");
        handNode.SetTransform(new Transform());

        var objectNode = new SceneNode("GrabbedObject");
        objectNode.SetTransform(new Transform());

        // Add distance joint on the grabbed object
        var joint = objectNode.AddComponent<DistanceJointComponent>()!;

        // Configure for VR grab: max distance = arm reach, spring params for elasticity
        joint.EnableMinDistance = false;
        joint.EnableMaxDistance = true;
        joint.MaxDistance = 2.0f;  // max arm reach in meters
        joint.Stiffness = 500f;   // fairly stiff spring
        joint.Damping = 50f;      // moderate damping
        joint.Tolerance = 0.01f;  // tight tolerance

        // Set breakable thresholds so grab releases on force
        joint.BreakForce = 1000f;
        joint.BreakTorque = 500f;

        // Set up anchor on the object's grab point
        joint.AnchorPosition = new Vector3(0, 0.05f, 0); // offset from center
        joint.AutoConfigureConnectedAnchor = false;
        joint.ConnectedAnchorPosition = Vector3.Zero; // center of hand

        // Verify all properties stuck
        joint.EnableMaxDistance.ShouldBeTrue();
        joint.MaxDistance.ShouldBe(2.0f);
        joint.Stiffness.ShouldBe(500f);
        joint.Damping.ShouldBe(50f);
        joint.BreakForce.ShouldBe(1000f);
        joint.BreakTorque.ShouldBe(500f);
        joint.AnchorPosition.ShouldBe(new Vector3(0, 0.05f, 0));
        joint.AutoConfigureConnectedAnchor.ShouldBeFalse();
        joint.ConnectedAnchorPosition.ShouldBe(Vector3.Zero);
    }

    [Test]
    public void VRGrab_BreakEventFiresOnForceExceed()
    {
        var objectNode = new SceneNode("GrabbedObject");
        objectNode.SetTransform(new Transform());
        var joint = objectNode.AddComponent<DistanceJointComponent>()!;

        joint.BreakForce = 500f;
        joint.BreakTorque = 250f;

        bool released = false;
        joint.JointBroken += _ => released = true;

        // Simulate physics backend detecting break
        joint.NotifyJointBroken();

        released.ShouldBeTrue();
        // After break, native joint should have been destroyed
        joint.NativeJoint.ShouldBeNull();
    }

    [Test]
    public void VRGrab_D6JointForPreciseGrabControl()
    {
        // D6 joint provides the most precise control for VR grabs
        var objectNode = new SceneNode("GrabbedObject");
        objectNode.SetTransform(new Transform());
        var joint = objectNode.AddComponent<D6JointComponent>()!;

        // Free all linear axes, lock angular for rigid grab
        joint.MotionX = JointMotion.Free;
        joint.MotionY = JointMotion.Free;
        joint.MotionZ = JointMotion.Free;
        joint.MotionTwist = JointMotion.Locked;
        joint.MotionSwing1 = JointMotion.Locked;
        joint.MotionSwing2 = JointMotion.Locked;

        // Set drive target to hand position
        joint.DriveTargetPosition = new Vector3(0.5f, 1.2f, -0.3f);

        // Verify configuration
        joint.MotionX.ShouldBe(JointMotion.Free);
        joint.MotionY.ShouldBe(JointMotion.Free);
        joint.MotionZ.ShouldBe(JointMotion.Free);
        joint.MotionTwist.ShouldBe(JointMotion.Locked);
        joint.DriveTargetPosition.ShouldBe(new Vector3(0.5f, 1.2f, -0.3f));
    }

    #endregion
}
