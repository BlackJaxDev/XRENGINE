using System.Numerics;

namespace XREngine.Input;

/// <summary>
/// Identifies the VR runtime currently driving tracked poses.
/// </summary>
#if false
public enum RuntimeVrRuntimeKind
{
    /// <summary>
    /// No VR runtime is active.
    /// </summary>
    None,

    /// <summary>
    /// OpenVR/SteamVR is active.
    /// </summary>
    OpenVR,

    /// <summary>
    /// OpenXR is active.
    /// </summary>
    OpenXR,
}

/// <summary>
/// Selects which pose cache should be used when resolving runtime VR transforms.
/// </summary>
public enum RuntimeVrPoseTiming
{
    /// <summary>
    /// Use the predicted display-time pose prepared for visibility collection and frame setup.
    /// </summary>
    Predicted,

    /// <summary>
    /// Use the latest pose located immediately before rendering.
    /// </summary>
    Late,

    /// <summary>
    /// Use the legacy OpenVR render-pose cache, or the late OpenXR pose cache.
    /// </summary>
    Recalc,
}

/// <summary>
/// Runtime-neutral identity and pose status for an OpenXR tracker user path.
/// </summary>
public readonly record struct RuntimeVrTrackerInfo(
    string UserPath,
    string? PersistentPath,
    string? RolePath,
    string? RoleName,
    bool PoseAvailable,
    bool RuntimeReported);
#endif

/// <summary>
/// Runtime-facing service contract for VR state, calibration, devices, and pose lookup.
/// </summary>
public interface IRuntimeVrStateServices
{
    /// <summary>
    /// Raised when the runtime advances the app frame and transform caches should be marked dirty.
    /// </summary>
    event Action? FrameAdvanced;

    /// <summary>
    /// Raised when VR transforms should refresh from the pose cache selected by the event argument.
    /// </summary>
    event Action<RuntimeVrPoseTiming>? RecalcMatrixOnDraw;

    /// <summary>
    /// Raised when the IPD scale used for avatar/view calibration changes.
    /// </summary>
    event Action<float>? IPDScalarChanged;

    /// <summary>
    /// Raised when the measured real-world user height changes.
    /// </summary>
    event Action<float>? RealWorldHeightChanged;

    /// <summary>
    /// Raised when the target avatar height changes.
    /// </summary>
    event Action<float>? DesiredAvatarHeightChanged;

    /// <summary>
    /// Raised when the authored model height used for avatar scaling changes.
    /// </summary>
    event Action<float>? ModelHeightChanged;

    /// <summary>
    /// Raised when OpenVR reports a newly detected tracked device.
    /// </summary>
    event Action<RuntimeVrDeviceInfo>? DeviceDetected;

    /// <summary>
    /// Runtime currently providing VR state.
    /// </summary>
    RuntimeVrRuntimeKind ActiveRuntime { get; }

    /// <summary>
    /// True when OpenXR is the active VR runtime.
    /// </summary>
    bool IsOpenXRActive { get; }

    /// <summary>
    /// True when the application is currently running in a VR session.
    /// </summary>
    bool IsInVR { get; }

    /// <summary>
    /// Host-owned calibration settings object consumed by avatar calibration code.
    /// </summary>
    object? CalibrationSettings { get; }

    /// <summary>
    /// User interpupillary distance in real-world meters.
    /// </summary>
    float RealWorldIPD { get; }

    /// <summary>
    /// Interpupillary distance after applying runtime avatar/view scaling.
    /// </summary>
    float ScaledIPD { get; }

    /// <summary>
    /// Ratio that converts model-space avatar height to real-world height.
    /// </summary>
    float ModelToRealWorldHeightRatio { get; }

    /// <summary>
    /// Authored model-space avatar height in meters.
    /// </summary>
    float ModelHeight { get; set; }

    /// <summary>
    /// OpenVR headset device when OpenVR tracking is available.
    /// </summary>
    RuntimeVrDeviceInfo? Headset { get; }

