using NUnit.Framework;
using Shouldly;
using XREngine.Editor.Benchmarks.PhysicsChain;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainBenchmarkDeterministicScenarioTests
{
    [Test]
    public void SameCaseSeedAndFrameProduceIdenticalInputs()
    {
        PhysicsChainBenchmarkCase matrixCase = CreateCase();
        var first = new PhysicsChainBenchmarkDeterministicScenario(matrixCase, 1234);
        var second = new PhysicsChainBenchmarkDeterministicScenario(matrixCase, 1234);

        for (int segment = 0; segment < matrixCase.DynamicSegmentCount; ++segment)
        {
            first.GetParentIndex(segment).ShouldBe(second.GetParentIndex(segment));
            first.GetRestLength(segment).ShouldBe(second.GetRestLength(segment));
        }
        for (int collider = 0; collider < first.ColliderCount; ++collider)
            first.GetCollider(collider).ShouldBe(second.GetCollider(collider));
        for (int chain = 0; chain < matrixCase.ChainCount; ++chain)
            first.Sample(chain, 987).ShouldBe(second.Sample(chain, 987));
    }

    [Test]
    public void ScenarioAxesControlTopologyOwnershipActivityAndColliderLayout()
    {
        PhysicsChainBenchmarkCase baseCase = CreateCase();
        var branched = new PhysicsChainBenchmarkDeterministicScenario(baseCase, 1);
        var linear = new PhysicsChainBenchmarkDeterministicScenario(
            baseCase with { Topology = PhysicsChainBenchmarkTopology.Linear },
            1);
        var unique = new PhysicsChainBenchmarkDeterministicScenario(
            baseCase with { ColliderOwnership = PhysicsChainBenchmarkColliderOwnership.Unique },
            1);

        branched.GetParentIndex(4).ShouldBe(1);
        linear.GetParentIndex(4).ShouldBe(3);
        branched.ColliderCount.ShouldBe(64);
        branched.GetCollider(0).ShouldNotBe(branched.GetCollider(1));
        branched.Sample(9, 0).ColliderSetIndex.ShouldBe(0);
        unique.Sample(9, 0).ColliderSetIndex.ShouldBe(9);

        int activeCount = 0;
        int visibleCount = 0;
        for (int chain = 0; chain < baseCase.ChainCount; ++chain)
        {
            PhysicsChainBenchmarkDynamicInput input = branched.Sample(chain, 0);
            if (input.IsActive)
                ++activeCount;
            if (input.IsVisible)
                ++visibleCount;
        }
        activeCount.ShouldBeLessThan(baseCase.ChainCount / 4);
        visibleCount.ShouldBeLessThan(baseCase.ChainCount / 3);
    }

    [Test]
    public void SeedChangesAuthoredAndDynamicInputsWithoutGlobalRandomState()
    {
        PhysicsChainBenchmarkCase matrixCase = CreateCase();
        var first = new PhysicsChainBenchmarkDeterministicScenario(matrixCase, 1);
        var second = new PhysicsChainBenchmarkDeterministicScenario(matrixCase, 2);

        first.GetRestLength(3).ShouldNotBe(second.GetRestLength(3));
        first.GetCollider(3).ShouldNotBe(second.GetCollider(3));
        first.Sample(3, 45).ShouldNotBe(second.Sample(3, 45));
    }

    private static PhysicsChainBenchmarkCase CreateCase()
        => new(
            100,
            8,
            PhysicsChainBenchmarkTopology.Branched,
            PhysicsChainBenchmarkColliderScenario.LargeBroadphase,
            PhysicsChainBenchmarkColliderOwnership.Shared,
            PhysicsChainBenchmarkActivityProfile.SleepingOffscreenHeavy,
            PhysicsChainBenchmarkRenderingMode.DiverseSkinnedRenderers,
            PhysicsChainBenchmarkExecutionMode.GpuQualityTiered,
            PhysicsChainBenchmarkReadbackMode.SparseSockets,
            PhysicsChainBenchmarkRenderBackend.OpenGL,
            60);
}
