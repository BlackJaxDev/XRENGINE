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
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        aggregate.Request(first, start, 5f);
        aggregate.Pump(2).ShouldBeFalse();
        aggregate.Request(first, start.AddMilliseconds(50), 5f);
        aggregate.LastWorkCount.ShouldBeLessThanOrEqualTo(2);
        aggregate.Count.ShouldBe(0);
        while (!aggregate.Pump(2))
            aggregate.LastWorkCount.ShouldBeLessThanOrEqualTo(2);
        aggregate.Count.ShouldBe(2);
        aggregate[0].Calls.ShouldBe(2);
        aggregate[0].TotalMilliseconds.ShouldBe(5);
        aggregate[0].PeakMilliseconds.ShouldBe(3);
        while (aggregate.IsRetiring)
            aggregate.Pump(2);
        aggregate.Request(first, start.AddMilliseconds(100), 5f);
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
        aggregate.Request(Snapshot(3, new Node("A", 4, ProfilerScopeKind.Unspecified, [])));
        Finish(aggregate);
        aggregate.CreatedAggregateCount.ShouldBe(4);
        aggregate[0].ShouldBeSameAs(firstBuffer);
        aggregate[0].Name.ShouldBe("A");
        aggregate[0].Calls.ShouldBe(1);
        aggregate[0].TotalMilliseconds.ShouldBe(4);
        aggregate.Request(Snapshot(4, new Node("B", 5, ProfilerScopeKind.Unspecified, [])));
        Finish(aggregate);
        aggregate.CreatedAggregateCount.ShouldBe(4);
        aggregate.Count.ShouldBe(2);
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

    [Test]
    public void MissingRootAndChildPersistThenExpireWithinThePumpBudget()
    {
        NativeProfilerRootAggregator aggregate = new();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Node child = new("Work", 2, ProfilerScopeKind.ConditionalLoop, []);
        Frame first = Snapshot(20, new Node("Update", 5, ProfilerScopeKind.Unspecified, [child]));
        aggregate.Request(first, start, 1f);
        Finish(aggregate);
        aggregate.Count.ShouldBe(1);
        aggregate.ChildCount.ShouldBe(1);

        Frame absent = Snapshot(21);
        aggregate.Request(absent, start.AddMilliseconds(500), 1f);
        Finish(aggregate);
        aggregate[0].Name.ShouldBe("Update");
        aggregate[0].IsInactive.ShouldBeTrue();
        aggregate[0].TotalMilliseconds.ShouldBe(5);
        aggregate.ChildAt(0).IsInactive.ShouldBeTrue();
        aggregate.ChildAt(0).TotalMilliseconds.ShouldBe(2);

        aggregate.Request(absent, start.AddMilliseconds(1001), 1f);
        Finish(aggregate);
        aggregate.Count.ShouldBe(0);
        aggregate.ChildCount.ShouldBe(0);
    }

    [Test]
    public void ReappearingRootKeepsKindAndChildParentIdentity()
    {
        NativeProfilerRootAggregator aggregate = new();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Node oldChild = new("Work", 1, ProfilerScopeKind.Unspecified, []);
        aggregate.Request(Snapshot(22,
            new Node("Root", 4, ProfilerScopeKind.Unspecified, [oldChild]),
            new Node("Root", 3, ProfilerScopeKind.ConditionalLoop, [])), start, 5f);
        Finish(aggregate);
        aggregate.Request(Snapshot(23,
            new Node("Root", 8, ProfilerScopeKind.Unspecified,
                [new Node("Other", 2, ProfilerScopeKind.Unspecified, [])])),
            start.AddSeconds(1), 5f);
        Finish(aggregate);
        aggregate.Count.ShouldBe(2);
        aggregate[0].TotalMilliseconds.ShouldBe(8);
        aggregate[0].IsInactive.ShouldBeFalse();
        aggregate[1].Kind.ShouldBe(ProfilerScopeKind.ConditionalLoop);
        aggregate[1].IsInactive.ShouldBeTrue();
        aggregate.ChildCount.ShouldBe(2);
        aggregate.ChildAt(0).Name.ShouldBe("Other");
        aggregate.ChildAt(1).Name.ShouldBe("Work");
        aggregate.ChildAt(1).RootIndex.ShouldBe(0);
        aggregate.ChildAt(1).IsInactive.ShouldBeTrue();
    }

    [Test]
    public void PersistenceRefreshKeepsThePublishedIndexUntilComplete()
    {
        NativeProfilerRootAggregator aggregate = new();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Node[] roots = Enumerable.Range(0, 300).Select(index =>
            new Node("Root " + index, 1, ProfilerScopeKind.Unspecified, [])).ToArray();
        aggregate.Request(Snapshot(24, roots), start, 5f);
        Finish(aggregate);
        Frame empty = Snapshot(25);
        aggregate.Request(empty, start.AddSeconds(1), 5f);
        aggregate.Pump(7).ShouldBeFalse();
        aggregate.LastWorkCount.ShouldBeLessThanOrEqualTo(7);
        aggregate.Count.ShouldBe(300);
        int pumps = 0;
        while (!aggregate.Pump(7))
        {
            aggregate.LastWorkCount.ShouldBeLessThanOrEqualTo(7);
            (++pumps).ShouldBeLessThan(1000);
        }
        aggregate.Count.ShouldBe(300);
        aggregate[0].IsInactive.ShouldBeTrue();
    }

    [Test]
    public void NewSessionDoesNotCarryOldRoots()
    {
        NativeProfilerRootAggregator aggregate = new();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        aggregate.Request(Snapshot(26,
            new Node("Old", 5, ProfilerScopeKind.Unspecified, [])), start, 5f);
        Finish(aggregate);
        Frame newSession = new(16, 2, 1, 0, 1, 1, 0, 0, 0, 0, 0,
            [new Thread(1, [new Node("New", 2, ProfilerScopeKind.Unspecified, [])])], null);
        aggregate.Request(newSession, start.AddMilliseconds(100), 5f);
        Finish(aggregate);
        aggregate.Count.ShouldBe(1);
        aggregate[0].Name.ShouldBe("New");
    }

    [Test]
    public void RepeatedSnapshotBecomesInactiveOnlyAfterTheUpdateInterval()
    {
        NativeProfilerRootAggregator aggregate = new();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Frame frame = Snapshot(27,
            new Node("Update", 5, ProfilerScopeKind.Unspecified,
                [new Node("Work", 2, ProfilerScopeKind.Unspecified, [])]));
        aggregate.Request(frame, start, 5f, 0.25f);
        Finish(aggregate);
        while (aggregate.IsRetiring)
            aggregate.Pump(7);
        aggregate.Request(frame, start.AddMilliseconds(249), 5f, 0.25f);
        aggregate.Pump(7).ShouldBeFalse();
        aggregate.LastWorkCount.ShouldBe(0);
        aggregate[0].IsInactive.ShouldBeFalse();
        aggregate.ChildAt(0).IsInactive.ShouldBeFalse();
        aggregate.Request(frame, start.AddMilliseconds(250), 5f, 0.25f);
        aggregate.Pump(7).ShouldBeFalse();
        aggregate.LastWorkCount.ShouldBe(0);
        aggregate[0].IsInactive.ShouldBeFalse();
        aggregate.Request(frame, start.AddMilliseconds(251), 5f, 0.25f);
        Finish(aggregate);
        aggregate[0].IsInactive.ShouldBeTrue();
        aggregate.ChildAt(0).IsInactive.ShouldBeTrue();
    }

    [Test]
    public void BackwardClockWithSameFrameRebuildsTheCurrentHierarchy()
    {
        NativeProfilerRootAggregator aggregate = new();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Frame frame = Snapshot(28,
            new Node("Update", 5, ProfilerScopeKind.Unspecified, []));
        aggregate.Request(frame, start, 5f);
        Finish(aggregate);
        aggregate.Request(frame, start.AddMilliseconds(-1), 5f);
        Finish(aggregate);
        aggregate.Count.ShouldBe(1);
        aggregate[0].Name.ShouldBe("Update");
        aggregate[0].IsInactive.ShouldBeFalse();
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
