using MemoryPack;
using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Files;

namespace XREngine.Components.Movement.Modules
{
    /// <summary>
    /// Arcade-style movement module - the snappiest and most responsive.
    /// Ground movement is instant with no acceleration curve.
    /// Air movement is completely locked - once you jump, you're committed to that direction.
    /// Best for classic platformers and retro-style games.
    /// </summary>
    [Serializable]
    [MemoryPackable]
    public partial class ArcadeMovementModule : MovementModule
    {
        [MemoryPackConstructor]
        public ArcadeMovementModule() { }
        public ArcadeMovementModule(string name) : base(name) { }

        /// <summary>
        /// Creates a new Arcade movement module with default settings.
        /// </summary>
        public static ArcadeMovementModule CreateDefault()
            => new("Arcade Movement") { };

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
                // Instant stop - no sliding
                horizontalVelocity = Vector3.Zero;
            }

            return MovementResult.FromHorizontalAndVertical(
                horizontalVelocity,
                context.VerticalSpeed,
                context.UpDirection);
        }

        public override MovementResult ProcessAirMovement(in MovementContext context)
        {
            // Zero air control - completely committed to jump direction
            // Just preserve horizontal velocity, no input influence
            Vector3 horizontal = context.HorizontalVelocity;
            
            // Apply gravity
            float newVerticalSpeed = context.VerticalSpeed + Vector3.Dot(context.Gravity, context.UpDirection) * context.DeltaTime;

            return MovementResult.FromHorizontalAndVertical(
                horizontal,
                newVerticalSpeed,
                context.UpDirection);
        }
    }
}
