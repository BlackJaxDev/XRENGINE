using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Transforms;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR;

/// <summary>
/// Updates a third-person camera anchor from the local player's published avatar pose.
/// The child boom performs the obstruction sweep; this component never moves an OpenXR eye.
/// </summary>
public sealed class VrSpectatorFollowComponent : XRComponent
{
    private const float DirectionEpsilonSquared = 1.0e-8f;
    private VRPlayerCharacterComponent? _player;
    private Transform? _anchor;
    private BoomTransform? _boom;
    private bool _hasPose;
    private long _lastDiscontinuityGeneration = -1;
    private Vector3 _position;
    private Vector3 _forward = Globals.Forward;
    private float _headingRadians;
    private float _height = 1.6f;
    private float _distance = 3.0f;
    private float _shoulderOffset;
    private float _aimHeight = 1.4f;
    private float _followSpeed = 12.0f;

    public VRPlayerCharacterComponent? Player
    {
        get => _player;
        set => SetField(ref _player, value);
    }

    public Transform? Anchor
    {
        get => _anchor;
        set => SetField(ref _anchor, value);
    }

    public BoomTransform? Boom
    {
        get => _boom;
        set => SetField(ref _boom, value);
    }

    public float Height
    {
        get => _height;
        set => SetField(ref _height, MathF.Max(0.0f, value));
    }

    public float Distance
    {
        get => _distance;
        set
        {
            if (!SetField(ref _distance, MathF.Max(0.0f, value)))
                return;
            if (Boom is not null)
                Boom.MaxLength = _distance;
        }
    }

    public float ShoulderOffset
    {
        get => _shoulderOffset;
        set => SetField(ref _shoulderOffset, value);
    }

    public float AimHeight
    {
        get => _aimHeight;
        set => SetField(ref _aimHeight, MathF.Max(0.0f, value));
    }

    /// <summary>Exponential interpolation rate in inverse seconds. Zero disables smoothing.</summary>
    public float FollowSpeed
    {
        get => _followSpeed;
        set => SetField(ref _followSpeed, MathF.Max(0.0f, value));
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        ResetFollow();
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, TickFollow);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, TickFollow);
        ResetFollow();
        base.OnComponentDeactivated();
    }

    /// <summary>Discard interpolation after a teleport, recenter, or output-mode change.</summary>
    public void ResetFollow()
    {
        _hasPose = false;
        _lastDiscontinuityGeneration = -1;
    }

    private void TickFollow()
    {
        VRPlayerCharacterComponent? player = Player;
        TransformBase? playerRoot = player?.PlayerRoot;
        Transform? anchor = Anchor;
        if (player is null || playerRoot is null || anchor is null)
            return;

        Vector3 rootPosition = playerRoot.WorldTranslation;
        if (player.PlayspaceRoot is { } playspace)
            rootPosition.Y = playspace.WorldTranslation.Y;
        TransformBase? hips = player.SpectatorHipsTransform;
        IHumanoidVrCalibrationRig? humanoid = player.GetHumanoid();
        Vector3 bodyForward;
        if (hips is not null && humanoid?.TryGetVrSemanticForwardInHipsBindSpace(out Vector3 hipsForward) == true)
            bodyForward = Vector3.TransformNormal(hipsForward, hips.WorldMatrix);
        else if (humanoid?.TryGetVrSemanticForwardInRootBindSpace(out Vector3 rootForward) == true)
            bodyForward = Vector3.TransformNormal(rootForward, humanoid.RootTransform.WorldMatrix);
        else
            bodyForward = Vector3.Transform(Globals.Forward, playerRoot.WorldRotation);
        bodyForward.Y = 0.0f;
        float targetHeading = bodyForward.LengthSquared() > DirectionEpsilonSquared
            ? MathF.Atan2(bodyForward.X, -bodyForward.Z)
            : _headingRadians;
        bool reset = !_hasPose || _lastDiscontinuityGeneration != player.DiscontinuityGeneration;
        float dt = MathF.Max(0.0f, RuntimeTransformServices.Current?.UndilatedUpdateDeltaSeconds ?? 0.0f);
        float alpha = FollowSpeed <= 0.0f ? 1.0f : 1.0f - MathF.Exp(-FollowSpeed * dt);
        if (reset)
        {
            _headingRadians = targetHeading;
            _hasPose = true;
            _lastDiscontinuityGeneration = player.DiscontinuityGeneration;
        }
        else
        {
            float headingDelta = MathF.IEEERemainder(targetHeading - _headingRadians, 2.0f * MathF.PI);
            _headingRadians += headingDelta * alpha;
        }
        _forward = new Vector3(MathF.Sin(_headingRadians), 0.0f, -MathF.Cos(_headingRadians));
        Vector3 right = Vector3.Normalize(Vector3.Cross(_forward, Globals.Up));
        Vector3 desiredPosition = rootPosition + Globals.Up * Height + right * ShoulderOffset;
        if (reset)
            _position = desiredPosition;
        else
            _position = Vector3.Lerp(_position, desiredPosition, alpha);

        Vector3 aimPoint = rootPosition + Globals.Up * AimHeight;
        Vector3 desiredCameraPosition = _position - _forward * Distance;
        Vector3 lookDirection = aimPoint - desiredCameraPosition;
        if (lookDirection.LengthSquared() <= DirectionEpsilonSquared)
            lookDirection = _forward;
        else
            lookDirection = Vector3.Normalize(lookDirection);

        Quaternion rotation = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(_position, lookDirection, Globals.Up));
        anchor.SetWorldTranslationRotation(_position, rotation);
        if (Boom is not null && Boom.MaxLength != Distance)
            Boom.MaxLength = Distance;
    }
}
