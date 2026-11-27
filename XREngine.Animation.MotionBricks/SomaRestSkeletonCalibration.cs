using System.Numerics;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Holds the fixed SOMA 30-joint canonical T-pose rest hierarchy and segment lengths
/// used to retarget SOMA poses into canonical XRENGINE humanoid muscles without
/// per-avatar calibration.
/// </summary>
public sealed class SomaRestSkeletonCalibration
{
    /// <summary>
    /// Nominal SOMA hips standing height in meters (101.28 cm in the upstream SOMA rig).
    /// </summary>
    public const float ReferenceHipsHeightMeters = 1.0128f;

    /// <summary>
    /// Default canonical calibration where every SOMA joint's rest world orientation is
    /// aligned with the character frame (+X left, +Y up, +Z forward).
    /// </summary>
    public static SomaRestSkeletonCalibration CanonicalDefault { get; } = CreateCanonicalDefault();

    /// <summary>
    /// Official NVIDIA SOMA N1 30-joint rig calibration derived from the SOMA BVH hierarchy,
    /// where bone-local X runs along each segment (e.g., spine +X = world +Y, left leg +X =
    /// world -Y, right leg -X = world -Y). Use this calibration with real SOMA ONNX/TensorRT weights.
    /// </summary>
    public static SomaRestSkeletonCalibration SomaN1Default { get; } = CreateSomaN1Default();

    /// <summary>
    /// Local reference transforms for all 30 SOMA joints in meters.
    /// </summary>
    public SomaPoseTransform[] ReferenceLocalPoses { get; }

    /// <summary>
    /// Global rest-pose orientations for all 30 SOMA joints in SOMA model space.
    /// </summary>
    public Quaternion[] RestWorldRotations { get; }

    /// <summary>
    /// Precomputed inverses of <see cref="RestWorldRotations"/> for allocation-free retargeting.
    /// </summary>
    public Quaternion[] InverseRestWorldRotations { get; }

    /// <summary>
    /// Hips standing height of this calibration in meters, used to scale stride length
    /// proportionally to the target avatar's leg height.
    /// </summary>
    public float HipsHeightMeters { get; }

    /// <summary>
    /// When true, <see cref="SomaToHumanoidMuscleConverter"/> and <see cref="HumanoidToSomaPoseSampler"/>
    /// account for XRENGINE's Mecanim zero-muscle neutral posture offsets (30 deg thigh pitch,
    /// 80 deg knee/elbow flexion, 40 deg arm drop, 30 deg arm forward) so straight limbs map to
    /// their exact Mecanim muscle values (such as +1.0 for straight knees and elbows).
    /// </summary>
    public bool UseMecanimNeutralPostureOffsets { get; }

    public SomaRestSkeletonCalibration(
        SomaPoseTransform[] referenceLocalPoses,
        Quaternion[] restWorldRotations,
        float hipsHeightMeters = ReferenceHipsHeightMeters,
        bool useMecanimNeutralPostureOffsets = false)
    {
        if (referenceLocalPoses.Length != SomaModelContract.JointCount)
            throw new ArgumentException($"Expected {SomaModelContract.JointCount} reference local poses.", nameof(referenceLocalPoses));
        if (restWorldRotations.Length != SomaModelContract.JointCount)
            throw new ArgumentException($"Expected {SomaModelContract.JointCount} rest world rotations.", nameof(restWorldRotations));

        ReferenceLocalPoses = referenceLocalPoses;
        RestWorldRotations = restWorldRotations;
        InverseRestWorldRotations = new Quaternion[SomaModelContract.JointCount];
        for (int i = 0; i < SomaModelContract.JointCount; i++)
            InverseRestWorldRotations[i] = Quaternion.Inverse(SomaPoseTransform.NormalizeOrIdentity(restWorldRotations[i]));

        HipsHeightMeters = float.IsFinite(hipsHeightMeters) && hipsHeightMeters > 0.1f
            ? hipsHeightMeters
            : ReferenceHipsHeightMeters;
        UseMecanimNeutralPostureOffsets = useMecanimNeutralPostureOffsets;
    }

