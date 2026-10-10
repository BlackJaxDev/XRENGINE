using System.Numerics;

namespace XREngine.Input;

#if false
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
#endif

public interface IRuntimeVrInputServices
{
    RuntimeVrRuntimeKind ActiveRuntime { get; }
    string ActiveServiceName { get; }
    void Update(float delta);
    bool RegisterBoolAction(string category, string name, Action<bool> callback, bool unregister);
    bool RegisterFloatAction(string category, string name, RuntimeVrScalarChanged callback, bool unregister);
    bool RegisterVector2Action(string category, string name, RuntimeVrVector2Changed callback, bool unregister);
    bool RegisterVector3Action(string category, string name, RuntimeVrVector3Changed callback, bool unregister);
    bool RegisterPoseAction(string category, string name, RuntimeVrPoseKind poseKind, bool leftHand, RuntimeVrPoseChanged callback, bool unregister);
    bool RegisterHandSkeletonSummaryAction(string category, string name, bool leftHand, RuntimeVrSkeletonSummaryChanged callback, bool unregister);
    bool RegisterHandSkeletonQuery(string category, string name, bool leftHand, bool unregister);
    bool TryGetPose(bool leftHand, RuntimeVrPoseKind poseKind, RuntimeVrPoseTiming timing, out RuntimeVrPoseState pose);
    bool TryGetHandJoint(bool leftHand, RuntimeVrHandJoint joint, out RuntimeVrHandJointState state);
    bool TryGetSkeletonSummary(bool leftHand, out RuntimeVrSkeletonSummary summary);
    bool VibrateAction(string category, string name, double duration, double frequency = 40, double amplitude = 1, double delay = 0);
    bool StopVibration(string category, string name);
}

/// <summary>Commands generated by the VR calibration bindings on their press edges.</summary>
public enum RuntimeVrCalibrationAction
{
    Open,
    Cancel,
    Capture,
    MeasurementLeftSelect,
    MeasurementRightSelect,
    MeasurementBothSelect,
}

/// <summary>Calibration input delivered regardless of desktop pawn possession.</summary>
public interface IRuntimeVrCalibrationInputServices
{
    event System.Action<RuntimeVrCalibrationAction>? CalibrationActionPressed;
    bool CalibrationOpenHeld { get; }
    bool CalibrationMeasurementEntryActive { get; set; }
}

public static class RuntimeVrInputServices
{
    private static readonly DefaultRuntimeVrInputServices Default = new();
    private static IRuntimeVrInputServices _current = Default;
    private static event System.Action<RuntimeVrCalibrationAction>? StaticCalibrationActionPressed;
    private static event System.Action? StaticActionsChanged;

    static RuntimeVrInputServices()
    {
    }

    public static IRuntimeVrInputServices Current
    {
        get => _current;
        set
        {
            var next = value ?? Default;
            if (ReferenceEquals(_current, next))
                return;

            DetachActionSetEvents(_current);
            DetachCalibrationActionEvents(_current);
            _current = next;
            AttachActionSetEvents(_current);
            AttachCalibrationActionEvents(_current);
        }
    }

    /// <summary>Whether no application VR input provider has been installed.</summary>
    public static bool IsDefaultProvider => ReferenceEquals(Current, Default);

    public static bool HasActions
        => Current is IRuntimeVrActionSetServices actionSet && actionSet.HasActions;

    public static RuntimeVrRuntimeKind ActiveRuntime
        => Current.ActiveRuntime;

    public static string ActiveServiceName
        => Current.ActiveServiceName;

    public static event System.Action? ActionsChanged
    {
        add => StaticActionsChanged += value;
        remove => StaticActionsChanged -= value;
    }

    public static event System.Action<RuntimeVrCalibrationAction>? CalibrationActionPressed
    {
        add => StaticCalibrationActionPressed += value;
        remove => StaticCalibrationActionPressed -= value;
    }

    public static bool CalibrationOpenHeld
        => Current is IRuntimeVrCalibrationInputServices calibration && calibration.CalibrationOpenHeld;

    public static bool CalibrationMeasurementEntryActive
    {
        get => Current is IRuntimeVrCalibrationInputServices calibration && calibration.CalibrationMeasurementEntryActive;
        set
        {
            if (Current is IRuntimeVrCalibrationInputServices calibration)
                calibration.CalibrationMeasurementEntryActive = value;
        }
    }

    public static void Update(float delta)
        => Current.Update(delta);

    public static bool RegisterBoolAction(string category, string name, Action<bool> callback, bool unregister = false)
        => Current.RegisterBoolAction(category, name, callback, unregister);

    public static bool RegisterFloatAction(string category, string name, RuntimeVrScalarChanged callback, bool unregister = false)
        => Current.RegisterFloatAction(category, name, callback, unregister);

    public static bool RegisterVector2Action(string category, string name, RuntimeVrVector2Changed callback, bool unregister = false)
        => Current.RegisterVector2Action(category, name, callback, unregister);

