using System.Numerics;
using MemoryPack;
using System.ComponentModel;

namespace XREngine.Components.Movement.Modules;

/// <summary>
/// Movement module that drives a character controller from animation root-motion
/// translation deltas while grounded, and uses standard Jolt gravity and air control
/// while airborne.
/// </summary>
[Serializable]
[MemoryPackable]
public partial class RootMotionMovementModule : MovementModule
{
    [MemoryPackIgnore]
    private readonly object _accumulatorSync = new();

    [MemoryPackIgnore]
    private Vector3 _pendingWorldTranslationDelta;

    [MemoryPackIgnore]
    private Quaternion _pendingYawDelta = Quaternion.Identity;

    [MemoryPackIgnore]
    private Vector3 _lastRequestedHorizontalDisplacement;

    [MemoryPackIgnore]
    private Vector3 _requestedHorizontalDisplacementSincePhysicsStep;

    private float _airControlFactor = 0.65f;
    private float _airMinSpeedFactor = 0.4f;
    private float _rootMotionScale = 1.0f;

    [MemoryPackConstructor]
    public RootMotionMovementModule() { }

    public RootMotionMovementModule(string name) : base(name) { }

    /// <summary>
    /// Scalar multiplier applied to incoming animation root-motion translation deltas.
    /// </summary>
    [Category("Root Motion")]
    [DisplayName("Root Motion Scale")]
    [Description("Multiplier applied to incoming animation root-motion translation deltas.")]
    public float RootMotionScale
    {
        get => _rootMotionScale;
        set => SetField(ref _rootMotionScale, float.IsFinite(value) ? MathF.Max(0.0f, value) : 1.0f);
    }

    /// <summary>
    /// Horizontal redirection blend factor while airborne (0 = none, 1 = instant).
    /// </summary>
    [Category("Air Movement")]
    [DisplayName("Air Control Factor")]
    [Description("Air control strength while falling or jumping (0-1).")]
    public float AirControlFactor
    {
        get => _airControlFactor;
        set => SetField(ref _airControlFactor, Math.Clamp(value, 0.0f, 1.0f));
    }

    /// <summary>
    /// Minimum horizontal speed fraction when redirecting from a standstill jump.
    /// </summary>
    [Category("Air Movement")]
    [DisplayName("Air Min Speed Factor")]
    [Description("Minimum horizontal air speed as a fraction of target ground speed.")]
    public float AirMinSpeedFactor
    {
        get => _airMinSpeedFactor;
        set => SetField(ref _airMinSpeedFactor, Math.Clamp(value, 0.0f, 1.0f));
    }

    /// <summary>
    /// Most recent planar root-motion displacement requested during <see cref="ProcessGroundMovement"/>.
    /// </summary>
    [MemoryPackIgnore]
    public Vector3 LastRequestedHorizontalDisplacement
    {
        get
        {
            lock (_accumulatorSync)
                return _lastRequestedHorizontalDisplacement;
        }
    }

    /// <summary>
    /// Accumulates one world-space root-motion translation and yaw delta emitted by
    /// <c>AnimStateMachineComponent.RootMotionEvaluated</c>. Safe to call across render
    /// and physics threads with zero heap allocations.
    /// </summary>
    public void AccumulateRootMotionDelta(Vector3 worldTranslationDelta, Quaternion yawDelta)
    {
        if (!IsFinite(worldTranslationDelta))
            worldTranslationDelta = Vector3.Zero;

        Quaternion safeYaw = IsFiniteNonZero(yawDelta) ? Quaternion.Normalize(yawDelta) : Quaternion.Identity;
        lock (_accumulatorSync)
        {
            _pendingWorldTranslationDelta += worldTranslationDelta;
            _pendingYawDelta = Quaternion.Normalize(_pendingYawDelta * safeYaw);
        }
    }

    /// <summary>
    /// Consumes and resets the accumulated animation yaw rotation delta.
    /// </summary>
    public Quaternion ConsumeAccumulatedYawDelta()
    {
        lock (_accumulatorSync)
        {
            Quaternion result = _pendingYawDelta;
            _pendingYawDelta = Quaternion.Identity;
            return result;
        }
    }

