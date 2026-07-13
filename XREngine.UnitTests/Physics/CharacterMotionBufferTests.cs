using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Scene.Physics;

namespace XREngine.UnitTests.Physics;

public sealed class CharacterMotionBufferTests
{
    private const float FixedDelta = 1.0f / 60.0f;

    [Test]
    public void EquivalentVelocityAndDisplacement_ProduceTheSameFixedStep()
    {
        CharacterMotionBuffer velocityBuffer = new();
        CharacterMotionBuffer displacementBuffer = new();
        Vector3 velocity = new(3.0f, -2.0f, 1.0f);

        velocityBuffer.Enqueue(new CharacterMotionCommand(
            velocity,
            CharacterMotionInputModel.Velocity,
            0.0f,
            FixedDelta));
        displacementBuffer.Enqueue(new CharacterMotionCommand(
            velocity * FixedDelta,
            CharacterMotionInputModel.Displacement,
            0.0f,
            FixedDelta));

        CharacterMotionStep velocityStep = velocityBuffer.Consume(FixedDelta);
        CharacterMotionStep displacementStep = displacementBuffer.Consume(FixedDelta);

        Vector3.Distance(velocityStep.Displacement, displacementStep.Displacement)
            .ShouldBeLessThan(0.000001f);
        Vector3.Distance(velocityStep.Velocity, displacementStep.Velocity)
            .ShouldBeLessThan(0.000001f);
    }

    [Test]
    public void DisplacementLongerThanFixedStep_IsConsumedProportionallyWithoutLoss()
    {
        CharacterMotionBuffer buffer = new();
        buffer.Enqueue(new CharacterMotionCommand(
            new Vector3(2.0f, 0.0f, 0.0f),
            CharacterMotionInputModel.Displacement,
            0.0f,
            FixedDelta * 2.0f));

        CharacterMotionStep first = buffer.Consume(FixedDelta);
        CharacterMotionStep second = buffer.Consume(FixedDelta);

        first.Displacement.X.ShouldBe(1.0f, 0.000001f);
        second.Displacement.X.ShouldBe(1.0f, 0.000001f);
        (first.Displacement.X + second.Displacement.X).ShouldBe(2.0f, 0.000001f);
    }

    [Test]
    public void TaggedCommands_AreNotReinterpretedWhenInputModelChanges()
    {
        CharacterMotionBuffer buffer = new();
        buffer.Enqueue(new CharacterMotionCommand(
            Vector3.UnitX * 6.0f,
            CharacterMotionInputModel.Velocity,
            0.0f,
            FixedDelta));
        buffer.Enqueue(new CharacterMotionCommand(
            Vector3.UnitX * 0.25f,
            CharacterMotionInputModel.Displacement,
            0.0f,
            FixedDelta));

        CharacterMotionStep velocityStep = buffer.Consume(FixedDelta);
        CharacterMotionStep displacementStep = buffer.Consume(FixedDelta);

        velocityStep.Displacement.X.ShouldBe(0.1f, 0.000001f);
        displacementStep.Displacement.X.ShouldBe(0.25f, 0.000001f);
    }

    [TestCase(30, CharacterMotionInputModel.Velocity)]
    [TestCase(60, CharacterMotionInputModel.Velocity)]
    [TestCase(120, CharacterMotionInputModel.Velocity)]
    [TestCase(144, CharacterMotionInputModel.Velocity)]
    [TestCase(30, CharacterMotionInputModel.Displacement)]
    [TestCase(60, CharacterMotionInputModel.Displacement)]
    [TestCase(120, CharacterMotionInputModel.Displacement)]
    [TestCase(144, CharacterMotionInputModel.Displacement)]
    public void ProducerCadence_DoesNotChangeOneSecondDistance(
        int producerRate,
        CharacterMotionInputModel inputModel)
    {
        CharacterMotionBuffer buffer = new();
        float producerDelta = 1.0f / producerRate;
        Vector3 velocity = Vector3.UnitX * 3.0f;
        for (int index = 0; index < producerRate; index++)
        {
            Vector3 value = inputModel == CharacterMotionInputModel.Velocity
                ? velocity
                : velocity * producerDelta;
            buffer.Enqueue(new CharacterMotionCommand(value, inputModel, 0.0f, producerDelta));
        }

        float distance = 0.0f;
        for (int index = 0; index < 60; index++)
            distance += buffer.Consume(FixedDelta).Displacement.X;

        distance.ShouldBe(3.0f, 0.0001f);
    }

    [Test]
    public void InvalidFixedDelta_DoesNotDropQueuedRemainder()
    {
        CharacterMotionBuffer buffer = new();
        buffer.Enqueue(new CharacterMotionCommand(
            Vector3.UnitX,
            CharacterMotionInputModel.Displacement,
            0.0f,
            FixedDelta));

        buffer.Consume(0.0f).Displacement.ShouldBe(Vector3.Zero);
        buffer.Consume(float.NaN).Displacement.ShouldBe(Vector3.Zero);
        buffer.Consume(FixedDelta).Displacement.ShouldBe(Vector3.UnitX);
    }

    [Test]
    public void OverflowCoalescing_RemainsAllocationBoundedAndPreservesDistance()
    {
        CharacterMotionBuffer buffer = new();
        CharacterMotionCommand command = new(
            Vector3.UnitX * 0.001f,
            CharacterMotionInputModel.Displacement,
            0.0f,
            FixedDelta);
        buffer.Enqueue(command);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 1; index < 10_000; index++)
            buffer.Enqueue(command);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBeLessThanOrEqualTo(128L);

        float totalDistance = 0.0f;
        for (int index = 0; index < 10_000; index++)
            totalDistance += buffer.Consume(FixedDelta).Displacement.X;

        totalDistance.ShouldBe(10.0f, 0.001f);
    }
}