    private static SomaRestSkeletonCalibration CreateCanonicalDefault()
    {
        var localPoses = new SomaPoseTransform[SomaModelContract.JointCount];
        var worldRotations = new Quaternion[SomaModelContract.JointCount];
        for (int i = 0; i < SomaModelContract.JointCount; i++)
        {
            localPoses[i] = SomaPoseTransform.Identity;
            worldRotations[i] = Quaternion.Identity;
        }

        // Approximate SOMA segment lengths converted from centimeters to meters in Y-up +Z-forward canonical T-pose.
        localPoses[0] = new SomaPoseTransform(new Vector3(0.0f, ReferenceHipsHeightMeters, 0.0f), Quaternion.Identity);
        localPoses[1] = new SomaPoseTransform(new Vector3(0.0f, 0.050f, 0.0f), Quaternion.Identity);   // Spine1
        localPoses[2] = new SomaPoseTransform(new Vector3(0.0f, 0.071f, 0.0f), Quaternion.Identity);   // Spine2
        localPoses[3] = new SomaPoseTransform(new Vector3(0.0f, 0.076f, 0.0f), Quaternion.Identity);   // Chest
        localPoses[4] = new SomaPoseTransform(new Vector3(0.0f, 0.263f, 0.0f), Quaternion.Identity);   // Neck1
        localPoses[5] = new SomaPoseTransform(new Vector3(0.0f, 0.080f, 0.0f), Quaternion.Identity);   // Neck2
        localPoses[6] = new SomaPoseTransform(new Vector3(0.0f, 0.064f, 0.0f), Quaternion.Identity);   // Head
        localPoses[7] = new SomaPoseTransform(new Vector3(0.0f, 0.031f, 0.04f), Quaternion.Identity);  // Jaw
        localPoses[8] = new SomaPoseTransform(new Vector3(0.032f, 0.082f, 0.045f), Quaternion.Identity);  // LeftEye
        localPoses[9] = new SomaPoseTransform(new Vector3(-0.032f, 0.082f, 0.045f), Quaternion.Identity); // RightEye

        localPoses[10] = new SomaPoseTransform(new Vector3(0.052f, 0.232f, 0.016f), Quaternion.Identity);  // LeftShoulder
        localPoses[11] = new SomaPoseTransform(new Vector3(0.159f, 0.0f, 0.0f), Quaternion.Identity);      // LeftArm
        localPoses[12] = new SomaPoseTransform(new Vector3(0.287f, 0.0f, 0.0f), Quaternion.Identity);      // LeftForeArm
        localPoses[13] = new SomaPoseTransform(new Vector3(0.271f, 0.0f, 0.0f), Quaternion.Identity);      // LeftHand
        localPoses[14] = new SomaPoseTransform(new Vector3(0.085f, 0.0f, 0.045f), Quaternion.Identity);    // LeftHandThumbEnd
        localPoses[15] = new SomaPoseTransform(new Vector3(0.165f, 0.0f, 0.0f), Quaternion.Identity);      // LeftHandMiddleEnd

        localPoses[16] = new SomaPoseTransform(new Vector3(-0.052f, 0.232f, 0.016f), Quaternion.Identity); // RightShoulder
        localPoses[17] = new SomaPoseTransform(new Vector3(-0.159f, 0.0f, 0.0f), Quaternion.Identity);     // RightArm
        localPoses[18] = new SomaPoseTransform(new Vector3(-0.287f, 0.0f, 0.0f), Quaternion.Identity);     // RightForeArm
        localPoses[19] = new SomaPoseTransform(new Vector3(-0.271f, 0.0f, 0.0f), Quaternion.Identity);     // RightHand
        localPoses[20] = new SomaPoseTransform(new Vector3(-0.085f, 0.0f, 0.045f), Quaternion.Identity);   // RightHandThumbEnd
        localPoses[21] = new SomaPoseTransform(new Vector3(-0.165f, 0.0f, 0.0f), Quaternion.Identity);     // RightHandMiddleEnd

        localPoses[22] = new SomaPoseTransform(new Vector3(0.10f, -0.05f, 0.0f), Quaternion.Identity);   // LeftLeg
        localPoses[23] = new SomaPoseTransform(new Vector3(0.0f, -0.44f, 0.0f), Quaternion.Identity);    // LeftShin
        localPoses[24] = new SomaPoseTransform(new Vector3(0.0f, -0.43f, 0.0f), Quaternion.Identity);    // LeftFoot
        localPoses[25] = new SomaPoseTransform(new Vector3(0.0f, -0.09f, 0.14f), Quaternion.Identity);   // LeftToeBase

        localPoses[26] = new SomaPoseTransform(new Vector3(-0.10f, -0.05f, 0.0f), Quaternion.Identity);  // RightLeg
        localPoses[27] = new SomaPoseTransform(new Vector3(0.0f, -0.44f, 0.0f), Quaternion.Identity);    // RightShin
        localPoses[28] = new SomaPoseTransform(new Vector3(0.0f, -0.43f, 0.0f), Quaternion.Identity);    // RightFoot
        localPoses[29] = new SomaPoseTransform(new Vector3(0.0f, -0.09f, 0.14f), Quaternion.Identity);   // RightToeBase

        return new SomaRestSkeletonCalibration(localPoses, worldRotations, ReferenceHipsHeightMeters);
    }

