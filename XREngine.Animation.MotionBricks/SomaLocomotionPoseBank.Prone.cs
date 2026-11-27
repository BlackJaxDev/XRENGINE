using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Animation.MotionBricks;

public sealed partial class SomaLocomotionPoseBank
{
    /// <summary>
    /// Builds a grounded prone pose with idle asymmetry and breathing when stationary, an
    /// asymmetric lift-reach-plant-pull contralateral crawl cycle when moving, and head and
    /// shoulder turn lead when pivoting in place, without heap allocations.
    /// </summary>
    public void BuildPronePose(
        float crawlPhaseRadians,
        float moveSpeedFraction,
        Vector3 rootGroundXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> destination,
        float idleBreathingTimeSeconds = 0.0f,
        float turnRateRadiansPerSecond = 0.0f)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Destination span must hold at least {JointCount} joints.", nameof(destination));

        float move = Math.Clamp(moveSpeedFraction, 0.0f, 1.0f);
        float idleWeight = 1.0f - move;
        float leftSin = MathF.Sin(crawlPhaseRadians);
        float leftCos = MathF.Cos(crawlPhaseRadians);
        float rightSin = -leftSin;
        float rightCos = -leftCos;

        float sinPhase = leftSin * move;
        float cosPhase = leftCos * move;
        float breath = MathF.Sin(idleBreathingTimeSeconds * 1.65f) * idleWeight;
        float idleHeadSway = MathF.Sin(idleBreathingTimeSeconds * 0.55f) * idleWeight;
        float turnLead = Math.Clamp(turnRateRadiansPerSecond * 0.35f, -1.0f, 1.0f);

        // Split each arm cycle into forward reach (+), pull-back (-), and swing lift (cos > 0).
        // Lifting only when cos > 0 raises the advancing forearm off the floor while keeping the
        // pulling forearm planted flat along the ground through the entire stance stroke.
        float leftReach = MathF.Max(0.0f, leftSin) * move;
        float leftPull = MathF.Max(0.0f, -leftSin) * move;
        float rightReach = MathF.Max(0.0f, rightSin) * move;
        float rightPull = MathF.Max(0.0f, -rightSin) * move;
        float leftLift = MathF.Pow(MathF.Max(0.0f, leftCos), 1.15f) * move;
        float rightLift = MathF.Pow(MathF.Max(0.0f, rightCos), 1.15f) * move;

        // Subtle resting asymmetry keeps prone idle looking organic instead of rigidly mirrored.
        float idleAsym = 0.07f * idleWeight;
        bool mecanim = _calibration.UseMecanimNeutralPostureOffsets;

        Array.Clear(_runtimeMuscleScratch);

        // Keep torso propped gently on forearms so the head looks cleanly forward along the travel path.
        _runtimeMuscleScratch[(int)EHumanoidValue.SpineFrontBack] = 0.22f + breath * 0.025f;
        _runtimeMuscleScratch[(int)EHumanoidValue.ChestFrontBack] = 0.30f + breath * 0.035f;
        _runtimeMuscleScratch[(int)EHumanoidValue.UpperChestFrontBack] = 0.22f + breath * 0.025f;
        _runtimeMuscleScratch[(int)EHumanoidValue.SpineLeftRight] = -sinPhase * 0.17f + turnLead * 0.06f;
        _runtimeMuscleScratch[(int)EHumanoidValue.ChestLeftRight] = -sinPhase * 0.09f + turnLead * 0.05f;
        _runtimeMuscleScratch[(int)EHumanoidValue.SpineTwistLeftRight] = sinPhase * 0.14f + idleAsym * 0.25f;
        _runtimeMuscleScratch[(int)EHumanoidValue.ChestTwistLeftRight] = -sinPhase * 0.15f - idleAsym * 0.25f;
        _runtimeMuscleScratch[(int)EHumanoidValue.UpperChestTwistLeftRight] = -sinPhase * 0.08f;

