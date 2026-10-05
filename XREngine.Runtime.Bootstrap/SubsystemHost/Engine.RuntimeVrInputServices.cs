using System.Numerics;
using System.Threading;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine;

internal sealed class EngineRuntimeVrInputServices : IRuntimeVrInputServices, IRuntimeVrActionSetServices, IRuntimeVrCalibrationInputServices, IDisposable
{
    private readonly Dictionary<string, BoolRegistration> _boolRegistrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FloatRegistration> _floatRegistrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector2Registration> _vector2Registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector3Registration> _vector3Registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PoseRegistration> _poseRegistrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SkeletonSummaryRegistration> _skeletonSummaryRegistrations = new(StringComparer.Ordinal);
    private BoolRegistration[] _boolSnapshot = [];
    private FloatRegistration[] _floatSnapshot = [];
    private Vector2Registration[] _vector2Snapshot = [];
    private Vector3Registration[] _vector3Snapshot = [];
    private PoseRegistration[] _poseSnapshot = [];
    private SkeletonSummaryRegistration[] _skeletonSummarySnapshot = [];
    private int _openXrInputResetPending;
    private readonly HashSet<string> _acceptedDiagnostics = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejectedDiagnostics = new(StringComparer.Ordinal);
    private readonly object _registrationLock = new();
    private readonly List<Action> _deferredRegistrationChanges = [];
    private int _dispatchDepth;
    private bool _disposed;

    private event Action? RuntimeActionsChanged;
    public event Action<RuntimeVrCalibrationAction>? CalibrationActionPressed;
    private bool _calibrationOpenHeld;
    private bool _calibrationCancelHeld;
    private bool _calibrationCaptureHeld;
    private bool _calibrationLeftSelectHeld;
    private bool _calibrationRightSelectHeld;
    private bool _calibrationLeftSelectReported;
    private bool _calibrationRightSelectReported;
    private long _calibrationLeftSelectDownTimestamp;
    private long _calibrationRightSelectDownTimestamp;
    public bool CalibrationOpenHeld => _calibrationOpenHeld;
    public bool CalibrationMeasurementEntryActive { get; set; }

    public EngineRuntimeVrInputServices()
    {
        OpenVrActionBackend.ActionsChanged += ForwardActionsChanged;
        RuntimeEngine.VRState.OpenXRSessionRunningChanged += OpenXRSessionRunningChanged;
    }

    public event Action? ActionsChanged
    {
        add => RuntimeActionsChanged += value;
        remove => RuntimeActionsChanged -= value;
    }

    public bool HasActions => OpenVrActionBackend.HasActions;

    public RuntimeVrRuntimeKind ActiveRuntime
        => RuntimeEngine.VRState.ActiveRuntime switch
        {
            RuntimeVrState.VRRuntime.OpenVR => RuntimeVrRuntimeKind.OpenVR,
            RuntimeVrState.VRRuntime.OpenXR => RuntimeVrRuntimeKind.OpenXR,
            _ => RuntimeEngine.VRState.OpenXRApi is not null ? RuntimeVrRuntimeKind.OpenXR : RuntimeVrRuntimeKind.None,
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
        if (_disposed)
            return;
        BeginActionDispatch();
        try
        {
            if (Interlocked.Exchange(ref _openXrInputResetPending, 0) != 0)
                DispatchNeutralActions();
            RuntimeVrRuntimeKind runtime = ActiveRuntime;
            if (runtime == RuntimeVrRuntimeKind.OpenXR && RuntimeEngine.VRState.OpenXRApi is { } openXrApi)
            {
                DispatchOpenXrActions(openXrApi);
                return;
            }

            if (runtime == RuntimeVrRuntimeKind.OpenVR)
                DispatchOpenVrActions();
            else
                DispatchNeutralActions();
        }
        finally
        {
            EndActionDispatch();
        }
    }

    private void BeginActionDispatch()
    {
        lock (_registrationLock)
            _dispatchDepth++;
    }

