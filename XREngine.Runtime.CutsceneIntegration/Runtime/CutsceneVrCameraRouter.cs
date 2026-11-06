using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Cutscenes;

/// <summary>
/// Applies the VR camera mode of a cutscene (<see cref="ECutsceneVrCameraMode"/>).
/// <para>
/// <see cref="Validate"/> checks a mode against the VR runtime before the open. A mode that cannot run fails the open.
/// The director never uses another mode. Outside a VR session, each mode is valid, and only the desktop viewport shows the shots.
/// </para>
/// <para>
/// An instance runs <see cref="ECutsceneVrCameraMode.RigAnchor"/>: the rig origin follows the position and the heading of the
/// shot camera. The rig stays upright, because a pitch or a roll of the horizon causes discomfort. Each camera cut and each
/// full discontinuity fades the headset to black over half of the fade time, moves the rig while the headset is black, and
/// fades back in over the other half. <see cref="Detach"/> puts back the local position and rotation that the rig origin had
/// at <see cref="Attach"/>, behind a fade from black.
/// </para>
/// <para>
/// No mode writes an eye camera or the head pose. The head pose stays local to the rig origin, so the user keeps head tracking.
/// The router does not change the selected view mode, the render pipeline, or the desktop mirror mode.
/// </para>
/// </summary>
public sealed class CutsceneVrCameraRouter
{
    private enum EFadePhase
    {
        None,
        FadingOut,
        FadingIn,
    }

    private readonly CutscenePlayer _player;
    private readonly ICutsceneVrHost _host;
    private readonly Transform _rigOrigin;
    private readonly float _halfFadeSeconds;
    private readonly List<CutsceneCompileDiagnostic>? _diagnostics;
    private readonly Action<CameraComponent?> _onActiveCameraChanged;
    private readonly Action<CutsceneDiscontinuity> _onDiscontinuity;
    private CameraComponent? _followedCamera;
    private EFadePhase _phase;
    private double _phaseEndSeconds;
    private Vector3 _originalTranslation;
    private Quaternion _originalRotation;
    private bool _attached;
    private bool _rigMoved;
    private bool _failed;

    /// <summary>Creates a router for the <see cref="ECutsceneVrCameraMode.RigAnchor"/> mode. Call <see cref="Validate"/> first.</summary>
    /// <param name="player">The player that selects the shot camera.</param>
    /// <param name="host">The VR state and the headset fade.</param>
    /// <param name="rigOrigin">The rig origin: the transform whose child is the tracked headset.</param>
    /// <param name="rigCutFadeSeconds">The full time of one fade through black. See <see cref="CutsceneAsset.RigCutFadeSeconds"/>.</param>
    /// <param name="diagnostics">The list that receives a failure during playback, or null.</param>
    public CutsceneVrCameraRouter(
        CutscenePlayer player,
        ICutsceneVrHost host,
        Transform rigOrigin,
        float rigCutFadeSeconds,
        List<CutsceneCompileDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(rigOrigin);
        if (!float.IsFinite(rigCutFadeSeconds) || rigCutFadeSeconds <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(rigCutFadeSeconds), rigCutFadeSeconds, "The rig cut fade time must be finite and greater than zero.");

        _player = player;
        _host = host;
        _rigOrigin = rigOrigin;
        _halfFadeSeconds = rigCutFadeSeconds * 0.5f;
        _diagnostics = diagnostics;

        // The delegates are created once, so a frame never allocates.
        _onActiveCameraChanged = OnActiveCameraChanged;
        _onDiscontinuity = OnDiscontinuity;
    }

    /// <summary>The rig origin that the router moves.</summary>
    public Transform RigOrigin => _rigOrigin;

    /// <summary>The number of fades through black that the router started.</summary>
    public int CutFadeCount { get; private set; }

    /// <summary>True while a fade to black or a fade back in runs.</summary>
    public bool IsFading => _phase != EFadePhase.None;

    /// <summary>True after the router moved the rig origin, until <see cref="Detach"/> puts it back.</summary>
    public bool IsRigMoved => _rigMoved;

    /// <summary>True after the headset fade became unavailable during playback. The rig is back at its original pose, and it stays there.</summary>
    public bool HasFailed => _failed;

