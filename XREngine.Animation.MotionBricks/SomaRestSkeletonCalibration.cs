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

    public SomaRestSkeletonCalibration(
        SomaPoseTransform[] referenceLocalPoses,
        Quaternion[] restWorldRotations,
        float hipsHeightMeters = ReferenceHipsHeightMeters)
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
}
