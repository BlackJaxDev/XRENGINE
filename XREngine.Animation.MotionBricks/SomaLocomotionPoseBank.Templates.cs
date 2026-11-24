using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Animation.MotionBricks;

public sealed partial class SomaLocomotionPoseBank
{
    private static void BlendTemplates(
        ReadOnlySpan<SomaPoseTransform> a,
        ReadOnlySpan<SomaPoseTransform> b,
        float weight,
        Span<SomaPoseTransform> destination)
    {
        float w = Math.Clamp(weight, 0.0f, 1.0f);
        for (int i = 0; i < TemplateSize; i++)
            destination[i] = SomaPoseTransform.Lerp(a[i], b[i], w);
    }

    private static void PlaceAndScaleVelocity(
        ReadOnlySpan<SomaPoseTransform> template,
        Vector3 targetGroundPosition,
        float facingYawRadians,
        float speedMetersPerSecond,
        float relativeTravelYawRadians,
        Span<SomaPoseTransform> destination)
    {
        Quaternion facingRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facingYawRadians);
        float desiredSpeed = MathF.Max(0.0f, speedMetersPerSecond);

        // Mirror NVIDIA's adjust_target_pose_speed: when the template comes from real moving mocap
        // (non-zero ground velocity across frames 0..3), scale only the ground XZ offsets relative to
        // frame 0 so natural mocap root height, sway, and all 29 joint rotations remain intact.
        Vector3 p0 = template[0].Position;
        Vector3 pLast = template[(TemplateFrames - 1) * JointCount].Position;
        float dx = pLast.X - p0.X;
        float dz = pLast.Z - p0.Z;
        float sourceSpeed = MathF.Sqrt(dx * dx + dz * dz) * (SomaModelContract.SampleRateHz / (TemplateFrames - 1));

        if (sourceSpeed > 0.05f && desiredSpeed > 0.05f)
        {
            float speedScale = Math.Clamp(desiredSpeed / sourceSpeed, 0.0f, 2.5f);
            float sourceTravelYaw = MathF.Atan2(dx, dz);
            float desiredTravelYaw = facingYawRadians + relativeTravelYawRadians;
            Quaternion xzTravelRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, desiredTravelYaw - sourceTravelYaw);

            for (int frame = 0; frame < TemplateFrames; frame++)
            {
                int frameBase = frame * JointCount;
                SomaPoseTransform rootTemplate = template[frameBase];
                var scaledLocalXZ = new Vector3(
                    (rootTemplate.Position.X - p0.X) * speedScale,
                    0.0f,
                    (rootTemplate.Position.Z - p0.Z) * speedScale);
                Vector3 worldOffset = Vector3.Transform(scaledLocalXZ, xzTravelRot);
                var worldRootPos = new Vector3(
                    targetGroundPosition.X + worldOffset.X,
                    rootTemplate.Position.Y,
                    targetGroundPosition.Z + worldOffset.Z);
                Quaternion worldRootRot = SomaPoseTransform.NormalizeOrIdentity(facingRot * rootTemplate.Rotation);
                destination[frameBase] = new SomaPoseTransform(worldRootPos, worldRootRot);

                for (int joint = 1; joint < JointCount; joint++)
                    destination[frameBase + joint] = template[frameBase + joint];
            }

