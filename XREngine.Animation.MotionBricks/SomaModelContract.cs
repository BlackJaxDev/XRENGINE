namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Defines the fixed timing, tensor dimensions, and 30-joint skeleton layout
/// of the NVIDIA MotionBricks SOMA human model (<c>soma_n1.json</c>).
/// </summary>
public static class SomaModelContract
{
    public const int SchemaVersion = 1;
    public const int BatchSize = 1;
    public const float SampleRateHz = 30.0f;
    public const int PosesPerToken = 4;
    public const int ContextPoseCount = PosesPerToken;
    public const int TargetPoseCapacity = PosesPerToken;
    public const int ContextAndTargetPoseCount = 2 * PosesPerToken;
    public const int MinTokenCount = 6;
    public const int MaxTokenCount = 16;
    public const int AllowedTokenSlotCount = MaxTokenCount - MinTokenCount + 1;
    public const int BoundaryFrameCount = PosesPerToken - 1;
    public const int MaxRawFrameCount = MaxTokenCount * PosesPerToken;
    public const int MaxValidFrameCount = MaxRawFrameCount - 2 * BoundaryFrameCount;

    public const int JointCount = 30;
    public const int ChildJointCount = JointCount - 1;
    public const int GlobalRootFeatureCount = 5;
    public const int LocalRootFeatureCount = 4;
    public const int LocalPosePositionFeatureCount = ChildJointCount * 3;
    public const int LocalPoseRotationFeatureCount = JointCount * 6;
    public const int LocalPoseFeatureCount = LocalPosePositionFeatureCount + LocalPoseRotationFeatureCount;
    public const int JointVelocityFeatureCount = JointCount * 3;
    public const int FootContactCount = 4;
    public const int GlobalMotionFeatureCount =
        GlobalRootFeatureCount
        + LocalPosePositionFeatureCount
        + LocalPoseRotationFeatureCount
        + JointVelocityFeatureCount
        + FootContactCount;

    public const int GlobalRootTensorElementCount = BatchSize * ContextAndTargetPoseCount * GlobalRootFeatureCount;
    public const int LocalRootTensorElementCount = BatchSize * ContextAndTargetPoseCount * LocalRootFeatureCount;
    public const int LocalPosesTensorElementCount = BatchSize * ContextAndTargetPoseCount * LocalPoseFeatureCount;
    public const int MaskTensorElementCount = BatchSize * ContextAndTargetPoseCount;
    public const int AllowedTokensTensorElementCount = BatchSize * AllowedTokenSlotCount;
    public const int PredGlobalMotionsTensorElementCount = BatchSize * MaxRawFrameCount * GlobalMotionFeatureCount;
    public const int PredNumTokensTensorElementCount = BatchSize;

    public const int RootJointIndex = 0;
    public const int LeftLegJointIndex = 22;
    public const int LeftFootJointIndex = 24;
    public const int RightLegJointIndex = 26;
    public const int RightFootJointIndex = 28;

    /// <summary>
    /// Exact 30 joint names in SOMA model order. Index 0 is always <c>Hips</c>.
    /// </summary>
    public static readonly string[] JointNames =
    [
        "Hips",
        "Spine1",
        "Spine2",
        "Chest",
        "Neck1",
        "Neck2",
        "Head",
        "Jaw",
        "LeftEye",
        "RightEye",
        "LeftShoulder",
        "LeftArm",
        "LeftForeArm",
        "LeftHand",
        "LeftHandThumbEnd",
        "LeftHandMiddleEnd",
        "RightShoulder",
        "RightArm",
        "RightForeArm",
        "RightHand",
        "RightHandThumbEnd",
        "RightHandMiddleEnd",
        "LeftLeg",
        "LeftShin",
        "LeftFoot",
        "LeftToeBase",
        "RightLeg",
        "RightShin",
        "RightFoot",
        "RightToeBase",
    ];

    /// <summary>
    /// Topologically sorted parent indices for the 30-joint SOMA skeleton.
    /// Every parent index is strictly less than its child index, and <c>-1</c> marks the root.
    /// </summary>
    public static readonly int[] ParentIndices =
    [
        -1, // 0: Hips
        0,  // 1: Spine1
        1,  // 2: Spine2
        2,  // 3: Chest
        3,  // 4: Neck1
        4,  // 5: Neck2
        5,  // 6: Head
        6,  // 7: Jaw
        6,  // 8: LeftEye
        6,  // 9: RightEye
        3,  // 10: LeftShoulder
        10, // 11: LeftArm
        11, // 12: LeftForeArm
        12, // 13: LeftHand
        13, // 14: LeftHandThumbEnd
        13, // 15: LeftHandMiddleEnd
        3,  // 16: RightShoulder
        16, // 17: RightArm
        17, // 18: RightForeArm
        18, // 19: RightHand
        19, // 20: RightHandThumbEnd
        19, // 21: RightHandMiddleEnd
        0,  // 22: LeftLeg
        22, // 23: LeftShin
        23, // 24: LeftFoot
        24, // 25: LeftToeBase
        0,  // 26: RightLeg
        26, // 27: RightShin
        27, // 28: RightFoot
        28, // 29: RightToeBase
    ];

    /// <summary>
    /// Joint indices corresponding to the four predicted foot-contact channels:
    /// <c>LeftFoot</c>, <c>LeftToeBase</c>, <c>RightFoot</c>, and <c>RightToeBase</c>.
    /// </summary>
    public static readonly int[] FootContactJointIndices = [24, 25, 28, 29];

    /// <summary>
    /// Computes the number of usable 30 Hz frames left after trimming boundary frames
    /// from both ends of a generated motion sequence.
    /// </summary>
    public static int ValidFrameCount(int tokenCount)
    {
        if (tokenCount < MinTokenCount || tokenCount > MaxTokenCount)
            throw new ArgumentOutOfRangeException(
                nameof(tokenCount),
                tokenCount,
                $"Token count must be between {MinTokenCount} and {MaxTokenCount}.");

        return tokenCount * PosesPerToken - 2 * BoundaryFrameCount;
    }

    /// <summary>
    /// Finds the SOMA model joint index for the exact joint name.
    /// </summary>
    public static bool TryGetJointIndex(string jointName, out int index)
    {
        for (int i = 0; i < JointNames.Length; i++)
        {
            if (string.Equals(JointNames[i], jointName, StringComparison.Ordinal))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }
}
