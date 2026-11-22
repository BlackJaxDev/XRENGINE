using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Holds preallocated SOMA 4-pose target templates for 1D linear locomotion (idle, walk, jog, run)
/// and 2D directional strafing (idle, forward, backward, left, right), and blends/places target
/// poses into model space without heap allocations.
/// </summary>
public sealed class SomaLocomotionPoseBank
{
    private const int JointCount = SomaModelContract.JointCount;
    private const int TemplateFrames = SomaModelContract.TargetPoseCapacity;
    private const int TemplateSize = TemplateFrames * JointCount;

    private readonly SomaPoseTransform[] _idleTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _walkTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _jogTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _runTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _backwardTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _strafeLeftTemplate = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _strafeRightTemplate = new SomaPoseTransform[TemplateSize];

    private readonly SomaPoseTransform[] _scratchA = new SomaPoseTransform[TemplateSize];
    private readonly SomaPoseTransform[] _scratchB = new SomaPoseTransform[TemplateSize];

    public SomaLocomotionPoseBank(SomaRestSkeletonCalibration? calibration = null)
    {
        SomaRestSkeletonCalibration cal = calibration ?? SomaRestSkeletonCalibration.CanonicalDefault;
        BuildDefaultTemplates(cal);
    }

    /// <summary>
    /// Copies frame 0 of the upright relaxed idle template into <paramref name="destination"/>.
    /// </summary>
    public void CopyIdlePose(Span<SomaPoseTransform> destination)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Destination span must hold at least {JointCount} joints.", nameof(destination));

