using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainArenaCompactionPolicyTests
{
    private static readonly PhysicsChainArenaCompactionPolicy Policy = new()
    {
        MinimumCapacity = 100,
        MinimumReclaimableSlots = 20,
        FragmentationThreshold = 0.25,
    };

    [Test]
    public void BelowThresholdDoesNotRequestCompaction()
    {
        PhysicsChainArenaCompactionDecision decision = Policy.Evaluate(
            new PhysicsChainArenaSnapshot(100, 80, 1, 1, 0),
            activeFrameCount: 0);

        decision.Kind.ShouldBe(PhysicsChainArenaCompactionDecisionKind.NotRequired);
    }

    [Test]
    public void ActiveFramesDeferRebuildAndPreserveCurrentGeneration()
    {
        PhysicsChainArenaCompactionDecision decision = Policy.Evaluate(
            new PhysicsChainArenaSnapshot(100, 50, 1, 1, 0),
            activeFrameCount: 2);

        decision.Kind.ShouldBe(PhysicsChainArenaCompactionDecisionKind.DeferredUntilFramesComplete);
        decision.ReclaimableSlots.ShouldBe(50);
    }

    [Test]
    public void QuiescentFragmentedArenaUsesOutOfBandRebuildAndSwap()
    {
        PhysicsChainArenaCompactionDecision decision = Policy.Evaluate(
            new PhysicsChainArenaSnapshot(100, 50, 1, 1, 0),
            activeFrameCount: 0);

        decision.Kind.ShouldBe(PhysicsChainArenaCompactionDecisionKind.RebuildAndSwap);
        decision.Reason.ShouldContain("retire the old generation");
    }
}