    private void EndActionDispatch()
    {
        lock (_registrationLock)
        {
            if (--_dispatchDepth != 0)
                return;

            // Changes requested by callbacks become visible together after this dispatch.
            try
            {
                if (!_disposed)
                {
                    for (int i = 0; i < _deferredRegistrationChanges.Count; i++)
                        _deferredRegistrationChanges[i]();
                }
            }
            finally
            {
                _deferredRegistrationChanges.Clear();
            }
        }
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
        if (ActiveRuntime == RuntimeVrRuntimeKind.OpenXR && RuntimeEngine.VRState.OpenXRApi is { } openXrApi)
            return openXrApi.TryGetControllerPoseState(leftHand, poseKind, MapPoseTiming(openXrApi, timing), out pose);

        var controller = leftHand
            ? OpenVrRuntimeBackend.Api.LeftController
            : OpenVrRuntimeBackend.Api.RightController;
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
        if (RuntimeEngine.VRState.OpenXRApi is { } openXrApi && openXrApi.TryGetHandJointState(leftHand, joint, out state))
            return true;

        state = default;
        return false;
    }

    public bool TryGetSkeletonSummary(bool leftHand, out RuntimeVrSkeletonSummary summary)
    {
        if (RuntimeEngine.VRState.OpenXRApi is { } openXrApi && openXrApi.TryGetSkeletonSummary(leftHand, out summary))
            return true;

        summary = default;
        return false;
    }

    public bool VibrateAction(string category, string name, double duration, double frequency = 40, double amplitude = 1, double delay = 0)
    {
        if (ActiveRuntime == RuntimeVrRuntimeKind.OpenXR && RuntimeEngine.VRState.OpenXRApi is { } openXrApi)
            return openXrApi.ApplyHapticAction(category, name, duration, frequency, amplitude, delay);

        if (OpenVrActionBackend.Vibrate(category, name, duration, frequency, amplitude, delay))
            return true;

        LogRegistrationResult(RuntimeVrActionValueType.Haptic, category, name, accepted: false, unregister: false);
        return false;
    }

    public bool StopVibration(string category, string name)
    {
        if (ActiveRuntime == RuntimeVrRuntimeKind.OpenXR && RuntimeEngine.VRState.OpenXRApi is { } openXrApi)
            return openXrApi.StopHapticAction(category, name);

        return true;
    }

    private void DispatchOpenXrActions(IOpenXrRuntime openXrApi)
    {
        DispatchCalibrationActions(openXrApi);
        foreach (BoolRegistration registration in Volatile.Read(ref _boolSnapshot))
        {
            bool available = openXrApi.TryGetBooleanActionState(registration.Category, registration.Name, out bool value, out bool active) && active;
            if (available) registration.Dispatch(value);
            else registration.Release();
        }

        foreach (FloatRegistration registration in Volatile.Read(ref _floatSnapshot))
        {
            bool available = openXrApi.TryGetFloatActionState(registration.Category, registration.Name, out float value, out bool active) && active;
            if (available) registration.Dispatch(value);
            else registration.Release();
        }

        foreach (Vector2Registration registration in Volatile.Read(ref _vector2Snapshot))
        {
            bool available = openXrApi.TryGetVector2ActionState(registration.Category, registration.Name, out Vector2 value, out bool active) && active;
            if (available) registration.Dispatch(value);
            else registration.Release();
        }

        foreach (Vector3Registration registration in Volatile.Read(ref _vector3Snapshot))
        {
            bool available = openXrApi.TryGetVector3ActionState(registration.Category, registration.Name, out Vector3 value, out bool active) && active;
            if (available) registration.Dispatch(value);
            else registration.Release();
        }

        foreach (PoseRegistration registration in Volatile.Read(ref _poseSnapshot))
        {
            if (openXrApi.TryGetControllerPoseState(registration.LeftHand, registration.PoseKind, RuntimeOpenXrPoseTiming.Predicted, out RuntimeVrPoseState pose))
                registration.Dispatch(in pose);
            else
                registration.Release();
        }

        foreach (SkeletonSummaryRegistration registration in Volatile.Read(ref _skeletonSummarySnapshot))
        {
            if (openXrApi.TryGetSkeletonSummary(registration.LeftHand, out RuntimeVrSkeletonSummary summary))
                registration.Dispatch(in summary);
            else
                registration.Release();
        }
    }

