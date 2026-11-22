using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Allocation-free inverse of <see cref="SomaToHumanoidMuscleConverter"/> that reconstructs
/// 30-joint SOMA local poses from canonical XRENGINE humanoid muscles and Body transforms.
/// Used to seed MotionBricks context slots when entering procedural locomotion from a
/// traditional <see cref="AnimationClip"/> state.
/// </summary>
public sealed class HumanoidToSomaPoseSampler
{
    private const float DegreesToRadians = MathF.PI / 180.0f;

    private readonly SomaRestSkeletonCalibration _calibration;
    private readonly Quaternion[] _canonicalLocalDeltas = new Quaternion[SomaModelContract.JointCount];
    private readonly Quaternion[] _worldDeltas = new Quaternion[SomaModelContract.JointCount];
    private readonly Quaternion[] _worldRotations = new Quaternion[SomaModelContract.JointCount];

    public HumanoidToSomaPoseSampler(SomaRestSkeletonCalibration? calibration = null)
        => _calibration = calibration ?? SomaRestSkeletonCalibration.CanonicalDefault;

    /// <summary>
    /// Converts one canonical humanoid muscle frame and Body transform into a 30-joint SOMA
    /// local pose without heap allocation.
    /// </summary>
    public void SampleSomaPose(
        Vector3 bodyPosition,
        Quaternion bodyRotation,
        ReadOnlySpan<float> muscles,
        float avatarLegScale,
        Span<SomaPoseTransform> destinationLocalPose)
    {
        if (destinationLocalPose.Length < SomaModelContract.JointCount)
            throw new ArgumentException($"Destination pose must hold at least {SomaModelContract.JointCount} joints.", nameof(destinationLocalPose));
        if (muscles.Length < MotionBricksEvaluatedPose.MuscleChannelCount)
            throw new ArgumentException($"Muscle span must contain at least {MotionBricksEvaluatedPose.MuscleChannelCount} channels.", nameof(muscles));

        float scale = float.IsFinite(avatarLegScale) && avatarLegScale > 0.01f ? avatarLegScale : 1.0f;

        for (int i = 0; i < SomaModelContract.JointCount; i++)
            _canonicalLocalDeltas[i] = Quaternion.Identity;

        // Spine chain: Spine(1), Chest(2), UpperChest(3).
        _canonicalLocalDeltas[1] = BuildVerticalDelta(
            muscles,
            EHumanoidValue.SpineTwistLeftRight,
            EHumanoidValue.SpineFrontBack,
            EHumanoidValue.SpineLeftRight,
            SomaToHumanoidMuscleConverter.SpineRange);
        _canonicalLocalDeltas[2] = BuildVerticalDelta(
            muscles,
            EHumanoidValue.ChestTwistLeftRight,
            EHumanoidValue.ChestFrontBack,
            EHumanoidValue.ChestLeftRight,
            SomaToHumanoidMuscleConverter.SpineRange);
        _canonicalLocalDeltas[3] = BuildVerticalDelta(
            muscles,
            EHumanoidValue.UpperChestTwistLeftRight,
            EHumanoidValue.UpperChestFrontBack,
            EHumanoidValue.UpperChestLeftRight,
            SomaToHumanoidMuscleConverter.SpineRange);

        // Split canonical Neck evenly across SOMA Neck1(4) and Neck2(5) so delta(4)*delta(5) == neckCombined.
        Quaternion neckCombined = BuildVerticalDelta(
            muscles,
            EHumanoidValue.NeckTurnLeftRight,
            EHumanoidValue.NeckNodDownUp,
            EHumanoidValue.NeckTiltLeftRight,
            SomaToHumanoidMuscleConverter.NeckHeadRange);
        Quaternion halfNeck = SomaPoseTransform.NormalizeOrIdentity(Quaternion.Slerp(Quaternion.Identity, neckCombined, 0.5f));
        _canonicalLocalDeltas[4] = halfNeck;
        _canonicalLocalDeltas[5] = halfNeck;

        _canonicalLocalDeltas[6] = BuildVerticalDelta(
            muscles,
            EHumanoidValue.HeadTurnLeftRight,
            EHumanoidValue.HeadNodDownUp,
            EHumanoidValue.HeadTiltLeftRight,
            SomaToHumanoidMuscleConverter.NeckHeadRange);

        // Arms: Left(10..13) and Right(16..19).
        BuildArmChain(isLeft: true, shoulderIdx: 10, armIdx: 11, foreArmIdx: 12, handIdx: 13, muscles);
        BuildArmChain(isLeft: false, shoulderIdx: 16, armIdx: 17, foreArmIdx: 18, handIdx: 19, muscles);

        // Legs: Left(22..25) and Right(26..29).
        BuildLegChain(isLeft: true, legIdx: 22, shinIdx: 23, footIdx: 24, toeIdx: 25, muscles);
        BuildLegChain(isLeft: false, legIdx: 26, shinIdx: 27, footIdx: 28, toeIdx: 29, muscles);

        // Reconstruct world deltas, world rotations, and SOMA local transforms in topological order.
        Quaternion[] restWorld = _calibration.RestWorldRotations;
        SomaPoseTransform[] refLocal = _calibration.ReferenceLocalPoses;
        int[] parents = SomaModelContract.ParentIndices;

        Vector3 somaHipsPos = bodyPosition / scale;
        if (!float.IsFinite(somaHipsPos.X) || !float.IsFinite(somaHipsPos.Y) || !float.IsFinite(somaHipsPos.Z))
            somaHipsPos = new Vector3(0.0f, _calibration.HipsHeightMeters, 0.0f);
        else if (somaHipsPos.Y <= 0.1f)
            somaHipsPos.Y = _calibration.HipsHeightMeters;

        _worldDeltas[0] = SomaPoseTransform.NormalizeOrIdentity(bodyRotation);
        _worldRotations[0] = SomaPoseTransform.NormalizeOrIdentity(_worldDeltas[0] * restWorld[0]);
        destinationLocalPose[0] = new SomaPoseTransform(somaHipsPos, _worldRotations[0]);

        for (int joint = 1; joint < SomaModelContract.JointCount; joint++)
        {
            int parent = parents[joint];
            _worldDeltas[joint] = SomaPoseTransform.NormalizeOrIdentity(_worldDeltas[parent] * _canonicalLocalDeltas[joint]);
            _worldRotations[joint] = SomaPoseTransform.NormalizeOrIdentity(_worldDeltas[joint] * restWorld[joint]);
            Quaternion localRot = SomaPoseTransform.NormalizeOrIdentity(
                Quaternion.Inverse(_worldRotations[parent]) * _worldRotations[joint]);
            destinationLocalPose[joint] = new SomaPoseTransform(refLocal[joint].Position, localRot);
        }
    }

