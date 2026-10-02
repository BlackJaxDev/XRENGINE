using System.Numerics;
using XREngine.Input;
using XREngine.Rendering;

namespace XREngine.Browser;

internal sealed partial class BrowserUnavailableHostServices
{
    private sealed class BrowserVrLifecycleServices : IRuntimeVrLifecycleServices
    {
        public bool InitializeOpenXR(XRWindow? window) => throw Unsupported("VrLifecycle.InitializeOpenXR");
        public bool StopOpenXR() => false;
        public Task<bool> InitializeLocal(IRuntimeOpenVrActionManifest actionManifest,
            RuntimeOpenVrApplicationManifest vrManifest, XRWindow window)
            => Task.FromException<bool>(Unsupported("VrLifecycle.InitializeLocal"));
        public void InitRenderEmulated(XRWindow window) => throw Unsupported("VrLifecycle.InitRenderEmulated");
        public Task<bool> InitializeClient(IRuntimeOpenVrActionManifest actionManifest,
            RuntimeOpenVrApplicationManifest vrManifest)
            => Task.FromException<bool>(Unsupported("VrLifecycle.InitializeClient"));
        public bool InitializeServer() => throw Unsupported("VrLifecycle.InitializeServer");
        public void StartInputClient() => throw Unsupported("VrLifecycle.StartInputClient");
        public void StopInputServer() { }
        public Task SendInputs() => Task.FromException(Unsupported("VrLifecycle.SendInputs"));
    }

    private sealed class BrowserVrInputServices : IRuntimeVrInputServices
    {
        public RuntimeVrRuntimeKind ActiveRuntime => RuntimeVrRuntimeKind.None;
        public string ActiveServiceName => "Browser.VrInput.Unsupported";
        public void Update(float delta) { }
        public bool RegisterBoolAction(string category, string name, Action<bool> callback, bool unregister)
            => false;
        public bool RegisterFloatAction(string category, string name, RuntimeVrScalarChanged callback, bool unregister)
            => false;
        public bool RegisterVector2Action(string category, string name, RuntimeVrVector2Changed callback, bool unregister)
            => false;
        public bool RegisterVector3Action(string category, string name, RuntimeVrVector3Changed callback, bool unregister)
            => false;
        public bool RegisterPoseAction(string category, string name, RuntimeVrPoseKind poseKind, bool leftHand,
            RuntimeVrPoseChanged callback, bool unregister)
            => false;
        public bool RegisterHandSkeletonSummaryAction(string category, string name, bool leftHand,
            RuntimeVrSkeletonSummaryChanged callback, bool unregister)
            => false;
        public bool RegisterHandSkeletonQuery(string category, string name, bool leftHand, bool unregister)
            => false;
        public bool TryGetPose(bool leftHand, RuntimeVrPoseKind poseKind, RuntimeVrPoseTiming timing, out RuntimeVrPoseState pose)
        { pose = default; return false; }
        public bool TryGetHandJoint(bool leftHand, RuntimeVrHandJoint joint, out RuntimeVrHandJointState state)
        { state = default; return false; }
        public bool TryGetSkeletonSummary(bool leftHand, out RuntimeVrSkeletonSummary summary)
        { summary = default; return false; }
        public bool VibrateAction(string category, string name, double duration, double frequency = 40,
            double amplitude = 1, double delay = 0) => false;
        public bool StopVibration(string category, string name) => false;
    }

    private sealed class BrowserVrStateServices : IRuntimeVrStateServices
    {
        public event Action? FrameAdvanced { add { } remove { } }
        public event Action<RuntimeVrPoseTiming>? RecalcMatrixOnDraw { add { } remove { } }
        public event Action<float>? IPDScalarChanged { add { } remove { } }
        public event Action<float>? RealWorldHeightChanged { add { } remove { } }
        public event Action<float>? DesiredAvatarHeightChanged { add { } remove { } }
        public event Action<float>? ModelHeightChanged { add { } remove { } }
        public event Action<RuntimeVrDeviceInfo>? DeviceDetected { add { } remove { } }
        public RuntimeVrRuntimeKind ActiveRuntime => RuntimeVrRuntimeKind.None;
        public bool IsOpenXRActive => false;
        public bool IsInVR => false;
        public object? CalibrationSettings => null;
        public float RealWorldIPD => 0;
        public float ScaledIPD => 0;
        public float ModelToRealWorldHeightRatio => 1;
        public float ModelHeight
        {
            get => 1;
            set => throw Unsupported("VrState.ModelHeight");
        }
        public RuntimeVrDeviceInfo? Headset => null;
        public RuntimeVrDeviceInfo? LeftController => null;
        public RuntimeVrDeviceInfo? RightController => null;
        public IReadOnlyList<RuntimeVrDeviceInfo> TrackedDevices => Array.Empty<RuntimeVrDeviceInfo>();
        public string[] GetKnownOpenXrTrackerUserPaths() => [];
        public RuntimeVrTrackerInfo[] GetKnownOpenXrTrackers() => [];
        public bool IsGenericTracker(uint deviceIndex) => false;
        public bool TryGetDeviceLocalPose(uint deviceIndex, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        { pose = Matrix4x4.Identity; return false; }
        public bool TryGetHeadLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        { pose = Matrix4x4.Identity; return false; }
        public bool TryGetControllerLocalPose(bool leftHand, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        { pose = Matrix4x4.Identity; return false; }
        public bool TryGetTrackerLocalPose(string trackerUserPath, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        { pose = Matrix4x4.Identity; return false; }
        public bool TryGetHeadToEyeLocalPose(bool leftEye, out Matrix4x4 pose)
        { pose = Matrix4x4.Identity; return false; }
        public bool TryGetEyeProjectionMatrix(bool leftEye, float nearPlane, float farPlane, out Matrix4x4 projection)
        { projection = Matrix4x4.Identity; return false; }
    }
}