    /// <summary>
    /// OpenVR left controller device when OpenVR tracking is available.
    /// </summary>
    RuntimeVrDeviceInfo? LeftController { get; }

    /// <summary>
    /// OpenVR right controller device when OpenVR tracking is available.
    /// </summary>
    RuntimeVrDeviceInfo? RightController { get; }

    /// <summary>
    /// OpenVR tracked devices currently known to the runtime.
    /// </summary>
    IReadOnlyList<RuntimeVrDeviceInfo> TrackedDevices { get; }

    /// <summary>
    /// OpenXR tracker user paths currently known to the runtime.
    /// Includes SteamVR role paths and persistent paths when the active runtime exposes them.
    /// </summary>
    string[] GetKnownOpenXrTrackerUserPaths();

    /// <summary>
    /// OpenXR tracker identities currently known to the runtime.
    /// </summary>
    RuntimeVrTrackerInfo[] GetKnownOpenXrTrackers();

    /// <summary>
    /// Returns true when the OpenVR tracked device index represents a generic tracker.
    /// </summary>
    bool IsGenericTracker(uint deviceIndex);

    /// <summary>Copies the requested device pose from the runtime's selected pose cache.</summary>
    bool TryGetDeviceLocalPose(uint deviceIndex, RuntimeVrPoseTiming timing, out Matrix4x4 pose);

    /// <summary>
    /// Attempts to resolve the headset local pose from the requested pose cache.
    /// </summary>
    bool TryGetHeadLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose);

    /// <summary>
    /// Attempts to resolve a controller local pose from the requested pose cache.
    /// </summary>
    bool TryGetControllerLocalPose(bool leftHand, RuntimeVrPoseTiming timing, out Matrix4x4 pose);

    /// <summary>
    /// Attempts to resolve an OpenXR tracker local pose by user path from the requested pose cache.
    /// </summary>
    bool TryGetTrackerLocalPose(string trackerUserPath, RuntimeVrPoseTiming timing, out Matrix4x4 pose);

    /// <summary>
    /// Attempts to resolve the local eye offset relative to the headset pose.
    /// </summary>
    bool TryGetHeadToEyeLocalPose(bool leftEye, out Matrix4x4 pose);

    /// <summary>Gets the runtime's asymmetric projection for an eye without initializing a runtime.</summary>
    bool TryGetEyeProjectionMatrix(bool leftEye, float nearPlane, float farPlane, out Matrix4x4 projection);
}

/// <summary>
/// Static runtime access point for VR state services used by input and transform integration code.
/// </summary>
public static class RuntimeVrStateServices
{
    private static readonly DefaultRuntimeVrStateServices Default = new();
    private static IRuntimeVrStateServices _current = Default;

    private static event Action? StaticFrameAdvanced;
    private static event Action<RuntimeVrPoseTiming>? StaticRecalcMatrixOnDraw;
    private static event Action<float>? StaticIPDScalarChanged;
    private static event Action<float>? StaticRealWorldHeightChanged;
    private static event Action<float>? StaticDesiredAvatarHeightChanged;
    private static event Action<float>? StaticModelHeightChanged;
    private static event Action<RuntimeVrDeviceInfo>? StaticDeviceDetected;
    private static readonly object DeviceEventSync = new();

    static RuntimeVrStateServices()
    {
        Attach(_current);
    }

    #region Service selection

    /// <summary>
    /// Current concrete VR state service. Assigning <see langword="null"/> resets to the safe no-op service.
    /// </summary>
    public static IRuntimeVrStateServices Current
    {
        get => _current;
        set
        {
            IRuntimeVrStateServices next = value ?? Default;
            lock (DeviceEventSync)
            {
                if (ReferenceEquals(_current, next))
                    return;

                Detach(_current);
                _current = next;
                Attach(_current);
            }
        }
    }

    /// <summary>Whether no application VR state provider has been installed.</summary>
    public static bool IsDefaultProvider => ReferenceEquals(Current, Default);

    #endregion

    #region Runtime state