    /// <summary>
    /// Converts four 30 Hz <see cref="HumanoidPoseHistoryFrame"/> snapshots into a flat
    /// <c>4 * 30</c> SOMA context pose buffer.
    /// </summary>
    public void SampleFourContextPoses(
        ReadOnlySpan<HumanoidPoseHistoryFrame> fourContextFrames,
        float avatarLegScale,
        Span<SomaPoseTransform> destinationFourSomaPoses)
    {
        if (fourContextFrames.Length < SomaModelContract.ContextPoseCount)
            throw new ArgumentException($"Expected {SomaModelContract.ContextPoseCount} humanoid context frames.", nameof(fourContextFrames));
        if (destinationFourSomaPoses.Length < SomaModelContract.ContextPoseCount * SomaModelContract.JointCount)
            throw new ArgumentException("Destination buffer is too small for four 30-joint SOMA poses.", nameof(destinationFourSomaPoses));

        for (int f = 0; f < SomaModelContract.ContextPoseCount; f++)
        {
            HumanoidPoseHistoryFrame frame = fourContextFrames[f];
            SampleSomaPose(
                frame.BodyPosition,
                frame.BodyRotation,
                frame.MuscleValues,
                avatarLegScale,
                destinationFourSomaPoses.Slice(f * SomaModelContract.JointCount, SomaModelContract.JointCount));
        }
    }

    /// <summary>
    /// Exact inverse of <see cref="SomaToHumanoidMuscleConverter.DegreesToNormalizedMuscle"/>.
    /// </summary>
    public static float NormalizedMuscleToDegrees(float muscle, Vector2 range)
    {
        if (!float.IsFinite(muscle) || MathF.Abs(muscle) <= 1.0e-6f)
            return 0.0f;

        return muscle >= 0.0f ? muscle * range.Y : -muscle * range.X;
    }

    /// <summary>
    /// Exact inverse of <see cref="SomaToHumanoidMuscleConverter.DecomposeSwingTwistDegrees"/>.
    /// </summary>
    public static Quaternion ComposeSwingTwistQuaternion(
        float frontBackDegrees,
        float twistDegrees,
        float leftRightDegrees,
        Quaternion jointBasis)
        => ComposeSwingTwistQuaternion(
            frontBackDegrees,
            twistDegrees,
            leftRightDegrees,
            jointBasis,
            Quaternion.Identity);

