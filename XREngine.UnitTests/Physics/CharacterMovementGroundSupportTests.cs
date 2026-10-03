using NUnit.Framework;
using Shouldly;
using XREngine.Components.Movement;

namespace XREngine.UnitTests.Physics;

public sealed class CharacterMovementGroundSupportTests
{
    [Test]
    public void SupportTransitions_UpdateWalkingAndFallingModes()
    {
        CharacterMovement3DComponent movement = new();
        movement.MovementMode.ShouldBe(CharacterMovement3DComponent.EMovementMode.Falling);

        movement.ReconcileGroundSupport(true);
        movement.MovementMode.ShouldBe(CharacterMovement3DComponent.EMovementMode.Walking);

        movement.ReconcileGroundSupport(false);
        movement.MovementMode.ShouldBe(CharacterMovement3DComponent.EMovementMode.Falling);
    }
}