    private static SomaRestSkeletonCalibration CreateSomaN1Default()
    {
        // T-pose rest world orientations for the official NVIDIA SOMA N1 rig (+X left, +Y up, +Z forward).
        // SOMA's standing ankle-to-toe foot axis slopes ~17.5 deg downward from ankle to ball of foot
        // and toe-base slopes ~11.5 deg downward; matching these rest axes keeps flat standing idle at
        // zero FootUpDown and ToesUpDown instead of pitching the avatar onto her toes.
        Quaternion spineRest = CreateBasisQuaternion(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX);
        Quaternion leftArmRest = CreateBasisQuaternion(Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY);
        Quaternion rightArmRest = CreateBasisQuaternion(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY);
        Quaternion leftLegRest = CreateBasisQuaternion(-Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitX);
        Quaternion leftFootRest = CreateBasisQuaternion(
            new Vector3(0.0f, -0.300706f, 0.953717f),
            new Vector3(0.0f, -0.953717f, -0.300706f),
            Vector3.UnitX);
        Quaternion leftToeRest = CreateBasisQuaternion(
            new Vector3(0.0f, -0.199368f, 0.979925f),
            new Vector3(0.0f, -0.979925f, -0.199368f),
            Vector3.UnitX);
        Quaternion rightLegRest = CreateBasisQuaternion(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX);
        Quaternion rightFootRest = CreateBasisQuaternion(
            new Vector3(0.0f, 0.300706f, -0.953717f),
            new Vector3(0.0f, 0.953717f, 0.300706f),
            Vector3.UnitX);
        Quaternion rightToeRest = CreateBasisQuaternion(
            new Vector3(0.0f, 0.199368f, -0.979925f),
            new Vector3(0.0f, 0.979925f, 0.199368f),
            Vector3.UnitX);

        var worldRotations = new Quaternion[SomaModelContract.JointCount];
        for (int i = 0; i <= 9; i++)
            worldRotations[i] = spineRest;
        for (int i = 10; i <= 15; i++)
            worldRotations[i] = leftArmRest;
        for (int i = 16; i <= 21; i++)
            worldRotations[i] = rightArmRest;
        worldRotations[22] = leftLegRest;
        worldRotations[23] = leftLegRest;
        worldRotations[24] = leftFootRest;
        worldRotations[25] = leftToeRest;
        worldRotations[26] = rightLegRest;
        worldRotations[27] = rightLegRest;
        worldRotations[28] = rightFootRest;
        worldRotations[29] = rightToeRest;

        // Bone-local segment offsets in meters from the SOMA N1 BVH skeleton hierarchy.
        Vector3[] localOffsets =
        [
            new(0.0f, 1.012816f, 0.0f),              // 0  Hips
            new(0.050039f, -0.000371f, -0.000137f),  // 1  Spine1
            new(0.071254f, 0.0f, 0.0f),              // 2  Spine2
            new(0.075940f, 0.0f, 0.0f),              // 3  Chest
            new(0.263131f, -0.004603f, -0.001817f),  // 4  Neck1
            new(0.080459f, 0.0f, 0.0f),              // 5  Neck2
            new(0.064328f, 0.0f, 0.0f),              // 6  Head
            new(0.001087f, 0.031294f, 0.000026f),    // 7  Jaw
            new(0.044517f, 0.081664f, 0.032064f),    // 8  LeftEye
            new(0.044369f, 0.081358f, -0.032224f),   // 9  RightEye
            new(0.232189f, 0.051956f, 0.016217f),    // 10 LeftShoulder
            new(0.159021f, 0.0f, 0.0f),              // 11 LeftArm
            new(0.287393f, 0.0f, 0.0f),              // 12 LeftForeArm
            new(0.270940f, 0.0f, 0.0f),              // 13 LeftHand
            new(0.129614f, 0.033803f, 0.008338f),    // 14 LeftHandThumbEnd
            new(0.191158f, 0.009450f, -0.004061f),   // 15 LeftHandMiddleEnd
            new(0.231617f, 0.052961f, -0.013801f),   // 16 RightShoulder
            new(-0.160272f, 0.0f, 0.0f),             // 17 RightArm
            new(-0.287366f, 0.0f, 0.0f),             // 18 RightForeArm
            new(-0.271336f, 0.0f, 0.0f),             // 19 RightHand
            new(-0.129567f, -0.033510f, -0.008307f), // 20 RightHandThumbEnd
            new(-0.191053f, -0.009448f, 0.004118f),  // 21 RightHandMiddleEnd
            new(-0.084431f, 0.025676f, 0.100432f),   // 22 LeftLeg
            new(0.432292f, 0.0f, 0.0f),              // 23 LeftShin
            new(0.422986f, 0.0f, 0.0f),              // 24 LeftFoot
            new(0.141659f, 0.0f, 0.0f),              // 25 LeftToeBase
            new(-0.083039f, 0.025927f, -0.100473f),  // 26 RightLeg
            new(-0.433697f, 0.0f, 0.0f),             // 27 RightShin
            new(-0.422608f, 0.0f, 0.0f),             // 28 RightFoot
            new(-0.142222f, 0.0f, 0.0f),             // 29 RightToeBase
        ];

        int[] parents = SomaModelContract.ParentIndices;
        var localPoses = new SomaPoseTransform[SomaModelContract.JointCount];
        localPoses[0] = new SomaPoseTransform(localOffsets[0], worldRotations[0]);
        for (int i = 1; i < SomaModelContract.JointCount; i++)
        {
            Quaternion localRot = SomaPoseTransform.NormalizeOrIdentity(
                Quaternion.Inverse(worldRotations[parents[i]]) * worldRotations[i]);
            localPoses[i] = new SomaPoseTransform(localOffsets[i], localRot);
        }

        return new SomaRestSkeletonCalibration(
            localPoses,
            worldRotations,
            ReferenceHipsHeightMeters,
            useMecanimNeutralPostureOffsets: true);
    }

    private static Quaternion CreateBasisQuaternion(Vector3 xAxis, Vector3 yAxis, Vector3 zAxis)
    {
        var matrix = new Matrix4x4(
            xAxis.X, xAxis.Y, xAxis.Z, 0.0f,
            yAxis.X, yAxis.Y, yAxis.Z, 0.0f,
            zAxis.X, zAxis.Y, zAxis.Z, 0.0f,
            0.0f,    0.0f,    0.0f,    1.0f);
        return SomaPoseTransform.NormalizeOrIdentity(Quaternion.CreateFromRotationMatrix(matrix));
    }
}