    internal static Quaternion ComposeSwingTwistQuaternion(
        float frontBackDegrees,
        float twistDegrees,
        float leftRightDegrees,
        Quaternion jointBasis,
        Quaternion neutralOffsetInBasis)
    {
        float twistHalf = 0.5f * twistDegrees * DegreesToRadians;
        var twist = new Quaternion(0.0f, MathF.Sin(twistHalf), 0.0f, MathF.Cos(twistHalf));

        float fbRad = frontBackDegrees * DegreesToRadians;
        float lrRad = leftRightDegrees * DegreesToRadians;
        float swingAngle = MathF.Sqrt(fbRad * fbRad + lrRad * lrRad);
        Quaternion swing;
        if (swingAngle > 1.0e-6f)
        {
            float sinHalfOverAngle = MathF.Sin(0.5f * swingAngle) / swingAngle;
            swing = new Quaternion(
                fbRad * sinHalfOverAngle,
                0.0f,
                lrRad * sinHalfOverAngle,
                MathF.Cos(0.5f * swingAngle));
        }
        else
        {
            swing = Quaternion.Identity;
        }

        Quaternion q = SomaPoseTransform.NormalizeOrIdentity(
            SomaPoseTransform.NormalizeOrIdentity(neutralOffsetInBasis) * swing * twist);
        Quaternion basisNorm = SomaPoseTransform.NormalizeOrIdentity(jointBasis);
        return SomaPoseTransform.NormalizeOrIdentity(basisNorm * q * Quaternion.Inverse(basisNorm));
    }

    private static Quaternion BuildVerticalDelta(
        ReadOnlySpan<float> muscles,
        EHumanoidValue twistMuscle,
        EHumanoidValue frontBackMuscle,
        EHumanoidValue leftRightMuscle,
        Vector2 range)
    {
        float twistDeg = NormalizedMuscleToDegrees(muscles[(int)twistMuscle], range);
        float frontBackDeg = -NormalizedMuscleToDegrees(muscles[(int)frontBackMuscle], range);
        float leftRightDeg = -NormalizedMuscleToDegrees(muscles[(int)leftRightMuscle], range);
        return ComposeSwingTwistQuaternion(frontBackDeg, twistDeg, leftRightDeg, SomaToHumanoidMuscleConverter.VerticalJointBasis);
    }

