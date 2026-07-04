using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using BooleanAction = OpenVR.NET.Input.BooleanAction;
using HandSkeletonAction = OpenVR.NET.Input.HandSkeletonAction;
using HapticAction = OpenVR.NET.Input.HapticAction;
using OpenVRAction = OpenVR.NET.Input.Action;
using PoseAction = OpenVR.NET.Input.PoseAction;
using ScalarAction = OpenVR.NET.Input.ScalarAction;
using Vector2Action = OpenVR.NET.Input.Vector2Action;
using Vector3Action = OpenVR.NET.Input.Vector3Action;

namespace XREngine;

internal sealed class EngineRuntimeVrInputServices : IRuntimeVrInputServices, IRuntimeVrLegacyActionServices
{
    private readonly Dictionary<string, BoolRegistration> _boolRegistrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FloatRegistration> _floatRegistrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector2Registration> _vector2Registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector3Registration> _vector3Registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PoseRegistration> _poseRegistrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SkeletonSummaryRegistration> _skeletonSummaryRegistrations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acceptedDiagnostics = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejectedDiagnostics = new(StringComparer.Ordinal);

    private event Action<Dictionary<string, Dictionary<string, OpenVRAction>>>? RuntimeActionsChanged;

    public EngineRuntimeVrInputServices()
    {
        Engine.VRState.ActionsChanged += ForwardActionsChanged;
        Engine.VRState.OpenXRSessionRunningChanged += OpenXRSessionRunningChanged;
    }

    public event Action<Dictionary<string, Dictionary<string, OpenVRAction>>>? ActionsChanged
    {
        add => RuntimeActionsChanged += value;
        remove => RuntimeActionsChanged -= value;
    }

    public Dictionary<string, Dictionary<string, OpenVRAction>> Actions
        => Engine.VRState.Actions;

    public RuntimeVrRuntimeKind ActiveRuntime
        => Engine.VRState.ActiveRuntime switch
        {
            Engine.VRState.VRRuntime.OpenVR => RuntimeVrRuntimeKind.OpenVR,
            Engine.VRState.VRRuntime.OpenXR => RuntimeVrRuntimeKind.OpenXR,
            _ => Engine.VRState.OpenXRApi is not null ? RuntimeVrRuntimeKind.OpenXR : RuntimeVrRuntimeKind.None,
        };

    public string ActiveServiceName
        => ActiveRuntime switch
        {
            RuntimeVrRuntimeKind.OpenVR => "OpenVR",
            RuntimeVrRuntimeKind.OpenXR => "OpenXR",
            _ => "None",
        };

    public void Update(float delta)
    {
        RuntimeVrRuntimeKind runtime = ActiveRuntime;
        if (runtime == RuntimeVrRuntimeKind.OpenXR && Engine.VRState.OpenXRApi is { } openXrApi)
        {
            DispatchOpenXrActions(openXrApi);
            return;
        }

        if (runtime == RuntimeVrRuntimeKind.OpenVR)
            DispatchOpenVrActions();
    }

    public bool RegisterBoolAction(string category, string name, Action<bool> callback, bool unregister)
    {
        string key = MakeKey(category, name);
        return RegisterCallback(
            _boolRegistrations,
            key,
            callback,
            unregister,
            () => new BoolRegistration(category, name),
            RuntimeVrActionValueType.Boolean);
    }

    public bool RegisterFloatAction(string category, string name, RuntimeVrScalarChanged callback, bool unregister)
    {
        string key = MakeKey(category, name);
        return RegisterCallback(
            _floatRegistrations,
            key,
            callback,
            unregister,
            () => new FloatRegistration(category, name),
            RuntimeVrActionValueType.Float);
    }

    public bool RegisterVector2Action(string category, string name, RuntimeVrVector2Changed callback, bool unregister)
    {
        string key = MakeKey(category, name);
        return RegisterCallback(
            _vector2Registrations,
            key,
            callback,
            unregister,
            () => new Vector2Registration(category, name),
            RuntimeVrActionValueType.Vector2);
    }