    private void DispatchCalibrationActions(IOpenXrRuntime openXrApi)
    {
        bool open = ReadCalibrationButton(openXrApi, "OpenCalibration");
        bool cancel = ReadCalibrationButton(openXrApi, "CancelCalibration");
        bool leftSelect = ReadCalibrationButton(openXrApi, "CalibrationTriggerLeft");
        bool rightSelect = ReadCalibrationButton(openXrApi, "CalibrationTriggerRight");
        bool capture = leftSelect && rightSelect;

        if (open && !_calibrationOpenHeld)
            CalibrationActionPressed?.Invoke(RuntimeVrCalibrationAction.Open);
        if (cancel && !_calibrationCancelHeld)
            CalibrationActionPressed?.Invoke(RuntimeVrCalibrationAction.Cancel);
        if (CalibrationMeasurementEntryActive)
        {
            if (capture && !_calibrationCaptureHeld)
                CalibrationActionPressed?.Invoke(RuntimeVrCalibrationAction.MeasurementBothSelect);
            if (!leftSelect && _calibrationLeftSelectHeld && !_calibrationLeftSelectReported && !_calibrationCaptureHeld)
                CalibrationActionPressed?.Invoke(RuntimeVrCalibrationAction.MeasurementLeftSelect);
            if (!rightSelect && _calibrationRightSelectHeld && !_calibrationRightSelectReported && !_calibrationCaptureHeld)
                CalibrationActionPressed?.Invoke(RuntimeVrCalibrationAction.MeasurementRightSelect);
            if (!capture)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                long chordWindow = System.Diagnostics.Stopwatch.Frequency / 10;
                if (leftSelect && !_calibrationLeftSelectHeld)
                    _calibrationLeftSelectDownTimestamp = now;
                if (rightSelect && !_calibrationRightSelectHeld)
                    _calibrationRightSelectDownTimestamp = now;
                if (leftSelect && !_calibrationLeftSelectReported && now - _calibrationLeftSelectDownTimestamp >= chordWindow)
                {
                    CalibrationActionPressed?.Invoke(RuntimeVrCalibrationAction.MeasurementLeftSelect);
                    _calibrationLeftSelectReported = true;
                }
                if (rightSelect && !_calibrationRightSelectReported && now - _calibrationRightSelectDownTimestamp >= chordWindow)
                {
                    CalibrationActionPressed?.Invoke(RuntimeVrCalibrationAction.MeasurementRightSelect);
                    _calibrationRightSelectReported = true;
                }
            }
        }
        else if (capture && !_calibrationCaptureHeld)
            CalibrationActionPressed?.Invoke(RuntimeVrCalibrationAction.Capture);

