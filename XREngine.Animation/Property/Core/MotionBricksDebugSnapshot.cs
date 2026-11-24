using System.Numerics;

namespace XREngine.Animation;

/// <summary>
/// Preallocated debug visualization snapshot copied from an active
/// <see cref="IMotionBricksLocomotionDriver"/> without heap allocations. Holds the projected
/// future ground trajectory dots, the current 30-joint SOMA skeleton, and future predicted
/// skeletons from the active trajectory buffer.
/// </summary>
public sealed class MotionBricksDebugSnapshot
{
    public const int MaxTrajectoryPoints = 12;
    public const int SomaJointCount = 30;

    /// <summary>
    /// Parent joint index for each of the 30 SOMA skeleton joints (-1 for Hips root).
    /// </summary>
    public static readonly int[] SomaJointParentIndices =
    [
        -1, // 0  Hips
        0,  // 1  Spine1
        1,  // 2  Spine2
        2,  // 3  Chest
        3,  // 4  Neck1
        4,  // 5  Neck2
        5,  // 6  Head
        6,  // 7  Jaw
        6,  // 8  LeftEye
        6,  // 9  RightEye
        3,  // 10 LeftShoulder
        10, // 11 LeftArm
        11, // 12 LeftForeArm
        12, // 13 LeftHand
        13, // 14 LeftHandThumbEnd
        13, // 15 LeftHandMiddleEnd
        3,  // 16 RightShoulder
        16, // 17 RightArm
        17, // 18 RightForeArm
        18, // 19 RightHand
        19, // 20 RightHandThumbEnd
        19, // 21 RightHandMiddleEnd
        0,  // 22 LeftLeg
        22, // 23 LeftShin
        23, // 24 LeftFoot
        24, // 25 LeftToeBase
        0,  // 26 RightLeg
        26, // 27 RightShin
        27, // 28 RightFoot
        28, // 29 RightToeBase
    ];

    public MotionBricksTrajectoryPoint[] TrajectoryPoints { get; } = new MotionBricksTrajectoryPoint[MaxTrajectoryPoints];
    public int TrajectoryPointCount { get; set; }

    public Vector3[] CurrentSkeletonJoints { get; } = new Vector3[SomaJointCount];
    public Vector4 CurrentFootContacts { get; set; }
    public bool HasCurrentSkeleton { get; set; }

    public Vector3[] FutureMidSkeletonJoints { get; } = new Vector3[SomaJointCount];
    public float FutureMidLeadSeconds { get; set; }
    public bool HasFutureMidSkeleton { get; set; }

    public Vector3[] FutureEndSkeletonJoints { get; } = new Vector3[SomaJointCount];
    public float FutureEndLeadSeconds { get; set; }
    public bool HasFutureEndSkeleton { get; set; }

    public Vector3 CurrentRootGroundPosition { get; set; }
    public Vector3 TargetRootGroundPosition { get; set; }
    public float AvatarLegScale { get; set; } = 1.0f;
    public bool IsValid { get; set; }

    public void Clear()
    {
        TrajectoryPointCount = 0;
        CurrentFootContacts = Vector4.Zero;
        HasCurrentSkeleton = false;
        FutureMidLeadSeconds = 0.0f;
        HasFutureMidSkeleton = false;
        FutureEndLeadSeconds = 0.0f;
        HasFutureEndSkeleton = false;
        CurrentRootGroundPosition = Vector3.Zero;
        TargetRootGroundPosition = Vector3.Zero;
        IsValid = false;
    }
}
