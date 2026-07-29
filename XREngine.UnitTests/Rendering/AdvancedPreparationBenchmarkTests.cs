using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedPreparationBenchmarkTests
{
    private static readonly uint[] InstanceCounts = [1u, 8u, 32u, 128u];
    private static readonly EAdvancedPreparationBenchmarkScenario[] Scenarios =
        Enum.GetValues<EAdvancedPreparationBenchmarkScenario>();

    [TestCaseSource(nameof(Cases))]
    public void RequiredSkeletalMatrixUsesOneWarmedAggregateDispatch(
        uint instances,
        EAdvancedPreparationBenchmarkScenario scenario)
    {
        AdvancedPreparationBenchmarkRunner runner = new(128);
        runner.Run(instances, scenario, verticesPerInstance: 1_000u);

        AdvancedPreparationBenchmarkSample sample =
            runner.Run(
                instances,
                scenario,
                verticesPerInstance: 1_000u);

        sample.SkeletalInstanceCount.ShouldBe(instances);
        sample.Scenario.ShouldBe(scenario);
        sample.AdmittedJobCount.ShouldBe(instances);
        sample.DispatchCount.ShouldBe(1u);
        sample.VertexCount.ShouldBe((ulong)instances * 1_000UL);
        sample.ManagedBytesAllocated.ShouldBe(0L);
        sample.ElapsedTicks.ShouldBeGreaterThanOrEqualTo(0L);
        TestContext.Progress.WriteLine(
            $"ADV_PREP_BENCH instances={instances} scenario={scenario} " +
            $"jobs={sample.AdmittedJobCount} dispatches={sample.DispatchCount} " +
            $"vertices={sample.VertexCount} ticks={sample.ElapsedTicks} " +
            $"allocated={sample.ManagedBytesAllocated}");
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        for (int countIndex = 0;
             countIndex < InstanceCounts.Length;
             countIndex++)
        {
            for (int scenarioIndex = 0;
                 scenarioIndex < Scenarios.Length;
                 scenarioIndex++)
            {
                yield return new TestCaseData(
                    InstanceCounts[countIndex],
                    Scenarios[scenarioIndex])
                    .SetName(
                        $"Skeletal_{InstanceCounts[countIndex]}_{Scenarios[scenarioIndex]}");
            }
        }
    }
}
