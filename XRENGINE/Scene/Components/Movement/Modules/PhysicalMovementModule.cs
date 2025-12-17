using Extensions;
using MemoryPack;
using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Files;

namespace XREngine.Components.Movement.Modules
{
    /// <summary>
    /// Physics-based movement module - force-driven with momentum.
    /// Ground movement uses acceleration forces that build up velocity over time.
    /// Air movement is minimal, preserving jump momentum with slight corrections.
    /// Best for realistic/tactical games like Halo 3.
    /// </summary>
    [Serializable]
    [MemoryPackable]
    public partial class PhysicalMovementModule : MovementModule
    {
        [MemoryPackConstructor]
        public PhysicalMovementModule() { }
        public PhysicalMovementModule(string name) : base(name) { }

        private float _groundAcceleration = 400.0f;
        private float _groundDeceleration = 300.0f;
        private float _airAcceleration = 2.0f;

        /// <summary>
        /// How quickly the character accelerates on the ground (m/s²).
        /// Higher values make movement feel snappier while retaining physics feel.
        /// </summary>
        [Category("Ground Movement")]
        [DisplayName("Ground Acceleration")]
        [Description("Force-based ground acceleration in m/s².")]
        public float GroundAcceleration
        {
            get => _groundAcceleration;
            set => SetField(ref _groundAcceleration, MathF.Max(0, value));
        }

        /// <summary>
        /// How quickly the character decelerates on the ground when not providing input (m/s²).
        /// Simulates friction bringing the character to a stop.
        /// </summary>
        [Category("Ground Movement")]
        [DisplayName("Ground Deceleration")]
        [Description("Friction-based ground deceleration in m/s².")]
        public float GroundDeceleration
        {
            get => _groundDeceleration;
            set => SetField(ref _groundDeceleration, MathF.Max(0, value));
        }

        /// <summary>
        /// Very minimal air acceleration for slight course corrections.
        /// Set to 0 for completely locked air movement like Arcade.
        /// </summary>
        [Category("Air Movement")]
        [DisplayName("Air Acceleration")]
        [Description("Minimal air acceleration for course corrections (0 = no air control).")]
        public float AirAcceleration
        {
            get => _airAcceleration;
            set => SetField(ref _airAcceleration, MathF.Max(0, value));
        }

        /// <summary>
        /// Creates a new Physical movement module with default settings.
        /// </summary>
        public static PhysicalMovementModule CreateDefault()
            => new("Physical Movement")
            {
                GroundAcceleration = 400.0f,
                GroundDeceleration = 300.0f,
                AirAcceleration = 2.0f
            };

        public override MovementResult ProcessGroundMovement(in MovementContext context)
        {
            Vector3 horizontal = context.HorizontalVelocity;
            float dt = context.DeltaTime;

            if (context.InputDirection != Vector3.Zero)
            {
                // Apply acceleration force in input direction
                Vector3 accelerationForce = context.InputDirection * GroundAcceleration * dt;
                horizontal += accelerationForce;
            }
            else
            {
                // No input - apply friction/deceleration to slow down
                float speed = horizontal.Length();
                if (speed > 0.001f)
                {
                    float decelAmount = GroundDeceleration * dt;
                    float newSpeed = MathF.Max(0, speed - decelAmount);
                    horizontal = Vector3.Normalize(horizontal) * newSpeed;
                }
                else
                {
                    horizontal = Vector3.Zero;
                }
            }

            // Clamp horizontal speed to target speed
            float horizontalSpeed = horizontal.Length();
            if (horizontalSpeed > context.TargetSpeed)
                horizontal = Vector3.Normalize(horizontal) * context.TargetSpeed;

            return MovementResult.FromHorizontalAndVertical(
                horizontal,
                context.VerticalSpeed,
                context.UpDirection);
        }

        public override MovementResult ProcessAirMovement(in MovementContext context)
        {
            Vector3 horizontal = context.HorizontalVelocity;
            float dt = context.DeltaTime;

            // Very minimal air control (or none if AirAcceleration is 0)
            if (context.InputDirection != Vector3.Zero && AirAcceleration > 0.001f)
            {
                // Only allow slight course corrections, not full direction changes
                // This creates the "committed to the jump" feeling
                Vector3 airForce = context.InputDirection * AirAcceleration * dt;
                horizontal += airForce;
            }

            // Clamp horizontal speed to preserve momentum but not exceed max speed
            float horizontalSpeed = horizontal.Length();
            if (horizontalSpeed > context.MaxSpeed)
                horizontal = Vector3.Normalize(horizontal) * context.MaxSpeed;

            // Apply gravity
            float newVerticalSpeed = context.VerticalSpeed + Vector3.Dot(context.Gravity, context.UpDirection) * context.DeltaTime;

            return MovementResult.FromHorizontalAndVertical(
                horizontal,
                newVerticalSpeed,
                context.UpDirection);
        }
    }
}