        _calibrationOpenHeld = open;
        _calibrationCancelHeld = cancel;
        _calibrationCaptureHeld = capture;
        _calibrationLeftSelectHeld = leftSelect;
        _calibrationRightSelectHeld = rightSelect;
        if (!leftSelect || capture)
            _calibrationLeftSelectReported = false;
        if (!rightSelect || capture)
            _calibrationRightSelectReported = false;
    }

    private static bool ReadCalibrationButton(IOpenXrRuntime openXrApi, string name)
        => openXrApi.TryGetBooleanActionState("Global", name, out bool value, out bool active) && active && value;

    private void DispatchOpenVrActions()
    {
        foreach (BoolRegistration registration in Volatile.Read(ref _boolSnapshot))
        {
            if (OpenVrActionBackend.TryReadBool(registration.Category, registration.Name, out bool value))
                registration.Dispatch(value);
            else
                registration.Release();
        }

        foreach (FloatRegistration registration in Volatile.Read(ref _floatSnapshot))
        {
            if (OpenVrActionBackend.TryReadFloat(registration.Category, registration.Name, out float value))
                registration.Dispatch(value);
            else
                registration.Release();
        }

        foreach (Vector2Registration registration in Volatile.Read(ref _vector2Snapshot))
        {
            if (OpenVrActionBackend.TryReadVector2(registration.Category, registration.Name, out Vector2 value))
                registration.Dispatch(value);
            else
                registration.Release();
        }

        foreach (Vector3Registration registration in Volatile.Read(ref _vector3Snapshot))
        {
            if (OpenVrActionBackend.TryReadVector3(registration.Category, registration.Name, out Vector3 value))
                registration.Dispatch(value);
            else
                registration.Release();
        }

        foreach (PoseRegistration registration in Volatile.Read(ref _poseSnapshot))
        {
            if (OpenVrActionBackend.TryReadPose(registration.Category, registration.Name, out RuntimeVrPoseState pose))
                registration.Dispatch(in pose);
            else
                registration.Release();
        }

        foreach (SkeletonSummaryRegistration registration in Volatile.Read(ref _skeletonSummarySnapshot))
        {
            if (OpenVrActionBackend.TryReadSkeletonSummary(registration.Category, registration.Name, out RuntimeVrSkeletonSummary runtimeSummary))
                registration.Dispatch(in runtimeSummary);
            else
                registration.Release();
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
        lock (_registrationLock)
        {
            if (_disposed)
                return false;
            if (_dispatchDepth == 0)
                return ApplyRegistrationChange(registrations, key, callback, unregister, createRegistration, valueType);

            _deferredRegistrationChanges.Add(() =>
                ApplyRegistrationChange(registrations, key, callback, unregister, createRegistration, valueType));
            if (unregister)
                return registrations.ContainsKey(key);
            TRegistration pending = registrations.TryGetValue(key, out TRegistration? existing)
                ? existing
                : createRegistration();
            return RuntimeAcceptsAction(valueType, pending.Category, pending.Name);
        }
    }

    private bool ApplyRegistrationChange<TRegistration, TCallback>(
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
            RebuildRegistrationSnapshot(registrations);
            LogRegistrationResult(valueType, existing.Category, existing.Name, accepted: true, unregister: true);
            return true;
        }

        if (!registrations.TryGetValue(key, out TRegistration? registration))
            registrations.Add(key, registration = createRegistration());

        registration.Add(callback);
        RebuildRegistrationSnapshot(registrations);
        bool accepted = RuntimeAcceptsAction(valueType, registration.Category, registration.Name);
        LogRegistrationResult(valueType, registration.Category, registration.Name, accepted, unregister: false);
        return accepted;
    }

    private void RebuildRegistrationSnapshot<TRegistration>(Dictionary<string, TRegistration> registrations)
    {
        // Registration changes are infrequent; dispatch reads fixed arrays without copying each frame.
        if (ReferenceEquals(registrations, _boolRegistrations))
            Volatile.Write(ref _boolSnapshot, CopyValues(_boolRegistrations));
        else if (ReferenceEquals(registrations, _floatRegistrations))
            Volatile.Write(ref _floatSnapshot, CopyValues(_floatRegistrations));
        else if (ReferenceEquals(registrations, _vector2Registrations))
            Volatile.Write(ref _vector2Snapshot, CopyValues(_vector2Registrations));
        else if (ReferenceEquals(registrations, _vector3Registrations))
            Volatile.Write(ref _vector3Snapshot, CopyValues(_vector3Registrations));
        else if (ReferenceEquals(registrations, _poseRegistrations))
            Volatile.Write(ref _poseSnapshot, CopyValues(_poseRegistrations));
        else if (ReferenceEquals(registrations, _skeletonSummaryRegistrations))
            Volatile.Write(ref _skeletonSummarySnapshot, CopyValues(_skeletonSummaryRegistrations));
    }

    private static TRegistration[] CopyValues<TRegistration>(Dictionary<string, TRegistration> registrations)
    {
        TRegistration[] snapshot = new TRegistration[registrations.Count];
        registrations.Values.CopyTo(snapshot, 0);
        return snapshot;
    }

    private void DispatchNeutralActions()
    {
        foreach (BoolRegistration registration in Volatile.Read(ref _boolSnapshot))
            registration.Release();
        foreach (FloatRegistration registration in Volatile.Read(ref _floatSnapshot))
            registration.Release();
        foreach (Vector2Registration registration in Volatile.Read(ref _vector2Snapshot))
            registration.Release();
        foreach (Vector3Registration registration in Volatile.Read(ref _vector3Snapshot))
            registration.Release();
        foreach (PoseRegistration registration in Volatile.Read(ref _poseSnapshot))
            registration.Release();
        foreach (SkeletonSummaryRegistration registration in Volatile.Read(ref _skeletonSummarySnapshot))
            registration.Release();
    }

    private bool RuntimeAcceptsAction(RuntimeVrActionValueType valueType, string category, string name)
    {
        if (RuntimeEngine.VRState.OpenXRApi is { } openXrApi && openXrApi.IsInputActionKnown(category, name, valueType))
            return true;

        return (valueType == RuntimeVrActionValueType.HandSkeleton && RuntimeEngine.VRState.OpenXRApi is not null) ||
            OpenVrActionBackend.HasAction(valueType, category, name);
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

    private void ForwardActionsChanged()
        => RuntimeActionsChanged?.Invoke();

    private void OpenXRSessionRunningChanged(bool running)
    {
        if (running)
            RuntimeActionsChanged?.Invoke();
        else
        {
            Interlocked.Exchange(ref _openXrInputResetPending, 1);
            _calibrationOpenHeld = false;
            _calibrationCancelHeld = false;
            _calibrationCaptureHeld = false;
            _calibrationLeftSelectHeld = false;
            _calibrationRightSelectHeld = false;
            _calibrationLeftSelectReported = false;
            _calibrationRightSelectReported = false;
            CalibrationMeasurementEntryActive = false;
        }
    }

    public void Dispose()
    {
        OpenVrActionBackend.ActionsChanged -= ForwardActionsChanged;
        RuntimeEngine.VRState.OpenXRSessionRunningChanged -= OpenXRSessionRunningChanged;
        RuntimeActionsChanged = null;
        lock (_registrationLock)
        {
            _disposed = true;
            _deferredRegistrationChanges.Clear();
            _boolRegistrations.Clear();
            _floatRegistrations.Clear();
            _vector2Registrations.Clear();
            _vector3Registrations.Clear();
            _poseRegistrations.Clear();
            _skeletonSummaryRegistrations.Clear();
            Volatile.Write(ref _boolSnapshot, []);
            Volatile.Write(ref _floatSnapshot, []);
            Volatile.Write(ref _vector2Snapshot, []);
            Volatile.Write(ref _vector3Snapshot, []);
            Volatile.Write(ref _poseSnapshot, []);
            Volatile.Write(ref _skeletonSummarySnapshot, []);
        }
        CalibrationActionPressed = null;
    }

    private static RuntimeOpenXrPoseTiming MapPoseTiming(IOpenXrRuntime openXrApi, RuntimeVrPoseTiming timing)
        => timing == RuntimeVrPoseTiming.Late || timing == RuntimeVrPoseTiming.Recalc
            ? RuntimeOpenXrPoseTiming.Late
            : RuntimeOpenXrPoseTiming.Predicted;

    private static string MakeKey(string category, string name)
        => string.Concat(category, "/", name);

    private static string MakePoseKey(string category, string name, RuntimeVrPoseKind poseKind, bool leftHand)
        => string.Concat(category, "/", name, "/", poseKind.ToString(), "/", leftHand ? "left" : "right");

    private static string MakeHandKey(string category, string name, bool leftHand)
        => string.Concat(category, "/", name, "/", leftHand ? "left" : "right");

    private abstract class CallbackRegistration<TCallback>(string category, string name)
        where TCallback : Delegate
    {
        private TCallback[] _callbacks = [];

        public string Category { get; } = category;
        public string Name { get; } = name;
        protected TCallback[] Callbacks => Volatile.Read(ref _callbacks);
        public int Count => _callbacks.Length;

        public void Add(TCallback callback)
        {
            TCallback[] current = _callbacks;
            if (Array.IndexOf(current, callback) >= 0)
                return;
            TCallback[] next = new TCallback[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = callback;
            Volatile.Write(ref _callbacks, next);
        }

        public void Remove(TCallback callback)
        {
            TCallback[] current = _callbacks;
            int index = Array.IndexOf(current, callback);
            if (index < 0)
                return;
            TCallback[] next = new TCallback[current.Length - 1];
            if (index > 0)
                Array.Copy(current, 0, next, 0, index);
            if (index < next.Length)
                Array.Copy(current, index + 1, next, index, next.Length - index);
            Volatile.Write(ref _callbacks, next);
        }
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
            Action<bool>[] callbacks = Callbacks;
            for (int i = 0; i < callbacks.Length; i++)
                callbacks[i](value);
        }

        public void Release()
        {
            if (_hasValue && _value)
                Dispatch(false);
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
            RuntimeVrScalarChanged[] callbacks = Callbacks;
            for (int i = 0; i < callbacks.Length; i++)
                callbacks[i](previous, value);
        }

        public void Release()
        {
            if (_hasValue && _value != 0.0f)
                Dispatch(0.0f);
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
            RuntimeVrVector2Changed[] callbacks = Callbacks;
            for (int i = 0; i < callbacks.Length; i++)
                callbacks[i](previous, value);
        }

        public void Release()
        {
            if (_hasValue && _value != Vector2.Zero)
                Dispatch(Vector2.Zero);
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
            RuntimeVrVector3Changed[] callbacks = Callbacks;
            for (int i = 0; i < callbacks.Length; i++)
                callbacks[i](previous, value);
        }

        public void Release()
        {
            if (_hasValue && _value != Vector3.Zero)
                Dispatch(Vector3.Zero);
        }
    }

    private sealed class PoseRegistration(string category, string name, RuntimeVrPoseKind poseKind, bool leftHand) : CallbackRegistration<RuntimeVrPoseChanged>(category, name)
    {
        private bool _hasValidPose;
        public RuntimeVrPoseKind PoseKind { get; } = poseKind;
        public bool LeftHand { get; } = leftHand;

        public void Dispatch(in RuntimeVrPoseState pose)
        {
            if (!pose.IsValid)
                return;

            _hasValidPose = true;
            RuntimeVrPoseChanged[] callbacks = Callbacks;
            for (int i = 0; i < callbacks.Length; i++)
                callbacks[i](in pose);
        }

        public void Release()
        {
            if (!_hasValidPose)
                return;
            _hasValidPose = false;
            RuntimeVrPoseState unavailable = default;
            RuntimeVrPoseChanged[] callbacks = Callbacks;
            for (int i = 0; i < callbacks.Length; i++)
                callbacks[i](in unavailable);
        }
    }

    private sealed class SkeletonSummaryRegistration(string category, string name, bool leftHand) : CallbackRegistration<RuntimeVrSkeletonSummaryChanged>(category, name)
    {
        private bool _hasActiveSummary;
        public bool LeftHand { get; } = leftHand;

        public void Dispatch(in RuntimeVrSkeletonSummary summary)
        {
            _hasActiveSummary = summary.IsActive;
            RuntimeVrSkeletonSummaryChanged[] callbacks = Callbacks;
            for (int i = 0; i < callbacks.Length; i++)
                callbacks[i](in summary);
        }

        public void Release()
        {
            if (!_hasActiveSummary)
                return;
            _hasActiveSummary = false;
            RuntimeVrSkeletonSummary unavailable = default;
            RuntimeVrSkeletonSummaryChanged[] callbacks = Callbacks;
            for (int i = 0; i < callbacks.Length; i++)
                callbacks[i](in unavailable);
        }
    }
}
