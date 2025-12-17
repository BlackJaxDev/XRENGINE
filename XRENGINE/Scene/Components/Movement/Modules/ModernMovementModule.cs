using Extensions;
using MemoryPack;
using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Files;

namespace XREngine.Components.Movement.Modules
{
    /// <summary>
    /// Modern-style movement module - responsive with full air control.
    /// Ground movement is instant like Arcade, but with optional slide stopping.
    /// Air movement allows full redirection mid-air via velocity blending.
    /// Best for modern action games and shooters.
    /// </summary>
    [Serializable]
    [MemoryPackable]
    public partial class ModernMovementModule : MovementModule
    {
        [MemoryPackConstructor]
        public ModernMovementModule() { }
        public ModernMovementModule(string name) : base(name) { }

        private float _stoppingFactor = 0.85f;
        private float _airControlFactor = 0.7f;
        private float _airMinSpeedFactor = 0.5f;

        /// <summary>
        /// Frame-by-frame velocity multiplier when stopping (0-1).
        /// Lower values = faster stopping. 0.85 means 15% velocity reduction per frame.
        /// Set to 0 for instant stopping like Arcade mode.
        /// </summary>
        [Category("Ground Movement")]
        [DisplayName("Stopping Factor")]
        [Description("Velocity retained each frame when stopping (0 = instant, 0.85 = slight slide).")]
        public float StoppingFactor
        {
            get => _stoppingFactor;
            set => SetField(ref _stoppingFactor, value.Clamp(0.0f, 1.0f));
        }

        /// <summary>
        /// How much air control the player has (0-1).
        /// 0 = no air control (like Arcade), 1 = full instant air control.
        /// This is the blend factor toward the target direction each frame.
        /// </summary>
        [Category("Air Movement")]
        [DisplayName("Air Control Factor")]
        [Description("Air control strength (0 = none, 1 = full redirection).")]
        public float AirControlFactor
        {
            get => _airControlFactor;
            set => SetField(ref _airControlFactor, value.Clamp(0.0f, 1.0f));
        }

        /// <summary>
        /// Minimum speed factor when redirecting in air (0-1).
        /// If current speed is below this * TargetSpeed, use this as minimum.
        /// Prevents feeling "stuck" when jumping from standstill.
        /// </summary>
        [Category("Air Movement")]
        [DisplayName("Air Min Speed Factor")]
        [Description("Minimum air speed as fraction of ground speed (prevents slow air movement).")]
        public float AirMinSpeedFactor
        {
            get => _airMinSpeedFactor;
            set => SetField(ref _airMinSpeedFactor, value.Clamp(0.0f, 1.0f));
        }

        /// <summary>
        /// Creates a new Modern movement module with default settings.
        /// </summary>
        public static ModernMovementModule CreateDefault()
            => new("Modern Movement")
            {
                StoppingFactor = 0.85f,
                AirControlFactor = 0.7f,
                AirMinSpeedFactor = 0.5f
            };

        public override MovementResult ProcessGroundMovement(in MovementContext context)
        {
            Vector3 horizontalVelocity;

            if (context.InputDirection != Vector3.Zero)
            {
                // Instantly set velocity to target speed in input direction
                horizontalVelocity = context.InputDirection * context.TargetSpeed;
            }
            else
            {
                // Apply stopping factor for slight slide, or instant stop if factor is 0
                Vector3 horizontal = context.HorizontalVelocity;
                horizontalVelocity = horizontal * StoppingFactor;
                
                // Snap to zero when very slow
                if (horizontalVelocity.Length() < 0.1f)
                    horizontalVelocity = Vector3.Zero;
            }

            return MovementResult.FromHorizontalAndVertical(
                horizontalVelocity,
                context.VerticalSpeed,
                context.UpDirection);
        }

        public override MovementResult ProcessAirMovement(in MovementContext context)
        {
            Vector3 horizontal = context.HorizontalVelocity;
            float currentSpeed = horizontal.Length();

            if (context.InputDirection != Vector3.Zero && AirControlFactor > 0.001f)
            {
                // Target velocity based on input at current speed (or min speed if stopped)
                float minSpeed = context.TargetSpeed * AirMinSpeedFactor;
                float targetSpeed = MathF.Max(currentSpeed, minSpeed);
                Vector3 targetVelocity = context.InputDirection * targetSpeed;

                // Directly blend toward target based on air control factor
                horizontal = Vector3.Lerp(horizontal, targetVelocity, AirControlFactor);
            }

            // Clamp horizontal speed
            horizontal = ClampHorizontalSpeed(horizontal + context.UpDirection * context.VerticalSpeed, context.MaxSpeed, context.UpDirection);
            horizontal -= context.UpDirection * Vector3.Dot(horizontal, context.UpDirection);

            // Apply gravity
            float newVerticalSpeed = context.VerticalSpeed + Vector3.Dot(context.Gravity, context.UpDirection) * context.DeltaTime;

            return MovementResult.FromHorizontalAndVertical(
                horizontal,
                newVerticalSpeed,
                context.UpDirection);
        }
    }
}
