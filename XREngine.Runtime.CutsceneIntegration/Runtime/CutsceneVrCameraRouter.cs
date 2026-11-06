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
/// full discontinuity fades the headset to black over half of the fade time. At black, the rig moves, the router publishes a
/// teleport and resets the eye view history, and the headset stays black for at least <see cref="BlackHoldRenderFrames"/>
/// render frames, so the frames in flight with the old pose are not visible. Then the headset fades back in over the other half.
/// <see cref="Detach"/> puts back the local position and rotation that the rig origin had at <see cref="Attach"/>.
/// </para>
/// <para>
/// No mode writes an eye camera pose or the head pose. The head pose stays local to the rig origin, so the user keeps head tracking.
/// The router does not change the selected view mode, the render pipeline, or the desktop mirror mode.
/// A failed headset fade never throws out of the router: the router puts the rig back, adds one diagnostic, and stops.
/// </para>
/// </summary>
public sealed class CutsceneVrCameraRouter
{
    /// <summary>The minimum number of render frames that the headset stays black after a rig move.</summary>
    public const int BlackHoldRenderFrames = 2;

    /// <summary>
    /// The maximum time in seconds that the headset stays black after a rig move while it waits for render frames.
    /// It keeps the headset from staying black when no frame renders.
    /// </summary>
    public const double MaximumBlackHoldSeconds = 1.0;

    private enum EFadePhase
    {
        None,
        FadingOut,
        HoldingBlack,
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
    private ulong _holdStartFrame;
    private Vector3 _originalTranslation;
    private Quaternion _originalRotation;
    private bool _attached;
    private bool _rigMoved;
    private bool _faded;
    private bool _failed;
    private bool _teleportPublisherMissingReported;

    /// <summary>Creates a router for the <see cref="ECutsceneVrCameraMode.RigAnchor"/> mode. Call <see cref="Validate"/> first.</summary>
    /// <param name="player">The player that selects the shot camera.</param>
    /// <param name="host">The VR state and the headset fade.</param>
    /// <param name="rigOrigin">The rig origin: an ancestor of the tracked head.</param>
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

    /// <summary>The number of rig moves at black, each with a teleport and an eye view history reset.</summary>
    public int RigMoveCount { get; private set; }

    /// <summary>True while a fade to black, the black hold, or a fade back in runs.</summary>
    public bool IsFading => _phase != EFadePhase.None;

    /// <summary>True while the headset stays black after a rig move.</summary>
    public bool IsHoldingBlack => _phase == EFadePhase.HoldingBlack;

    /// <summary>True after the router moved the rig origin, until it puts the rig back.</summary>
    public bool IsRigMoved => _rigMoved;

    /// <summary>
    /// True after the headset fade or the VR session became unavailable during playback. The rig is back at its original pose,
    /// and it stays there.
    /// </summary>
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
                return ValidateRigOrigin(host, rigOrigin);

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
    /// A failed fade does not throw; it stops the router with a diagnostic.
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
    /// While the headset fades to black, the rig holds. At black, the rig moves to the shot camera and the headset stays black for
    /// the hold. Then it fades back in. Between cuts, the rig follows the shot camera. In a gap between shots, the rig holds.
    /// If the VR session or the headset fade stops, the rig goes back at once. It does not allocate.
    /// </summary>
    public void Update()
    {
        if (!_attached || _failed)
            return;

        if (!_host.IsInVR || !_host.CanFadeHeadset)
        {
            Fail("The VR session or the headset fade became unavailable during playback");
            return;
        }

        double now = _host.TimeSeconds;
        CameraComponent? active = _player.ActiveCamera;
        switch (_phase)
        {
            case EFadePhase.FadingOut:
                if (now < _phaseEndSeconds)
                    return;

                // The headset is black, so the move is not visible.
                _followedCamera = active;
                if (active is null)
                {
                    StartFadeIn(now);
                    return;
                }

                PlaceRig(active);
                OnRigMovedAtBlack();
                _phase = EFadePhase.HoldingBlack;
                _holdStartFrame = _host.RenderFrameId;
                _phaseEndSeconds = now + MaximumBlackHoldSeconds;
                return;

            case EFadePhase.HoldingBlack:
                if (active is not null && ReferenceEquals(active, _followedCamera))
                    PlaceRig(active);
                if (_host.RenderFrameId - _holdStartFrame >= BlackHoldRenderFrames || now >= _phaseEndSeconds)
                    StartFadeIn(now);
                return;

            case EFadePhase.FadingIn:
                if (now >= _phaseEndSeconds)
                {
                    _phase = EFadePhase.None;
                    _faded = false;
                }
                break;
        }

        if (active is not null && ReferenceEquals(active, _followedCamera))
            PlaceRig(active);
    }

