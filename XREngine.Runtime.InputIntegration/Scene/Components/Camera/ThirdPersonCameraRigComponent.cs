using System.ComponentModel;
using System.Numerics;
using XREngine.Components.Movement;
using XREngine.Components.Scene.Transforms;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Manages third-person camera boom framing independently from <see cref="CharacterPawnComponent"/>,
/// including centered zoom, lowered close left/right shoulder presets, and stance-aware
/// vertical pivot smoothing across standing, crouched, prone, and airborne states.
/// </summary>
[Category("Camera")]
[DisplayName("Third-Person Camera Rig")]
[Description("Controls third-person boom distance, shoulder views, and stance-aware camera pivot height smoothing.")]
public sealed class ThirdPersonCameraRigComponent : XRComponent
{
    private BoomTransform? _cameraBoom;
    private Transform? _cameraPivotTransform;
    private CharacterMovement3DComponent? _characterMovement;
    private EThirdPersonCameraViewMode _viewMode = EThirdPersonCameraViewMode.Centered;

    private bool _zoomInHeld;
    private bool _zoomOutHeld;
    private bool _gamepadZoomInHeld;
    private bool _gamepadZoomOutHeld;

    private float _centeredBoomDistance = 2.15f;
    private float _centeredBoomMinDistance = 0.95f;
    private float _centeredBoomMaxDistance = 2.55f;
    private float _centeredBoomZoomSpeed = 2.0f;
    private float _shoulderBoomDistance = 1.12f;
    private float _shoulderLateralOffset = 0.34f;
    private float _shoulderVerticalOffset = -0.06f;

    private float _standingPivotHeight = 1.18f;
    private float _crouchedPivotHeight = 0.82f;
    private float _pronePivotHeight = 0.40f;
    private float _groundedVerticalFollowSpeed = 9.5f;
    private float _airborneRiseFollowSpeed = 2.4f;
    private float _airborneFallFollowSpeed = 4.8f;
    private float _framingBlendSpeed = 10.0f;
    private bool _affectedByTimeDilation = true;

    private bool _centeredBoomDistanceInitialized;
    private bool _cameraPivotWorldYInitialized;
    private float _smoothedCameraPivotWorldY;
    private float _smoothedVerticalFollowSpeed;

    /// <summary>
    /// Target third-person <see cref="BoomTransform"/>. When unset, resolved from this node
    /// or beneath <see cref="CameraPivotTransform"/>.
    /// </summary>
    [Category("References")]
    public BoomTransform? CameraBoom
    {
        get => _cameraBoom;
        set
        {
            if (SetField(ref _cameraBoom, value))
                _centeredBoomDistanceInitialized = false;
        }
    }

    /// <summary>
    /// Pivot transform positioned above the character whose local Y tracks stance height smoothly.
    /// </summary>
    [Category("References")]
    public Transform? CameraPivotTransform
    {
        get => _cameraPivotTransform;
        set
        {
            if (SetField(ref _cameraPivotTransform, value))
                _cameraPivotWorldYInitialized = false;
        }
    }

    /// <summary>
    /// Character movement component queried for stance (<c>CrouchState</c>) and grounded status.
    /// </summary>
    [Category("References")]
    public CharacterMovement3DComponent? CharacterMovement
    {
        get => _characterMovement;
        set => SetField(ref _characterMovement, value);
    }

    /// <summary>
    /// Active framing preset (centered above head, left shoulder, or right shoulder).
    /// </summary>
    [Category("Framing")]
    public EThirdPersonCameraViewMode ViewMode
    {
        get => _viewMode;
        set => SetField(ref _viewMode, value);
    }

    /// <summary>Minimum centered above-head boom distance when zooming in.</summary>
    [Category("Zoom")]
    public float CenteredBoomMinDistance
    {
        get => _centeredBoomMinDistance;
        set => SetField(ref _centeredBoomMinDistance, MathF.Max(0.5f, value));
    }

    /// <summary>Maximum centered above-head boom distance when zooming out.</summary>
    [Category("Zoom")]
    public float CenteredBoomMaxDistance
    {
        get => _centeredBoomMaxDistance;
        set => SetField(ref _centeredBoomMaxDistance, MathF.Max(_centeredBoomMinDistance, value));
    }

    /// <summary>Zoom speed in meters per second while holding zoom-in or zoom-out.</summary>
    [Category("Zoom")]
    public float CenteredBoomZoomSpeed
    {
        get => _centeredBoomZoomSpeed;
        set => SetField(ref _centeredBoomZoomSpeed, MathF.Max(0.1f, value));
    }