    public bool RegisterVector3Action(string category, string name, RuntimeVrVector3Changed callback, bool unregister)
    {
        string key = MakeKey(category, name);
        return RegisterCallback(
            _vector3Registrations,
            key,
            callback,
            unregister,
            () => new Vector3Registration(category, name),
            RuntimeVrActionValueType.Vector3);
    }

    public bool RegisterPoseAction(string category, string name, RuntimeVrPoseKind poseKind, bool leftHand, RuntimeVrPoseChanged callback, bool unregister)
    {
        string key = MakePoseKey(category, name, poseKind, leftHand);
        return RegisterCallback(
            _poseRegistrations,
            key,
            callback,
            unregister,
            () => new PoseRegistration(category, name, poseKind, leftHand),
            RuntimeVrActionValueType.Pose);
    }

    public bool RegisterHandSkeletonSummaryAction(string category, string name, bool leftHand, RuntimeVrSkeletonSummaryChanged callback, bool unregister)
    {
        string key = MakeHandKey(category, name, leftHand);
        return RegisterCallback(
            _skeletonSummaryRegistrations,
            key,
            callback,
            unregister,
            () => new SkeletonSummaryRegistration(category, name, leftHand),
            RuntimeVrActionValueType.HandSkeleton);
    }

    public bool RegisterHandSkeletonQuery(string category, string name, bool leftHand, bool unregister)
    {
        bool accepted = RuntimeAcceptsAction(RuntimeVrActionValueType.HandSkeleton, category, name);
        LogRegistrationResult(RuntimeVrActionValueType.HandSkeleton, category, name, accepted, unregister);
        return accepted;
    }

    public bool TryGetPose(bool leftHand, RuntimeVrPoseKind poseKind, RuntimeVrPoseTiming timing, out RuntimeVrPoseState pose)
    {
        if (ActiveRuntime == RuntimeVrRuntimeKind.OpenXR && Engine.VRState.OpenXRApi is { } openXrApi)
            return openXrApi.TryGetControllerPoseState(leftHand, poseKind, MapPoseTiming(openXrApi, timing), out pose);

        var controller = leftHand
            ? Engine.VRState.OpenVRApi.LeftController
            : Engine.VRState.OpenVRApi.RightController;
        if ((timing == RuntimeVrPoseTiming.Recalc ? controller?.RenderDeviceToAbsoluteTrackingMatrix : controller?.DeviceToAbsoluteTrackingMatrix) is Matrix4x4 localPose)
        {
            Matrix4x4.Decompose(localPose, out _, out Quaternion rotation, out Vector3 position);
            pose = new RuntimeVrPoseState(localPose, position, rotation, Vector3.Zero, Vector3.Zero, isActive: true, isValid: true);
            return true;
        }

        pose = default;
        return false;
    }

    public bool TryGetHandJoint(bool leftHand, RuntimeVrHandJoint joint, out RuntimeVrHandJointState state)
    {
        if (Engine.VRState.OpenXRApi is { } openXrApi && openXrApi.TryGetHandJointState(leftHand, joint, out state))
            return true;

        state = default;
        return false;
    }

    public bool TryGetSkeletonSummary(bool leftHand, out RuntimeVrSkeletonSummary summary)
    {
        if (Engine.VRState.OpenXRApi is { } openXrApi && openXrApi.TryGetSkeletonSummary(leftHand, out summary))
            return true;

        summary = default;
        return false;
    }

    public bool VibrateAction(string category, string name, double duration, double frequency = 40, double amplitude = 1, double delay = 0)
    {
        if (ActiveRuntime == RuntimeVrRuntimeKind.OpenXR && Engine.VRState.OpenXRApi is { } openXrApi)
            return openXrApi.ApplyHapticAction(category, name, duration, frequency, amplitude, delay);

        if (TryGetOpenVrAction(category, name, out HapticAction? hapticAction))
            return hapticAction.TriggerVibration(duration, frequency, amplitude, delay);

        LogRegistrationResult(RuntimeVrActionValueType.Haptic, category, name, accepted: false, unregister: false);
        return false;
    }

    public bool StopVibration(string category, string name)
    {
        if (ActiveRuntime == RuntimeVrRuntimeKind.OpenXR && Engine.VRState.OpenXRApi is { } openXrApi)
            return openXrApi.StopHapticAction(category, name);

        return true;
    }

