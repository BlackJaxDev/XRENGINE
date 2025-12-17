using MemoryPack;
using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Rendering.Physics.Physx;

namespace XREngine.Components.Movement.Modules
{
    /// <summary>
    /// Abstract base class for character movement modules.
    /// Defines how the character responds to input on the ground, in the air, and while swimming.
    /// Inherit from this class to create custom movement behaviors.
    /// </summary>
    [Serializable]
    [MemoryPackable]
    [MemoryPackUnion(0, typeof(ArcadeMovementModule))]
    [MemoryPackUnion(1, typeof(ModernMovementModule))]
    [MemoryPackUnion(2, typeof(PhysicalMovementModule))]
    public abstract partial class MovementModule : XRAsset
    {
        [MemoryPackConstructor]
        protected MovementModule() { }
        protected MovementModule(string name) : base(name) { }

        // Speed settings
        private float _walkingSpeed = 8.0f;
        private float _runSpeedMultiplier = 1.5f;
        private float _maxSpeed = 50.0f;

        // Jump settings
        private bool _canJump = true;
        private float _jumpForce = 5.0f;
        private float _jumpHoldForce = 8.0f;
        private float _maxJumpDuration = 0.1f;
        private float _coyoteTime = 0.2f;

        // Gravity settings
        private float _gravityScale = 5.5f;

        // Ground movement settings
        private float _groundFriction = 0.1f;
        private float _stepOffset = 0.0f;
        private float _slopeLimitDegrees = 45.0f;
        private bool _slideOnSteepSlopes = true;

        // Swimming settings
        private float _swimSpeedMultiplier = 0.6f;
        private float _swimControl = 0.1f;

        #region Speed Properties

        /// <summary>
        /// Base walking speed in meters per second.
        /// </summary>
        [Category("Speed")]
        [DisplayName("Walking Speed")]
        [Description("Base movement speed when walking.")]
        public float WalkingSpeed
        {
            get => _walkingSpeed;
            set => SetField(ref _walkingSpeed, MathF.Max(0, value));
        }

        /// <summary>
        /// Multiplier applied to walking speed when running/sprinting.
        /// </summary>
        [Category("Speed")]
        [DisplayName("Run Speed Multiplier")]
        [Description("Speed multiplier when running (1.5 = 50% faster).")]
        public float RunSpeedMultiplier
        {
            get => _runSpeedMultiplier;
            set => SetField(ref _runSpeedMultiplier, MathF.Max(1.0f, value));
        }

        /// <summary>
        /// Maximum movement speed regardless of other factors.
        /// </summary>
        [Category("Speed")]
        [DisplayName("Max Speed")]
        [Description("Absolute maximum movement speed.")]
        public float MaxSpeed
        {
            get => _maxSpeed;
            set => SetField(ref _maxSpeed, MathF.Max(0, value));
        }

        /// <summary>
        /// Gets the current running speed based on walking speed and multiplier.
        /// </summary>
        [MemoryPackIgnore]
        public float RunningSpeed => WalkingSpeed * RunSpeedMultiplier;

        #endregion

        #region Jump Properties

        /// <summary>
        /// Whether this module allows jumping at all.
        /// </summary>
        [Category("Jumping")]
        [DisplayName("Can Jump")]
        [Description("Whether jumping is enabled for this movement style.")]
        public bool CanJump
        {
            get => _canJump;
            set => SetField(ref _canJump, value);
        }

        /// <summary>
        /// Initial upward force applied when jumping.
        /// </summary>
        [Category("Jumping")]
        [DisplayName("Jump Force")]
        [Description("Initial upward velocity when jumping.")]
        public float JumpForce
        {
            get => _jumpForce;
            set => SetField(ref _jumpForce, MathF.Max(0, value));
        }

        /// <summary>
        /// Additional force applied while holding the jump button.
        /// </summary>
        [Category("Jumping")]
        [DisplayName("Jump Hold Force")]
        [Description("Additional force while jump button is held.")]
        public float JumpHoldForce
        {
            get => _jumpHoldForce;
            set => SetField(ref _jumpHoldForce, MathF.Max(0, value));
        }

        /// <summary>
        /// Maximum duration the jump can be sustained by holding the button.
        /// </summary>
        [Category("Jumping")]
        [DisplayName("Max Jump Duration")]
        [Description("How long jump can be sustained by holding button.")]
        public float MaxJumpDuration
        {
            get => _maxJumpDuration;
            set => SetField(ref _maxJumpDuration, MathF.Max(0, value));
        }

        /// <summary>
        /// Grace period after leaving ground where jump is still allowed.
        /// Makes jumping feel more responsive and forgiving.
        /// </summary>
        [Category("Jumping")]
        [DisplayName("Coyote Time")]
        [Description("Grace period after leaving ground where jump is still allowed.")]
        public float CoyoteTime
        {
            get => _coyoteTime;
            set => SetField(ref _coyoteTime, MathF.Max(0, value));
        }

        #endregion

        #region Gravity Properties

