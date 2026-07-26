using System.Numerics;
using OpenVR.NET;
using OpenVR.NET.Manifest;
using Valve.VR;
using XREngine.Extensions;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Models.Materials;

namespace XREngine;

/// <summary>
/// Owns process-wide VR rendering state. Runtime startup and transport remain application-host
/// capabilities supplied through <see cref="LifecycleServices"/>.
/// </summary>
public sealed class RuntimeVrState
{
    public enum VRRuntime
    {
        None,
        OpenVR,
        OpenXR,
    }

    private readonly Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>> _actions = [];
    private float _ipdScalar = 1.0f;
    private float _realWorldHeight = 1.8f;
    private float _desiredAvatarHeight = 1.8f;
    private float _modelHeight = 1.0f;
    private VR? _openVrApi;
    private XRViewport? _leftEyeViewport;
    private XRViewport? _rightEyeViewport;
    private (XRCamera? LeftEyeCamera, XRCamera? RightEyeCamera, IRuntimeRenderWorld? World, SceneNode? HMDNode) _viewInformation;

    public IRuntimeVrLifecycleServices LifecycleServices { get; set; } = NullRuntimeVrLifecycleServices.Instance;

    public VRRuntime ActiveRuntime { get; set; }
    public bool IsOpenVRActive => ActiveRuntime == VRRuntime.OpenVR;
    public bool IsOpenXRActive => ActiveRuntime == VRRuntime.OpenXR;
    public bool IsInVR { get; set; }

    public OpenXRAPI? OpenXRApi { get; set; }
    public VR OpenVRApi
    {
        get => _openVrApi ??= new VR();
        set => _openVrApi = value ?? throw new ArgumentNullException(nameof(value));
    }
    public VR? OpenVRApiIfCreated => _openVrApi;

    public object? CalibrationSettings { get; set; }
    public Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>> Actions => _actions;

    public event Action<Dictionary<string, Dictionary<string, OpenVR.NET.Input.Action>>>? ActionsChanged;
    public event Action<bool>? OpenXRSessionRunningChanged;
    public event Action<RuntimeVrPoseTiming>? RecalcMatrixOnDraw;
    public event Action<float>? IPDScalarChanged;
    public event Action<float>? RealWorldHeightChanged;
    public event Action<float>? DesiredAvatarHeightChanged;
    public event Action<float>? ModelHeightChanged;

    public XRViewport? LeftEyeViewport
    {
        get => _leftEyeViewport;
        set
        {
            _leftEyeViewport = value;
            ApplyViewInformation(value, _viewInformation.LeftEyeCamera);
        }
    }

    public XRViewport? RightEyeViewport
    {
        get => _rightEyeViewport;
        set
        {
            _rightEyeViewport = value;
            ApplyViewInformation(value, _viewInformation.RightEyeCamera);
        }
    }
    public XRFrameBuffer? VRStereoRenderTarget { get; set; }
    public XRTexture2DArrayView? StereoLeftViewTexture { get; set; }
    public XRTexture2DArrayView? StereoRightViewTexture { get; set; }
    public XRTexture2D? VRLeftEyeViewTexture { get; set; }
    public XRMaterialFrameBuffer? VRLeftEyeRenderTarget { get; set; }
    public XRMaterialFrameBuffer? VRRightEyeRenderTarget { get; set; }
    public XRTexture2D? VRRightEyeViewTexture { get; set; }
    public AbstractRenderer? Renderer { get; set; }
    public XRViewport? StereoViewport { get; set; }
    public XRRenderPipelineInstance? TwoPassLeftPipeline { get; set; }
    public XRRenderPipelineInstance? TwoPassRightPipeline { get; set; }
    public RenderCommandCollection? SharedMeshRenderCommands { get; set; }
    public uint LastRenderWidth { get; set; }
    public uint LastRenderHeight { get; set; }
    public bool OpenVrRuntimeActiveForRender { get; set; }
    public bool EmulatedRenderActive { get; set; }
    public XRWindow? RenderWindow { get; set; }
    public VRTextureBounds_t SingleTextureBounds { get; set; } = new()
    {
        uMin = 0.0f,
        vMin = 0.0f,
        uMax = 1.0f,
        vMax = 1.0f,
    };
    public Texture_t EyeTexture { get; set; } = new()
    {
        eColorSpace = EColorSpace.Auto,
        eType = Valve.VR.ETextureType.OpenGL,
    };

    public (XRCamera? LeftEyeCamera, XRCamera? RightEyeCamera, IRuntimeRenderWorld? World, SceneNode? HMDNode) ViewInformation
    {
        get => _viewInformation;
        set
        {
            _viewInformation = value;
            ApplyViewInformation(_leftEyeViewport, value.LeftEyeCamera);
            ApplyViewInformation(_rightEyeViewport, value.RightEyeCamera);
        }
    }

