using System.Numerics;
using OpenVR.NET.Devices;

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
    Recalc,
}

public interface IRuntimeVrStateServices
{
    event Action? FrameAdvanced;
    event Action? RecalcMatrixOnDraw;
    event Action<float>? IPDScalarChanged;
    event Action<float>? RealWorldHeightChanged;
    event Action<float>? DesiredAvatarHeightChanged;
    event Action<float>? ModelHeightChanged;
    event Action<VrDevice>? DeviceDetected;

    RuntimeVrRuntimeKind ActiveRuntime { get; }
    bool IsOpenXRActive { get; }
    bool IsInVR { get; }
    object? CalibrationSettings { get; }
    float RealWorldIPD { get; }
    float ScaledIPD { get; }
    float ModelToRealWorldHeightRatio { get; }
    VrDevice? Headset { get; }
    VrDevice? LeftController { get; }
    VrDevice? RightController { get; }
    IEnumerable<VrDevice> TrackedDevices { get; }

    bool IsGenericTracker(uint deviceIndex);
    bool TryGetHeadLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose);
    bool TryGetControllerLocalPose(bool leftHand, RuntimeVrPoseTiming timing, out Matrix4x4 pose);
    bool TryGetTrackerLocalPose(string trackerUserPath, RuntimeVrPoseTiming timing, out Matrix4x4 pose);
    bool TryGetHeadToEyeLocalPose(bool leftEye, out Matrix4x4 pose);
}

public static class RuntimeVrStateServices
{
    private static readonly DefaultRuntimeVrStateServices Default = new();
    private static IRuntimeVrStateServices _current = Default;

    private static event Action? StaticFrameAdvanced;
    private static event Action? StaticRecalcMatrixOnDraw;
    private static event Action<float>? StaticIPDScalarChanged;
    private static event Action<float>? StaticRealWorldHeightChanged;
    private static event Action<float>? StaticDesiredAvatarHeightChanged;
    private static event Action<float>? StaticModelHeightChanged;
    private static event Action<VrDevice>? StaticDeviceDetected;

    static RuntimeVrStateServices()
    {
        Attach(_current);
    }

    public static IRuntimeVrStateServices Current
    {
        get => _current;
        set
        {
            IRuntimeVrStateServices next = value ?? Default;
            if (ReferenceEquals(_current, next))
                return;

            Detach(_current);
            _current = next;
            Attach(_current);
        }
    }

    public static RuntimeVrRuntimeKind ActiveRuntime
        => Current.ActiveRuntime;

    public static bool IsOpenXRActive
        => Current.IsOpenXRActive;

    public static bool IsInVR
        => Current.IsInVR;

    public static object? CalibrationSettings
        => Current.CalibrationSettings;

    public static float RealWorldIPD
        => Current.RealWorldIPD;

    public static float ScaledIPD
        => Current.ScaledIPD;

    public static float ModelToRealWorldHeightRatio
        => Current.ModelToRealWorldHeightRatio;

    public static VrDevice? Headset
        => Current.Headset;

    public static VrDevice? LeftController
        => Current.LeftController;

    public static VrDevice? RightController
        => Current.RightController;

    public static IEnumerable<VrDevice> TrackedDevices
        => Current.TrackedDevices;

    public static event Action? FrameAdvanced
    {
        add => StaticFrameAdvanced += value;
        remove => StaticFrameAdvanced -= value;
    }

    public static event Action? RecalcMatrixOnDraw
    {
        add => StaticRecalcMatrixOnDraw += value;
        remove => StaticRecalcMatrixOnDraw -= value;
    }

    public static event Action<float>? IPDScalarChanged
    {
        add => StaticIPDScalarChanged += value;
        remove => StaticIPDScalarChanged -= value;
    }

    public static event Action<float>? RealWorldHeightChanged
    {
        add => StaticRealWorldHeightChanged += value;
        remove => StaticRealWorldHeightChanged -= value;
    }

    public static event Action<float>? DesiredAvatarHeightChanged
    {
        add => StaticDesiredAvatarHeightChanged += value;
        remove => StaticDesiredAvatarHeightChanged -= value;
    }

    public static event Action<float>? ModelHeightChanged
    {
        add => StaticModelHeightChanged += value;
        remove => StaticModelHeightChanged -= value;
    }

    public static event Action<VrDevice>? DeviceDetected
    {
        add => StaticDeviceDetected += value;
        remove => StaticDeviceDetected -= value;
    }

    public static bool IsGenericTracker(uint deviceIndex)
        => Current.IsGenericTracker(deviceIndex);

    public static bool TryGetHeadLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        => Current.TryGetHeadLocalPose(timing, out pose);

    public static bool TryGetControllerLocalPose(bool leftHand, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        => Current.TryGetControllerLocalPose(leftHand, timing, out pose);

    public static bool TryGetTrackerLocalPose(string trackerUserPath, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        => Current.TryGetTrackerLocalPose(trackerUserPath, timing, out pose);

    public static bool TryGetHeadToEyeLocalPose(bool leftEye, out Matrix4x4 pose)
        => Current.TryGetHeadToEyeLocalPose(leftEye, out pose);

    private static void Attach(IRuntimeVrStateServices services)
    {
        services.FrameAdvanced += ForwardFrameAdvanced;
        services.RecalcMatrixOnDraw += ForwardRecalcMatrixOnDraw;
        services.IPDScalarChanged += ForwardIPDScalarChanged;
        services.RealWorldHeightChanged += ForwardRealWorldHeightChanged;
        services.DesiredAvatarHeightChanged += ForwardDesiredAvatarHeightChanged;
        services.ModelHeightChanged += ForwardModelHeightChanged;
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
        services.DeviceDetected -= ForwardDeviceDetected;
    }

    private static void ForwardFrameAdvanced()
        => StaticFrameAdvanced?.Invoke();

    private static void ForwardRecalcMatrixOnDraw()
        => StaticRecalcMatrixOnDraw?.Invoke();

    private static void ForwardIPDScalarChanged(float value)
        => StaticIPDScalarChanged?.Invoke(value);

    private static void ForwardRealWorldHeightChanged(float value)
        => StaticRealWorldHeightChanged?.Invoke(value);

    private static void ForwardDesiredAvatarHeightChanged(float value)
        => StaticDesiredAvatarHeightChanged?.Invoke(value);

    private static void ForwardModelHeightChanged(float value)
        => StaticModelHeightChanged?.Invoke(value);

    private static void ForwardDeviceDetected(VrDevice device)
        => StaticDeviceDetected?.Invoke(device);

    private sealed class DefaultRuntimeVrStateServices : IRuntimeVrStateServices
    {
        public event Action? FrameAdvanced
        {
            add { }
            remove { }
        }

        public event Action? RecalcMatrixOnDraw
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

        public event Action<VrDevice>? DeviceDetected
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
        public VrDevice? Headset => null;
        public VrDevice? LeftController => null;
        public VrDevice? RightController => null;
        public IEnumerable<VrDevice> TrackedDevices => Array.Empty<VrDevice>();

        public bool IsGenericTracker(uint deviceIndex)
            => false;

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
    }
}