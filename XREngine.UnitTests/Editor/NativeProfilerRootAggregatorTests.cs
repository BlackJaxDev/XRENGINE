using NUnit.Framework;
using Shouldly;
using XREngine.Data.Profiling;
using XREngine.Editor;
using Frame = XREngine.Engine.CodeProfiler.ProfilerFrameSnapshot;
using Thread = XREngine.Engine.CodeProfiler.ProfilerThreadSnapshot;
using Node = XREngine.Engine.CodeProfiler.ProfilerNodeSnapshot;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeProfilerRootAggregatorTests
{
    [Test]
    public void RootAggregationKeepsCompleteSnapshotAndBoundsEachPump()
    {
        NativeProfilerRootAggregator aggregate = new();
        Frame first = Snapshot(1,
            new Node("Update", 2, ProfilerScopeKind.Unspecified, []),
            new Node("Update", 3, ProfilerScopeKind.Unspecified, []),
            new Node("Render", 4, ProfilerScopeKind.Unspecified, []));
        aggregate.Request(first);
        aggregate.Pump(2).ShouldBeFalse();
        aggregate.Request(first);
        aggregate.LastWorkCount.ShouldBeLessThanOrEqualTo(2);
        aggregate.Count.ShouldBe(0);
        while (!aggregate.Pump(2))
            aggregate.LastWorkCount.ShouldBeLessThanOrEqualTo(2);
        aggregate.Count.ShouldBe(2);
        aggregate[0].Calls.ShouldBe(2);
        aggregate[0].TotalMilliseconds.ShouldBe(5);
        aggregate[0].PeakMilliseconds.ShouldBe(3);
        aggregate.Pump(2).ShouldBeFalse();
        aggregate.LastWorkCount.ShouldBe(0);

        aggregate.Request(Snapshot(2, new Node("New", 1, ProfilerScopeKind.Unspecified, [])));
        aggregate.Pump(1).ShouldBeFalse();
        aggregate[0].Name.ShouldBe("Update");
        aggregate.CancelPending();
        aggregate.Pump(2).ShouldBeFalse();
        aggregate[0].Name.ShouldBe("Update");
    }

    [Test]
    public void RootRowsKeepLazyFormattingAndStableIdUntilNextCompleteSnapshot()
    {
        NativeProfilerRows rows = new();
        rows.SelectView(ENativeProfilerView.CpuRoots).ShouldBeTrue();
        rows.SetRootParameters(0, 0, true).ShouldBeFalse();
        rows.Request(Snapshot(1,
            new Node("Slow", 8, ProfilerScopeKind.Unspecified, []),
            new Node("Fast", 2, ProfilerScopeKind.Unspecified, [])));
        while (!rows.Advance(1))
            rows.LastIndexWork.ShouldBeLessThanOrEqualTo(1);
        rows.Count.ShouldBe(6);
        rows.FormattedRowCount.ShouldBe(0);
        rows.GetItem(4).Scope.ShouldContain("Slow");
        ulong id = rows.GetItemId(4);
        rows.SetRootParameters(0, 4, true).ShouldBeTrue();
        rows.Count.ShouldBe(5);
        rows.GetItem(4).Scope.ShouldContain("Fast");
        rows.TryGetIndexOfItemId(id, out _).ShouldBeFalse();
    }

    [Test]
    public void ChildTraversalExcludesDownstreamRenderWaitWithinThePumpBudget()
    {
        Node wait = new("EngineTimer.CollectVisibleThread.WaitForRender", 3,
            ProfilerScopeKind.Unspecified, []);
        Node root = new("Update", 10, ProfilerScopeKind.Unspecified, [wait]);
        NativeProfilerRootAggregator aggregate = new();
        aggregate.Request(Snapshot(3, root));
        int pumps = 0;
        while (!aggregate.Pump(1))
        {
            aggregate.LastWorkCount.ShouldBeLessThanOrEqualTo(1);
            (++pumps).ShouldBeLessThan(20);
        }
        aggregate[0].TotalMilliseconds.ShouldBe(7);
    }

    [Test]
    public void AlternatingSnapshotsReuseBothAggregateBuffersAfterWarmup()
    {
        NativeProfilerRootAggregator aggregate = new();
        aggregate.Request(Snapshot(1, new Node("A", 2, ProfilerScopeKind.Unspecified, [])));
        Finish(aggregate);
        NativeProfilerRootAggregate firstBuffer = aggregate[0];
        aggregate.Request(Snapshot(2, new Node("B", 3, ProfilerScopeKind.Unspecified, [])));
        Finish(aggregate);
        aggregate.CreatedAggregateCount.ShouldBe(2);
        aggregate.Request(Snapshot(3, new Node("C", 4, ProfilerScopeKind.Unspecified, [])));
        Finish(aggregate);
        aggregate.CreatedAggregateCount.ShouldBe(2);
        aggregate[0].ShouldBeSameAs(firstBuffer);
        aggregate[0].Name.ShouldBe("C");
        aggregate[0].Calls.ShouldBe(1);
        aggregate[0].TotalMilliseconds.ShouldBe(4);
    }

    [Test]
    public void CancelledDeepTraversalRetiresReferencesWithinEachPumpBudget()
    {
        Node chain = new("Leaf", 1, ProfilerScopeKind.Unspecified, []);
        for (int index = 0; index < 300; index++)
            chain = new("Branch", 1, ProfilerScopeKind.Unspecified, [chain]);
        NativeProfilerRootAggregator aggregate = new();
        aggregate.Request(Snapshot(4, chain));
        aggregate.Pump(140).ShouldBeFalse();
        aggregate.CancelPending();
        aggregate.IsRetiring.ShouldBeTrue();
        aggregate.Request(Snapshot(5, new Node("Replacement", 7, ProfilerScopeKind.Unspecified, [])));
        int pumps = 0;
        while (!aggregate.Pump(7))
        {
            aggregate.LastWorkCount.ShouldBeLessThanOrEqualTo(7);
            aggregate.PublishedFrame.ShouldBeNull();
            (++pumps).ShouldBeLessThan(200);
        }
        aggregate.IsRetiring.ShouldBeTrue();
        aggregate[0].Name.ShouldBe("Replacement");
        aggregate[0].TotalMilliseconds.ShouldBe(7);
    }

    [Test]
    public void CancelledWideIndexRetiresStoredNamesBeforeAdmittingReplacement()
    {
        Node[] wide = Enumerable.Range(0, 600).Select(index =>
            new Node("Root " + index, 1, ProfilerScopeKind.Unspecified, [])).ToArray();
        NativeProfilerRootAggregator aggregate = new();
        aggregate.Request(Snapshot(6, wide));
        aggregate.Pump(400).ShouldBeFalse();
        aggregate.CreatedAggregateCount.ShouldBeGreaterThan(100);
        aggregate.CancelPending();
        aggregate.Request(Snapshot(7, new Node("Current", 9, ProfilerScopeKind.Unspecified, [])));
        int retirementPumps = 0;
        while (aggregate.PublishedFrame is null)
        {
            aggregate.Pump(7);
            aggregate.LastWorkCount.ShouldBeLessThanOrEqualTo(7);
            (++retirementPumps).ShouldBeLessThan(200);
        }
        retirementPumps.ShouldBeGreaterThan(20);
        aggregate[0].Name.ShouldBe("Current");
        aggregate[0].TotalMilliseconds.ShouldBe(9);
    }

    [Test]
    public void ChildPathsMergeRepeatedCallsAndKeepDistinctParents()
    {
        Node left = new("Left", 4, ProfilerScopeKind.Unspecified,
            [new Node("Work", 2, ProfilerScopeKind.Unspecified, [])]);
        Node right = new("Right", 3, ProfilerScopeKind.Unspecified,
            [new Node("Work", 1, ProfilerScopeKind.Unspecified, [])]);
        NativeProfilerRootAggregator aggregate = new();
        aggregate.Request(Snapshot(8,
            new Node("Update", 9, ProfilerScopeKind.Unspecified, [left, right]),
            new Node("Update", 9, ProfilerScopeKind.Unspecified, [left])));
        Finish(aggregate);
        aggregate.Count.ShouldBe(1);
        aggregate.ChildCount.ShouldBe(4);
        aggregate.ChildAt(0).Name.ShouldBe("Left");
        aggregate.ChildAt(0).Calls.ShouldBe(2);
        aggregate.ChildAt(1).Name.ShouldBe("Work");
        aggregate.ChildAt(1).ParentIndex.ShouldBe(0);
        aggregate.ChildAt(1).Calls.ShouldBe(2);
        aggregate.ChildAt(3).Name.ShouldBe("Work");
        aggregate.ChildAt(3).ParentIndex.ShouldBe(2);
        aggregate.ChildAt(3).Calls.ShouldBe(1);

        NativeProfilerRows rows = new();
        rows.SelectView(ENativeProfilerView.CpuRoots);
        rows.Request(Snapshot(9,
            new Node("Update", 9, ProfilerScopeKind.Unspecified, [left, right])));
        while (!rows.Advance(3)) rows.LastIndexWork.ShouldBeLessThanOrEqualTo(3);
        rows.SelectRoot("Update", ProfilerScopeKind.Unspecified).ShouldBeTrue();
        rows.Count.ShouldBe(10);
        rows.GetItem(6).Scope.ShouldContain("Left");
        rows.GetItem(7).Scope.ShouldContain("Work");
        rows.GetItem(9).Scope.ShouldContain("Work");
    }

    private static void Finish(NativeProfilerRootAggregator aggregate)
    {
        int pumps = 0;
        while (!aggregate.Pump(7))
        {
            aggregate.LastWorkCount.ShouldBeLessThanOrEqualTo(7);
            (++pumps).ShouldBeLessThan(1000);
        }
    }

    private static Frame Snapshot(ulong id, params Node[] roots)
        => new(16, 1, (long)id, 0, id, id, 0, 0, 0, 0, 0,
            new[] { new Thread(1, roots) }, null);
}