    public float RealWorldIPD
    {
        get
        {
            switch (ActiveRuntime)
            {
                case VRRuntime.OpenVR:
                    VR? vr = OpenVRApiIfCreated;
                    if (vr?.Headset is null)
                        return 0f;

                    ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                    return vr.CVR.GetFloatTrackedDeviceProperty(
                        vr.Headset.DeviceIndex,
                        ETrackedDeviceProperty.Prop_UserIpdMeters_Float,
                        ref error);
                case VRRuntime.OpenXR:
                    return OpenXRApi?.TryGetLatestIPD(out float ipd) == true ? ipd : 0f;
                default:
                    return 0f;
            }
        }
    }

    public float IPDScalar
    {
        get => _ipdScalar;
        set
        {
            if (_ipdScalar == value)
                return;

            _ipdScalar = value;
            IPDScalarChanged?.Invoke(value);
        }
    }

    public float ScaledIPD => RealWorldIPD * ModelToRealWorldHeightRatio * IPDScalar;
    public float RealToDesiredAvatarHeightRatio => DesiredAvatarHeight / RealWorldHeight;
    public float ModelToRealWorldHeightRatio => RealWorldHeight / ModelHeight;
    public float RealWorldToDesiredAvatarHeightRatio => DesiredAvatarHeight / RealWorldHeight;

    public float RealWorldHeight
    {
        get => _realWorldHeight;
        set
        {
            if (_realWorldHeight == value)
                return;

            _realWorldHeight = value;
            RealWorldHeightChanged?.Invoke(value);
        }
    }

    public float DesiredAvatarHeight
    {
        get => _desiredAvatarHeight;
        set
        {
            if (_desiredAvatarHeight == value)
                return;

            _desiredAvatarHeight = value;
            DesiredAvatarHeightChanged?.Invoke(value);
        }
    }

    public float ModelHeight
    {
        get => _modelHeight;
        set
        {
            if (_modelHeight == value)
                return;

            _modelHeight = value;
            ModelHeightChanged?.Invoke(value);
        }
    }

    public Matrix4x4 CombinedProjectionMatrix { get; set; } = Matrix4x4.Identity;
    public Frustum? StereoCullingFrustum { get; set; }
    public bool IsPowerSaving { get; set; }
    public uint LastFrameSampleIndex { get; set; }
    public float GpuFrametime { get; set; }
    public float CpuFrametime { get; set; }
    public float TotalFrametime { get; set; }
    public float Framerate { get; set; }
    public float MaxFrametime { get; set; }

    public bool InitializeOpenXR(XRWindow? window)
        => LifecycleServices.InitializeOpenXR(window);

    public Task<bool> InitializeLocal(IActionManifest actionManifest, VrManifest vrManifest, XRWindow window)
        => LifecycleServices.InitializeLocal(actionManifest, vrManifest, window);

    public void InitRenderEmulated(XRWindow window)
        => LifecycleServices.InitRenderEmulated(window);

    public Task<bool> IninitializeClient(IActionManifest actionManifest, VrManifest vrManifest)
        => LifecycleServices.InitializeClient(actionManifest, vrManifest);

    public bool InitializeServer()
        => LifecycleServices.InitializeServer();

    public void StartInputClient()
        => LifecycleServices.StartInputClient();

    public void StopInputServer()
        => LifecycleServices.StopInputServer();

    public Task SendInputs()
        => LifecycleServices.SendInputs();

    /// <summary>
    /// Tries to render the OpenXR desktop mirror into the currently bound target.
    /// </summary>
    public bool TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
        => OpenXRApi?.TryRenderDesktopMirrorComposition(targetWidth, targetHeight) == true;

    public void InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming timing)
        => RecalcMatrixOnDraw?.Invoke(timing);

    public void NotifyActionsChanged()
        => ActionsChanged?.Invoke(_actions);

    public void NotifyOpenXRSessionRunningChanged(bool running)
        => OpenXRSessionRunningChanged?.Invoke(running);

    private void ApplyViewInformation(XRViewport? viewport, XRCamera? camera)
    {
        if (viewport is null)
            return;

        viewport.Camera = camera;
        viewport.WorldInstanceOverride = _viewInformation.World;
    }

    public readonly struct VRInputData
    {
        public ETrackedDeviceClass DeviceClass { get; init; }
        public ETrackingResult TrackingResult { get; init; }
        public bool Connected { get; init; }
        public bool PoseValid { get; init; }
        public Quaternion Rotation { get; init; }
        public Vector3 Position { get; init; }
        public Vector3 Velocity { get; init; }
        public Vector3 AngularVelocity { get; init; }
        public Quaternion RenderRotation { get; init; }
        public Vector3 RenderPosition { get; init; }
        public uint unPacketNum { get; init; }
        public ulong ulButtonPressed { get; init; }
        public ulong ulButtonTouched { get; init; }
        public VRControllerAxis_t rAxis0 { get; init; }
        public VRControllerAxis_t rAxis1 { get; init; }
        public VRControllerAxis_t rAxis2 { get; init; }
        public VRControllerAxis_t rAxis3 { get; init; }
        public VRControllerAxis_t rAxis4 { get; init; }
    }
}