        _idleTemplate.AsSpan(0, JointCount).CopyTo(destination);
    }

    /// <summary>
    /// Blends the 1D linear locomotion templates (<c>Idle -> Walk -> Jog -> Run</c>) for a
    /// normalized speed parameter in <c>[0, 1]</c> and places the resulting target poses
    /// at <paramref name="targetGroundPosition"/> facing <paramref name="facingYawRadians"/>.
    /// </summary>
    public void BuildLinear1DTarget(
        float normalizedSpeed,
        float targetSpeedMetersPerSecond,
        Vector3 targetGroundPosition,
        float facingYawRadians,
        Span<SomaPoseTransform> destination)
    {
        float t = Math.Clamp(normalizedSpeed, 0.0f, 1.0f);
        if (t <= 0.35f)
        {
            BlendTemplates(_idleTemplate, _walkTemplate, t / 0.35f, _scratchA);
        }
        else if (t <= 0.70f)
        {
            BlendTemplates(_walkTemplate, _jogTemplate, (t - 0.35f) / 0.35f, _scratchA);
        }
        else
        {
            BlendTemplates(_jogTemplate, _runTemplate, (t - 0.70f) / 0.30f, _scratchA);
        }

        PlaceAndScaleVelocity(_scratchA, targetGroundPosition, facingYawRadians, targetSpeedMetersPerSecond, 0.0f, destination);
    }

    /// <summary>
    /// Blends the 2D directional strafe templates (<c>Idle, Forward, Backward, Left, Right</c>)
    /// using local-space velocity components <paramref name="localForward"/> and
    /// <paramref name="localRight"/> (both in <c>[-1, 1]</c>) while facing <paramref name="facingYawRadians"/>.
    /// </summary>
    public void BuildDirectional2DTarget(
        float localForward,
        float localRight,
        float targetSpeedMetersPerSecond,
        Vector3 targetGroundPosition,
        float facingYawRadians,
        float travelYawRadians,
        Span<SomaPoseTransform> destination)
    {
        float mag = Math.Clamp(MathF.Sqrt(localForward * localForward + localRight * localRight), 0.0f, 1.0f);
        if (mag <= 1.0e-4f)
        {
            PlaceAndScaleVelocity(_idleTemplate, targetGroundPosition, facingYawRadians, 0.0f, 0.0f, destination);
            return;
        }

        SomaPoseTransform[] fbTemplate = localForward >= 0.0f ? _walkTemplate : _backwardTemplate;
        SomaPoseTransform[] lrTemplate = localRight >= 0.0f ? _strafeRightTemplate : _strafeLeftTemplate;

        float fbAbs = MathF.Abs(localForward);
        float lrAbs = MathF.Abs(localRight);
        float axisSum = fbAbs + lrAbs;
        float lrWeight = axisSum > 1.0e-5f ? lrAbs / axisSum : 0.0f;

        BlendTemplates(fbTemplate, lrTemplate, lrWeight, _scratchB);
        BlendTemplates(_idleTemplate, _scratchB, mag, _scratchA);

        float relativeTravelYaw = CriticalSpringDamper.WrapAngleRadians(travelYawRadians - facingYawRadians);
        PlaceAndScaleVelocity(_scratchA, targetGroundPosition, facingYawRadians, targetSpeedMetersPerSecond, relativeTravelYaw, destination);
    }

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
        Quaternion travelRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facingYawRadians + relativeTravelYawRadians);
        Vector3 travelDir = Vector3.Transform(Vector3.UnitZ, travelRot);
        float stepPerFrame = MathF.Max(0.0f, speedMetersPerSecond) / SomaModelContract.SampleRateHz;

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
        PopulateTemplate(_idleTemplate, calibration, hipDrop: 0.0f, spineLean: 0.0f, strideSwing: 0.0f, kneeBend: 0.02f, armSwing: 0.0f, strafeSign: 0.0f);
        PopulateTemplate(_walkTemplate, calibration, hipDrop: 0.02f, spineLean: 0.06f, strideSwing: 0.32f, kneeBend: 0.35f, armSwing: 0.25f, strafeSign: 0.0f);
        PopulateTemplate(_jogTemplate, calibration, hipDrop: 0.04f, spineLean: 0.12f, strideSwing: 0.50f, kneeBend: 0.65f, armSwing: 0.48f, strafeSign: 0.0f);
        PopulateTemplate(_runTemplate, calibration, hipDrop: 0.06f, spineLean: 0.20f, strideSwing: 0.68f, kneeBend: 0.90f, armSwing: 0.72f, strafeSign: 0.0f);
        PopulateTemplate(_backwardTemplate, calibration, hipDrop: 0.03f, spineLean: -0.06f, strideSwing: -0.28f, kneeBend: 0.30f, armSwing: -0.20f, strafeSign: 0.0f);
        PopulateTemplate(_strafeLeftTemplate, calibration, hipDrop: 0.03f, spineLean: 0.04f, strideSwing: 0.18f, kneeBend: 0.32f, armSwing: 0.18f, strafeSign: 1.0f);
        PopulateTemplate(_strafeRightTemplate, calibration, hipDrop: 0.03f, spineLean: 0.04f, strideSwing: 0.18f, kneeBend: 0.32f, armSwing: 0.18f, strafeSign: -1.0f);
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
            float phase = frame * (MathF.PI * 0.22f);
            float sinPhase = MathF.Sin(phase);
            float cosPhase = MathF.Cos(phase);
            int frameBase = frame * JointCount;

            Array.Clear(muscles);

            // Subtle forward spine lean during locomotion.
            muscles[(int)EHumanoidValue.SpineFrontBack] = -Math.Clamp(spineLean, -0.5f, 0.5f);

            bool mecanimNeutral = calibration.UseMecanimNeutralPostureOffsets;
            float baseArmDownUp = mecanimNeutral ? -0.65f : -0.85f;
            float baseForearmStretch = mecanimNeutral ? 0.72f : 0.30f;
            float baseUpperLegFrontBack = mecanimNeutral ? 0.58f : 0.0f;
            float baseLowerLegStretch = mecanimNeutral ? 0.90f : -0.08f;

            // Natural relaxed standing arms at the sides with slight elbow flexion and counter-phase swing.
            muscles[(int)EHumanoidValue.LeftArmDownUp] = baseArmDownUp;
            muscles[(int)EHumanoidValue.RightArmDownUp] = baseArmDownUp;
            muscles[(int)EHumanoidValue.LeftForearmStretch] = baseForearmStretch;
            muscles[(int)EHumanoidValue.RightForearmStretch] = baseForearmStretch;
            muscles[(int)EHumanoidValue.LeftArmFrontBack] = Math.Clamp(0.05f - sinPhase * armSwing * 0.45f, -0.8f, 0.8f);
            muscles[(int)EHumanoidValue.RightArmFrontBack] = Math.Clamp(0.05f + sinPhase * armSwing * 0.45f, -0.8f, 0.8f);

            // Counter-phase leg stride, knee flexion, and subtle strafe abduction.
            float legRoll = strafeSign * (0.14f + 0.08f * cosPhase);
            muscles[(int)EHumanoidValue.LeftUpperLegFrontBack] = Math.Clamp(baseUpperLegFrontBack + sinPhase * strideSwing * 0.6f, -0.9f, 0.95f);
            muscles[(int)EHumanoidValue.RightUpperLegFrontBack] = Math.Clamp(baseUpperLegFrontBack - sinPhase * strideSwing * 0.6f, -0.9f, 0.95f);
            muscles[(int)EHumanoidValue.LeftUpperLegInOut] = Math.Clamp(0.05f + legRoll, -0.6f, 0.6f);
            muscles[(int)EHumanoidValue.RightUpperLegInOut] = Math.Clamp(0.05f - legRoll, -0.6f, 0.6f);
            muscles[(int)EHumanoidValue.LeftLowerLegStretch] = Math.Clamp(baseLowerLegStretch - MathF.Max(0.0f, cosPhase) * kneeBend * 0.7f, -0.9f, 1.0f);
            muscles[(int)EHumanoidValue.RightLowerLegStretch] = Math.Clamp(baseLowerLegStretch - MathF.Max(0.0f, -cosPhase) * kneeBend * 0.7f, -0.9f, 1.0f);

            float bob = -hipDrop + MathF.Abs(sinPhase) * hipDrop * 0.5f;
            var bodyPos = new Vector3(0.0f, baseHipsY + bob, 0.0f);
            sampler.SampleSomaPose(
                bodyPos,
                Quaternion.Identity,
                muscles,
                avatarLegScale: 1.0f,
                destination.AsSpan(frameBase, JointCount));
        }
    }
}