    private void DispatchOpenXrActions(OpenXRAPI openXrApi)
    {
        foreach (BoolRegistration registration in _boolRegistrations.Values)
        {
            if (openXrApi.TryGetBooleanActionState(registration.Category, registration.Name, out bool value, out bool active) && active)
                registration.Dispatch(value);
        }

        foreach (FloatRegistration registration in _floatRegistrations.Values)
        {
            if (openXrApi.TryGetFloatActionState(registration.Category, registration.Name, out float value, out bool active) && active)
                registration.Dispatch(value);
        }

        foreach (Vector2Registration registration in _vector2Registrations.Values)
        {
            if (openXrApi.TryGetVector2ActionState(registration.Category, registration.Name, out Vector2 value, out bool active) && active)
                registration.Dispatch(value);
        }

        foreach (Vector3Registration registration in _vector3Registrations.Values)
        {
            if (openXrApi.TryGetVector3ActionState(registration.Category, registration.Name, out Vector3 value, out bool active) && active)
                registration.Dispatch(value);
        }

        foreach (PoseRegistration registration in _poseRegistrations.Values)
        {
            if (openXrApi.TryGetControllerPoseState(registration.LeftHand, registration.PoseKind, OpenXRAPI.OpenXrPoseTiming.Predicted, out RuntimeVrPoseState pose))
                registration.Dispatch(in pose);
        }

        foreach (SkeletonSummaryRegistration registration in _skeletonSummaryRegistrations.Values)
        {
            if (openXrApi.TryGetSkeletonSummary(registration.LeftHand, out RuntimeVrSkeletonSummary summary))
                registration.Dispatch(in summary);
        }
    }

    private void DispatchOpenVrActions()
    {
        foreach (BoolRegistration registration in _boolRegistrations.Values)
        {
            if (TryGetOpenVrAction(registration.Category, registration.Name, out BooleanAction? action))
            {
                action.Update();
                registration.Dispatch(action.Value);
            }
        }

        foreach (FloatRegistration registration in _floatRegistrations.Values)
        {
            if (TryGetOpenVrAction(registration.Category, registration.Name, out ScalarAction? action))
            {
                action.Update();
                registration.Dispatch(action.Value);
            }
        }

        foreach (Vector2Registration registration in _vector2Registrations.Values)
        {
            if (TryGetOpenVrAction(registration.Category, registration.Name, out Vector2Action? action))
            {
                action.Update();
                registration.Dispatch(action.Value);
            }
        }

        foreach (Vector3Registration registration in _vector3Registrations.Values)
        {
            if (TryGetOpenVrAction(registration.Category, registration.Name, out Vector3Action? action))
            {
                action.Update();
                registration.Dispatch(action.Value);
            }
        }

        foreach (PoseRegistration registration in _poseRegistrations.Values)
        {
            if (TryGetOpenVrAction(registration.Category, registration.Name, out PoseAction? action) &&
                action.FetchDataForPrediction(0.0f) is { } data)
            {
                var pose = new RuntimeVrPoseState(
                    data.DeviceToAbsoluteTrackingMatrix,
                    data.Position,
                    data.Rotation,
                    data.Velocity,
                    data.AngularVelocity,
                    isActive: true,
                    isValid: true);
                registration.Dispatch(in pose);
            }
        }

        foreach (SkeletonSummaryRegistration registration in _skeletonSummaryRegistrations.Values)
        {
            if (TryGetOpenVrAction(registration.Category, registration.Name, out HandSkeletonAction? action) &&
                action.GetSummary(Valve.VR.EVRSummaryType.FromDevice) is { } summary)
            {
                action.Update();
                var runtimeSummary = new RuntimeVrSkeletonSummary(
                    summary.ThumbCurl,
                    summary.IndexCurl,
                    summary.MiddleCurl,
                    summary.RingCurl,
                    summary.PinkyCurl,
                    summary.ThumbIndexSplay,
                    summary.IndexMiddleSplay,
                    summary.MiddleRingSplay,
                    summary.RingPinkySplay,
                    hasRealHandJoints: true,
                    isActive: true);
                registration.Dispatch(in runtimeSummary);
            }
        }
    }

