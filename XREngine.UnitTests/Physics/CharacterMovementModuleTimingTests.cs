using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Movement.Modules;

namespace XREngine.UnitTests.Physics;

public sealed class CharacterMovementModuleTimingTests
{
    [Test]
    public void ModernStopping_IsEquivalentAcrossProducerCadences()
    {
        ModernMovementModule module = new() { StoppingFactor = 0.85f };
        MovementModule.MovementContext longStep = CreateContext(Vector3.Zero, Vector3.UnitX * 10.0f, 1.0f / 30.0f);
        MovementModule.MovementResult once = module.ProcessGroundMovement(in longStep);

        MovementModule.MovementContext firstShort = CreateContext(Vector3.Zero, Vector3.UnitX * 10.0f, 1.0f / 60.0f);
        MovementModule.MovementResult first = module.ProcessGroundMovement(in firstShort);
        MovementModule.MovementContext secondShort = CreateContext(Vector3.Zero, first.NewVelocity, 1.0f / 60.0f);
        MovementModule.MovementResult second = module.ProcessGroundMovement(in secondShort);

        Vector3.Distance(once.NewVelocity, second.NewVelocity).ShouldBeLessThan(0.00001f);
    }

    [Test]
    public void ModernAirControl_IsEquivalentAcrossProducerCadences()
    {
        ModernMovementModule module = new()
        {
            AirControlFactor = 0.7f,
            AirMinSpeedFactor = 0.5f,
        };
        Vector3 initialVelocity = Vector3.Zero;
        MovementModule.MovementContext longStep = CreateContext(Vector3.UnitZ, initialVelocity, 1.0f / 30.0f);
        MovementModule.MovementResult once = module.ProcessAirMovement(in longStep);

        MovementModule.MovementContext firstShort = CreateContext(Vector3.UnitZ, initialVelocity, 1.0f / 60.0f);
        MovementModule.MovementResult first = module.ProcessAirMovement(in firstShort);
        MovementModule.MovementContext secondShort = CreateContext(Vector3.UnitZ, first.NewVelocity, 1.0f / 60.0f);
        MovementModule.MovementResult second = module.ProcessAirMovement(in secondShort);

        Vector3.Distance(once.NewVelocity, second.NewVelocity).ShouldBeLessThan(0.00001f);
    }

    [Test]
    public void SwimmingBlend_IsEquivalentAcrossProducerCadences()
    {
        ArcadeMovementModule module = new() { SwimControl = 0.1f };
        Vector3 initialVelocity = Vector3.UnitX * 5.0f;
        MovementModule.MovementContext longStep = CreateContext(Vector3.UnitZ, initialVelocity, 1.0f / 30.0f);
        MovementModule.MovementResult once = module.ProcessSwimmingMovement(in longStep);

        MovementModule.MovementContext firstShort = CreateContext(Vector3.UnitZ, initialVelocity, 1.0f / 60.0f);
        MovementModule.MovementResult first = module.ProcessSwimmingMovement(in firstShort);
        MovementModule.MovementContext secondShort = CreateContext(Vector3.UnitZ, first.NewVelocity, 1.0f / 60.0f);
        MovementModule.MovementResult second = module.ProcessSwimmingMovement(in secondShort);

        Vector3.Distance(once.NewVelocity, second.NewVelocity).ShouldBeLessThan(0.00001f);
    }

    private static MovementModule.MovementContext CreateContext(
        Vector3 input,
        Vector3 velocity,
        float deltaTime)
        => new(
            input,
            velocity,
            8.0f,
            50.0f,
            deltaTime,
            false,
            false,
            Vector3.UnitY,
            Vector3.Zero);
}