            return;
        }

        Quaternion travelRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facingYawRadians + relativeTravelYawRadians);
        Vector3 travelDir = Vector3.Transform(Vector3.UnitZ, travelRot);
        float stepPerFrame = desiredSpeed / SomaModelContract.SampleRateHz;

        for (int frame = 0; frame < TemplateFrames; frame++)
        {
            int frameBase = frame * JointCount;
            SomaPoseTransform rootTemplate = template[frameBase];

            Vector3 worldRootPos = new Vector3(targetGroundPosition.X, rootTemplate.Position.Y, targetGroundPosition.Z)
                + travelDir * (stepPerFrame * frame);
            Quaternion worldRootRot = SomaPoseTransform.NormalizeOrIdentity(facingRot * rootTemplate.Rotation);
            destination[frameBase] = new SomaPoseTransform(worldRootPos, worldRootRot);

            for (int joint = 1; joint < JointCount; joint++)
                destination[frameBase + joint] = template[frameBase + joint];
        }
    }

    private void BuildDefaultTemplates(SomaRestSkeletonCalibration calibration)
    {
        // Build standing and crouched idle templates directly from the zero-amplitude breathing idle
        // so SOMA braking trajectories wind into the exact same pose with zero discontinuity.
        for (int frame = 0; frame < TemplateFrames; frame++)
        {
            BuildBreathingIdlePose(
                idleTimeSeconds: 0.0f,
                StandingStyleIndex,
                Vector3.Zero,
                facingYawRadians: 0.0f,
                _idleTemplate.AsSpan(frame * JointCount, JointCount),
                breathingAmplitudeScale: 0.0f);
            BuildBreathingIdlePose(
                idleTimeSeconds: 0.0f,
                CrouchedStyleIndex,
                Vector3.Zero,
                facingYawRadians: 0.0f,
                _crouchIdleTemplate.AsSpan(frame * JointCount, JointCount),
                breathingAmplitudeScale: 0.0f);
        }

        PopulateTemplate(_walkTemplate, calibration, hipDrop: 0.015f, spineLean: 0.03f, strideSwing: 0.30f, kneeBend: 0.32f, armSwing: 0.22f, strafeSign: 0.0f);
        PopulateTemplate(_jogTemplate, calibration, hipDrop: 0.025f, spineLean: 0.07f, strideSwing: 0.44f, kneeBend: 0.55f, armSwing: 0.40f, strafeSign: 0.0f);
        PopulateTemplate(_runTemplate, calibration, hipDrop: 0.035f, spineLean: 0.11f, strideSwing: 0.56f, kneeBend: 0.72f, armSwing: 0.55f, strafeSign: 0.0f);
        PopulateTemplate(_backwardTemplate, calibration, hipDrop: 0.03f, spineLean: -0.06f, strideSwing: -0.28f, kneeBend: 0.30f, armSwing: -0.20f, strafeSign: 0.0f);
        PopulateTemplate(_strafeLeftTemplate, calibration, hipDrop: 0.03f, spineLean: 0.04f, strideSwing: 0.18f, kneeBend: 0.32f, armSwing: 0.18f, strafeSign: 1.0f);
        PopulateTemplate(_strafeRightTemplate, calibration, hipDrop: 0.03f, spineLean: 0.04f, strideSwing: 0.18f, kneeBend: 0.32f, armSwing: 0.18f, strafeSign: -1.0f);

        // Balanced athletic crouch movement templates with arms hanging near the hips and thighs.
        PopulateCrouchTemplate(_crouchWalkTemplate, calibration, strideSwing: 0.24f, kneeBend: 0.22f, armSwing: 0.16f, strafeSign: 0.0f);
        PopulateCrouchTemplate(_crouchBackwardTemplate, calibration, strideSwing: -0.20f, kneeBend: 0.20f, armSwing: -0.14f, strafeSign: 0.0f);
        PopulateCrouchTemplate(_crouchStrafeLeftTemplate, calibration, strideSwing: 0.16f, kneeBend: 0.20f, armSwing: 0.12f, strafeSign: 1.0f);
        PopulateCrouchTemplate(_crouchStrafeRightTemplate, calibration, strideSwing: 0.16f, kneeBend: 0.20f, armSwing: 0.12f, strafeSign: -1.0f);

        // Lay-down prone templates (flat on stomach, propped on elbows).
        for (int frame = 0; frame < TemplateFrames; frame++)
        {
            BuildPronePose(0.0f, 0.0f, Vector3.Zero, 0.0f, _proneIdleTemplate.AsSpan(frame * JointCount, JointCount));
            BuildPronePose(frame * (MathF.PI * 0.35f), 1.0f, Vector3.Zero, 0.0f, _proneMoveTemplate.AsSpan(frame * JointCount, JointCount));
        }

        BuildJumpAndLandingPoses(calibration);
    }

    private void BuildJumpAndLandingPoses(SomaRestSkeletonCalibration calibration)
    {
        var sampler = new HumanoidToSomaPoseSampler(calibration);
        float[] muscles = new float[MotionBricksEvaluatedPose.MuscleChannelCount];
        bool mecanimNeutral = calibration.UseMecanimNeutralPostureOffsets;
        float baseHipsY = calibration.HipsHeightMeters - 0.02f;

        // Jump ascent: athletic launch with lifted arms, slight chest lift, asymmetric tucked knees, toes pointed.
        Array.Clear(muscles);
        muscles[(int)EHumanoidValue.SpineFrontBack] = 0.08f;
        muscles[(int)EHumanoidValue.ChestFrontBack] = 0.10f;
        muscles[(int)EHumanoidValue.LeftArmDownUp] = mecanimNeutral ? -0.15f : -0.30f;
        muscles[(int)EHumanoidValue.RightArmDownUp] = mecanimNeutral ? -0.15f : -0.30f;
        muscles[(int)EHumanoidValue.LeftArmFrontBack] = 0.22f;
        muscles[(int)EHumanoidValue.RightArmFrontBack] = -0.12f;
        muscles[(int)EHumanoidValue.LeftForearmStretch] = mecanimNeutral ? 0.35f : -0.15f;
        muscles[(int)EHumanoidValue.RightForearmStretch] = mecanimNeutral ? 0.35f : -0.15f;
        muscles[(int)EHumanoidValue.LeftUpperLegFrontBack] = mecanimNeutral ? -0.15f : -0.45f;
        muscles[(int)EHumanoidValue.RightUpperLegFrontBack] = mecanimNeutral ? 0.35f : -0.15f;
        muscles[(int)EHumanoidValue.LeftLowerLegStretch] = mecanimNeutral ? 0.15f : -0.55f;
        muscles[(int)EHumanoidValue.RightLowerLegStretch] = mecanimNeutral ? 0.55f : -0.25f;
        muscles[(int)EHumanoidValue.LeftFootUpDown] = 0.35f;
        muscles[(int)EHumanoidValue.RightFootUpDown] = 0.45f;
        sampler.SampleSomaPose(
            new Vector3(0.0f, baseHipsY + 0.04f, 0.0f),
            Quaternion.Identity,
            muscles,
            avatarLegScale: 1.0f,
            _jumpAscentPose);

        // Jump apex and fall: knees drawn up ready to absorb impact, arms out for balance, forward torso lean.
        Array.Clear(muscles);
        muscles[(int)EHumanoidValue.SpineFrontBack] = -0.18f;
        muscles[(int)EHumanoidValue.ChestFrontBack] = -0.12f;
        muscles[(int)EHumanoidValue.LeftArmDownUp] = mecanimNeutral ? -0.18f : -0.35f;
        muscles[(int)EHumanoidValue.RightArmDownUp] = mecanimNeutral ? -0.18f : -0.35f;
        muscles[(int)EHumanoidValue.LeftArmFrontBack] = 0.15f;
        muscles[(int)EHumanoidValue.RightArmFrontBack] = 0.15f;
        muscles[(int)EHumanoidValue.LeftForearmStretch] = mecanimNeutral ? 0.42f : -0.10f;
        muscles[(int)EHumanoidValue.RightForearmStretch] = mecanimNeutral ? 0.42f : -0.10f;
        muscles[(int)EHumanoidValue.LeftUpperLegFrontBack] = mecanimNeutral ? 0.05f : -0.32f;
        muscles[(int)EHumanoidValue.RightUpperLegFrontBack] = mecanimNeutral ? -0.05f : -0.38f;
        muscles[(int)EHumanoidValue.LeftLowerLegStretch] = mecanimNeutral ? 0.32f : -0.45f;
        muscles[(int)EHumanoidValue.RightLowerLegStretch] = mecanimNeutral ? 0.22f : -0.52f;
        muscles[(int)EHumanoidValue.LeftFootUpDown] = -0.15f;
        muscles[(int)EHumanoidValue.RightFootUpDown] = -0.15f;
        sampler.SampleSomaPose(
            new Vector3(0.0f, baseHipsY - 0.08f, 0.0f),
            Quaternion.Identity,
            muscles,
            avatarLegScale: 1.0f,
            _jumpFallPose);

        // Landing absorption dip: lowered hips, bent knees, slight forward lean, arms steadied.
        Array.Clear(muscles);
        muscles[(int)EHumanoidValue.SpineFrontBack] = -0.24f;
        muscles[(int)EHumanoidValue.ChestFrontBack] = -0.14f;
        muscles[(int)EHumanoidValue.LeftArmDownUp] = mecanimNeutral ? -0.45f : -0.65f;
        muscles[(int)EHumanoidValue.RightArmDownUp] = mecanimNeutral ? -0.45f : -0.65f;
        muscles[(int)EHumanoidValue.LeftForearmStretch] = mecanimNeutral ? 0.45f : 0.0f;
        muscles[(int)EHumanoidValue.RightForearmStretch] = mecanimNeutral ? 0.45f : 0.0f;
        muscles[(int)EHumanoidValue.LeftUpperLegFrontBack] = mecanimNeutral ? -0.10f : -0.42f;
        muscles[(int)EHumanoidValue.RightUpperLegFrontBack] = mecanimNeutral ? -0.10f : -0.42f;
        muscles[(int)EHumanoidValue.LeftLowerLegStretch] = mecanimNeutral ? 0.22f : -0.55f;
        muscles[(int)EHumanoidValue.RightLowerLegStretch] = mecanimNeutral ? 0.22f : -0.55f;
        muscles[(int)EHumanoidValue.LeftFootUpDown] = -0.30f;
        muscles[(int)EHumanoidValue.RightFootUpDown] = -0.30f;
        sampler.SampleSomaPose(
            new Vector3(0.0f, baseHipsY - 0.22f, 0.0f),
            Quaternion.Identity,
            muscles,
            avatarLegScale: 1.0f,
            _landingPose);
    }

    private static void PopulateTemplate(
        SomaPoseTransform[] destination,
        SomaRestSkeletonCalibration calibration,
        float hipDrop,
        float spineLean,
        float strideSwing,
        float kneeBend,
        float armSwing,
        float strafeSign)
    {
        var sampler = new HumanoidToSomaPoseSampler(calibration);
        float[] muscles = new float[MotionBricksEvaluatedPose.MuscleChannelCount];
        float baseHipsY = calibration.HipsHeightMeters - 0.02f;

        for (int frame = 0; frame < TemplateFrames; frame++)
        {
            // Advance at a realistic 30 Hz gait phase (~1.1 s stride period) so recovery target
            // conditioning never asks SOMA for an unnaturally fast leg snap across the 4 target frames.
            float phase = frame * (MathF.PI * 0.06f);
            float sinPhase = MathF.Sin(phase);
            float cosPhase = MathF.Cos(phase);
            int frameBase = frame * JointCount;

            Array.Clear(muscles);

            // Subtle forward spine lean during locomotion, relaxed upright alignment during idle.
            muscles[(int)EHumanoidValue.SpineFrontBack] = -Math.Clamp(spineLean, -0.5f, 0.65f);

            bool mecanimNeutral = calibration.UseMecanimNeutralPostureOffsets;
            float baseArmDownUp = mecanimNeutral ? -0.76f : -0.88f;
            float baseForearmStretch = mecanimNeutral ? 0.58f : 0.22f;
            float baseUpperLegFrontBack = mecanimNeutral ? 0.56f : 0.0f;
            float baseLowerLegStretch = mecanimNeutral ? 0.86f : -0.08f;

            // Natural relaxed standing arms hanging cleanly at the sides with soft elbows and wrists.
            muscles[(int)EHumanoidValue.LeftShoulderDownUp] = -0.07f;
            muscles[(int)EHumanoidValue.RightShoulderDownUp] = -0.07f;
            muscles[(int)EHumanoidValue.LeftArmDownUp] = Math.Clamp(baseArmDownUp, -0.95f, 0.5f);
            muscles[(int)EHumanoidValue.RightArmDownUp] = Math.Clamp(baseArmDownUp, -0.95f, 0.5f);
            muscles[(int)EHumanoidValue.LeftForearmStretch] = Math.Clamp(baseForearmStretch, -0.9f, 1.0f);
            muscles[(int)EHumanoidValue.RightForearmStretch] = Math.Clamp(baseForearmStretch, -0.9f, 1.0f);
            muscles[(int)EHumanoidValue.LeftArmFrontBack] = Math.Clamp(-0.04f - sinPhase * armSwing * 0.45f, -0.8f, 0.8f);
            muscles[(int)EHumanoidValue.RightArmFrontBack] = Math.Clamp(-0.04f + sinPhase * armSwing * 0.45f, -0.8f, 0.8f);
            muscles[(int)EHumanoidValue.LeftHandDownUp] = -0.12f;
            muscles[(int)EHumanoidValue.RightHandDownUp] = -0.12f;

            // Counter-phase leg stride, knee flexion, and narrower natural stance width.
            float legRoll = strafeSign * (0.14f + 0.08f * cosPhase);
            muscles[(int)EHumanoidValue.LeftUpperLegFrontBack] = Math.Clamp(baseUpperLegFrontBack + sinPhase * strideSwing * 0.6f, -0.95f, 0.95f);
            muscles[(int)EHumanoidValue.RightUpperLegFrontBack] = Math.Clamp(baseUpperLegFrontBack - sinPhase * strideSwing * 0.6f, -0.95f, 0.95f);
            muscles[(int)EHumanoidValue.LeftUpperLegInOut] = Math.Clamp(0.02f + legRoll, -0.6f, 0.6f);
            muscles[(int)EHumanoidValue.RightUpperLegInOut] = Math.Clamp(0.02f - legRoll, -0.6f, 0.6f);
            muscles[(int)EHumanoidValue.LeftLowerLegStretch] = Math.Clamp(baseLowerLegStretch - MathF.Max(0.0f, cosPhase) * kneeBend * 0.7f, -0.95f, 1.0f);
            muscles[(int)EHumanoidValue.RightLowerLegStretch] = Math.Clamp(baseLowerLegStretch - MathF.Max(0.0f, -cosPhase) * kneeBend * 0.7f, -0.95f, 1.0f);

            float bob = -hipDrop + MathF.Abs(sinPhase) * MathF.Min(hipDrop, 0.06f) * 0.5f;
            float hipsY = MathF.Max(0.35f, baseHipsY + bob);
            var bodyPos = new Vector3(0.0f, hipsY, 0.0f);
            sampler.SampleSomaPose(
                bodyPos,
                Quaternion.Identity,
                muscles,
                avatarLegScale: 1.0f,
                destination.AsSpan(frameBase, JointCount));
        }
    }

    private static void PopulateCrouchTemplate(
        SomaPoseTransform[] destination,
        SomaRestSkeletonCalibration calibration,
        float strideSwing,
        float kneeBend,
        float armSwing,
        float strafeSign)
    {
        var sampler = new HumanoidToSomaPoseSampler(calibration);
        float[] muscles = new float[MotionBricksEvaluatedPose.MuscleChannelCount];
        bool mecanim = calibration.UseMecanimNeutralPostureOffsets;
        float baseHipsY = calibration.HipsHeightMeters - 0.36f;

        for (int frame = 0; frame < TemplateFrames; frame++)
        {
            float phase = frame * (MathF.PI * 0.22f);
            float sinPhase = MathF.Sin(phase);
            float cosPhase = MathF.Cos(phase);
            int frameBase = frame * JointCount;

            Array.Clear(muscles);

            // Lean torso naturally forward over the front knee so center of gravity stays over the feet,
            // while lifting the neck and head to look straight ahead.
            muscles[(int)EHumanoidValue.SpineFrontBack] = -0.24f;
            muscles[(int)EHumanoidValue.ChestFrontBack] = -0.14f;
            muscles[(int)EHumanoidValue.NeckNodDownUp] = 0.18f;
            muscles[(int)EHumanoidValue.HeadNodDownUp] = 0.16f;

            // Keep arms hanging naturally down near the hips and thighs with gravity rather than held out forward.
            float armDownUp = mecanim ? -0.82f : -0.90f;
            float forearmStretch = mecanim ? 0.62f : 0.25f;
            muscles[(int)EHumanoidValue.LeftShoulderDownUp] = -0.10f;
            muscles[(int)EHumanoidValue.RightShoulderDownUp] = -0.10f;
            muscles[(int)EHumanoidValue.LeftArmDownUp] = armDownUp;
            muscles[(int)EHumanoidValue.RightArmDownUp] = armDownUp;
            muscles[(int)EHumanoidValue.LeftArmFrontBack] = Math.Clamp(-0.18f - sinPhase * armSwing * 0.40f, -0.8f, 0.8f);
            muscles[(int)EHumanoidValue.RightArmFrontBack] = Math.Clamp(-0.14f + sinPhase * armSwing * 0.40f, -0.8f, 0.8f);
            muscles[(int)EHumanoidValue.LeftForearmStretch] = forearmStretch;
            muscles[(int)EHumanoidValue.RightForearmStretch] = forearmStretch;

            // Staggered balanced athletic squat: left foot slightly forward (flat), right foot slightly back
            // on the ball of the foot so the stance is physically balanced without an invisible chair.
            float leftUpperBase = mecanim ? -0.28f : -0.78f;
            float rightUpperBase = mecanim ? 0.06f : -0.45f;
            float leftKneeBase = mecanim ? -0.16f : -0.92f;
            float rightKneeBase = mecanim ? -0.34f : -0.95f;
            float legRoll = strafeSign * (0.12f + 0.06f * cosPhase);

            muscles[(int)EHumanoidValue.LeftUpperLegFrontBack] = Math.Clamp(leftUpperBase + sinPhase * strideSwing * 0.55f, -0.95f, 0.95f);
            muscles[(int)EHumanoidValue.RightUpperLegFrontBack] = Math.Clamp(rightUpperBase - sinPhase * strideSwing * 0.55f, -0.95f, 0.95f);
            muscles[(int)EHumanoidValue.LeftUpperLegInOut] = Math.Clamp(0.15f + legRoll, -0.6f, 0.6f);
            muscles[(int)EHumanoidValue.RightUpperLegInOut] = Math.Clamp(0.15f - legRoll, -0.6f, 0.6f);
            muscles[(int)EHumanoidValue.LeftLowerLegStretch] = Math.Clamp(leftKneeBase - MathF.Max(0.0f, cosPhase) * kneeBend * 0.55f, -0.95f, 1.0f);
            muscles[(int)EHumanoidValue.RightLowerLegStretch] = Math.Clamp(rightKneeBase - MathF.Max(0.0f, -cosPhase) * kneeBend * 0.55f, -0.95f, 1.0f);
            muscles[(int)EHumanoidValue.LeftFootUpDown] = -0.52f;
            muscles[(int)EHumanoidValue.RightFootUpDown] = -0.22f;
            muscles[(int)EHumanoidValue.RightToesUpDown] = -0.30f;

            float hipsY = MathF.Max(0.42f, baseHipsY + MathF.Abs(sinPhase) * 0.02f);
            sampler.SampleSomaPose(
                new Vector3(0.0f, hipsY, 0.0f),
                Quaternion.Identity,
                muscles,
                avatarLegScale: 1.0f,
                destination.AsSpan(frameBase, JointCount));
        }
    }
}