    private bool RegisterCallback<TRegistration, TCallback>(
        Dictionary<string, TRegistration> registrations,
        string key,
        TCallback callback,
        bool unregister,
        Func<TRegistration> createRegistration,
        RuntimeVrActionValueType valueType)
        where TRegistration : CallbackRegistration<TCallback>
        where TCallback : Delegate
    {
        if (unregister)
        {
            if (!registrations.TryGetValue(key, out TRegistration? existing))
                return false;

            existing.Remove(callback);
            if (existing.Count == 0)
                registrations.Remove(key);

            LogRegistrationResult(valueType, existing.Category, existing.Name, accepted: true, unregister: true);
            return true;
        }

        if (!registrations.TryGetValue(key, out TRegistration? registration))
            registrations.Add(key, registration = createRegistration());

        registration.Add(callback);
        bool accepted = RuntimeAcceptsAction(valueType, registration.Category, registration.Name);
        LogRegistrationResult(valueType, registration.Category, registration.Name, accepted, unregister: false);
        return accepted;
    }

    private bool RuntimeAcceptsAction(RuntimeVrActionValueType valueType, string category, string name)
    {
        if (Engine.VRState.OpenXRApi is { } openXrApi && openXrApi.IsInputActionKnown(category, name, valueType))
            return true;

        return valueType switch
        {
            RuntimeVrActionValueType.Boolean => TryGetOpenVrAction(category, name, out BooleanAction? _),
            RuntimeVrActionValueType.Float => TryGetOpenVrAction(category, name, out ScalarAction? _),
            RuntimeVrActionValueType.Vector2 => TryGetOpenVrAction(category, name, out Vector2Action? _),
            RuntimeVrActionValueType.Vector3 => TryGetOpenVrAction(category, name, out Vector3Action? _),
            RuntimeVrActionValueType.Pose => TryGetOpenVrAction(category, name, out PoseAction? _),
            RuntimeVrActionValueType.Haptic => TryGetOpenVrAction(category, name, out HapticAction? _),
            RuntimeVrActionValueType.HandSkeleton => Engine.VRState.OpenXRApi is not null ||
                                                     TryGetOpenVrAction(category, name, out HandSkeletonAction? _),
            _ => false,
        };
    }

    private static bool TryGetOpenVrAction<TAction>(string category, string name, [NotNullWhen(true)] out TAction? action)
        where TAction : OpenVRAction
    {
        action = null;
        if (!Engine.VRState.Actions.TryGetValue(category, out Dictionary<string, OpenVRAction>? actions) ||
            !actions.TryGetValue(name, out OpenVRAction? raw) ||
            raw is not TAction typed)
        {
            return false;
        }

        action = typed;
        return true;
    }

    private void LogRegistrationResult(RuntimeVrActionValueType valueType, string category, string name, bool accepted, bool unregister)
    {
        string key = $"{accepted}:{unregister}:{valueType}:{category}/{name}";
        HashSet<string> destination = accepted ? _acceptedDiagnostics : _rejectedDiagnostics;
        if (!destination.Add(key))
            return;

        string verb = unregister ? "unregistered" : accepted ? "accepted" : "rejected";
        Debug.Out($"VR input service {ActiveServiceName} {verb} {valueType} action {category}/{name}.");
    }

    private void ForwardActionsChanged(Dictionary<string, Dictionary<string, OpenVRAction>> actions)
        => RuntimeActionsChanged?.Invoke(actions);

    private void OpenXRSessionRunningChanged(bool running)
    {
        if (running)
            RuntimeActionsChanged?.Invoke(Actions);
    }

    private static OpenXRAPI.OpenXrPoseTiming MapPoseTiming(OpenXRAPI openXrApi, RuntimeVrPoseTiming timing)
        => timing == RuntimeVrPoseTiming.Late || timing == RuntimeVrPoseTiming.Recalc
            ? OpenXRAPI.OpenXrPoseTiming.Late
            : OpenXRAPI.OpenXrPoseTiming.Predicted;

    private static string MakeKey(string category, string name)
        => string.Concat(category, "/", name);

