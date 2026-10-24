using NUnit.Framework;
using Shouldly;
using XREngine.Data.Profiling;
using XREngine.Editor;
using Frame = XREngine.Engine.CodeProfiler.ProfilerFrameSnapshot;
using Thread = XREngine.Engine.CodeProfiler.ProfilerThreadSnapshot;
using Node = XREngine.Engine.CodeProfiler.ProfilerNodeSnapshot;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeProfilerRootHistoryTests
{
    [Test]
    public void FixedHistoryCopiesTheNewestSamplesInOrder()
    {
        NativeProfilerRootHistory history = new();
        for (int index = 0; index < NativeProfilerRootHistory.Capacity + 10; index++)
            history.Append(index + 1);
        float[] destination = new float[3];
        history.CopyLatest(destination, 3).ShouldBe(3);
        destination.ShouldBe(new[] { 728f, 729f, 730f });
        history.Count.ShouldBe(NativeProfilerRootHistory.Capacity);
    }

    [Test]
    public void RootSeriesKeepScopeIdentityAndExpireWithTheRoot()
    {
        NativeProfilerRows rows = new();
        rows.SelectView(ENativeProfilerView.CpuRoots);
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        rows.Request(Snapshot(1,
            new Node("Work", 2, ProfilerScopeKind.Unspecified, []),
            new Node("Work", 4, ProfilerScopeKind.ConditionalLoop, [])),
            nowUtc: start, persistenceSeconds: 1f);
        Finish(rows);
        rows.GetItem(4).CpuPlotName.ShouldBe("Work");
        rows.GetItem(5).CpuPlotName.ShouldBe("Work");
        ProfilerScopeKind[] displayedKinds = [rows.GetItem(4).CpuPlotKind, rows.GetItem(5).CpuPlotKind];
        displayedKinds.ShouldContain(ProfilerScopeKind.Unspecified);
        displayedKinds.ShouldContain(ProfilerScopeKind.ConditionalLoop);
        float[] samples = new float[3];
        rows.CopyRootHistory("Work", ProfilerScopeKind.Unspecified, samples, 3).ShouldBe(1);
        samples[0].ShouldBe(500f);
        rows.CopyRootHistory("Work", ProfilerScopeKind.ConditionalLoop, samples, 3).ShouldBe(1);
        samples[0].ShouldBe(250f);

        rows.Request(Snapshot(2,
            new Node("Work", 1, ProfilerScopeKind.Unspecified, [])),
            nowUtc: start.AddMilliseconds(500), persistenceSeconds: 1f);
        Finish(rows);
        rows.CopyRootHistory("Work", ProfilerScopeKind.Unspecified, samples, 3).ShouldBe(2);
        samples[0].ShouldBe(500f);
        samples[1].ShouldBe(1000f);
        rows.CopyRootHistory("Work", ProfilerScopeKind.ConditionalLoop, samples, 3).ShouldBe(2);
        samples[1].ShouldBe(0f);

        rows.Request(Snapshot(3), nowUtc: start.AddMilliseconds(1001), persistenceSeconds: 1f);
        Finish(rows);
        rows.CopyRootHistory("Work", ProfilerScopeKind.ConditionalLoop, samples, 3).ShouldBe(0);
    }

    private static Frame Snapshot(long id, params Node[] roots)
        => new(16, 1, id, 0, (ulong)id, (ulong)id, 0, 0, 0, 0, 0,
            [new Thread(1, roots)], null);

    private static void Finish(NativeProfilerRows rows)
    {
        int attempts = 0;
        while (!rows.Advance(7))
        {
            rows.LastIndexWork.ShouldBeLessThanOrEqualTo(7);
            (++attempts).ShouldBeLessThan(10000);
        }
    }
}
