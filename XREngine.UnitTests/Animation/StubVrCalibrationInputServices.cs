using XREngine.Input;

namespace XREngine.UnitTests.Animation;

/// <summary>Dispatches the same registered runtime-neutral actions used by the local VR player.</summary>
internal sealed class StubVrCalibrationInputServices : IRuntimeVrInputServices
{
    private readonly Dictionary<string, Action<bool>> _booleans = [];
    private readonly Dictionary<string, RuntimeVrScalarChanged> _floats = [];
    public RuntimeVrRuntimeKind ActiveRuntime => RuntimeVrRuntimeKind.OpenXR;
    public string ActiveServiceName => "Calibration input regression";
    public void Update(float delta) { }
    public void EmitBoolean(string name, bool pressed) { if (_booleans.TryGetValue(name, out var action)) action(pressed); }
    public void EmitFloat(string name, float value) { if (_floats.TryGetValue(name, out var action)) action(0, value); }
    public bool HasBoolean(string name) => _booleans.ContainsKey(name);
    public bool RegisterBoolAction(string category, string name, Action<bool> callback, bool unregister)
    {
        if (unregister) _booleans.Remove(name); else _booleans[name] = callback;
        return true;
    }
    public bool RegisterFloatAction(string category, string name, RuntimeVrScalarChanged callback, bool unregister)
    {
        if (unregister) _floats.Remove(name); else _floats[name] = callback;
        return true;
    }
    public bool RegisterVector2Action(string category, string name, RuntimeVrVector2Changed callback, bool unregister) => false;
    public bool RegisterVector3Action(string category, string name, RuntimeVrVector3Changed callback, bool unregister) => false;
    public bool RegisterPoseAction(string category, string name, RuntimeVrPoseKind poseKind, bool leftHand, RuntimeVrPoseChanged callback, bool unregister) => false;
    public bool RegisterHandSkeletonSummaryAction(string category, string name, bool leftHand, RuntimeVrSkeletonSummaryChanged callback, bool unregister) => false;
    public bool RegisterHandSkeletonQuery(string category, string name, bool leftHand, bool unregister) => false;
    public bool TryGetPose(bool leftHand, RuntimeVrPoseKind poseKind, RuntimeVrPoseTiming timing, out RuntimeVrPoseState pose) { pose = default; return false; }
    public bool TryGetHandJoint(bool leftHand, RuntimeVrHandJoint joint, out RuntimeVrHandJointState state) { state = default; return false; }
    public bool TryGetSkeletonSummary(bool leftHand, out RuntimeVrSkeletonSummary summary) { summary = default; return false; }
    public bool VibrateAction(string category, string name, double duration, double frequency = 40, double amplitude = 1, double delay = 0) => false;
    public bool StopVibration(string category, string name) => false;
}
