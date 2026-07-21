using NUnit.Framework;
using Shouldly;
using XREngine.Editor.Benchmarks.PhysicsChain;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainBenchmarkRequiredMatrixTests
{
    [Test]
    public void RequiredMatrixContainsEveryNamedAxisAndStableCaseCount()
    {
        PhysicsChainBenchmarkRequiredMatrix.CaseCount.ShouldBe(786_432);

        HashSet<int> counts = [];
        HashSet<int> segments = [];
        HashSet<PhysicsChainBenchmarkActivityProfile> activity = [];
        HashSet<PhysicsChainBenchmarkRenderBackend> backends = [];
        HashSet<int> rates = [];
        int enumerated = 0;
        foreach (PhysicsChainBenchmarkCase item in PhysicsChainBenchmarkRequiredMatrix.Enumerate())
        {
            counts.Add(item.ChainCount);
            segments.Add(item.DynamicSegmentCount);
            activity.Add(item.ActivityProfile);
            backends.Add(item.RenderBackend);
            rates.Add(item.FixedSimulationRateHz);
            ++enumerated;
        }

        enumerated.ShouldBe(PhysicsChainBenchmarkRequiredMatrix.CaseCount);
        counts.ShouldBe([100, 500, 1_000, 2_000, 5_000, 10_000], ignoreOrder: true);
        segments.ShouldBe([4, 8, 16, 32], ignoreOrder: true);
        rates.ShouldBe([30, 60, 90, 120], ignoreOrder: true);
        activity.ShouldContain(PhysicsChainBenchmarkActivityProfile.SleepingOffscreenHeavy);
        backends.ShouldContain(PhysicsChainBenchmarkRenderBackend.OpenGL);
        backends.ShouldContain(PhysicsChainBenchmarkRenderBackend.Vulkan);
    }

    [Test]
    public void AcceptanceRequiresThreeOrMoreMatchedRuns()
    {
        new PhysicsChainBenchmarkRunPolicy().Validate();
        Should.Throw<ArgumentOutOfRangeException>(() => new PhysicsChainBenchmarkRunPolicy { MatchedRunCount = 2 }.Validate());
    }
}
