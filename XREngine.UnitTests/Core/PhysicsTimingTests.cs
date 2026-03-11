using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Core;

public sealed class PhysicsTimingTests
{
    [Test]
    public void PhysxControllerActorProxy_DeltaTicksToSeconds_RemainsStableAtLargeAbsoluteTicks()
    {
        long baseTicks = Stopwatch.Frequency * 60L * 60L * 18L;
        long currentTicks = baseTicks + Stopwatch.Frequency * 3L / 200L;

        PhysxControllerActorProxy.DeltaTicksToSeconds(currentTicks, baseTicks).ShouldBe(0.015f, 0.000001f);
    }

    [Test]
    public void RigidBodyTransform_InterpolationAlpha_UsesTickRatioAndClamps()
    {
        long fixedTicks = Stopwatch.Frequency / 30L;
        float expectedHalfRatio = (float)((fixedTicks / 2L) / (double)fixedTicks);

        RigidBodyTransform.ComputeInterpolationAlpha(fixedTicks / 2L, fixedTicks).ShouldBe(expectedHalfRatio, 0.000001f);
        RigidBodyTransform.ComputeInterpolationAlpha(fixedTicks * 2L, fixedTicks).ShouldBe(1.0f, 0.000001f);
    }

    [Test]
    public void RigidBodyTransform_ShouldUseImmediatePhysicsPose_ForDiscreteOrSlowUpdate()
    {
        long fixedTicks = Stopwatch.Frequency / 60L;

        RigidBodyTransform.ShouldUseImmediatePhysicsPose(RigidBodyTransform.EInterpolationMode.Discrete, fixedTicks / 2L, fixedTicks).ShouldBeTrue();
        RigidBodyTransform.ShouldUseImmediatePhysicsPose(RigidBodyTransform.EInterpolationMode.Interpolate, fixedTicks + 1L, fixedTicks).ShouldBeTrue();
        RigidBodyTransform.ShouldUseImmediatePhysicsPose(RigidBodyTransform.EInterpolationMode.Interpolate, fixedTicks / 2L, fixedTicks).ShouldBeFalse();
    }
}