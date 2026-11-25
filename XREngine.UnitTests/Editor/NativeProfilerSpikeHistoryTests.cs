using NUnit.Framework;
using Shouldly;
using XREngine.Data.Profiling;
using XREngine.Editor;
using Frame = XREngine.Engine.CodeProfiler.ProfilerFrameSnapshot;
using Thread = XREngine.Engine.CodeProfiler.ProfilerThreadSnapshot;
using Node = XREngine.Engine.CodeProfiler.ProfilerNodeSnapshot;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeProfilerSpikeHistoryTests
{
    [Test]
    public void RepeatedHotPathAggregatesOnlyDistinctFramesAndClearRetiresHistory()
    {
        NativeProfilerSpikeHistory history = new();
        Frame first = Snapshot(1);
        Dictionary<int, float[]> samples = new() { [7] = [10f, 10f, 20f] };
        history.Record(first, samples, 10f, 1f, 2).ShouldBeTrue();
        history.Count.ShouldBe(1);
        history[0].HotPath.ShouldBe("Root > Hot");
        history[0].SeenCount.ShouldBe(1);
        history.Record(first, samples, 10f, 1f, 2).ShouldBeFalse();
        history[0].SeenCount.ShouldBe(1);

        history.Record(Snapshot(2), samples, 10f, 1f, 2).ShouldBeTrue();
        history.Count.ShouldBe(1);
        history[0].SeenCount.ShouldBe(2);
        history.Clear().ShouldBeTrue();
        history.Count.ShouldBe(0);
        history.Clear().ShouldBeFalse();
    }

    [Test]
    public void SpikeRowsSortByWorstDropAndHonorTheMinimumDrop()
    {
        NativeProfilerSpikeHistory history = new();
        Node root = new("Work", 2, ProfilerScopeKind.Unspecified, Array.Empty<Node>());
        Frame frame = new(0.03f, 1, 1, 0, 1, 1, 0, 0, 0, 0, 0,
            new[] { new Thread(7, new[] { root }), new Thread(8, new[] { root }) }, null);
        Dictionary<int, float[]> samples = new()
        {
            [7] = [10f, 10f, 20f],
            [8] = [10f, 10f, 30f],
        };
        history.Record(frame, samples, 10f, 1f, 2, minimumDropFps: 55f).ShouldBeTrue();
        history.Count.ShouldBe(1);
        history[0].ThreadId.ShouldBe(8);
        history[0].WorstDropFps.ShouldBeGreaterThan(60f);

        history.Record(new Frame(0.04f, 1, 2, 0, 2, 2, 0, 0, 0, 0, 0,
            new[] { new Thread(7, new[] { root }), new Thread(8, new[] { root }) }, null),
            samples, 10f, 1f, 2).ShouldBeTrue();
        history.Count.ShouldBe(2);
        history[0].ThreadId.ShouldBe(8);
        history[1].ThreadId.ShouldBe(7);
    }

    [Test]
    public void SortColumnAndOrderPersistAcrossRecordsAndClear()
    {
        NativeProfilerSpikeHistory history = new();
        Node root = new("Work", 2, ProfilerScopeKind.Unspecified, Array.Empty<Node>());
        Dictionary<int, float[]> samples = new()
        {
            [7] = [10f, 10f, 20f],
            [8] = [10f, 10f, 30f],
        };
        history.Record(new Frame(0.03f, 1, 1, 0, 1, 1, 0, 0, 0, 0, 0,
            new[] { new Thread(8, new[] { root }), new Thread(7, new[] { root }) }, null),
            samples, 10f, 1f, 2).ShouldBeTrue();
        history.SortColumn.ShouldBe(ENativeProfilerSpikeSort.DropFps);
        history.SortDescending.ShouldBeTrue();
        history[0].ThreadId.ShouldBe(8);

        ulong revision = history.Revision;
        history.SetSort(ENativeProfilerSpikeSort.Thread, descending: false).ShouldBeTrue();
        history.Revision.ShouldBeGreaterThan(revision);
        history[0].ThreadId.ShouldBe(7);
        history[1].ThreadId.ShouldBe(8);
        history.SetSort(ENativeProfilerSpikeSort.Thread, descending: false).ShouldBeFalse();
        history.SetSort((ENativeProfilerSpikeSort)99, descending: false).ShouldBeFalse();

        history.Record(new Frame(0.04f, 1, 2, 0, 2, 2, 0, 0, 0, 0, 0,
            new[] { new Thread(8, new[] { root }), new Thread(7, new[] { root }) }, null),
            samples, 10f, 1f, 2).ShouldBeTrue();
        history[0].ThreadId.ShouldBe(7);

        history.SetSort(ENativeProfilerSpikeSort.DropPercent, descending: true).ShouldBeTrue();
        history[0].ThreadId.ShouldBe(8);
        NativeProfilerSpikeHistory.DropPercent(history[0]).ShouldBeGreaterThan(
            NativeProfilerSpikeHistory.DropPercent(history[1]));

        history.Clear().ShouldBeTrue();
        history.SortColumn.ShouldBe(ENativeProfilerSpikeSort.DropPercent);
    }

    private static Frame Snapshot(ulong frame)
    {
        Node hot = new("Hot", 3, ProfilerScopeKind.Unspecified, Array.Empty<Node>());
        Node root = new("Root", 5, ProfilerScopeKind.Unspecified, new[] { hot });
        return new(0.02f, 1, (long)frame, 0, frame, frame, 0, 0, 0, 0, 0,
            new[] { new Thread(7, new[] { root }) }, null);
    }
}