    /// <summary>
    /// Checks that a VR camera mode can run. The check does not change anything.
    /// </summary>
    /// <param name="mode">The mode of the cutscene.</param>
    /// <param name="host">The VR state.</param>
    /// <param name="desktopViewport">The viewport that the director claims, or null.</param>
    /// <param name="rigOrigin">The rig origin of the director, or null.</param>
    /// <returns>The reason why the mode cannot run, or null if it can run.</returns>
    public static string? Validate(ECutsceneVrCameraMode mode, ICutsceneVrHost host, XRViewport? desktopViewport, TransformBase? rigOrigin)
    {
        ArgumentNullException.ThrowIfNull(host);

        // An eye camera belongs to the VR runtime. A claim on its viewport would take the headset view away from the user.
        if (desktopViewport is not null && host.IsEyeViewport(desktopViewport))
            return "The target viewport renders a headset eye. A cutscene claims only a desktop viewport, and it never changes an eye camera.";

        if (!host.IsInVR)
            return null;

        switch (mode)
        {
            case ECutsceneVrCameraMode.SpectatorOnly:
                return null;

            case ECutsceneVrCameraMode.RigAnchor:
                if (!host.CanFadeHeadset)
                    return $"VR camera mode {nameof(ECutsceneVrCameraMode.RigAnchor)} needs a headset fade, and the {host.ActiveRuntime} runtime gives none. The cutscene does not use another mode.";
                if (rigOrigin is null)
                    return $"VR camera mode {nameof(ECutsceneVrCameraMode.RigAnchor)} needs a rig origin. Set {nameof(CutsceneDirectorComponent)}.{nameof(CutsceneDirectorComponent.VrRigOrigin)} before the open.";
                if (rigOrigin is not Transform)
                    return $"VR camera mode {nameof(ECutsceneVrCameraMode.RigAnchor)} needs a rig origin of type {nameof(Transform)}, and the rig origin is a {rigOrigin.GetType().Name}.";
                return null;

            case ECutsceneVrCameraMode.WorldScreen:
                return $"VR camera mode {nameof(ECutsceneVrCameraMode.WorldScreen)} is not supported: the engine has no world-surface consumer for a camera capture. The cutscene does not use another mode.";

            default:
                return $"VR camera mode {(int)mode} is not known. The cutscene does not use another mode.";
        }
    }

    /// <summary>
    /// Gets a note when the desktop does not show the claimed viewport during a VR session, because the selected mirror mode
    /// shows the headset image or nothing. The router does not change the mirror mode.
    /// </summary>
    /// <param name="host">The VR state.</param>
    /// <param name="desktopViewport">The viewport that the director claims, or null.</param>
    /// <param name="note">The note, or null.</param>
    /// <returns>True if there is a note.</returns>
    public static bool TryGetDesktopVisibilityNote(ICutsceneVrHost host, XRViewport? desktopViewport, [NotNullWhen(true)] out string? note)
    {
        ArgumentNullException.ThrowIfNull(host);
        note = null;
        if (desktopViewport is null || !host.IsInVR)
            return false;

        EVrMirrorMode mirror = host.MirrorMode;
        if (mirror is not (EVrMirrorMode.Off or EVrMirrorMode.BlitSubmittedEye or EVrMirrorMode.CyclopeanReconstruct))
            return false;

        note = $"The VR mirror mode is {mirror}, so the desktop does not show the cutscene camera. The mirror mode does not change.";
        return true;
    }

    /// <summary>
    /// Starts to move the rig origin with the shot camera. It records the local pose of the rig origin, so <see cref="Detach"/>
    /// can put it back. If the player shows a shot now, a fade through black starts. A second call does nothing.
    /// </summary>
    public void Attach()
    {
        if (_attached)
            return;

        _attached = true;
        _failed = false;
        _originalTranslation = _rigOrigin.Translation;
        _originalRotation = _rigOrigin.Rotation;
        _player.ActiveCameraChanged += _onActiveCameraChanged;
        _player.Discontinuity += _onDiscontinuity;
        if (_player.ActiveCamera is not null)
            RequestCut();
    }

    /// <summary>
    /// Advances the fade and moves the rig origin. Call it once in each frame, after the player tick.
    /// While the headset fades to black, the rig holds. At black, the rig moves to the shot camera, and the headset fades back in.
    /// Between cuts, the rig follows the shot camera. In a gap between shots, the rig holds. It does not allocate.
    /// </summary>
    public void Update()
    {
        if (!_attached || _failed)
            return;

        if (_phase != EFadePhase.None && !_host.CanFadeHeadset)
        {
            FailForLostFade();
            return;
        }

        double now = _host.TimeSeconds;
        if (_phase == EFadePhase.FadingOut)
        {
            if (now < _phaseEndSeconds)
                return;

            // The headset is black, so the move is not visible.
            CameraComponent? camera = _player.ActiveCamera;
            _followedCamera = camera;
            if (camera is not null)
                PlaceRig(camera);

            _host.FadeHeadset(_halfFadeSeconds, 0.0f);
            _phase = EFadePhase.FadingIn;
            _phaseEndSeconds = now + _halfFadeSeconds;
            return;
        }

        if (_phase == EFadePhase.FadingIn && now >= _phaseEndSeconds)
            _phase = EFadePhase.None;

        CameraComponent? active = _player.ActiveCamera;
        if (active is not null && ReferenceEquals(active, _followedCamera))
            PlaceRig(active);
    }

