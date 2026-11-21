using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Allocation-free converter from decoded 30-joint SOMA local poses to canonical XRENGINE
/// normalized humanoid muscles (<see cref="EHumanoidValue"/>) and scaled Body root poses.
/// Uses the exact inverse of <c>HumanoidComponent.NativePoseSolver</c> and
/// <c>HumanoidSettings</c> canonical muscle ranges.
/// </summary>
public sealed class SomaToHumanoidMuscleConverter
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;

    // Canonical default muscle degree ranges matching HumanoidSettings.cs.
    private static readonly Vector2 SpineRange = new(-40.0f, 40.0f);
    private static readonly Vector2 NeckHeadRange = new(-40.0f, 40.0f);
    private static readonly Vector2 ShoulderDownUpRange = new(15.0f, -30.0f);
    private static readonly Vector2 ShoulderFrontBackRange = new(15.0f, -15.0f);
    private static readonly Vector2 ArmTwistRange = new(-90.0f, 90.0f);
    private static readonly Vector2 ArmDownUpRange = new(60.0f, -100.0f);
    private static readonly Vector2 ArmFrontBackRange = new(100.0f, -100.0f);
    private static readonly Vector2 ForearmTwistRange = new(-90.0f, 90.0f);
    private static readonly Vector2 ForearmStretchRange = new(-80.0f, 80.0f);
    private static readonly Vector2 HandDownUpRange = new(80.0f, -80.0f);
    private static readonly Vector2 HandInOutRange = new(40.0f, -40.0f);
    private static readonly Vector2 UpperLegTwistRange = new(-60.0f, 60.0f);
    private static readonly Vector2 RightUpperLegTwistRange = new(60.0f, -60.0f);
    private static readonly Vector2 UpperLegFrontBackRange = new(90.0f, -50.0f);
    private static readonly Vector2 UpperLegInOutRange = new(60.0f, -60.0f);
    private static readonly Vector2 LowerLegTwistRange = new(-90.0f, 90.0f);
    private static readonly Vector2 RightLowerLegTwistRange = new(90.0f, -90.0f);
    private static readonly Vector2 LowerLegStretchRange = new(-80.0f, 80.0f);
    private static readonly Vector2 FootTwistRange = new(30.0f, -30.0f);
    private static readonly Vector2 FootUpDownRange = new(50.0f, -50.0f);
    private static readonly Vector2 ToesUpDownRange = new(50.0f, -50.0f);

    // Precomputed basis rotations that map canonical character axes into each joint's
    // (X = frontBack, Y = twist, Z = leftRight) decomposition frame.
    private static readonly Quaternion VerticalJointBasis = Quaternion.Identity;
    private static readonly Quaternion LeftArmBasis = CreateBasisRotation(
        frontBackAxis: Vector3.UnitY,
        twistAxis: Vector3.UnitX,
        leftRightAxis: -Vector3.UnitZ);
    private static readonly Quaternion RightArmBasis = CreateBasisRotation(
        frontBackAxis: Vector3.UnitY,
        twistAxis: -Vector3.UnitX,
        leftRightAxis: Vector3.UnitZ);

    private readonly SomaRestSkeletonCalibration _calibration;
    private readonly Quaternion[] _worldRotations = new Quaternion[SomaModelContract.JointCount];
    private readonly Quaternion[] _worldDeltas = new Quaternion[SomaModelContract.JointCount];

    public SomaToHumanoidMuscleConverter(SomaRestSkeletonCalibration? calibration = null)
        => _calibration = calibration ?? SomaRestSkeletonCalibration.CanonicalDefault;

    /// <summary>
    /// Converts one 30-joint SOMA local pose frame into normalized humanoid muscles and a
    /// proportional Body pose.
    /// </summary>
    public void ConvertFrame(
        ReadOnlySpan<SomaPoseTransform> localPose,
        float avatarLegScale,
        Span<float> destinationMuscles,
        out Vector3 bodyPosition,
        out Quaternion bodyRotation)
    {
        if (localPose.Length < SomaModelContract.JointCount)
            throw new ArgumentException($"Local pose must contain {SomaModelContract.JointCount} joints.", nameof(localPose));
        if (destinationMuscles.Length < MotionBricksEvaluatedPose.MuscleChannelCount)
            throw new ArgumentException($"Destination muscle span must have at least {MotionBricksEvaluatedPose.MuscleChannelCount} channels.", nameof(destinationMuscles));

        float scale = float.IsFinite(avatarLegScale) && avatarLegScale > 0.01f ? avatarLegScale : 1.0f;

        // Compose world orientations and extract rest-relative character-space deltas.
        _worldRotations[0] = SomaPoseTransform.NormalizeOrIdentity(localPose[0].Rotation);
        _worldDeltas[0] = SomaPoseTransform.NormalizeOrIdentity(_worldRotations[0] * _calibration.InverseRestWorldRotations[0]);

        int[] parents = SomaModelContract.ParentIndices;
        Quaternion[] invRestWorld = _calibration.InverseRestWorldRotations;
        for (int joint = 1; joint < SomaModelContract.JointCount; joint++)
        {
            Quaternion localRot = SomaPoseTransform.NormalizeOrIdentity(localPose[joint].Rotation);
            _worldRotations[joint] = SomaPoseTransform.NormalizeOrIdentity(_worldRotations[parents[joint]] * localRot);
            _worldDeltas[joint] = SomaPoseTransform.NormalizeOrIdentity(_worldRotations[joint] * invRestWorld[joint]);
        }

        // Body root in humanoid meters scaled by target avatar leg proportions.
        bodyPosition = localPose[0].Position * scale;
        bodyRotation = _worldDeltas[0];

        // Spine chain: SOMA Spine1(1), Spine2(2), Chest(3) -> Spine, Chest, UpperChest.
        ConvertVerticalRole(
            ComputeCanonicalLocalDelta(1, 0),
            destinationMuscles,
            EHumanoidValue.SpineTwistLeftRight,
            EHumanoidValue.SpineFrontBack,
            EHumanoidValue.SpineLeftRight,
            SpineRange);
        ConvertVerticalRole(
            ComputeCanonicalLocalDelta(2, 1),
            destinationMuscles,
            EHumanoidValue.ChestTwistLeftRight,
            EHumanoidValue.ChestFrontBack,
            EHumanoidValue.ChestLeftRight,
            SpineRange);
        ConvertVerticalRole(
            ComputeCanonicalLocalDelta(3, 2),
            destinationMuscles,
            EHumanoidValue.UpperChestTwistLeftRight,
            EHumanoidValue.UpperChestFrontBack,
            EHumanoidValue.UpperChestLeftRight,
            SpineRange);

        // Combine SOMA Neck1(4) and Neck2(5) into canonical Neck, then Head(6).
        Quaternion neckCombined = SomaPoseTransform.NormalizeOrIdentity(
            ComputeCanonicalLocalDelta(4, 3) * ComputeCanonicalLocalDelta(5, 4));
        ConvertVerticalRole(
            neckCombined,
            destinationMuscles,
            EHumanoidValue.NeckTurnLeftRight,
            EHumanoidValue.NeckNodDownUp,
            EHumanoidValue.NeckTiltLeftRight,
            NeckHeadRange);
        ConvertVerticalRole(
            ComputeCanonicalLocalDelta(6, 5),
            destinationMuscles,
            EHumanoidValue.HeadTurnLeftRight,
            EHumanoidValue.HeadNodDownUp,
            EHumanoidValue.HeadTiltLeftRight,
            NeckHeadRange);

        // Arms: Left (10..13) and Right (16..19).
        ConvertArmChain(isLeft: true, shoulderIdx: 10, armIdx: 11, foreArmIdx: 12, handIdx: 13, destinationMuscles);
        ConvertArmChain(isLeft: false, shoulderIdx: 16, armIdx: 17, foreArmIdx: 18, handIdx: 19, destinationMuscles);

        // Legs: Left (22..25) and Right (26..29).
        ConvertLegChain(isLeft: true, legIdx: 22, shinIdx: 23, footIdx: 24, toeIdx: 25, destinationMuscles);
        ConvertLegChain(isLeft: false, legIdx: 26, shinIdx: 27, footIdx: 28, toeIdx: 29, destinationMuscles);
    }

    /// <summary>
    /// Exact inverse of <c>HumanoidComponent.GetMuscleDegrees</c> for both symmetric and
    /// sign-inverted asymmetric muscle ranges.
    /// </summary>
    public static float DegreesToNormalizedMuscle(float degrees, Vector2 range)
    {
        if (!float.IsFinite(degrees) || MathF.Abs(degrees) <= 1.0e-6f)
            return 0.0f;

        bool matchesPositiveBranch = (degrees > 0.0f && range.Y > 0.0f)
            || (degrees < 0.0f && range.Y < 0.0f);
        if (matchesPositiveBranch && MathF.Abs(range.Y) > 1.0e-5f)
            return Math.Clamp(degrees / range.Y, -2.0f, 2.0f);

        if (MathF.Abs(range.X) > 1.0e-5f)
            return Math.Clamp(-degrees / range.X, -2.0f, 2.0f);

        return 0.0f;
    }

    /// <summary>
    /// Decomposes a canonical delta quaternion into <c>(frontBackDegrees, twistDegrees, leftRightDegrees)</c>
    /// via swing-twist around the basis Y axis, matching <c>CompiledHumanoidPoseSolver</c>.
    /// </summary>
    public static void DecomposeSwingTwistDegrees(
        Quaternion canonicalDelta,
        Quaternion jointBasis,
        out float frontBackDegrees,
        out float twistDegrees,
        out float leftRightDegrees)
    {
        // Express canonicalDelta in the joint's (X=frontBack, Y=twist, Z=leftRight) frame.
        Quaternion basisNorm = SomaPoseTransform.NormalizeOrIdentity(jointBasis);
        Quaternion q = SomaPoseTransform.NormalizeOrIdentity(
            Quaternion.Inverse(basisNorm) * SomaPoseTransform.NormalizeOrIdentity(canonicalDelta) * basisNorm);
        if (q.W < 0.0f)
            q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);

        // Extract Y-axis twist component: q = swing * twist.
        Quaternion twist = SomaPoseTransform.NormalizeOrIdentity(new Quaternion(0.0f, q.Y, 0.0f, q.W));
        if (twist.W < 0.0f)
            twist = new Quaternion(-twist.X, -twist.Y, -twist.Z, -twist.W);

        Quaternion swing = SomaPoseTransform.NormalizeOrIdentity(q * Quaternion.Inverse(twist));
        if (swing.W < 0.0f)
            swing = new Quaternion(-swing.X, -swing.Y, -swing.Z, -swing.W);

        twistDegrees = 2.0f * MathF.Atan2(twist.Y, twist.W) * RadiansToDegrees;

        float sinHalf = MathF.Sqrt(swing.X * swing.X + swing.Z * swing.Z);
        if (sinHalf <= 1.0e-6f)
        {
            frontBackDegrees = 0.0f;
            leftRightDegrees = 0.0f;
            return;
        }

        float swingRadians = 2.0f * MathF.Atan2(sinHalf, swing.W);
        float scale = (swingRadians / sinHalf) * RadiansToDegrees;
        frontBackDegrees = swing.X * scale;
        leftRightDegrees = swing.Z * scale;
    }

    private Quaternion ComputeCanonicalLocalDelta(int jointIndex, int parentIndex)
        => SomaPoseTransform.NormalizeOrIdentity(Quaternion.Inverse(_worldDeltas[parentIndex]) * _worldDeltas[jointIndex]);

    private static void ConvertVerticalRole(
        Quaternion delta,
        Span<float> muscles,
        EHumanoidValue twistMuscle,
        EHumanoidValue frontBackMuscle,
        EHumanoidValue leftRightMuscle,
        Vector2 range)
    {
        DecomposeSwingTwistDegrees(delta, VerticalJointBasis, out float frontBackDeg, out float twistDeg, out float leftRightDeg);

        // NativePoseSolver.StageRole applies twistSign=+1, frontBackSign=-1, leftRightSign=-1.
        muscles[(int)twistMuscle] = DegreesToNormalizedMuscle(twistDeg, range);
        muscles[(int)frontBackMuscle] = DegreesToNormalizedMuscle(-frontBackDeg, range);
        muscles[(int)leftRightMuscle] = DegreesToNormalizedMuscle(-leftRightDeg, range);
    }

    private void ConvertArmChain(
        bool isLeft,
        int shoulderIdx,
        int armIdx,
        int foreArmIdx,
        int handIdx,
        Span<float> muscles)
    {
        float side = isLeft ? 1.0f : -1.0f;
        Quaternion basis = isLeft ? LeftArmBasis : RightArmBasis;

        DecomposeSwingTwistDegrees(ComputeCanonicalLocalDelta(shoulderIdx, 3), basis, out float shFrontBack, out _, out float shLeftRight);
        muscles[(int)(isLeft ? EHumanoidValue.LeftShoulderFrontBack : EHumanoidValue.RightShoulderFrontBack)] =
            DegreesToNormalizedMuscle(side * shFrontBack, ShoulderFrontBackRange);
        muscles[(int)(isLeft ? EHumanoidValue.LeftShoulderDownUp : EHumanoidValue.RightShoulderDownUp)] =
            DegreesToNormalizedMuscle(shLeftRight, ShoulderDownUpRange);

        DecomposeSwingTwistDegrees(ComputeCanonicalLocalDelta(armIdx, shoulderIdx), basis, out float armFrontBack, out float armTwist, out float armLeftRight);
        muscles[(int)(isLeft ? EHumanoidValue.LeftArmTwistInOut : EHumanoidValue.RightArmTwistInOut)] =
            DegreesToNormalizedMuscle(side * armTwist, ArmTwistRange);
        muscles[(int)(isLeft ? EHumanoidValue.LeftArmFrontBack : EHumanoidValue.RightArmFrontBack)] =
            DegreesToNormalizedMuscle(side * armFrontBack, ArmFrontBackRange);
        muscles[(int)(isLeft ? EHumanoidValue.LeftArmDownUp : EHumanoidValue.RightArmDownUp)] =
            DegreesToNormalizedMuscle(armLeftRight, ArmDownUpRange);

        DecomposeSwingTwistDegrees(ComputeCanonicalLocalDelta(foreArmIdx, armIdx), basis, out float foreFrontBack, out float foreTwist, out _);
        muscles[(int)(isLeft ? EHumanoidValue.LeftForearmTwistInOut : EHumanoidValue.RightForearmTwistInOut)] =
            DegreesToNormalizedMuscle(side * foreTwist, ForearmTwistRange);
        muscles[(int)(isLeft ? EHumanoidValue.LeftForearmStretch : EHumanoidValue.RightForearmStretch)] =
            DegreesToNormalizedMuscle(-side * foreFrontBack, ForearmStretchRange);

        DecomposeSwingTwistDegrees(ComputeCanonicalLocalDelta(handIdx, foreArmIdx), basis, out float handFrontBack, out _, out float handLeftRight);
        muscles[(int)(isLeft ? EHumanoidValue.LeftHandInOut : EHumanoidValue.RightHandInOut)] =
            DegreesToNormalizedMuscle(side * handFrontBack, HandInOutRange);
        muscles[(int)(isLeft ? EHumanoidValue.LeftHandDownUp : EHumanoidValue.RightHandDownUp)] =
            DegreesToNormalizedMuscle(handLeftRight, HandDownUpRange);
    }

    private void ConvertLegChain(
        bool isLeft,
        int legIdx,
        int shinIdx,
        int footIdx,
        int toeIdx,
        Span<float> muscles)
    {
        float side = isLeft ? 1.0f : -1.0f;

        DecomposeSwingTwistDegrees(ComputeCanonicalLocalDelta(legIdx, 0), VerticalJointBasis, out float upFrontBack, out float upTwist, out float upLeftRight);
        muscles[(int)(isLeft ? EHumanoidValue.LeftUpperLegTwistInOut : EHumanoidValue.RightUpperLegTwistInOut)] =
            DegreesToNormalizedMuscle(upTwist, isLeft ? UpperLegTwistRange : RightUpperLegTwistRange);
        muscles[(int)(isLeft ? EHumanoidValue.LeftUpperLegFrontBack : EHumanoidValue.RightUpperLegFrontBack)] =
            DegreesToNormalizedMuscle(-upFrontBack, UpperLegFrontBackRange);
        muscles[(int)(isLeft ? EHumanoidValue.LeftUpperLegInOut : EHumanoidValue.RightUpperLegInOut)] =
            DegreesToNormalizedMuscle(-side * upLeftRight, UpperLegInOutRange);

        DecomposeSwingTwistDegrees(ComputeCanonicalLocalDelta(shinIdx, legIdx), VerticalJointBasis, out float lowFrontBack, out float lowTwist, out _);
        muscles[(int)(isLeft ? EHumanoidValue.LeftLowerLegTwistInOut : EHumanoidValue.RightLowerLegTwistInOut)] =
            DegreesToNormalizedMuscle(lowTwist, isLeft ? LowerLegTwistRange : RightLowerLegTwistRange);
        muscles[(int)(isLeft ? EHumanoidValue.LeftLowerLegStretch : EHumanoidValue.RightLowerLegStretch)] =
            DegreesToNormalizedMuscle(-lowFrontBack, LowerLegStretchRange);

        DecomposeSwingTwistDegrees(ComputeCanonicalLocalDelta(footIdx, shinIdx), VerticalJointBasis, out float footFrontBack, out float footTwist, out _);
        muscles[(int)(isLeft ? EHumanoidValue.LeftFootTwistInOut : EHumanoidValue.RightFootTwistInOut)] =
            DegreesToNormalizedMuscle(side * footTwist, FootTwistRange);
        muscles[(int)(isLeft ? EHumanoidValue.LeftFootUpDown : EHumanoidValue.RightFootUpDown)] =
            DegreesToNormalizedMuscle(-footFrontBack, FootUpDownRange);

        DecomposeSwingTwistDegrees(ComputeCanonicalLocalDelta(toeIdx, footIdx), VerticalJointBasis, out float toeFrontBack, out _, out _);
        muscles[(int)(isLeft ? EHumanoidValue.LeftToesUpDown : EHumanoidValue.RightToesUpDown)] =
            DegreesToNormalizedMuscle(-toeFrontBack, ToesUpDownRange);
    }

    private static Quaternion CreateBasisRotation(Vector3 frontBackAxis, Vector3 twistAxis, Vector3 leftRightAxis)
    {
        // Rows map UnitX -> frontBackAxis, UnitY -> twistAxis, UnitZ -> leftRightAxis in System.Numerics.
        var matrix = new Matrix4x4(
            frontBackAxis.X, frontBackAxis.Y, frontBackAxis.Z, 0.0f,
            twistAxis.X,     twistAxis.Y,     twistAxis.Z,     0.0f,
            leftRightAxis.X, leftRightAxis.Y, leftRightAxis.Z, 0.0f,
            0.0f,            0.0f,            0.0f,            1.0f);
        return SomaPoseTransform.NormalizeOrIdentity(Quaternion.CreateFromRotationMatrix(matrix));
    }
}