    private static string MakePoseKey(string category, string name, RuntimeVrPoseKind poseKind, bool leftHand)
        => string.Concat(category, "/", name, "/", poseKind.ToString(), "/", leftHand ? "left" : "right");

    private static string MakeHandKey(string category, string name, bool leftHand)
        => string.Concat(category, "/", name, "/", leftHand ? "left" : "right");

    private abstract class CallbackRegistration<TCallback>(string category, string name)
        where TCallback : Delegate
    {
        private readonly List<TCallback> _callbacks = [];

        public string Category { get; } = category;
        public string Name { get; } = name;
        protected List<TCallback> Callbacks => _callbacks;
        public int Count => _callbacks.Count;

        public void Add(TCallback callback)
        {
            if (!_callbacks.Contains(callback))
                _callbacks.Add(callback);
        }

        public void Remove(TCallback callback)
            => _callbacks.Remove(callback);
    }

    private sealed class BoolRegistration(string category, string name) : CallbackRegistration<Action<bool>>(category, name)
    {
        private bool _hasValue;
        private bool _value;

        public void Dispatch(bool value)
        {
            if (_hasValue && _value == value)
                return;

            _hasValue = true;
            _value = value;
            List<Action<bool>> callbacks = Callbacks;
            for (int i = 0; i < callbacks.Count; i++)
                callbacks[i](value);
        }
    }

    private sealed class FloatRegistration(string category, string name) : CallbackRegistration<RuntimeVrScalarChanged>(category, name)
    {
        private bool _hasValue;
        private float _value;

        public void Dispatch(float value)
        {
            if (_hasValue && _value.Equals(value))
                return;

            float previous = _value;
            _hasValue = true;
            _value = value;
            List<RuntimeVrScalarChanged> callbacks = Callbacks;
            for (int i = 0; i < callbacks.Count; i++)
                callbacks[i](previous, value);
        }
    }

    private sealed class Vector2Registration(string category, string name) : CallbackRegistration<RuntimeVrVector2Changed>(category, name)
    {
        private bool _hasValue;
        private Vector2 _value;

        public void Dispatch(Vector2 value)
        {
            if (_hasValue && _value.Equals(value))
                return;

            Vector2 previous = _value;
            _hasValue = true;
            _value = value;
            List<RuntimeVrVector2Changed> callbacks = Callbacks;
            for (int i = 0; i < callbacks.Count; i++)
                callbacks[i](previous, value);
        }
    }

    private sealed class Vector3Registration(string category, string name) : CallbackRegistration<RuntimeVrVector3Changed>(category, name)
    {
        private bool _hasValue;
        private Vector3 _value;

        public void Dispatch(Vector3 value)
        {
            if (_hasValue && _value.Equals(value))
                return;

            Vector3 previous = _value;
            _hasValue = true;
            _value = value;
            List<RuntimeVrVector3Changed> callbacks = Callbacks;
            for (int i = 0; i < callbacks.Count; i++)
                callbacks[i](previous, value);
        }
    }

    private sealed class PoseRegistration(string category, string name, RuntimeVrPoseKind poseKind, bool leftHand) : CallbackRegistration<RuntimeVrPoseChanged>(category, name)
    {
        public RuntimeVrPoseKind PoseKind { get; } = poseKind;
        public bool LeftHand { get; } = leftHand;

        public void Dispatch(in RuntimeVrPoseState pose)
        {
            if (!pose.IsValid)
                return;

            List<RuntimeVrPoseChanged> callbacks = Callbacks;
            for (int i = 0; i < callbacks.Count; i++)
                callbacks[i](in pose);
        }
    }

    private sealed class SkeletonSummaryRegistration(string category, string name, bool leftHand) : CallbackRegistration<RuntimeVrSkeletonSummaryChanged>(category, name)
    {
        public bool LeftHand { get; } = leftHand;

        public void Dispatch(in RuntimeVrSkeletonSummary summary)
        {
            List<RuntimeVrSkeletonSummaryChanged> callbacks = Callbacks;
            for (int i = 0; i < callbacks.Count; i++)
                callbacks[i](in summary);
        }
    }
}
