using System.Numerics;
using MonkeyBallVR;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components.Physics;
using XREngine.Scene.Physics;

namespace XREngine.UnitTests.Games;

[TestFixture]
public sealed class MonkeyBallGameplayTests
{
    [Test]
    public void EffectiveColliderCount_UsesLegacyGeometryOrEnabledCompoundShapes()
    {
        DynamicRigidBodyComponent body = new()
        {
            Geometry = new IPhysicsGeometry.Sphere(0.5f),
        };

        MonkeyBallGameComponent.CountEffectiveColliderShapes(body).ShouldBe(1);

        body.ColliderShapes =
        [
            new PhysicsColliderShape
            {
                Enabled = false,
                Geometry = new IPhysicsGeometry.Box(Vector3.One),
            },
            new PhysicsColliderShape
            {
                Enabled = true,
                Geometry = new IPhysicsGeometry.Sphere(0.5f),
            },
            new PhysicsColliderShape
            {
                Enabled = true,
            },
        ];

        MonkeyBallGameComponent.CountEffectiveColliderShapes(body).ShouldBe(1);
    }

    [Test]
    public void CameraRelativeInputToWorld_RotatesForwardWithCameraYaw()
    {
        Vector2 worldTilt = MonkeyBallGameComponent.CameraRelativeInputToWorld(
            new Vector2(0.0f, 1.0f),
            MathF.PI * 0.5f);

        Vector2.Distance(worldTilt, new Vector2(-1.0f, 0.0f))
            .ShouldBeLessThan(1.0e-5f);
    }

    [Test]
    public void CalculateStageTargetRotation_UsesCameraRelativeAxes()
    {
        Quaternion cameraYaw = Quaternion.CreateFromAxisAngle(
            Globals.Up,
            MathF.PI * 0.5f);
        Quaternion forwardAtRotatedCamera =
            MonkeyBallGameComponent.CalculateStageTargetRotation(
                new Vector2(0.0f, 1.0f),
                cameraYaw,
                12.0f);
        Quaternion leftAtWorldCamera =
            MonkeyBallGameComponent.CalculateStageTargetRotation(
                new Vector2(-1.0f, 0.0f),
                Quaternion.Identity,
                12.0f);

        MathF.Abs(Quaternion.Dot(forwardAtRotatedCamera, leftAtWorldCamera))
            .ShouldBe(1.0f, 1.0e-5f);
    }

    [Test]
    public void ResolveStagePivotTranslation_PreservesFullThreeDimensionalPivot()
    {
        Vector3 pivot = new(3.0f, 2.0f, -4.0f);
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(0.4f, 0.2f, -0.1f);
        Vector3 translation =
            MonkeyBallGameComponent.ResolveStagePivotTranslation(pivot, rotation);
        Vector3 resolvedPivot = translation + Vector3.Transform(pivot, rotation);

        Vector3.Distance(resolvedPivot, pivot).ShouldBeLessThan(1.0e-5f);
    }

    [Test]
    public void CreateYawFacing_IgnoresVerticalVelocityAndFacesHorizontalMotion()
    {
        Vector3 velocity = new(3.0f, 8.0f, -4.0f);
        Quaternion heading = MonkeyBallGameComponent.CreateYawFacing(velocity);
        Vector3 forward = Vector3.Transform(Globals.Forward, heading);
        forward.Y = 0.0f;
        forward = Vector3.Normalize(forward);
        Vector3 expected = Vector3.Normalize(new Vector3(3.0f, 0.0f, -4.0f));

        Vector3.Distance(forward, expected).ShouldBeLessThan(1.0e-5f);
    }

    [Test]
    public void CalculateDesktopCameraPose_FollowsBallFacesHeadingAndHasNoRoll()
    {
        Vector3 ballPosition = new(7.0f, 3.0f, -2.0f);
        Vector3 velocity = new(3.0f, 0.0f, -4.0f);
        Vector3 offset = new(0.0f, 2.5f, 5.5f);
        Quaternion heading = MonkeyBallGameComponent.CreateYawFacing(velocity);

        (Vector3 cameraPosition, Quaternion cameraRotation) =
            MonkeyBallGameComponent.CalculateDesktopCameraPose(
                ballPosition,
                heading,
                offset,
                -14.0f);

        Vector3.Distance(ballPosition, cameraPosition)
            .ShouldBe(offset.Length(), 1.0e-5f);
        Vector3 cameraRight = Vector3.Transform(Globals.Right, cameraRotation);
        MathF.Abs(cameraRight.Y).ShouldBeLessThan(1.0e-5f);
        Vector3 cameraForward = Vector3.Transform(Globals.Forward, cameraRotation);
        cameraForward.Y = 0.0f;
        Vector3.Distance(
                Vector3.Normalize(cameraForward),
                Vector3.Normalize(velocity))
            .ShouldBeLessThan(1.0e-5f);
    }
}
