using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeProfilerProjectionTests
{
    [Test]
    public void WideAndDeepTreesStayWithinEachTraversalBudget()
    {
        ProfilerProjectionTestNode[] wide = Enumerable.Range(0, 1000)
            .Select(_ => new ProfilerProjectionTestNode()).ToArray();
        ProfilerProjectionTestNode deep = new();
        for (int index = 0; index < 999; index++) deep = new(deep);
        var projection = Create();
        projection.Begin([wide, new[] { deep }]);
        int pumps = 0;
        while (!projection.Pump(7))
        {
            projection.LastWorkCount.ShouldBeLessThanOrEqualTo(7);
            projection.Count.ShouldBe(0);
            (++pumps).ShouldBeLessThan(2000);
        }
        projection.LastWorkCount.ShouldBeLessThanOrEqualTo(7);
        projection.Count.ShouldBe(2002);
        projection[0].Node.ShouldBeNull();
        projection[1].Node.ShouldBeSameAs(wide[0]);
        projection[1001].GroupIndex.ShouldBe(1);
        projection[1001].Node.ShouldBeNull();
        projection[2001].Depth.ShouldBe(1000);
    }

    [Test]
    public void ReplacementAndTailRetirementPublishOnlyACompleteIndex()
    {
        var projection = Create();
        ProfilerProjectionTestNode first = new();
        ProfilerProjectionTestNode[] many = Enumerable.Range(0, 100)
            .Select(_ => new ProfilerProjectionTestNode()).ToArray();
        projection.Begin([many]);
        while (!projection.Pump(8)) { }
        projection.Count.ShouldBe(101);
        projection.Begin([new[] { first }]);
        while (!projection.Pump(8)) projection.Count.ShouldBe(101);
        projection.Count.ShouldBe(2);

        // The reused back buffer still contains the earlier wide index.
        ProfilerProjectionTestNode next = new();
        projection.Begin([new[] { next }]);
        int retirementSteps = 0;
        while (!projection.Pump(3))
        {
            projection.Count.ShouldBe(2);
            projection[1].Node.ShouldBeSameAs(first);
            projection.LastWorkCount.ShouldBeLessThanOrEqualTo(3);
            retirementSteps += projection.LastWorkCount;
        }
        retirementSteps.ShouldBeGreaterThan(90);
        projection[1].Node.ShouldBeSameAs(next);
    }

    [Test]
    public void CancellationKeepsThePublishedIndexAndReleasesPendingWork()
    {
        var projection = Create();
        ProfilerProjectionTestNode published = new();
        projection.Begin([new[] { published }]);
        while (!projection.Pump(8)) { }
        projection.Begin([new[] { new ProfilerProjectionTestNode(new ProfilerProjectionTestNode()) }]);
        projection.Pump(1).ShouldBeFalse();
        projection.Cancel();
        projection.IsBuilding.ShouldBeFalse();
        projection[1].Node.ShouldBeSameAs(published);
        projection.Pump(2).ShouldBeFalse();
        projection.LastWorkCount.ShouldBeLessThanOrEqualTo(2);
        projection.Clear();
        projection.Count.ShouldBe(0);
    }

    [Test]
    public void CancelledWideIndexRetiresWithinLaterPumpBudgets()
    {
        var projection = Create();
        ProfilerProjectionTestNode[] many = Enumerable.Range(0, 1000)
            .Select(_ => new ProfilerProjectionTestNode()).ToArray();
        projection.Begin([many]);
        projection.Pump(900).ShouldBeFalse();
        projection.Cancel();
        projection.LastWorkCount.ShouldBe(0);
        int steps = 0;
        do
        {
            projection.Pump(7).ShouldBeFalse();
            projection.LastWorkCount.ShouldBeLessThanOrEqualTo(7);
            projection.Count.ShouldBe(0);
            steps += projection.LastWorkCount;
            steps.ShouldBeLessThan(2000);
        } while (projection.LastWorkCount != 0);
        steps.ShouldBeGreaterThan(800);
    }

    private static NativeProfilerProjection<IReadOnlyList<ProfilerProjectionTestNode>, ProfilerProjectionTestNode> Create()
        => new(static group => group, static node => node.Children);
}
