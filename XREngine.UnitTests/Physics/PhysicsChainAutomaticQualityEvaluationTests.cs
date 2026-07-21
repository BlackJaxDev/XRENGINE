using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainAutomaticQualityEvaluationTests
{
    [TestCase(200.0f, 0.20f, 50, PhysicsChainQualityTier.Strict)]
    [TestCase(200.0f, 0.03f, 50, PhysicsChainQualityTier.Hz30)]
    [TestCase(50.0f, 0.0f, 50, PhysicsChainQualityTier.Hz15)]
    [TestCase(200.0f, 0.0f, 50, PhysicsChainQualityTier.Hz7_5)]
    [TestCase(200.0f, 0.0f, 80, PhysicsChainQualityTier.Hz15)]
    [TestCase(2.0f, 0.0f, 10, PhysicsChainQualityTier.Hz30)]
    public void DistanceProjectedSizeAndImportanceResolveVisibleTier(
        float distance,
        float projectedSize,
        int importance,
        PhysicsChainQualityTier expected)
    {
        var observation = new PhysicsChainAutomaticQualityObservation(distance, projectedSize, Visible: true);

        PhysicsChainAutomaticQualityEvaluation.ResolveVisibleTier(observation, importance).ShouldBe(expected);
    }

    [Test]
    public void InvalidObservationFailsSafeToStrict()
    {
        var observation = new PhysicsChainAutomaticQualityObservation(float.NaN, 0.0f, Visible: true);

        observation.IsValid.ShouldBeFalse();
        PhysicsChainAutomaticQualityEvaluation.ResolveVisibleTier(observation, 0)
            .ShouldBe(PhysicsChainQualityTier.Strict);
    }
}