    private void BuildArmChain(
        bool isLeft,
        int shoulderIdx,
        int armIdx,
        int foreArmIdx,
        int handIdx,
        ReadOnlySpan<float> muscles)
    {
        float side = isLeft ? 1.0f : -1.0f;
        bool mecanim = _calibration.UseMecanimNeutralPostureOffsets;
        Quaternion basis = mecanim
            ? (isLeft ? SomaToHumanoidMuscleConverter.LeftArmMecanimBasis : SomaToHumanoidMuscleConverter.RightArmMecanimBasis)
            : (isLeft ? SomaToHumanoidMuscleConverter.LeftArmBasis : SomaToHumanoidMuscleConverter.RightArmBasis);
        Quaternion upperArmNeutral = mecanim
            ? (isLeft ? SomaToHumanoidMuscleConverter.LeftUpperArmNeutralOffset : SomaToHumanoidMuscleConverter.RightUpperArmNeutralOffset)
            : Quaternion.Identity;
        Quaternion lowerArmNeutral = mecanim
            ? (isLeft ? SomaToHumanoidMuscleConverter.LeftLowerArmNeutralOffset : SomaToHumanoidMuscleConverter.RightLowerArmNeutralOffset)
            : Quaternion.Identity;

        float shFrontBack = side * NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftShoulderFrontBack : EHumanoidValue.RightShoulderFrontBack)],
            SomaToHumanoidMuscleConverter.ShoulderFrontBackRange);
        float shLeftRight = NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftShoulderDownUp : EHumanoidValue.RightShoulderDownUp)],
            SomaToHumanoidMuscleConverter.ShoulderDownUpRange);
        _canonicalLocalDeltas[shoulderIdx] = ComposeSwingTwistQuaternion(shFrontBack, 0.0f, shLeftRight, basis);

        float armTwist = side * NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftArmTwistInOut : EHumanoidValue.RightArmTwistInOut)],
            SomaToHumanoidMuscleConverter.ArmTwistRange);
        float armFrontBack = side * NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftArmFrontBack : EHumanoidValue.RightArmFrontBack)],
            SomaToHumanoidMuscleConverter.ArmFrontBackRange);
        float armLeftRight = NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftArmDownUp : EHumanoidValue.RightArmDownUp)],
            SomaToHumanoidMuscleConverter.ArmDownUpRange);
        _canonicalLocalDeltas[armIdx] = ComposeSwingTwistQuaternion(armFrontBack, armTwist, armLeftRight, basis, upperArmNeutral);

        float foreTwist = side * NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftForearmTwistInOut : EHumanoidValue.RightForearmTwistInOut)],
            SomaToHumanoidMuscleConverter.ForearmTwistRange);
        float foreFrontBack = -side * NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftForearmStretch : EHumanoidValue.RightForearmStretch)],
            SomaToHumanoidMuscleConverter.ForearmStretchRange);
        _canonicalLocalDeltas[foreArmIdx] = ComposeSwingTwistQuaternion(foreFrontBack, foreTwist, 0.0f, basis, lowerArmNeutral);

        float handFrontBack = side * NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftHandInOut : EHumanoidValue.RightHandInOut)],
            SomaToHumanoidMuscleConverter.HandInOutRange);
        float handLeftRight = NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftHandDownUp : EHumanoidValue.RightHandDownUp)],
            SomaToHumanoidMuscleConverter.HandDownUpRange);
        _canonicalLocalDeltas[handIdx] = ComposeSwingTwistQuaternion(handFrontBack, 0.0f, handLeftRight, basis);
    }

    private void BuildLegChain(
        bool isLeft,
        int legIdx,
        int shinIdx,
        int footIdx,
        int toeIdx,
        ReadOnlySpan<float> muscles)
    {
        float side = isLeft ? 1.0f : -1.0f;
        bool mecanim = _calibration.UseMecanimNeutralPostureOffsets;
        Quaternion basis = SomaToHumanoidMuscleConverter.VerticalJointBasis;
        Quaternion upperLegNeutral = mecanim ? SomaToHumanoidMuscleConverter.UpperLegNeutralOffset : Quaternion.Identity;
        Quaternion lowerLegNeutral = mecanim ? SomaToHumanoidMuscleConverter.LowerLegNeutralOffset : Quaternion.Identity;

        float upTwist = NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftUpperLegTwistInOut : EHumanoidValue.RightUpperLegTwistInOut)],
            isLeft ? SomaToHumanoidMuscleConverter.UpperLegTwistRange : SomaToHumanoidMuscleConverter.RightUpperLegTwistRange);
        float upFrontBack = -NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftUpperLegFrontBack : EHumanoidValue.RightUpperLegFrontBack)],
            SomaToHumanoidMuscleConverter.UpperLegFrontBackRange);
        float upLeftRight = -side * NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftUpperLegInOut : EHumanoidValue.RightUpperLegInOut)],
            SomaToHumanoidMuscleConverter.UpperLegInOutRange);
        _canonicalLocalDeltas[legIdx] = ComposeSwingTwistQuaternion(upFrontBack, upTwist, upLeftRight, basis, upperLegNeutral);

        float lowTwist = NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftLowerLegTwistInOut : EHumanoidValue.RightLowerLegTwistInOut)],
            isLeft ? SomaToHumanoidMuscleConverter.LowerLegTwistRange : SomaToHumanoidMuscleConverter.RightLowerLegTwistRange);
        float lowFrontBack = -NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftLowerLegStretch : EHumanoidValue.RightLowerLegStretch)],
            SomaToHumanoidMuscleConverter.LowerLegStretchRange);
        _canonicalLocalDeltas[shinIdx] = ComposeSwingTwistQuaternion(lowFrontBack, lowTwist, 0.0f, basis, lowerLegNeutral);

        float footTwist = side * NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftFootTwistInOut : EHumanoidValue.RightFootTwistInOut)],
            SomaToHumanoidMuscleConverter.FootTwistRange);
        float footFrontBack = -NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftFootUpDown : EHumanoidValue.RightFootUpDown)],
            SomaToHumanoidMuscleConverter.FootUpDownRange);
        _canonicalLocalDeltas[footIdx] = ComposeSwingTwistQuaternion(footFrontBack, footTwist, 0.0f, basis);

        float toeFrontBack = -NormalizedMuscleToDegrees(
            muscles[(int)(isLeft ? EHumanoidValue.LeftToesUpDown : EHumanoidValue.RightToesUpDown)],
            SomaToHumanoidMuscleConverter.ToesUpDownRange);
        _canonicalLocalDeltas[toeIdx] = ComposeSwingTwistQuaternion(toeFrontBack, 0.0f, 0.0f, basis);
    }
}