    /// <summary>Boom distance used in lowered close left/right shoulder views.</summary>
    [Category("Shoulder View")]
    public float ShoulderBoomDistance
    {
        get => _shoulderBoomDistance;
        set => SetField(ref _shoulderBoomDistance, MathF.Max(0.5f, value));
    }

    /// <summary>Lateral camera-local offset magnitude in meters for shoulder views.</summary>
    [Category("Shoulder View")]
    public float ShoulderLateralOffset
    {
        get => _shoulderLateralOffset;
        set => SetField(ref _shoulderLateralOffset, MathF.Max(0.0f, value));
    }

    /// <summary>Vertical camera-local offset in meters (negative lowers toward chest) for shoulder views.</summary>
    [Category("Shoulder View")]
    public float ShoulderVerticalOffset
    {
        get => _shoulderVerticalOffset;
        set => SetField(ref _shoulderVerticalOffset, value);
    }

    /// <summary>Target pivot height above the character root while standing.</summary>
    [Category("Pivot Height")]
    public float StandingPivotHeight
    {
        get => _standingPivotHeight;
        set => SetField(ref _standingPivotHeight, value);
    }

    /// <summary>Target pivot height above the character root while crouched.</summary>
    [Category("Pivot Height")]
    public float CrouchedPivotHeight
    {
        get => _crouchedPivotHeight;
        set => SetField(ref _crouchedPivotHeight, value);
    }

    /// <summary>Target pivot height above the character root while prone.</summary>
    [Category("Pivot Height")]
    public float PronePivotHeight
    {
        get => _pronePivotHeight;
        set => SetField(ref _pronePivotHeight, value);
    }

    /// <summary>Vertical follow speed while grounded.</summary>
    [Category("Smoothing")]
    public float GroundedVerticalFollowSpeed
    {
        get => _groundedVerticalFollowSpeed;
        set => SetField(ref _groundedVerticalFollowSpeed, MathF.Max(0.1f, value));
    }

    /// <summary>Vertical follow speed while rising in the air.</summary>
    [Category("Smoothing")]
    public float AirborneRiseFollowSpeed
    {
        get => _airborneRiseFollowSpeed;
        set => SetField(ref _airborneRiseFollowSpeed, MathF.Max(0.1f, value));
    }

    /// <summary>Vertical follow speed while falling in the air.</summary>
    [Category("Smoothing")]
    public float AirborneFallFollowSpeed
    {
        get => _airborneFallFollowSpeed;
        set => SetField(ref _airborneFallFollowSpeed, MathF.Max(0.1f, value));
    }

    /// <summary>Exponential blend rate used when transitioning between centered and shoulder framing.</summary>
    [Category("Smoothing")]
    public float FramingBlendSpeed
    {
        get => _framingBlendSpeed;
        set => SetField(ref _framingBlendSpeed, MathF.Max(0.1f, value));
    }

    /// <summary>When true, framing smoothing uses dilated update time; otherwise undilated time.</summary>
    [Category("Smoothing")]
    public bool AffectedByTimeDilation
    {
        get => _affectedByTimeDilation;
        set => SetField(ref _affectedByTimeDilation, value);
    }

    /// <summary>Toggles the lowered, close over-the-left-shoulder camera view.</summary>
    public void ToggleLeftShoulderView()
    {
        ViewMode = _viewMode == EThirdPersonCameraViewMode.LeftShoulder
            ? EThirdPersonCameraViewMode.Centered
            : EThirdPersonCameraViewMode.LeftShoulder;
    }

    /// <summary>Toggles the lowered, close over-the-right-shoulder camera view.</summary>
    public void ToggleRightShoulderView()
    {
        ViewMode = _viewMode == EThirdPersonCameraViewMode.RightShoulder
            ? EThirdPersonCameraViewMode.Centered
            : EThirdPersonCameraViewMode.RightShoulder;
    }

    /// <summary>Sets whether keyboard zoom-in is held.</summary>
    public void SetZoomInHeld(bool held)
        => _zoomInHeld = held;

    /// <summary>Sets whether keyboard zoom-out is held.</summary>
    public void SetZoomOutHeld(bool held)
        => _zoomOutHeld = held;

    /// <summary>Sets whether gamepad zoom-in (D-Pad Up) is held.</summary>
    public void SetGamepadZoomInHeld(bool held)
        => _gamepadZoomInHeld = held;

    /// <summary>Sets whether gamepad zoom-out (D-Pad Down) is held.</summary>
    public void SetGamepadZoomOutHeld(bool held)
        => _gamepadZoomOutHeld = held;