    /// <summary>
    /// Stops the routing and puts back the local pose of the rig origin from <see cref="Attach"/>. If the rig moved, the headset
    /// goes black at once, the rig moves back, and the headset fades back in. A fade that runs ends with the scene visible.
    /// The rig goes back also when a fade fails. A call on a detached router does nothing.
    /// </summary>
    public void Detach()
    {
        if (!_attached)
            return;

        _attached = false;
        _player.ActiveCameraChanged -= _onActiveCameraChanged;
        _player.Discontinuity -= _onDiscontinuity;
        bool wasMoved = _rigMoved;
        bool canFade = !_failed && _host.IsInVR && _host.CanFadeHeadset;
        try
        {
            if (wasMoved && canFade && TryFade(0.0f, 1.0f))
                _faded = true;
        }
        finally
        {
            if (wasMoved)
                RestoreRig();
            _phase = EFadePhase.None;
            _followedCamera = null;
        }

        if (wasMoved && !_failed && _host.IsInVR)
        {
            NotifyTeleport();
            _host.ResetEyeViewHistory();
        }

        // Each fade that this router started ends with the scene visible.
        if (_faded)
            TryFade(_halfFadeSeconds, 0.0f);
        _faded = false;
    }

    /// <summary>
    /// Finds the reason why a transform cannot be the rig origin, or null if it can. The rig origin must be a <see cref="Transform"/>
    /// and a strict ancestor of the tracked head, so a move of it never writes the head pose or an eye camera.
    /// </summary>
    private static string? ValidateRigOrigin(ICutsceneVrHost host, TransformBase? rigOrigin)
    {
        const string Mode = nameof(ECutsceneVrCameraMode.RigAnchor);
        if (rigOrigin is null)
            return $"VR camera mode {Mode} needs a rig origin. Set {nameof(CutsceneDirectorComponent)}.{nameof(CutsceneDirectorComponent.VrRigOrigin)} before the open.";
        if (rigOrigin is not Transform)
            return $"VR camera mode {Mode} needs a rig origin of type {nameof(Transform)}, and the rig origin is a {rigOrigin.GetType().Name}.";

        TransformBase? head = host.HeadTransform;
        if (head is null)
            return $"VR camera mode {Mode} needs the tracked head of the VR session to check the rig origin, and the session published none.";
        if (ReferenceEquals(rigOrigin, head))
            return $"The rig origin is the tracked head. VR camera mode {Mode} never writes the head pose. Use an ancestor of the head.";
        if (host.IsEyeCameraTransform(rigOrigin))
            return $"The rig origin is a headset eye camera. VR camera mode {Mode} never writes an eye camera. Use an ancestor of the head.";

        for (TransformBase? parent = head.Parent; parent is not null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, rigOrigin))
                return null;
        }

        return $"The rig origin is not an ancestor of the tracked head, so a move of it does not carry the user. VR camera mode {Mode} needs an ancestor of the head.";
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
        // A second cut during the fade to black or the black hold moves the rig to the newest shot while the headset is black.
        if (!_attached || _failed || _phase is EFadePhase.FadingOut)
            return;

        if (_phase == EFadePhase.HoldingBlack)
        {
            _followedCamera = null;
            _phase = EFadePhase.FadingOut;
            _phaseEndSeconds = _host.TimeSeconds;
            return;
        }

        if (!_host.IsInVR || !_host.CanFadeHeadset || !TryFade(_halfFadeSeconds, 1.0f))
        {
            Fail("The headset fade failed at a camera cut");
            return;
        }

        _faded = true;
        _phase = EFadePhase.FadingOut;
        _phaseEndSeconds = _host.TimeSeconds + _halfFadeSeconds;
        CutFadeCount++;
    }

    private void StartFadeIn(double now)
    {
        if (!TryFade(_halfFadeSeconds, 0.0f))
        {
            Fail("The headset fade failed after a rig move");
            return;
        }

        _phase = EFadePhase.FadingIn;
        _phaseEndSeconds = now + _halfFadeSeconds;
    }

    private void OnRigMovedAtBlack()
    {
        RigMoveCount++;
        NotifyTeleport();

        // The eye views keep their poses. Only their temporal history and exposure adaptation restart at the new place.
        _host.ResetEyeViewHistory();
    }

    private void NotifyTeleport()
    {
        if (_host.TryPublishTeleport() || _teleportPublisherMissingReported)
            return;

        _teleportPublisherMissingReported = true;
        _diagnostics?.Add(new CutsceneCompileDiagnostic(-1, -1,
            "The application host installed no VR teleport publisher, so spectator follow and tracker code see the rig move as motion."));
    }

    private bool TryFade(float seconds, float opacity)
    {
        try
        {
            return _host.TryFadeHeadset(seconds, opacity);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A host must not throw, but a cutscene restore must not depend on it.
            return false;
        }
    }

    private void Fail(string reason)
    {
        // Without a fade, a move of the rig would be visible. The rig goes back to the pose of the user and stays there.
        _failed = true;
        _phase = EFadePhase.None;
        _followedCamera = null;
        try
        {
            // A fade that this router started must not stay black, if the runtime still accepts a call.
            if (_faded)
                TryFade(0.0f, 0.0f);
        }
        finally
        {
            _faded = false;
            if (_rigMoved)
                RestoreRig();
        }

        _diagnostics?.Add(new CutsceneCompileDiagnostic(-1, -1,
            $"{reason}, so VR camera mode {nameof(ECutsceneVrCameraMode.RigAnchor)} stopped. The rig origin is back at its original pose."));
    }

    private void RestoreRig()
    {
        _rigMoved = false;
        _rigOrigin.Translation = _originalTranslation;
        _rigOrigin.Rotation = _originalRotation;
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