    /// <inheritdoc cref="IRuntimeVrStateServices.ActiveRuntime"/>
    public static RuntimeVrRuntimeKind ActiveRuntime
        => Current.ActiveRuntime;

    /// <inheritdoc cref="IRuntimeVrStateServices.IsOpenXRActive"/>
    public static bool IsOpenXRActive
        => Current.IsOpenXRActive;

    /// <inheritdoc cref="IRuntimeVrStateServices.IsInVR"/>
    public static bool IsInVR
        => Current.IsInVR;

    /// <inheritdoc cref="IRuntimeVrStateServices.CalibrationSettings"/>
    public static object? CalibrationSettings
        => Current.CalibrationSettings;

    /// <inheritdoc cref="IRuntimeVrStateServices.RealWorldIPD"/>
    public static float RealWorldIPD
        => Current.RealWorldIPD;

    /// <inheritdoc cref="IRuntimeVrStateServices.ScaledIPD"/>
    public static float ScaledIPD
        => Current.ScaledIPD;

    /// <inheritdoc cref="IRuntimeVrStateServices.ModelToRealWorldHeightRatio"/>
    public static float ModelToRealWorldHeightRatio
        => Current.ModelToRealWorldHeightRatio;

    /// <inheritdoc cref="IRuntimeVrStateServices.ModelHeight"/>
    public static float ModelHeight
    {
        get => Current.ModelHeight;
        set => Current.ModelHeight = value;
    }

    /// <inheritdoc cref="IRuntimeVrStateServices.Headset"/>
    public static RuntimeVrDeviceInfo? Headset
        => Current.Headset;

    /// <inheritdoc cref="IRuntimeVrStateServices.LeftController"/>
    public static RuntimeVrDeviceInfo? LeftController
        => Current.LeftController;

    /// <inheritdoc cref="IRuntimeVrStateServices.RightController"/>
    public static RuntimeVrDeviceInfo? RightController
        => Current.RightController;

    /// <inheritdoc cref="IRuntimeVrStateServices.TrackedDevices"/>
    public static IReadOnlyList<RuntimeVrDeviceInfo> TrackedDevices
        => Current.TrackedDevices;

    /// <inheritdoc cref="IRuntimeVrStateServices.GetKnownOpenXrTrackerUserPaths"/>
    public static string[] GetKnownOpenXrTrackerUserPaths()
        => Current.GetKnownOpenXrTrackerUserPaths();

    /// <inheritdoc cref="IRuntimeVrStateServices.GetKnownOpenXrTrackers"/>
    public static RuntimeVrTrackerInfo[] GetKnownOpenXrTrackers()
        => Current.GetKnownOpenXrTrackers();

    #endregion

    #region Forwarded events

    /// <inheritdoc cref="IRuntimeVrStateServices.FrameAdvanced"/>
    public static event Action? FrameAdvanced
    {
        add => StaticFrameAdvanced += value;
        remove => StaticFrameAdvanced -= value;
    }

    /// <inheritdoc cref="IRuntimeVrStateServices.RecalcMatrixOnDraw"/>
    public static event Action<RuntimeVrPoseTiming>? RecalcMatrixOnDraw
    {
        add => StaticRecalcMatrixOnDraw += value;
        remove => StaticRecalcMatrixOnDraw -= value;
    }

    /// <inheritdoc cref="IRuntimeVrStateServices.IPDScalarChanged"/>
    public static event Action<float>? IPDScalarChanged
    {
        add => StaticIPDScalarChanged += value;
        remove => StaticIPDScalarChanged -= value;
    }

    /// <inheritdoc cref="IRuntimeVrStateServices.RealWorldHeightChanged"/>
    public static event Action<float>? RealWorldHeightChanged
    {
        add => StaticRealWorldHeightChanged += value;
        remove => StaticRealWorldHeightChanged -= value;
    }

    /// <inheritdoc cref="IRuntimeVrStateServices.DesiredAvatarHeightChanged"/>
    public static event Action<float>? DesiredAvatarHeightChanged
    {
        add => StaticDesiredAvatarHeightChanged += value;
        remove => StaticDesiredAvatarHeightChanged -= value;
    }

