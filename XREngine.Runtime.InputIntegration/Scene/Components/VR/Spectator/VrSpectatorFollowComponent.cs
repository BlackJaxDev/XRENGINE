using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Transforms;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Scene.Transforms;
using SpectatorOutput = XREngine.Components.Lights.VrSpectatorOutputComponent;

namespace XREngine.Components.VR;

/// <summary>A local VR player's independent monoscopic follow view, evaluated after animation.</summary>
public sealed class VrSpectatorFollowComponent : XRComponent
{
    private readonly VrSpectatorFollowState _follow = new();
    private VRPlayerCharacterComponent? _player;
    private IHumanoidVrCalibrationRig? _rig;
    private IVRIKSolverHandle? _solver;
    private TransformBase? _playerRoot;
    private CameraComponent? _camera;
    private BoomTransform? _boom;
    private SpectatorOutput? _output;
    private long _discontinuityVersion = -1;
    private VrSpectatorFollowSettings _settings = new();
    private Quaternion _lastRotation = Quaternion.Identity;
    private IDisposable? _firstPersonVisibility;
    public VRPlayerCharacterComponent? Player { get => _player; set { if (SetField(ref _player, value)) { _rig = value?.GetHumanoid(); _solver = value?.GetIKSolver(); ResetFollow(); } } }
    public TransformBase? PlayerRoot { get => _playerRoot; set { if (SetField(ref _playerRoot, value)) ResetFollow(); } }
    public CameraComponent? Camera { get => _camera; set => SetField(ref _camera, value); }
    public BoomTransform? Boom { get => _boom; set => SetField(ref _boom, value); }
    public SpectatorOutput? Output { get => _output; set => SetField(ref _output, value); }
    public VrSpectatorFollowSettings Settings { get => _settings; set => SetField(ref _settings, value ?? throw new ArgumentNullException(nameof(value))); }

    /// <summary>Transfers ownership of the explicit eye-only visibility scope to this rig.</summary>
    public void OwnFirstPersonVisibility(IDisposable visibility)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        if (_firstPersonVisibility is not null)
            throw new InvalidOperationException("Clear existing eye visibility before creating its replacement.");
        SetField(ref _firstPersonVisibility, visibility);
    }

    public void ClearFirstPersonVisibility()
    {
        IDisposable? previous = _firstPersonVisibility;
        SetField(ref _firstPersonVisibility, null);
        previous?.Dispose();
    }

    /// <summary>May be driven by the active player even while spectator rendering is disabled.</summary>
    public void UpdateFirstPersonVisibility(bool isCalibrating)
    {
        if (_firstPersonVisibility is VrFirstPersonVisibilityScope visibility)
            visibility.SetHiddenInEyes(!isCalibrating);
    }

    protected override void OnDestroying()
    {
        ClearFirstPersonVisibility();
        base.OnDestroying();
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _rig = Player?.GetHumanoid();
        _solver = Player?.GetIKSolver();
        ResetFollow();
        RegisterTick(ETickGroup.Late, (int)ETickOrder.Scene + 1, TickFollow);
    }
    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, (int)ETickOrder.Scene + 1, TickFollow);
        base.OnComponentDeactivated();
    }

    public void ResetFollow()
    {
        _discontinuityVersion = -1;
        _follow.Reset();
        Camera?.Camera.InvalidateTemporalHistory();
        Output?.ResetHistory();
    }

    private void TickFollow() => Evaluate(RuntimeTransformServices.Current?.UndilatedUpdateDeltaSeconds ?? 0f);

    /// <summary>Simulation-thread entry point, also used by deterministic follow tests.</summary>
    public void Evaluate(float deltaSeconds)
    {
        if (PlayerRoot is null || Camera is null || Boom?.Parent is not Transform anchor || Camera.Transform is not Transform cameraTransform)
            return;
        long version = RuntimeVrDiscontinuityServices.Version;
        bool reset = version != _discontinuityVersion;
        if (reset)
        {
            _follow.Reset();
            _rig = Player?.GetHumanoid();
            _solver = Player?.GetIKSolver();
            Camera.Camera.InvalidateTemporalHistory();
            Output?.ResetHistory();
            _discontinuityVersion = version;
        }
        Matrix4x4? hips = null;
        Vector3? semanticForward = null;
        UpdateFirstPersonVisibility(Player?.IsCalibrating == true);
        if (_rig is not null)
        {
            var source = _rig.GetIKTarget(EHumanoidIKTarget.Hips);
            if (source.tfm is not null && (source.tfm is not IVrTrackingPoseSource pose || pose.PoseCurrentlyUsable)
                && _solver?.GetCalibratedTarget(EHumanoidIKTarget.Hips) is { } calibratedHips)
                hips = calibratedHips.WorldMatrix;
        }
        if (_rig is not null)
        {
            if (hips is Matrix4x4 hipsWorld && _rig.TryGetVrSemanticForwardInHipsBindSpace(out Vector3 hipsForward))
                semanticForward = Vector3.TransformNormal(hipsForward, hipsWorld);
            else if (_rig.TryGetVrSemanticForwardInRootBindSpace(out Vector3 rootForward))
                semanticForward = Vector3.TransformNormal(rootForward, _rig.RootTransform.WorldMatrix);
        }
        VrSpectatorFollowPose follow = _follow.Evaluate(PlayerRoot.WorldMatrix, hips, Settings, deltaSeconds, semanticForward);
        VrSpectatorFollowState.ApplyWorldPose(anchor, follow.BoomOrigin);
        Boom.MaxLength = Settings.Distance;
        Boom.Evaluate(deltaSeconds, reset);
        Boom.RecalculateMatrices(forceWorldRecalc: true);
        Vector3 position = Boom.WorldTranslation;
        _lastRotation = VrSpectatorFollowState.LookAt(position, follow.AimPoint, _lastRotation);
        Matrix4x4 cameraWorld = Matrix4x4.CreateFromQuaternion(_lastRotation);
        cameraWorld.Translation = position;
        VrSpectatorFollowState.ApplyWorldPose(cameraTransform, cameraWorld);
        if (Camera.Camera.Parameters is XRPerspectiveCameraParameters lens)
        {
            lens.VerticalFieldOfView = Settings.FieldOfView;
            if (Output is { IsActive: true })
                lens.AspectRatio = Output.Width / (float)Output.Height;
        }
    }

    /// <summary>Changes only a desktop viewport, never pawn possession, XR cameras, tracking origin, or audio.</summary>
    public void RouteDesktop(XRViewport viewport, CameraComponent camera)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(camera);
        viewport.CameraComponent = camera;
        if (Output is not null)
            Output.IsActive = !ReferenceEquals(camera, Camera);
        camera.Camera.InvalidateTemporalHistory();
        ResetFollow();
    }
}
