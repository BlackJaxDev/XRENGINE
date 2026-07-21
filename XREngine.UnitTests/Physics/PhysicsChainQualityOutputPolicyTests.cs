using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainQualityOutputPolicyTests
{
    [Test]
    public void IndependentOverridesDoNotCoupleSimulationAndPublishedOutputs()
    {
        PhysicsChainQualityPolicy policy = PhysicsChainQualityPolicy.Resolve(
            PhysicsChainQualityTier.Hz15,
            authoredRateHz: 60.0f)
            .WithOverrides(
                PhysicsChainPolicyControl.Enabled,
                PhysicsChainPolicyControl.Disabled,
                PhysicsChainOutputControl.Hold,
                PhysicsChainOutputControl.EverySimulationStep,
                PhysicsChainOutputControl.Hold);

        policy.SimulationEnabled.ShouldBeTrue();
        policy.SimulationRateHz.ShouldBe(15.0f);
        policy.CollisionEnabled.ShouldBeFalse();
        policy.PaletteCadence.ShouldBe(PhysicsChainOutputCadence.Hold);
        policy.BoundsCadence.ShouldBe(PhysicsChainOutputCadence.EverySimulationStep);
        policy.TransformMirrorCadence.ShouldBe(PhysicsChainOutputCadence.Hold);
    }

    [Test]
    public void DisablingSimulationAlsoDisablesCollisionButDoesNotForceOutputCadence()
    {
        PhysicsChainQualityPolicy policy = PhysicsChainQualityPolicy.Resolve(
            PhysicsChainQualityTier.Strict,
            authoredRateHz: 90.0f)
            .WithOverrides(
                PhysicsChainPolicyControl.Disabled,
                PhysicsChainPolicyControl.Enabled,
                PhysicsChainOutputControl.EverySimulationStep,
                PhysicsChainOutputControl.Hold,
                PhysicsChainOutputControl.EverySimulationStep);

        policy.SimulationEnabled.ShouldBeFalse();
        policy.CollisionEnabled.ShouldBeFalse();
        policy.PaletteCadence.ShouldBe(PhysicsChainOutputCadence.EverySimulationStep);
        policy.BoundsCadence.ShouldBe(PhysicsChainOutputCadence.Hold);
        policy.TransformMirrorCadence.ShouldBe(PhysicsChainOutputCadence.EverySimulationStep);
    }

    [TestCase(PhysicsChainOffscreenBehavior.DecayThenSleep, 100, PhysicsChainOffscreenBehavior.DecayThenSleep)]
    [TestCase(PhysicsChainOffscreenBehavior.AutomaticByImportance, 75, PhysicsChainOffscreenBehavior.Simulate)]
    [TestCase(PhysicsChainOffscreenBehavior.AutomaticByImportance, 25, PhysicsChainOffscreenBehavior.DecayThenSleep)]
    [TestCase(PhysicsChainOffscreenBehavior.AutomaticByImportance, 24, PhysicsChainOffscreenBehavior.SleepImmediately)]
    public void OffscreenBehaviorIsExplicitAndImportanceDriven(
        PhysicsChainOffscreenBehavior authored,
        int importance,
        PhysicsChainOffscreenBehavior expected)
        => PhysicsChainComponent.ResolveOffscreenBehavior(authored, importance).ShouldBe(expected);

    [TestCase(0, PhysicsChainQualityTier.Hz30)]
    [TestCase(3, PhysicsChainQualityTier.Hz30)]
    [TestCase(4, PhysicsChainQualityTier.Hz15)]
    [TestCase(7, PhysicsChainQualityTier.Hz7_5)]
    [TestCase(10, PhysicsChainQualityTier.Sleep)]
    public void OffscreenDecayUsesDeterministicStagedCadence(int frame, PhysicsChainQualityTier expected)
        => PhysicsChainComponent.ResolveOffscreenDecayTier(
            PhysicsChainQualityTier.Strict,
            frame,
            decayFrames: 10).ShouldBe(expected);

    [Test]
    public void CadencePhaseAndProgressAreDeterministicAndBounded()
    {
        float first = PhysicsChainComponent.ComputeDeterministicQualityPhase(17, 3u);
        float repeated = PhysicsChainComponent.ComputeDeterministicQualityPhase(17, 3u);
        float distinct = PhysicsChainComponent.ComputeDeterministicQualityPhase(18, 3u);

        first.ShouldBe(repeated);
        distinct.ShouldNotBe(first);
        first.ShouldBeGreaterThanOrEqualTo(0.0f);
        first.ShouldBeLessThan(1.0f);
        PhysicsChainComponent.ComputeCadenceProgress(0.025f, 30.0f, 0.0f).ShouldBe(0.75f, 0.00001f);
        PhysicsChainComponent.ComputeCadenceProgress(10.0f, 30.0f, 0.0f).ShouldBeLessThan(1.0f);
    }

    [Test]
    public void PaletteInterpolationBlendsAffinePoseWithoutChangingEndpoints()
    {
        Matrix4x4 previous = Matrix4x4.CreateScale(1.0f)
            * Matrix4x4.CreateTranslation(0.0f, 0.0f, 0.0f);
        Matrix4x4 current = Matrix4x4.CreateScale(3.0f)
            * Matrix4x4.CreateTranslation(4.0f, 2.0f, 0.0f);

        Matrix4x4 midpoint = PhysicsChainPaletteInterpolation.Interpolate(previous, current, 0.5f);

        Vector3.Distance(midpoint.Translation, new Vector3(2.0f, 1.0f, 0.0f))
            .ShouldBeLessThan(0.00001f);
        Vector3.Distance(new Vector3(midpoint.M11, midpoint.M22, midpoint.M33), new Vector3(2.0f))
            .ShouldBeLessThan(0.00001f);
        PhysicsChainPaletteInterpolation.Interpolate(previous, current, -1.0f).ShouldBe(previous);
        PhysicsChainPaletteInterpolation.Interpolate(previous, current, 2.0f).ShouldBe(current);
    }
}