    /// <summary>Clears all held zoom flags when input focus is released.</summary>
    public void ClearHeldInputs()
    {
        _zoomInHeld = false;
        _zoomOutHeld = false;
        _gamepadZoomInHeld = false;
        _gamepadZoomOutHeld = false;
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Normal, (int)ETickOrder.Scene, TickCameraFraming);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, (int)ETickOrder.Scene, TickCameraFraming);
        ClearHeldInputs();
        base.OnComponentDeactivated();
    }

    private void TickCameraFraming()
    {
        IRuntimeTransformServices? services = RuntimeTransformServices.Current;
        if (services is null)
            return;

        float dt = _affectedByTimeDilation
            ? services.DilatedUpdateDeltaSeconds
            : services.UndilatedUpdateDeltaSeconds;
        StepFraming(dt);
    }

    /// <summary>
    /// Advances boom distance, shoulder offset, and vertical pivot smoothing by <paramref name="deltaSeconds"/>.
    /// Safe to call directly in deterministic tests.
    /// </summary>
    public void StepFraming(float deltaSeconds)
    {
        BoomTransform? boom = ResolveCameraBoom();
        if (boom is null)
            return;

        if (!_centeredBoomDistanceInitialized)
        {
            float initialLength = float.IsFinite(boom.MaxLength) && boom.MaxLength > 0.1f
                ? boom.MaxLength
                : _centeredBoomDistance;
            _centeredBoomMaxDistance = MathF.Max(_centeredBoomMaxDistance, MathF.Max(_centeredBoomMinDistance, initialLength));
            _centeredBoomDistance = Math.Clamp(initialLength, _centeredBoomMinDistance, _centeredBoomMaxDistance);
            _centeredBoomDistanceInitialized = true;
        }

        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0.0f)
            return;

        UpdatePivotHeight(deltaSeconds);

        bool zoomInHeld = _zoomInHeld || _gamepadZoomInHeld;
        bool zoomOutHeld = _zoomOutHeld || _gamepadZoomOutHeld;
        if (zoomInHeld || zoomOutHeld)
        {
            if (_viewMode != EThirdPersonCameraViewMode.Centered)
                ViewMode = EThirdPersonCameraViewMode.Centered;

            float zoomDelta = 0.0f;
            if (zoomInHeld)
                zoomDelta -= _centeredBoomZoomSpeed * deltaSeconds;
            if (zoomOutHeld)
                zoomDelta += _centeredBoomZoomSpeed * deltaSeconds;

            _centeredBoomDistance = Math.Clamp(
                _centeredBoomDistance + zoomDelta,
                _centeredBoomMinDistance,
                _centeredBoomMaxDistance);
        }

        Vector3 targetOffset;
        float targetDistance;
        switch (_viewMode)
        {
            case EThirdPersonCameraViewMode.LeftShoulder:
                targetOffset = new Vector3(-_shoulderLateralOffset, _shoulderVerticalOffset, 0.0f);
                targetDistance = _shoulderBoomDistance;
                break;
            case EThirdPersonCameraViewMode.RightShoulder:
                targetOffset = new Vector3(_shoulderLateralOffset, _shoulderVerticalOffset, 0.0f);
                targetDistance = _shoulderBoomDistance;
                break;
            default:
                targetOffset = Vector3.Zero;
                targetDistance = _centeredBoomDistance;
                break;
        }

        float alpha = 1.0f - MathF.Exp(-_framingBlendSpeed * deltaSeconds);
        Vector3 currentOffset = boom.ShoulderOffset;
        Vector3 nextOffset = Vector3.Lerp(currentOffset, targetOffset, alpha);
        if (Vector3.DistanceSquared(nextOffset, targetOffset) < 1.0e-6f)
            nextOffset = targetOffset;

        float nextDistance = boom.MaxLength + (targetDistance - boom.MaxLength) * alpha;
        if (MathF.Abs(nextDistance - targetDistance) < 1.0e-4f)
            nextDistance = targetDistance;

        boom.ShoulderOffset = nextOffset;
        boom.MaxLength = nextDistance;
    }

    private void UpdatePivotHeight(float dt)
    {
        Transform? pivotTransform = ResolveCameraPivotTransform();
        CharacterMovement3DComponent? movement = ResolveCharacterMovement();
        if (pivotTransform is null || movement is null)
            return;

        // Anchor the camera pivot in world Y using the live current-frame rigid-body position
        // rather than a 1-frame-stale cached WorldTranslation, so rapid fall-to-ground deceleration
        // on landing never injects a single-frame vertical camera jolt.
        float parentWorldY = ResolveLivePivotParentWorldY(pivotTransform, movement);
        if (!float.IsFinite(parentWorldY))
            return;

        float targetStanceHeight = movement.CrouchState switch
        {
            CharacterMovement3DComponent.ECrouchState.Crouched => _crouchedPivotHeight,
            CharacterMovement3DComponent.ECrouchState.Prone => _pronePivotHeight,
            _ => _standingPivotHeight,
        };
        float desiredWorldY = parentWorldY + targetStanceHeight;

        if (!_cameraPivotWorldYInitialized || MathF.Abs(desiredWorldY - _smoothedCameraPivotWorldY) > 4.0f)
        {
            _smoothedCameraPivotWorldY = desiredWorldY;
            _smoothedVerticalFollowSpeed = _groundedVerticalFollowSpeed;
            _cameraPivotWorldYInitialized = true;
        }
        else
        {
            bool isGrounded = movement.CharacterController?.IsGrounded
                ?? (movement.MovementMode == CharacterMovement3DComponent.EMovementMode.Walking);
            float targetFollowSpeed = isGrounded
                ? _groundedVerticalFollowSpeed
                : desiredWorldY > _smoothedCameraPivotWorldY
                    ? _airborneRiseFollowSpeed
                    : _airborneFallFollowSpeed;
            float speedBlend = 1.0f - MathF.Exp(-8.0f * dt);
            _smoothedVerticalFollowSpeed += (targetFollowSpeed - _smoothedVerticalFollowSpeed) * speedBlend;
            float verticalAlpha = 1.0f - MathF.Exp(-_smoothedVerticalFollowSpeed * dt);
            _smoothedCameraPivotWorldY += (desiredWorldY - _smoothedCameraPivotWorldY) * verticalAlpha;
            if (isGrounded && MathF.Abs(desiredWorldY - _smoothedCameraPivotWorldY) < 1.0e-4f)
                _smoothedCameraPivotWorldY = desiredWorldY;
        }

        float localY = _smoothedCameraPivotWorldY - parentWorldY;
        Vector3 currentTranslation = pivotTransform.Translation;
        if (MathF.Abs(currentTranslation.Y - localY) > 1.0e-5f)
            pivotTransform.Translation = new Vector3(currentTranslation.X, localY, currentTranslation.Z);
    }

    private float ResolveLivePivotParentWorldY(Transform pivotTransform, CharacterMovement3DComponent movement)
    {
        SceneNode? pivotParent = pivotTransform.SceneNode?.Parent;
        if (SceneNode?.Transform is RigidBodyTransform rb)
        {
            if (ReferenceEquals(pivotParent, SceneNode))
                return rb.Position.Y;
            if (ReferenceEquals(pivotParent?.Parent, SceneNode) && pivotParent.Transform is Transform footTransform)
                return rb.Position.Y + footTransform.Translation.Y;
            return rb.Position.Y - movement.HalfHeight;
        }

        return pivotParent?.Transform is { } parentTransform
            ? parentTransform.WorldTranslation.Y
            : Transform.WorldTranslation.Y - movement.HalfHeight;
    }

    private BoomTransform? ResolveCameraBoom()
    {
        if (_cameraBoom is not null)
            return _cameraBoom;

        if (SceneNode?.Transform is BoomTransform ownBoom)
        {
            _cameraBoom = ownBoom;
            return ownBoom;
        }

        Transform? pivot = ResolveCameraPivotTransform();
        if (pivot?.SceneNode?.FirstChild?.Transform is BoomTransform childBoom)
        {
            _cameraBoom = childBoom;
            return childBoom;
        }

        CharacterPawnComponent? pawn = SceneNode?.GetComponent<CharacterPawnComponent>();
        if (pawn?.CameraComponent?.SceneNode?.Parent?.Transform is BoomTransform pawnBoom)
        {
            _cameraBoom = pawnBoom;
            return pawnBoom;
        }

        return null;
    }

    private Transform? ResolveCameraPivotTransform()
    {
        if (_cameraPivotTransform is not null)
            return _cameraPivotTransform;

        if (_cameraBoom?.SceneNode?.Parent?.Transform is Transform boomParent)
        {
            _cameraPivotTransform = boomParent;
            return boomParent;
        }

        CharacterPawnComponent? pawn = SceneNode?.GetComponent<CharacterPawnComponent>();
        if (pawn?.ViewRotationTransform is { } viewPivot)
        {
            _cameraPivotTransform = viewPivot;
            return viewPivot;
        }

        return null;
    }

    private CharacterMovement3DComponent? ResolveCharacterMovement()
    {
        if (_characterMovement is not null)
            return _characterMovement;

        SceneNode? current = SceneNode;
        while (current is not null)
        {
            if (current.GetComponent<CharacterMovement3DComponent>() is { } movement)
            {
                _characterMovement = movement;
                return movement;
            }
            current = current.Parent;
        }

        return null;
    }
}
