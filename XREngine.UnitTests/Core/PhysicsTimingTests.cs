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

    [Test]
    public void PhysicsChainComponent_FixedUpdateRenderAlpha_UsesTickRatioAndClamps()
    {
        long fixedTicks = Stopwatch.Frequency / 50L;
        float expectedHalfRatio = (float)((fixedTicks / 2L) / (double)fixedTicks);

        Components.PhysicsChainComponent.ComputeFixedUpdateRenderAlpha(fixedTicks / 2L, fixedTicks).ShouldBe(expectedHalfRatio, 0.000001f);
        Components.PhysicsChainComponent.ComputeFixedUpdateRenderAlpha(fixedTicks * 3L, fixedTicks).ShouldBe(1.0f, 0.000001f);
    }

    [Test]
    public void PhysicsChainComponent_ShouldUseImmediateFixedUpdatePose_ForDiscreteOrSlowUpdate()
    {
        long fixedTicks = Stopwatch.Frequency / 60L;

        Components.PhysicsChainComponent.ShouldUseImmediateFixedUpdatePose(Components.PhysicsChainComponent.EInterpolationMode.Discrete, fixedTicks / 2L, fixedTicks).ShouldBeTrue();
        Components.PhysicsChainComponent.ShouldUseImmediateFixedUpdatePose(Components.PhysicsChainComponent.EInterpolationMode.Interpolate, fixedTicks + 1L, fixedTicks).ShouldBeTrue();
        Components.PhysicsChainComponent.ShouldUseImmediateFixedUpdatePose(Components.PhysicsChainComponent.EInterpolationMode.Interpolate, fixedTicks / 2L, fixedTicks).ShouldBeFalse();
    }

    [Test]
    public void PhysicsChainComponent_ResolveFixedUpdateRenderPosition_InterpolatesAndExtrapolates()
    {
        var previous = new System.Numerics.Vector3(1.0f, 2.0f, 3.0f);
        var current = new System.Numerics.Vector3(5.0f, 6.0f, 7.0f);

        Components.PhysicsChainComponent.ResolveFixedUpdateRenderPosition(previous, current, Components.PhysicsChainComponent.EInterpolationMode.Interpolate, 0.25f)
            .ShouldBe(new System.Numerics.Vector3(2.0f, 3.0f, 4.0f));

        Components.PhysicsChainComponent.ResolveFixedUpdateRenderPosition(previous, current, Components.PhysicsChainComponent.EInterpolationMode.Extrapolate, 0.5f)
            .ShouldBe(new System.Numerics.Vector3(7.0f, 8.0f, 9.0f));

        Components.PhysicsChainComponent.ResolveFixedUpdateRenderPosition(previous, current, Components.PhysicsChainComponent.EInterpolationMode.Discrete, 0.5f)
            .ShouldBe(current);
    }

    [Test]
    public void PhysicsChainComponent_ComputeSimulationTimeScale_UsesReferenceDeltaAndSpeed()
    {
        Components.PhysicsChainComponent.ComputeSimulationTimeScale(1.0f / 30.0f, 1.0f / 60.0f, 1.0f)
            .ShouldBe(2.0f, 0.000001f);

        Components.PhysicsChainComponent.ComputeSimulationTimeScale(1.0f / 60.0f, 1.0f / 60.0f, 0.5f)
            .ShouldBe(0.5f, 0.000001f);

        Components.PhysicsChainComponent.ComputeSimulationTimeScale(1.0f / 120.0f, 1.0f / 60.0f, 1.0f)
            .ShouldBe(0.5f, 0.000001f);
    }
}