    public static bool RegisterVector3Action(string category, string name, RuntimeVrVector3Changed callback, bool unregister = false)
        => Current.RegisterVector3Action(category, name, callback, unregister);

    public static bool RegisterPoseAction(string category, string name, RuntimeVrPoseKind poseKind, bool leftHand, RuntimeVrPoseChanged callback, bool unregister = false)
        => Current.RegisterPoseAction(category, name, poseKind, leftHand, callback, unregister);

    public static bool RegisterHandSkeletonSummaryAction(string category, string name, bool leftHand, RuntimeVrSkeletonSummaryChanged callback, bool unregister = false)
        => Current.RegisterHandSkeletonSummaryAction(category, name, leftHand, callback, unregister);

    public static bool RegisterHandSkeletonQuery(string category, string name, bool leftHand, bool unregister = false)
        => Current.RegisterHandSkeletonQuery(category, name, leftHand, unregister);

    public static bool TryGetPose(bool leftHand, RuntimeVrPoseKind poseKind, RuntimeVrPoseTiming timing, out RuntimeVrPoseState pose)
        => Current.TryGetPose(leftHand, poseKind, timing, out pose);

    public static bool TryGetHandJoint(bool leftHand, RuntimeVrHandJoint joint, out RuntimeVrHandJointState state)
        => Current.TryGetHandJoint(leftHand, joint, out state);

    public static bool TryGetSkeletonSummary(bool leftHand, out RuntimeVrSkeletonSummary summary)
        => Current.TryGetSkeletonSummary(leftHand, out summary);

    public static bool VibrateAction(string category, string name, double duration, double frequency = 40, double amplitude = 1, double delay = 0)
        => Current.VibrateAction(category, name, duration, frequency, amplitude, delay);

    public static bool StopVibration(string category, string name)
        => Current.StopVibration(category, name);

    private static void ForwardActionsChanged()
        => StaticActionsChanged?.Invoke();

    private static void ForwardCalibrationActionPressed(RuntimeVrCalibrationAction action)
        => StaticCalibrationActionPressed?.Invoke(action);

    private static void AttachCalibrationActionEvents(IRuntimeVrInputServices services)
    {
        if (services is IRuntimeVrCalibrationInputServices calibration)
            calibration.CalibrationActionPressed += ForwardCalibrationActionPressed;
    }

    private static void DetachCalibrationActionEvents(IRuntimeVrInputServices services)
    {
        if (services is IRuntimeVrCalibrationInputServices calibration)
            calibration.CalibrationActionPressed -= ForwardCalibrationActionPressed;
    }

    private static void AttachActionSetEvents(IRuntimeVrInputServices services)
    {
        if (services is IRuntimeVrActionSetServices actionSet)
            actionSet.ActionsChanged += ForwardActionsChanged;
    }

    private static void DetachActionSetEvents(IRuntimeVrInputServices services)
    {
        if (services is IRuntimeVrActionSetServices actionSet)
            actionSet.ActionsChanged -= ForwardActionsChanged;
    }

    private sealed class DefaultRuntimeVrInputServices : IRuntimeVrInputServices
    {
        public RuntimeVrRuntimeKind ActiveRuntime => RuntimeVrRuntimeKind.None;
        public string ActiveServiceName => "None";

        public void Update(float delta)
        {
        }

        public bool RegisterBoolAction(string category, string name, Action<bool> callback, bool unregister)
            => false;

        public bool RegisterFloatAction(string category, string name, RuntimeVrScalarChanged callback, bool unregister)
            => false;

        public bool RegisterVector2Action(string category, string name, RuntimeVrVector2Changed callback, bool unregister)
            => false;

        public bool RegisterVector3Action(string category, string name, RuntimeVrVector3Changed callback, bool unregister)
            => false;

        public bool RegisterPoseAction(string category, string name, RuntimeVrPoseKind poseKind, bool leftHand, RuntimeVrPoseChanged callback, bool unregister)
            => false;

        public bool RegisterHandSkeletonSummaryAction(string category, string name, bool leftHand, RuntimeVrSkeletonSummaryChanged callback, bool unregister)
            => false;

        public bool RegisterHandSkeletonQuery(string category, string name, bool leftHand, bool unregister)
            => false;

        public bool TryGetPose(bool leftHand, RuntimeVrPoseKind poseKind, RuntimeVrPoseTiming timing, out RuntimeVrPoseState pose)
        {
            pose = default;
            return false;
        }

        public bool TryGetHandJoint(bool leftHand, RuntimeVrHandJoint joint, out RuntimeVrHandJointState state)
        {
            state = default;
            return false;
        }

        public bool TryGetSkeletonSummary(bool leftHand, out RuntimeVrSkeletonSummary summary)
        {
            summary = default;
            return false;
        }

        public bool VibrateAction(string category, string name, double duration, double frequency = 40, double amplitude = 1, double delay = 0)
            => false;

        public bool StopVibration(string category, string name)
            => false;
    }
}