        // Stabilize head and neck forward against torso crawl twist while leading prone turns naturally.
        _runtimeMuscleScratch[(int)EHumanoidValue.NeckNodDownUp] = 0.32f - breath * 0.02f;
        _runtimeMuscleScratch[(int)EHumanoidValue.HeadNodDownUp] = 0.38f - breath * 0.02f;
        _runtimeMuscleScratch[(int)EHumanoidValue.NeckTurnLeftRight] =
            Math.Clamp(sinPhase * 0.06f - turnLead * 0.18f + idleHeadSway * 0.05f, -0.35f, 0.35f);
        _runtimeMuscleScratch[(int)EHumanoidValue.HeadTurnLeftRight] =
            Math.Clamp(sinPhase * 0.08f - turnLead * 0.24f + idleHeadSway * 0.07f, -0.45f, 0.45f);
        _runtimeMuscleScratch[(int)EHumanoidValue.HeadTiltLeftRight] =
            Math.Clamp(-cosPhase * 0.07f, -0.18f, 0.18f);

        // Clavicles protract as each arm reaches forward and retract during the grounded pull stroke.
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftShoulderFrontBack] =
            Math.Clamp((leftReach - leftPull) * 0.36f + idleAsym * 0.4f, -0.50f, 0.50f);
        _runtimeMuscleScratch[(int)EHumanoidValue.RightShoulderFrontBack] =
            Math.Clamp((rightReach - rightPull) * 0.36f - idleAsym * 0.4f, -0.50f, 0.50f);
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftShoulderDownUp] =
            Math.Clamp(leftLift * 0.16f - leftPull * 0.08f + breath * 0.03f, -0.35f, 0.35f);
        _runtimeMuscleScratch[(int)EHumanoidValue.RightShoulderDownUp] =
            Math.Clamp(rightLift * 0.16f - rightPull * 0.08f + breath * 0.03f, -0.35f, 0.35f);

        // Forearms rest ahead of the chest with flat pronated palms on the ground; during crawl,
        // the advancing arm lifts cleanly off the floor (negative ArmFrontBack lifts elbow + hand
        // away from the ground in 78 deg prone pitch), reaches forward, and pulls flat back.
        float baseArmDownUp = mecanim ? 0.44f : 0.28f;
        float baseArmFrontBack = mecanim ? -0.38f : -0.34f;
        float baseArmTwist = 0.16f;
        float baseForearmStretch = mecanim ? -0.10f : -0.38f;
        float baseForearmTwist = -0.34f;
        float baseHandDownUp = 0.22f;
        float baseHandInOut = -0.10f;

        _runtimeMuscleScratch[(int)EHumanoidValue.LeftArmDownUp] = Math.Clamp(
            baseArmDownUp + leftReach * 0.30f - leftPull * 0.12f + idleAsym * 0.5f, -0.40f, 0.86f);
        _runtimeMuscleScratch[(int)EHumanoidValue.RightArmDownUp] = Math.Clamp(
            baseArmDownUp + rightReach * 0.30f - rightPull * 0.12f - idleAsym * 0.5f, -0.40f, 0.86f);
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftArmFrontBack] = Math.Clamp(
            baseArmFrontBack - leftLift * 0.10f - leftPull * 0.08f, -0.68f, 0.05f);
        _runtimeMuscleScratch[(int)EHumanoidValue.RightArmFrontBack] = Math.Clamp(
            baseArmFrontBack - rightLift * 0.10f - rightPull * 0.08f, -0.68f, 0.05f);
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftArmTwistInOut] = Math.Clamp(
            baseArmTwist - leftReach * 0.14f + leftPull * 0.06f - leftLift * 0.12f, -0.25f, 0.50f);
        _runtimeMuscleScratch[(int)EHumanoidValue.RightArmTwistInOut] = Math.Clamp(
            baseArmTwist - rightReach * 0.14f + rightPull * 0.06f - rightLift * 0.12f, -0.25f, 0.50f);
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftForearmStretch] = Math.Clamp(
            baseForearmStretch + leftReach * 0.42f - leftPull * 0.20f + idleAsym * 0.4f, -0.70f, 0.65f);
        _runtimeMuscleScratch[(int)EHumanoidValue.RightForearmStretch] = Math.Clamp(
            baseForearmStretch + rightReach * 0.42f - rightPull * 0.20f - idleAsym * 0.4f, -0.70f, 0.65f);

        // Keep palms flat down on the ground across stance and softly level during the reach lift.
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftForearmTwistInOut] = Math.Clamp(
            baseForearmTwist - leftReach * 0.14f + leftPull * 0.10f, -0.75f, 0.20f);
        _runtimeMuscleScratch[(int)EHumanoidValue.RightForearmTwistInOut] = Math.Clamp(
            baseForearmTwist - rightReach * 0.14f + rightPull * 0.10f, -0.75f, 0.20f);
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftHandDownUp] = Math.Clamp(
            baseHandDownUp + leftReach * 0.22f - leftPull * 0.18f + leftLift * 0.08f, -0.25f, 0.65f);
        _runtimeMuscleScratch[(int)EHumanoidValue.RightHandDownUp] = Math.Clamp(
            baseHandDownUp + rightReach * 0.22f - rightPull * 0.18f + rightLift * 0.08f, -0.25f, 0.65f);
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftHandInOut] = Math.Clamp(
            baseHandInOut - leftReach * 0.08f, -0.35f, 0.25f);
        _runtimeMuscleScratch[(int)EHumanoidValue.RightHandInOut] = Math.Clamp(
            baseHandInOut - rightReach * 0.08f, -0.35f, 0.25f);

        // Contralateral leg push-off: hike the opposite knee out and low along the ground while
        // dorsiflexing the ankle so the inside edge of the foot pushes back smoothly without floating high.
        float baseUpperLegFB = mecanim ? 0.40f : 0.0f;
        float baseLowerLegStretch = mecanim ? 0.94f : 0.0f;
        float leftLegDrive = rightReach;
        float rightLegDrive = leftReach;
        float leftLegPush = rightLift;
        float rightLegPush = leftLift;

        _runtimeMuscleScratch[(int)EHumanoidValue.LeftUpperLegFrontBack] = baseUpperLegFB - leftLegDrive * 0.04f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightUpperLegFrontBack] = baseUpperLegFB - rightLegDrive * 0.04f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftUpperLegInOut] = 0.08f + leftLegDrive * 0.44f + idleAsym * 0.30f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightUpperLegInOut] = 0.08f + rightLegDrive * 0.44f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftUpperLegTwistInOut] = leftLegDrive * 0.34f + idleAsym * 0.25f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightUpperLegTwistInOut] = rightLegDrive * 0.34f - idleAsym * 0.15f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftLowerLegStretch] = Math.Clamp(
            baseLowerLegStretch - leftLegDrive * 0.20f - leftLegPush * 0.05f - idleAsym * 0.20f, 0.45f, 0.98f);
        _runtimeMuscleScratch[(int)EHumanoidValue.RightLowerLegStretch] = Math.Clamp(
            baseLowerLegStretch - rightLegDrive * 0.20f - rightLegPush * 0.05f, 0.45f, 0.98f);
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftFootUpDown] = Math.Clamp(
            0.90f - leftLegDrive * 0.34f - leftLegPush * 0.10f, 0.25f, 0.98f);
        _runtimeMuscleScratch[(int)EHumanoidValue.RightFootUpDown] = Math.Clamp(
            0.90f - rightLegDrive * 0.34f - rightLegPush * 0.10f, 0.25f, 0.98f);
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftFootTwistInOut] = -leftLegDrive * 0.26f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightFootTwistInOut] = -rightLegDrive * 0.26f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftToesUpDown] = leftLegDrive * 0.30f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightToesUpDown] = rightLegDrive * 0.30f;

        // Pitch pelvis forward 78 deg and shift torso roll onto the planted pulling elbow during swing.
        const float pronePitchRadians = 78.0f * (MathF.PI / 180.0f);
        float rollRadians = cosPhase * (3.6f * (MathF.PI / 180.0f));
        Quaternion facingRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facingYawRadians);
        Quaternion pronePitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pronePitchRadians);
        Quaternion crawlRoll = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rollRadians);
        Quaternion bodyRot = SomaPoseTransform.NormalizeOrIdentity(facingRot * pronePitch * crawlRoll);

        float hipsY = 0.185f + MathF.Abs(sinPhase) * 0.008f + breath * 0.003f;
        var bodyPos = new Vector3(rootGroundXZ.X, hipsY, rootGroundXZ.Z);
        _runtimeSampler.SampleSomaPose(
            bodyPos,
            bodyRot,
            _runtimeMuscleScratch,
            avatarLegScale: 1.0f,
            destination);
    }

    /// <summary>
    /// Builds a grounded hands-and-knees transition waypoint with flat extended palms beneath
    /// shoulders, knees planted on the floor, and lead-limb stagger, used between crouched and
    /// prone postures.
    /// </summary>
    private void BuildHandsAndKneesPose(
        Vector3 rootGroundXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> destination)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Destination span must hold at least {JointCount} joints.", nameof(destination));

        bool mecanim = _calibration.UseMecanimNeutralPostureOffsets;
        Array.Clear(_runtimeMuscleScratch);

        // Slight lead-limb stagger (left palm slightly ahead, right knee slightly ahead) keeps
        // dropping to prone and rising back up looking organic instead of mechanically mirrored.
        const float stagger = 0.08f;

        _runtimeMuscleScratch[(int)EHumanoidValue.SpineFrontBack] = 0.12f;
        _runtimeMuscleScratch[(int)EHumanoidValue.ChestFrontBack] = 0.18f;
        _runtimeMuscleScratch[(int)EHumanoidValue.SpineTwistLeftRight] = 0.06f;
        _runtimeMuscleScratch[(int)EHumanoidValue.ChestTwistLeftRight] = -0.06f;
        _runtimeMuscleScratch[(int)EHumanoidValue.NeckNodDownUp] = 0.28f;
        _runtimeMuscleScratch[(int)EHumanoidValue.HeadNodDownUp] = 0.34f;

        _runtimeMuscleScratch[(int)EHumanoidValue.LeftShoulderFrontBack] = 0.12f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightShoulderFrontBack] = -0.06f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftArmDownUp] = (mecanim ? 0.04f : -0.14f) + stagger;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightArmDownUp] = (mecanim ? 0.04f : -0.14f) - stagger * 0.7f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftArmFrontBack] = mecanim ? -0.58f : -0.45f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightArmFrontBack] = mecanim ? -0.58f : -0.45f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftArmTwistInOut] = 0.12f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightArmTwistInOut] = 0.12f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftForearmStretch] = mecanim ? 0.86f : 0.0f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightForearmStretch] = mecanim ? 0.86f : 0.0f;

        // Pronate forearms and extend wrists cleanly so palms sit flat on the floor with fingers forward.
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftForearmTwistInOut] = -0.35f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightForearmTwistInOut] = -0.35f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftHandDownUp] = 0.80f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightHandDownUp] = 0.80f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftHandInOut] = -0.08f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightHandInOut] = -0.08f;

        _runtimeMuscleScratch[(int)EHumanoidValue.LeftUpperLegFrontBack] = (mecanim ? -0.26f : -0.55f) + stagger * 0.6f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightUpperLegFrontBack] = (mecanim ? -0.26f : -0.55f) - stagger * 0.6f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftUpperLegInOut] = 0.08f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightUpperLegInOut] = 0.08f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftLowerLegStretch] = mecanim ? -0.22f : -0.60f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightLowerLegStretch] = mecanim ? -0.22f : -0.60f;
        _runtimeMuscleScratch[(int)EHumanoidValue.LeftFootUpDown] = 0.72f;
        _runtimeMuscleScratch[(int)EHumanoidValue.RightFootUpDown] = 0.72f;

        const float kneelPitchRadians = 68.0f * (MathF.PI / 180.0f);
        Quaternion facingRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facingYawRadians);
        Quaternion kneelPitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, kneelPitchRadians);
        Quaternion bodyRot = SomaPoseTransform.NormalizeOrIdentity(facingRot * kneelPitch);

        var bodyPos = new Vector3(rootGroundXZ.X, 0.47f, rootGroundXZ.Z);
        _runtimeSampler.SampleSomaPose(
            bodyPos,
            bodyRot,
            _runtimeMuscleScratch,
            avatarLegScale: 1.0f,
            destination);
    }

    /// <summary>
    /// Builds an articulated stance transition pose between upright or crouched and prone postures
    /// (<paramref name="proneBlend01"/> = <c>0</c> is upright or crouched; <c>1</c> is flat prone)
    /// routed through crouch and hands-and-knees waypoints so the character bends cleanly.
    /// </summary>
    public void BuildProneTransitionPose(
        float proneBlend01,
        bool useStandingEndpoint,
        Vector3 rootGroundXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> destination)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Destination span must hold at least {JointCount} joints.", nameof(destination));

        float t = Math.Clamp(proneBlend01, 0.0f, 1.0f);
        if (useStandingEndpoint)
        {
            if (t < 0.34f)
            {
                float u = SmoothStep01(t / 0.34f);
                CopyIdlePoseAtYaw(facingYawRadians, _transitionPoseA);
                _transitionPoseA[0] = new SomaPoseTransform(
                    new Vector3(rootGroundXZ.X, _transitionPoseA[0].Position.Y, rootGroundXZ.Z),
                    _transitionPoseA[0].Rotation);
                CopyCrouchIdlePoseAtYaw(rootGroundXZ, facingYawRadians, _transitionPoseB);
                BlendPoseSpan(_transitionPoseA, _transitionPoseB, u, destination);
            }
            else if (t < 0.70f)
            {
                float u = SmoothStep01((t - 0.34f) / 0.36f);
                CopyCrouchIdlePoseAtYaw(rootGroundXZ, facingYawRadians, _transitionPoseA);
                BuildHandsAndKneesPose(rootGroundXZ, facingYawRadians, _transitionPoseB);
                BlendPoseSpan(_transitionPoseA, _transitionPoseB, u, destination);
            }
            else
            {
                float u = SmoothStep01((t - 0.70f) / 0.30f);
                BuildHandsAndKneesPose(rootGroundXZ, facingYawRadians, _transitionPoseA);
                BuildPronePose(0.0f, 0.0f, rootGroundXZ, facingYawRadians, _transitionPoseB);
                BlendPoseSpan(_transitionPoseA, _transitionPoseB, u, destination);
            }
        }
        else
        {
            if (t < 0.52f)
            {
                float u = SmoothStep01(t / 0.52f);
                CopyCrouchIdlePoseAtYaw(rootGroundXZ, facingYawRadians, _transitionPoseA);
                BuildHandsAndKneesPose(rootGroundXZ, facingYawRadians, _transitionPoseB);
                BlendPoseSpan(_transitionPoseA, _transitionPoseB, u, destination);
            }
            else
            {
                float u = SmoothStep01((t - 0.52f) / 0.48f);
                BuildHandsAndKneesPose(rootGroundXZ, facingYawRadians, _transitionPoseA);
                BuildPronePose(0.0f, 0.0f, rootGroundXZ, facingYawRadians, _transitionPoseB);
                BlendPoseSpan(_transitionPoseA, _transitionPoseB, u, destination);
            }
        }
    }

    private static float SmoothStep01(float x)
    {
        float c = Math.Clamp(x, 0.0f, 1.0f);
        return c * c * (3.0f - 2.0f * c);
    }

    private static void BlendPoseSpan(
        ReadOnlySpan<SomaPoseTransform> a,
        ReadOnlySpan<SomaPoseTransform> b,
        float weight,
        Span<SomaPoseTransform> destination)
    {
        for (int i = 0; i < JointCount; i++)
            destination[i] = SomaPoseTransform.Lerp(a[i], b[i], weight);
    }
}
