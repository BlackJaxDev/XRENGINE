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