    /// <summary>
    /// Consumes the cumulative planar root-motion displacement requested across all update ticks
    /// since the previous physics feedback step.
    /// </summary>
    public Vector3 ConsumeRequestedHorizontalDisplacementSincePhysicsStep()
    {
        lock (_accumulatorSync)
        {
            Vector3 requested = _requestedHorizontalDisplacementSincePhysicsStep;
            _requestedHorizontalDisplacementSincePhysicsStep = Vector3.Zero;
            return requested;
        }
    }

    /// <summary>
    /// Discards any unconsumed root-motion deltas (for example after a teleport or mode reset).
    /// </summary>
    public void ClearPendingRootMotion()
    {
        lock (_accumulatorSync)
        {
            _pendingWorldTranslationDelta = Vector3.Zero;
            _pendingYawDelta = Quaternion.Identity;
            _lastRequestedHorizontalDisplacement = Vector3.Zero;
            _requestedHorizontalDisplacementSincePhysicsStep = Vector3.Zero;
        }
    }

    public override MovementResult ProcessGroundMovement(in MovementContext context)
    {
        Vector3 rawDelta;
        lock (_accumulatorSync)
        {
            rawDelta = _pendingWorldTranslationDelta;
            _pendingWorldTranslationDelta = Vector3.Zero;
        }

        Vector3 up = context.UpDirection.LengthSquared() > 1.0e-8f
            ? Vector3.Normalize(context.UpDirection)
            : Vector3.UnitY;

        Vector3 scaledDelta = rawDelta * _rootMotionScale;
        Vector3 horizontalDelta = scaledDelta - up * Vector3.Dot(scaledDelta, up);

        Vector3 horizontalVelocity = Vector3.Zero;
        if (float.IsFinite(context.DeltaTime) && context.DeltaTime > 1.0e-6f)
        {
            horizontalVelocity = horizontalDelta / context.DeltaTime;
            float speed = horizontalVelocity.Length();
            if (speed > context.MaxSpeed && context.MaxSpeed > 0.0f)
            {
                horizontalVelocity = horizontalVelocity / speed * context.MaxSpeed;
                horizontalDelta = horizontalVelocity * context.DeltaTime;
            }
        }

        lock (_accumulatorSync)
        {
            _lastRequestedHorizontalDisplacement = horizontalDelta;
            _requestedHorizontalDisplacementSincePhysicsStep += horizontalDelta;
        }

        return MovementResult.FromHorizontalAndVertical(
            horizontalVelocity,
            context.VerticalSpeed,
            up);
    }

    public override MovementResult ProcessAirMovement(in MovementContext context)
    {
        // Discard grounded root-motion translation while airborne so landing does not apply stale deltas.
        lock (_accumulatorSync)
        {
            _pendingWorldTranslationDelta = Vector3.Zero;
            _lastRequestedHorizontalDisplacement = Vector3.Zero;
            _requestedHorizontalDisplacementSincePhysicsStep = Vector3.Zero;
        }

        Vector3 horizontal = context.HorizontalVelocity;
        float currentSpeed = horizontal.Length();

        if (context.InputDirection != Vector3.Zero && AirControlFactor > 0.001f)
        {
            float minSpeed = context.TargetSpeed * AirMinSpeedFactor;
            float targetSpeed = MathF.Max(currentSpeed, minSpeed);
            Vector3 targetVelocity = context.InputDirection * targetSpeed;

            horizontal = Vector3.Lerp(
                horizontal,
                targetVelocity,
                TimeScaledBlend(AirControlFactor, context.DeltaTime));
        }

        horizontal = ClampHorizontalSpeed(horizontal + context.UpDirection * context.VerticalSpeed, context.MaxSpeed, context.UpDirection);
        horizontal -= context.UpDirection * Vector3.Dot(horizontal, context.UpDirection);

        float newVerticalSpeed = context.VerticalSpeed + Vector3.Dot(context.Gravity, context.UpDirection) * context.DeltaTime;

        return MovementResult.FromHorizontalAndVertical(
            horizontal,
            newVerticalSpeed,
            context.UpDirection,
            gravityApplied: true);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFiniteNonZero(Quaternion value)
        => float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z)
        && float.IsFinite(value.W)
        && value.LengthSquared() > 1.0e-8f;
}