    /// <summary>
    /// Stops the routing and puts back the local pose of the rig origin from <see cref="Attach"/>. If the rig moved, the headset
    /// goes black at once, the rig moves back, and the headset fades back in. A fade that runs ends with the scene visible.
    /// A call on a detached router does nothing.
    /// </summary>
    public void Detach()
    {
        if (!_attached)
            return;

        _attached = false;
        _player.ActiveCameraChanged -= _onActiveCameraChanged;
        _player.Discontinuity -= _onDiscontinuity;
        bool canFade = !_failed && _host.CanFadeHeadset;
        if (_rigMoved)
        {
            if (canFade)
                _host.FadeHeadset(0.0f, 1.0f);
            RestoreRig();
            if (canFade)
                _host.FadeHeadset(_halfFadeSeconds, 0.0f);
        }
        else if (_phase != EFadePhase.None && canFade)
        {
            _host.FadeHeadset(_halfFadeSeconds, 0.0f);
        }

        _phase = EFadePhase.None;
        _followedCamera = null;
    }

    private void OnActiveCameraChanged(CameraComponent? camera)
    {
        // In a gap between shots, the rig holds. The camera of the next shot always starts a fade, also when it is the same camera.
        if (camera is null)
        {
            _followedCamera = null;
            return;
        }

        if (!ReferenceEquals(camera, _followedCamera))
            RequestCut();
    }

    private void OnDiscontinuity(CutsceneDiscontinuity discontinuity)
    {
        // A clock resync is a small correction inside one shot, so the rig follows with no fade.
        if (discontinuity.Kind == ECutsceneDiscontinuityKind.Full && _player.ActiveCamera is not null)
            RequestCut();
    }

    private void RequestCut()
    {
        // A second cut during the fade to black moves the rig to the newest shot at the same black frame.
        if (_failed || _phase == EFadePhase.FadingOut)
            return;

        if (!_host.CanFadeHeadset)
        {
            FailForLostFade();
            return;
        }

        _host.FadeHeadset(_halfFadeSeconds, 1.0f);
        _phase = EFadePhase.FadingOut;
        _phaseEndSeconds = _host.TimeSeconds + _halfFadeSeconds;
        CutFadeCount++;
    }

    private void FailForLostFade()
    {
        // Without a fade, a move of the rig would be visible. The rig goes back to the pose of the user and stays there.
        _failed = true;
        _phase = EFadePhase.None;
        _followedCamera = null;
        if (_rigMoved)
            RestoreRig();
        _diagnostics?.Add(new CutsceneCompileDiagnostic(-1, -1,
            $"The headset fade became unavailable during playback, so VR camera mode {nameof(ECutsceneVrCameraMode.RigAnchor)} stopped. The rig origin is back at its original pose."));
    }

    private void RestoreRig()
    {
        _rigOrigin.Translation = _originalTranslation;
        _rigOrigin.Rotation = _originalRotation;
        _rigMoved = false;
    }

    private void PlaceRig(CameraComponent camera)
    {
        TransformBase cameraTransform = camera.Transform;

        // The player wrote local values in this tick, and the transform flush runs later, so the world matrices are recalculated here.
        RefreshWorldMatrix(cameraTransform);
        if (_rigOrigin.Parent is { } parent)
            RefreshWorldMatrix(parent);

        Matrix4x4 cameraWorld = cameraTransform.WorldMatrix;
        TransformBase.GetDirectionsXZ(cameraWorld, out Vector3 forward, out _);
        if (!float.IsFinite(forward.X) || !float.IsFinite(forward.Z) || forward.LengthSquared() < 1e-8f)
            forward = Globals.Forward;

        // Only the heading is used, so the rig stays upright.
        Quaternion heading = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, forward, Globals.Up));
        _rigOrigin.SetWorldTranslationRotation(cameraWorld.Translation, heading);
        _rigMoved = true;
    }

    /// <summary>
    /// Recalculates the world matrix of a transform and its ancestors, from the root down, so a local change of this tick
    /// is in the matrix. It does not allocate.
    /// </summary>
    /// <returns>True if the world matrix changed, so the children must also be recalculated.</returns>
    private static bool RefreshWorldMatrix(TransformBase transform)
    {
        bool parentChanged = transform.Parent is { } parent && RefreshWorldMatrix(parent);
        return transform.RecalculateMatrices(forceWorldRecalc: parentChanged) || parentChanged;
    }
}