        /// <summary>
        /// Multiplier for gravity. Higher values make falling feel snappier.
        /// 1.0 = realistic gravity, 2.0+ = faster falling for better game feel.
        /// </summary>
        [Category("Physics")]
        [DisplayName("Gravity Scale")]
        [Description("Gravity multiplier. Higher = snappier falling.")]
        public float GravityScale
        {
            get => _gravityScale;
            set => SetField(ref _gravityScale, value);
        }

        #endregion

        #region Ground Movement Properties

        /// <summary>
        /// Friction applied when landing or grounded. 
        /// Higher values = more velocity reduction on landing.
        /// </summary>
        [Category("Ground Movement")]
        [DisplayName("Ground Friction")]
        [Description("Friction applied when grounded (0-1).")]
        public float GroundFriction
        {
            get => _groundFriction;
            set => SetField(ref _groundFriction, MathF.Max(0, MathF.Min(1, value)));
        }

        /// <summary>
        /// Maximum height the character can automatically step up.
        /// Set to 0 to disable step climbing.
        /// </summary>
        [Category("Ground Movement")]
        [DisplayName("Step Offset")]
        [Description("Max height character can step up automatically.")]
        public float StepOffset
        {
            get => _stepOffset;
            set => SetField(ref _stepOffset, MathF.Max(0, value));
        }

        /// <summary>
        /// Maximum walkable slope angle in degrees.
        /// Slopes steeper than this will be considered non-walkable.
        /// </summary>
        [Category("Ground Movement")]
        [DisplayName("Slope Limit (Degrees)")]
        [Description("Max walkable slope angle in degrees.")]
        public float SlopeLimitDegrees
        {
            get => _slopeLimitDegrees;
            set => SetField(ref _slopeLimitDegrees, MathF.Max(0, MathF.Min(90, value)));
        }

        /// <summary>
        /// The maximum slope which the character can walk up, expressed as the cosine of desired limit angle.
        /// In general it is desirable to limit where the character can walk, in particular it is unrealistic for the character to be able to climb arbitary slopes.
        /// A value of 0 disables this feature.
        /// </summary>
        [Category("Ground")]
        [DisplayName("Slope Limit (Cosine)")]
        [Description("Cosine of max walkable slope angle.")]
        public float SlopeLimitCosine
        {
            get => MathF.Cos(SlopeLimitDegrees * MathF.PI / 180f);
            set => SlopeLimitDegrees = MathF.Acos(value) * 180f / MathF.PI;
        }

        /// <summary>
        /// The maximum slope which the character can walk up, expressed in radians.
        /// In general it is desirable to limit where the character can walk, in particular it is unrealistic for the character to be able to climb arbitary slopes.
        /// A value of 0 disables this feature.
        /// </summary>
        [Browsable(false)]
        public float SlopeLimitAngleRad
        {
            get => SlopeLimitDegrees * MathF.PI / 180f;
            set => SlopeLimitDegrees = value * 180f / MathF.PI;
        }

        /// <summary>
        /// Whether the character slides down slopes that exceed the slope limit.
        /// </summary>
        [Category("Ground Movement")]
        [DisplayName("Slide On Steep Slopes")]
        [Description("Whether to slide on non-walkable surfaces.")]
        public bool SlideOnSteepSlopes
        {
            get => _slideOnSteepSlopes;
            set => SetField(ref _slideOnSteepSlopes, value);
        }

        #endregion

        #region Swimming Properties

        /// <summary>
        /// Speed multiplier when swimming (relative to walking speed).
        /// </summary>
        [Category("Swimming")]
        [DisplayName("Swim Speed Multiplier")]
        [Description("Speed multiplier when swimming (0.6 = 60% of walk speed).")]
        public float SwimSpeedMultiplier
        {
            get => _swimSpeedMultiplier;
            set => SetField(ref _swimSpeedMultiplier, MathF.Max(0, value));
        }

        /// <summary>
        /// How quickly velocity blends toward input direction when swimming.
        /// Higher = more responsive, lower = more floaty.
        /// </summary>
        [Category("Swimming")]
        [DisplayName("Swim Control")]
        [Description("Swim responsiveness (0-1). Higher = more responsive.")]
        public float SwimControl
        {
            get => _swimControl;
            set => SetField(ref _swimControl, MathF.Max(0, MathF.Min(1, value)));
        }

        #endregion

        /// <summary>
        /// Context passed to movement module methods containing all necessary state.
        /// </summary>
        public readonly struct MovementContext
        {
            public readonly Vector3 InputDirection;
            public readonly Vector3 CurrentVelocity;
            public readonly float TargetSpeed;
            public readonly float MaxSpeed;
            public readonly float DeltaTime;
            public readonly bool IsGrounded;
            public readonly bool IsCollidingUp;
            public readonly Vector3 UpDirection;
            public readonly Vector3 Gravity;

            public MovementContext(
                Vector3 inputDirection,
                Vector3 currentVelocity,
                float targetSpeed,
                float maxSpeed,
                float deltaTime,
                bool isGrounded,
                bool isCollidingUp,
                Vector3 upDirection,
                Vector3 gravity)
            {
                InputDirection = inputDirection;
                CurrentVelocity = currentVelocity;
                TargetSpeed = targetSpeed;
                MaxSpeed = maxSpeed;
                DeltaTime = deltaTime;
                IsGrounded = isGrounded;
                IsCollidingUp = isCollidingUp;
                UpDirection = upDirection;
                Gravity = gravity;
            }

            /// <summary>
            /// Gets the horizontal component of the current velocity (perpendicular to up).
            /// </summary>
            public Vector3 HorizontalVelocity
            {
                get
                {
                    float verticalComponent = Vector3.Dot(CurrentVelocity, UpDirection);
                    return CurrentVelocity - UpDirection * verticalComponent;
                }
            }

            /// <summary>
            /// Gets the vertical component of the current velocity (along up direction).
            /// </summary>
            public float VerticalSpeed => Vector3.Dot(CurrentVelocity, UpDirection);
        }

