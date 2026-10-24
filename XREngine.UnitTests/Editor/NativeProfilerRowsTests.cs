using NUnit.Framework;
using Shouldly;
using XREngine.Data.Profiling;
using XREngine.Editor;
using Frame = XREngine.Engine.CodeProfiler.ProfilerFrameSnapshot;
using Thread = XREngine.Engine.CodeProfiler.ProfilerThreadSnapshot;
using Node = XREngine.Engine.CodeProfiler.ProfilerNodeSnapshot;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeProfilerRowsTests
{
    [Test]
    public void RepeatedSnapshotKeepsRowsAndOnlyTheLatestPendingRequestIsBuilt()
    {
        NativeProfilerRows rows = new();
        Frame first = Snapshot(1, 20);
        rows.Request(first);
        rows.Advance(1).ShouldBeFalse();
        rows.Count.ShouldBe(1);
        rows.Request(Snapshot(2, 2));
        rows.Request(Snapshot(3, 3));
        Finish(rows);
        rows.GetItem(0).Scope.ShouldBe("Frame 1 / Render 1");
        ulong previousId = rows.GetItemId(1);
        Finish(rows);
        rows.GetItem(0).Scope.ShouldBe("Frame 3 / Render 3");
        rows.Count.ShouldBe(5);
        rows.TryGetIndexOfItemId(previousId, out _).ShouldBeFalse();

        Frame stable = Snapshot(4, 4);
        rows.Request(stable);
        Finish(rows);
        ulong revision = rows.Revision;
        string title = rows.GetItem(0).Scope;
        ulong id = rows.GetItemId(2);
        rows.Request(stable);
        rows.Advance(3).ShouldBeFalse();
        rows.LastIndexWork.ShouldBe(0);
        rows.Revision.ShouldBe(revision);
        rows.GetItem(0).Scope.ShouldBeSameAs(title);
        rows.GetItemId(2).ShouldBe(id);
    }

    [Test]
    public void CompletedWorstWindowLabelsThePublishedHierarchy()
    {
        NativeProfilerRows rows = new();
        Frame frame = Snapshot(1, 1);
        rows.Request(frame);
        Finish(rows);
        rows.GetItem(0).Self.ShouldBeEmpty();
        ulong previousId = rows.GetItemId(0);

        rows.Request(frame, isWorst: true);
        Finish(rows);
        rows.GetItem(0).Self.ShouldContain("Worst CPU hierarchy");
        rows.GetItemId(0).ShouldNotBe(previousId);
        ulong worstId = rows.GetItemId(0);
        rows.Request(frame, isWorst: true);
        rows.Advance(3).ShouldBeFalse();
        rows.GetItemId(0).ShouldBe(worstId);
    }

    [Test]
    public void FormattingIsLazyBoundedAndCancelledIndexingKeepsTheDisplayedSnapshot()
    {
        NativeProfilerRows rows = new();
        rows.Request(Snapshot(1, 3000));
        Finish(rows);
        rows.FormattedRowCount.ShouldBe(0);
        for (int index = 0; index < rows.Count; index++) rows.GetItem(index);
        rows.FormattedRowCount.ShouldBe(2048);
        ulong id = rows.GetItemId(7);
        rows.Request(Snapshot(2, 3000));
        rows.Advance(1000).ShouldBeFalse();
        rows.CancelPending();
        rows.GetItem(0).Scope.ShouldBe("Frame 1 / Render 1");
        rows.GetItemId(7).ShouldBe(id);
        int retired = 0;
        do
        {
            rows.Advance(7).ShouldBeFalse();
            rows.LastIndexWork.ShouldBeLessThanOrEqualTo(7);
            rows.GetItemId(7).ShouldBe(id);
            retired += rows.LastIndexWork;
            retired.ShouldBeLessThan(10000);
        } while (rows.LastIndexWork != 0);
        retired.ShouldBeGreaterThan(900);
    }

    [Test]
    public void ComponentViewKeepsFrameDataLazyAndRetiresIdsAcrossViews()
    {
        NativeProfilerRows rows = new();
        rows.Request(Snapshot(1, 2));
        Finish(rows);
        ulong cpuId = rows.GetItemId(1);
        rows.SelectView(ENativeProfilerView.ComponentTimings).ShouldBeTrue();
        rows.Count.ShouldBe(1);
        rows.GetItem(0).Scope.ShouldContain("disabled");
        rows.TryGetIndexOfItemId(cpuId, out _).ShouldBeFalse();

        var components = new[]
        {
            new XREngine.Engine.CodeProfiler.ProfilerComponentTimingSnapshot(
                Guid.NewGuid(), "Motion", "MotionType", "Entity", 2.5f, 5, 3),
        };
        Frame frame = new(16, 1, 2, 0, 2, 2, 0, 0, 0, 0, 0,
            Array.Empty<Thread>(), new(16, components));
        rows.SetComponentFrame(frame, true).ShouldBeTrue();
        rows.Count.ShouldBe(2);
        rows.FormattedRowCount.ShouldBe(0);
        rows.GetItem(1).Scope.ShouldBe("Motion");
        rows.GetItem(1).Self.ShouldContain("Entity");
        rows.FormattedRowCount.ShouldBe(1);
        ulong revision = rows.Revision;
        rows.SetComponentFrame(frame, true).ShouldBeFalse();
        rows.Revision.ShouldBe(revision);
        rows.SetComponentFrame(frame, false).ShouldBeTrue();
        rows.Count.ShouldBe(1);
        rows.GetItem(0).Scope.ShouldContain("disabled");
        rows.SelectView(ENativeProfilerView.CpuTimings).ShouldBeTrue();
        rows.GetItem(1).Scope.ShouldBe("Scope 0");
        rows.GetItemId(1).ShouldNotBe(cpuId);
    }

    [Test]
    public void DiagnosticViewRetainsExactRowsUntilTheirValuesChange()
    {
        NativeProfilerRows rows = new();
        rows.SelectView(ENativeProfilerView.BvhMetrics).ShouldBeTrue();
        NativeProfilerDisplayRow[] diagnostics = [new("Build", "1.2", "3 items")];
        rows.SetDiagnostics(diagnostics).ShouldBeTrue();
        ulong revision = rows.Revision;
        ulong id = rows.GetItemId(0);
        rows.SetDiagnostics(diagnostics).ShouldBeFalse();
        rows.Revision.ShouldBe(revision);
        rows.GetItemId(0).ShouldBe(id);
        diagnostics[0] = new("Build", "2.4", "3 items");
        rows.SetDiagnostics(diagnostics).ShouldBeTrue();
        rows.GetItemId(0).ShouldNotBe(id);
        rows.GetItem(0).Total.ShouldBe("2.4");
        rows.SelectView(ENativeProfilerView.MainThreadInvokes).ShouldBeTrue();
        rows.GetItem(0).Scope.ShouldContain("Waiting");
        rows.TryGetIndexOfItemId(id, out _).ShouldBeFalse();
    }

    [Test]
    public void SettingsRefreshKeepsRowIdsSoAnEditorCanKeepItsFocus()
    {
        NativeProfilerRows rows = new();
        rows.SelectView(ENativeProfilerView.Settings).ShouldBeTrue();
        NativeProfilerDisplayRow[] first =
        [
            new("Snapshot interval", "33", "ms", (int)ENativeProfilerSetting.SnapshotInterval),
        ];
        rows.SetDiagnostics(first).ShouldBeTrue();
        ulong id = rows.GetItemId(0);
        ulong revision = rows.Revision;
        NativeProfilerDisplayRow[] updated =
        [
            new("Snapshot interval", "40", "ms", (int)ENativeProfilerSetting.SnapshotInterval),
        ];
        rows.SetDiagnostics(updated).ShouldBeTrue();
        rows.Revision.ShouldBeGreaterThan(revision);
        rows.GetItemId(0).ShouldBe(id);
        rows.GetItem(0).Total.ShouldBe("40");
        rows.TryGetIndexOfItemId(id, out int index).ShouldBeTrue();
        index.ShouldBe(0);
        NativeProfilerDisplayRow[] replacement =
        [
            new("History capacity", "240", "samples", (int)ENativeProfilerSetting.HistoryCapacity),
        ];
        rows.SetDiagnostics(replacement).ShouldBeTrue();
        rows.GetItemId(0).ShouldNotBe(id);
    }

    private static Frame Snapshot(ulong frame, int count)
    {
        Node[] nodes = Enumerable.Range(0, count).Select(index =>
            new Node("Scope " + index, 1, ProfilerScopeKind.Unspecified, Array.Empty<Node>())).ToArray();
        return new Frame(16, 1, (long)frame, 0, frame, frame, 0, 0, 0, 0, 0,
            new[] { new Thread(1, nodes) }, null);
    }

    private static void Finish(NativeProfilerRows rows)
    {
        int attempts = 0;
        while (!rows.Advance(7))
        {
            rows.LastIndexWork.ShouldBeLessThanOrEqualTo(7);
            (++attempts).ShouldBeLessThan(10000);
        }
        rows.LastIndexWork.ShouldBeLessThanOrEqualTo(7);
    }
}