    /// <inheritdoc cref="IRuntimeVrStateServices.ModelHeightChanged"/>
    public static event Action<float>? ModelHeightChanged
    {
        add => StaticModelHeightChanged += value;
        remove => StaticModelHeightChanged -= value;
    }

    /// <inheritdoc cref="IRuntimeVrStateServices.DeviceDetected"/>
    public static event Action<RuntimeVrDeviceInfo>? DeviceDetected
    {
        add
        {
            if (value is null)
                return;

            lock (DeviceEventSync)
            {
                if (StaticDeviceDetected is null)
                    Current.DeviceDetected += ForwardDeviceDetected;
                StaticDeviceDetected += value;
            }
        }
        remove
        {
            if (value is null)
                return;

            lock (DeviceEventSync)
            {
                StaticDeviceDetected -= value;
                if (StaticDeviceDetected is null)
                    Current.DeviceDetected -= ForwardDeviceDetected;
            }
        }
    }

    #endregion

    #region Pose and device queries

    /// <inheritdoc cref="IRuntimeVrStateServices.IsGenericTracker"/>
    public static bool IsGenericTracker(uint deviceIndex)
        => Current.IsGenericTracker(deviceIndex);

    /// <inheritdoc cref="IRuntimeVrStateServices.TryGetDeviceLocalPose"/>
    public static bool TryGetDeviceLocalPose(uint deviceIndex, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        => Current.TryGetDeviceLocalPose(deviceIndex, timing, out pose);

    /// <inheritdoc cref="IRuntimeVrStateServices.TryGetHeadLocalPose"/>
    public static bool TryGetHeadLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        => Current.TryGetHeadLocalPose(timing, out pose);

    /// <inheritdoc cref="IRuntimeVrStateServices.TryGetControllerLocalPose"/>
    public static bool TryGetControllerLocalPose(bool leftHand, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        => Current.TryGetControllerLocalPose(leftHand, timing, out pose);

    /// <inheritdoc cref="IRuntimeVrStateServices.TryGetTrackerLocalPose"/>
    public static bool TryGetTrackerLocalPose(string trackerUserPath, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        => Current.TryGetTrackerLocalPose(trackerUserPath, timing, out pose);

    /// <inheritdoc cref="IRuntimeVrStateServices.TryGetHeadToEyeLocalPose"/>
    public static bool TryGetHeadToEyeLocalPose(bool leftEye, out Matrix4x4 pose)
        => Current.TryGetHeadToEyeLocalPose(leftEye, out pose);

    /// <inheritdoc cref="IRuntimeVrStateServices.TryGetEyeProjectionMatrix"/>
    public static bool TryGetEyeProjectionMatrix(bool leftEye, float nearPlane, float farPlane, out Matrix4x4 projection)
        => Current.TryGetEyeProjectionMatrix(leftEye, nearPlane, farPlane, out projection);

    #endregion

    #region Concrete service event forwarding

    private static void Attach(IRuntimeVrStateServices services)
    {
        services.FrameAdvanced += ForwardFrameAdvanced;
        services.RecalcMatrixOnDraw += ForwardRecalcMatrixOnDraw;
        services.IPDScalarChanged += ForwardIPDScalarChanged;
        services.RealWorldHeightChanged += ForwardRealWorldHeightChanged;
        services.DesiredAvatarHeightChanged += ForwardDesiredAvatarHeightChanged;
        services.ModelHeightChanged += ForwardModelHeightChanged;
        if (StaticDeviceDetected is not null)
            services.DeviceDetected += ForwardDeviceDetected;
    }

    private static void Detach(IRuntimeVrStateServices services)
    {
        services.FrameAdvanced -= ForwardFrameAdvanced;
        services.RecalcMatrixOnDraw -= ForwardRecalcMatrixOnDraw;
        services.IPDScalarChanged -= ForwardIPDScalarChanged;
        services.RealWorldHeightChanged -= ForwardRealWorldHeightChanged;
        services.DesiredAvatarHeightChanged -= ForwardDesiredAvatarHeightChanged;
        services.ModelHeightChanged -= ForwardModelHeightChanged;
        if (StaticDeviceDetected is not null)
            services.DeviceDetected -= ForwardDeviceDetected;
    }

    private static void ForwardFrameAdvanced()
        => StaticFrameAdvanced?.Invoke();

    private static void ForwardRecalcMatrixOnDraw(RuntimeVrPoseTiming timing)
        => StaticRecalcMatrixOnDraw?.Invoke(timing);

    private static void ForwardIPDScalarChanged(float value)
        => StaticIPDScalarChanged?.Invoke(value);

    private static void ForwardRealWorldHeightChanged(float value)
        => StaticRealWorldHeightChanged?.Invoke(value);

    private static void ForwardDesiredAvatarHeightChanged(float value)
        => StaticDesiredAvatarHeightChanged?.Invoke(value);

    private static void ForwardModelHeightChanged(float value)
        => StaticModelHeightChanged?.Invoke(value);

    private static void ForwardDeviceDetected(RuntimeVrDeviceInfo device)
        => StaticDeviceDetected?.Invoke(device);

    #endregion

    /// <summary>
    /// Null-object service used before the host engine installs a concrete VR runtime service.
    /// </summary>
    private sealed class DefaultRuntimeVrStateServices : IRuntimeVrStateServices
    {
        public event Action? FrameAdvanced
        {
            add { }
            remove { }
        }

        public event Action<RuntimeVrPoseTiming>? RecalcMatrixOnDraw
        {
            add { }
            remove { }
        }

        public event Action<float>? IPDScalarChanged
        {
            add { }
            remove { }
        }

        public event Action<float>? RealWorldHeightChanged
        {
            add { }
            remove { }
        }

        public event Action<float>? DesiredAvatarHeightChanged
        {
            add { }
            remove { }
        }

        public event Action<float>? ModelHeightChanged
        {
            add { }
            remove { }
        }

        public event Action<RuntimeVrDeviceInfo>? DeviceDetected
        {
            add { }
            remove { }
        }

        public RuntimeVrRuntimeKind ActiveRuntime => RuntimeVrRuntimeKind.None;
        public bool IsOpenXRActive => false;
        public bool IsInVR => false;
        public object? CalibrationSettings => null;
        public float RealWorldIPD => 0.0f;
        public float ScaledIPD => 0.0f;
        public float ModelToRealWorldHeightRatio => 1.0f;
        public float ModelHeight { get; set; } = 1.0f;
        public RuntimeVrDeviceInfo? Headset => null;
        public RuntimeVrDeviceInfo? LeftController => null;
        public RuntimeVrDeviceInfo? RightController => null;
        public IReadOnlyList<RuntimeVrDeviceInfo> TrackedDevices => Array.Empty<RuntimeVrDeviceInfo>();

        public string[] GetKnownOpenXrTrackerUserPaths()
            => [];

        public RuntimeVrTrackerInfo[] GetKnownOpenXrTrackers()
            => [];

        public bool IsGenericTracker(uint deviceIndex)
            => false;

        public bool TryGetDeviceLocalPose(uint deviceIndex, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        {
            pose = Matrix4x4.Identity;
            return false;
        }

        public bool TryGetHeadLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        {
            pose = Matrix4x4.Identity;
            return false;
        }

        public bool TryGetControllerLocalPose(bool leftHand, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        {
            pose = Matrix4x4.Identity;
            return false;
        }

        public bool TryGetTrackerLocalPose(string trackerUserPath, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        {
            pose = Matrix4x4.Identity;
            return false;
        }

        public bool TryGetHeadToEyeLocalPose(bool leftEye, out Matrix4x4 pose)
        {
            pose = Matrix4x4.Identity;
            return false;
        }

        public bool TryGetEyeProjectionMatrix(bool leftEye, float nearPlane, float farPlane, out Matrix4x4 projection)
        {
            projection = Matrix4x4.Identity;
            return false;
        }
    }
}