        /// <summary>
        /// Movement modes that can be requested by a module.
        /// </summary>
        public enum ERequestedMode
        {
            /// <summary>No mode change requested - continue with current mode.</summary>
            None,
            /// <summary>Request transition to ground/walking movement.</summary>
            Ground,
            /// <summary>Request transition to air/falling movement.</summary>
            Air,
            /// <summary>Request transition to swimming movement.</summary>
            Swimming,
            /// <summary>Request transition to flying movement.</summary>
            Flying
        }

        /// <summary>
        /// The result of a movement calculation.
        /// </summary>
        public readonly struct MovementResult
        {
            public readonly Vector3 NewVelocity;
            /// <summary>
            /// If true, the module has already applied gravity to the velocity.
            /// If false, the calling code should apply gravity.
            /// </summary>
            public readonly bool GravityApplied;
            /// <summary>
            /// If not None, the module is requesting a transition to a different movement mode.
            /// The character controller should honor this request if possible.
            /// </summary>
            public readonly ERequestedMode RequestedMode;

            public MovementResult(Vector3 newVelocity, bool gravityApplied = false, ERequestedMode requestedMode = ERequestedMode.None)
            {
                NewVelocity = newVelocity;
                GravityApplied = gravityApplied;
                RequestedMode = requestedMode;
            }

            public static MovementResult FromHorizontalAndVertical(Vector3 horizontal, float vertical, Vector3 up, bool gravityApplied = false, ERequestedMode requestedMode = ERequestedMode.None)
            {
                return new MovementResult(horizontal + up * vertical, gravityApplied, requestedMode);
            }

            /// <summary>
            /// Creates a new result with a mode change request.
            /// </summary>
            public MovementResult WithRequestedMode(ERequestedMode mode)
            {
                return new MovementResult(NewVelocity, GravityApplied, mode);
            }

            /// <summary>
            /// Creates a result that requests transition to air movement.
            /// </summary>
            public static MovementResult RequestAir(Vector3 velocity, bool gravityApplied = false)
                => new(velocity, gravityApplied, ERequestedMode.Air);

            /// <summary>
            /// Creates a result that requests transition to ground movement.
            /// </summary>
            public static MovementResult RequestGround(Vector3 velocity, bool gravityApplied = false)
                => new(velocity, gravityApplied, ERequestedMode.Ground);

            /// <summary>
            /// Creates a result that requests transition to swimming movement.
            /// </summary>
            public static MovementResult RequestSwimming(Vector3 velocity, bool gravityApplied = false)
                => new(velocity, gravityApplied, ERequestedMode.Swimming);
        }

        /// <summary>
        /// Called each frame while the character is grounded.
        /// </summary>
        /// <param name="context">The movement context with input and state.</param>
        /// <returns>The resulting velocity after ground movement processing.</returns>
        public abstract MovementResult ProcessGroundMovement(in MovementContext context);

        /// <summary>
        /// Called each frame while the character is airborne.
        /// </summary>
        /// <param name="context">The movement context with input and state.</param>
        /// <returns>The resulting velocity after air movement processing.</returns>
        public abstract MovementResult ProcessAirMovement(in MovementContext context);

        /// <summary>
        /// Called each frame while the character is swimming.
        /// </summary>
        /// <param name="context">The movement context with input and state.</param>
        /// <returns>The resulting velocity after swimming movement processing.</returns>
        public virtual MovementResult ProcessSwimmingMovement(in MovementContext context)
        {
            // Default swimming implementation - direct control with drag
            Vector3 targetVelocity = context.InputDirection * context.TargetSpeed * SwimSpeedMultiplier;
            Vector3 newVelocity = Vector3.Lerp(context.CurrentVelocity, targetVelocity, SwimControl);
            return new MovementResult(newVelocity);
        }

        /// <summary>
        /// Helper to clamp horizontal speed while preserving vertical velocity.
        /// </summary>
        protected static Vector3 ClampHorizontalSpeed(Vector3 velocity, float maxSpeed, Vector3 up)
        {
            float verticalSpeed = Vector3.Dot(velocity, up);
            Vector3 horizontal = velocity - up * verticalSpeed;
            
            float horizontalSpeed = horizontal.Length();
            if (horizontalSpeed > maxSpeed)
                horizontal = horizontal / horizontalSpeed * maxSpeed;
            
            return horizontal + up * verticalSpeed;
        }
    }
}
