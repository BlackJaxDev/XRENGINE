using System.Numerics;

namespace XREngine.Input;

public enum RuntimeVrRuntimeKind
{
    None,
    OpenVR,
    OpenXR,
}

public enum RuntimeVrPoseTiming
{
    Predicted,
    Late,
    Recalc,
}

public readonly record struct RuntimeVrTrackerInfo(
    string UserPath,
    string? PersistentPath,
    string? RolePath,
    string? RoleName,
    bool PoseAvailable,
    bool RuntimeReported)
{
    /// <summary>True once a valid pose has been observed during this session.</summary>
    public bool EverTracked { get; init; }

    /// <summary>Frame snapshot containing the current valid pose, or zero when unavailable.</summary>
    public long SnapshotId { get; init; }

    /// <summary>OpenXR sample time of the current valid pose, or zero when unavailable.</summary>
    public long SampleTime { get; init; }

    /// <summary>Presence in the provider's latest enumeration; absence does not reveal whether SteamVR disabled the tracker.</summary>
    public bool Connected { get; init; }

    public bool ActionBound { get; init; }
    public bool ActionActive { get; init; }
    public bool PositionValid { get; init; }
    public bool OrientationValid { get; init; }
    public bool HasLastValidPose { get; init; }
    public Matrix4x4 LastValidPose { get; init; }
    public long LastValidSnapshotId { get; init; }
    public long LastValidSampleTime { get; init; }

    public RuntimeVrTrackerStatus Status => !Connected ? RuntimeVrTrackerStatus.DisabledOrNotReported :
        !ActionBound ? RuntimeVrTrackerStatus.DiscoveredUnbound :
        !ActionActive ? RuntimeVrTrackerStatus.BoundInactive :
        !PositionValid || !OrientationValid ? RuntimeVrTrackerStatus.TrackingLost :
        !PoseAvailable ? RuntimeVrTrackerStatus.Stale : RuntimeVrTrackerStatus.Usable;
}

public enum RuntimeVrActionValueType
{
    Boolean,
    Float,
    Vector2,
    Vector3,
    Pose,
    Haptic,
    HandSkeleton,
}

public enum RuntimeVrPoseKind
{
    Grip,
    Aim,
}

public enum RuntimeVrHandJoint
{
    Palm,
    Wrist,
    ThumbMetacarpal,
    ThumbProximal,
    ThumbDistal,
    ThumbTip,
    IndexMetacarpal,
    IndexProximal,
    IndexIntermediate,
    IndexDistal,
    IndexTip,
    MiddleMetacarpal,
    MiddleProximal,
    MiddleIntermediate,
    MiddleDistal,
    MiddleTip,
    RingMetacarpal,
    RingProximal,
    RingIntermediate,
    RingDistal,
    RingTip,
    LittleMetacarpal,
    LittleProximal,
    LittleIntermediate,
    LittleDistal,
    LittleTip,
}

public readonly struct RuntimeVrPoseState
{
    public RuntimeVrPoseState(
        Matrix4x4 localPose,
        Vector3 position,
        Quaternion rotation,
        Vector3 velocity,
        Vector3 angularVelocity,
        bool isActive,
        bool isValid)
    {
        LocalPose = localPose;
        Position = position;
        Rotation = rotation;
        Velocity = velocity;
        AngularVelocity = angularVelocity;
        IsActive = isActive;
        IsValid = isValid;
    }

    public Matrix4x4 LocalPose { get; }
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public Vector3 Velocity { get; }
    public Vector3 AngularVelocity { get; }
    public bool IsActive { get; }
    public bool IsValid { get; }
}

public readonly struct RuntimeVrHandJointState
{
    public RuntimeVrHandJointState(
        Vector3 position,
        Quaternion rotation,
        float radius,
        bool positionValid,
        bool rotationValid,
        bool tracked)
    {
        Position = position;
        Rotation = rotation;
        Radius = radius;
        PositionValid = positionValid;
        RotationValid = rotationValid;
        Tracked = tracked;
    }

    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public float Radius { get; }
    public bool PositionValid { get; }
    public bool RotationValid { get; }
    public bool Tracked { get; }
}

public readonly struct RuntimeVrSkeletonSummary
{
    public RuntimeVrSkeletonSummary(
        float thumbCurl,
        float indexCurl,
        float middleCurl,
        float ringCurl,
        float littleCurl,
        float thumbIndexSplay,
        float indexMiddleSplay,
        float middleRingSplay,
        float ringLittleSplay,
        bool hasRealHandJoints,
        bool isActive)
    {
        ThumbCurl = thumbCurl;
        IndexCurl = indexCurl;
        MiddleCurl = middleCurl;
        RingCurl = ringCurl;
        LittleCurl = littleCurl;
        ThumbIndexSplay = thumbIndexSplay;
        IndexMiddleSplay = indexMiddleSplay;
        MiddleRingSplay = middleRingSplay;
        RingLittleSplay = ringLittleSplay;
        HasRealHandJoints = hasRealHandJoints;
        IsActive = isActive;
    }

    public float ThumbCurl { get; }
    public float IndexCurl { get; }
    public float MiddleCurl { get; }
    public float RingCurl { get; }
    public float LittleCurl { get; }
    public float ThumbIndexSplay { get; }
    public float IndexMiddleSplay { get; }
    public float MiddleRingSplay { get; }
    public float RingLittleSplay { get; }
    public bool HasRealHandJoints { get; }
    public bool IsActive { get; }
}

public delegate void RuntimeVrScalarChanged(float oldValue, float newValue);
public delegate void RuntimeVrVector2Changed(Vector2 oldValue, Vector2 newValue);
public delegate void RuntimeVrVector3Changed(Vector3 oldValue, Vector3 newValue);
public delegate void RuntimeVrPoseChanged(in RuntimeVrPoseState pose);
public delegate void RuntimeVrSkeletonSummaryChanged(in RuntimeVrSkeletonSummary summary);
