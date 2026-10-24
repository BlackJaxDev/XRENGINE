using NUnit.Framework;
using Shouldly;
using XREngine.Data.Profiling;
using XREngine.Editor;
using Frame = XREngine.Engine.CodeProfiler.ProfilerFrameSnapshot;
using Thread = XREngine.Engine.CodeProfiler.ProfilerThreadSnapshot;
using Node = XREngine.Engine.CodeProfiler.ProfilerNodeSnapshot;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeProfilerWorstFrameSelectorTests
{
    [Test]
    public void CompletedWindowKeepsHighestThreadSnapshotUntilNextWindow()
    {
        NativeProfilerWorstFrameSelector selector = new();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Frame first = Snapshot(1, 1, 4, 9);
        Frame peak = Snapshot(1, 2, 14, 2);
        Frame later = Snapshot(1, 3, 3, 3);
        selector.Observe(first, start);
        selector.DisplayFrame.ShouldBeSameAs(first);
        selector.IsCompletedWindow.ShouldBeFalse();
        selector.Observe(peak, start.AddMilliseconds(200));
        selector.DisplayFrame.ShouldBeSameAs(peak);
        selector.Observe(later, start.AddMilliseconds(500));
        selector.DisplayFrame.ShouldBeSameAs(peak);
        selector.IsCompletedWindow.ShouldBeTrue();

        Frame next = Snapshot(1, 4, 7, 1);
        selector.Observe(next, start.AddMilliseconds(750));
        selector.DisplayFrame.ShouldBeSameAs(peak);
        selector.Observe(Snapshot(1, 5, 5, 1), start.AddMilliseconds(1000));
        selector.DisplayFrame.ShouldBeSameAs(next);
    }

    [Test]
    public void EqualPeaksKeepEarlierSnapshotAndDuplicatePublicationDoesNotAdvanceWindow()
    {
        NativeProfilerWorstFrameSelector selector = new();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Frame first = Snapshot(1, 1, 8);
        selector.Observe(first, start);
        selector.Observe(first, start.AddSeconds(1));
        selector.IsCompletedWindow.ShouldBeFalse();
        selector.Observe(Snapshot(1, 2, 8), start.AddSeconds(1));
        selector.DisplayFrame.ShouldBeSameAs(first);
    }

    [Test]
    public void EmptySnapshotsAndSessionResetDoNotKeepPriorWindow()
    {
        NativeProfilerWorstFrameSelector selector = new();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Frame first = Snapshot(1, 1, 20);
        selector.Observe(first, start);
        selector.Observe(Snapshot(1, 2, 1), start.AddMilliseconds(500));
        selector.DisplayFrame.ShouldBeSameAs(first);
        selector.Observe(Snapshot(1, 3), start.AddMilliseconds(750));
        selector.DisplayFrame.ShouldBeSameAs(first);

        selector.Observe(Snapshot(2, 1), start.AddMilliseconds(800));
        selector.DisplayFrame.ShouldBeNull();
        selector.IsCompletedWindow.ShouldBeFalse();

        Frame newSession = Snapshot(2, 2, 2);
        selector.Observe(newSession, start.AddMilliseconds(800));
        selector.DisplayFrame.ShouldBeSameAs(newSession);
        selector.IsCompletedWindow.ShouldBeFalse();
        selector.Reset();
        selector.DisplayFrame.ShouldBeNull();
    }

    [Test]
    public void BoundaryFrameStartsTheNextWindowAndBackwardTimeResetsTheSelection()
    {
        NativeProfilerWorstFrameSelector selector = new();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Frame boundary = Snapshot(1, 2, 12);
        selector.Observe(Snapshot(1, 1, 2), start);
        selector.Observe(boundary, start.AddMilliseconds(500));
        selector.DisplayFrame.ShouldBeSameAs(boundary);
        selector.Observe(Snapshot(1, 3, 3), start.AddMilliseconds(1000));
        selector.DisplayFrame.ShouldBeSameAs(boundary);

        Frame afterClockRollback = Snapshot(1, 4, 4);
        selector.Observe(afterClockRollback, start.AddMilliseconds(100));
        selector.DisplayFrame.ShouldBeSameAs(afterClockRollback);
        selector.IsCompletedWindow.ShouldBeFalse();
    }

    private static Frame Snapshot(long session, long publication, params float[] threadMilliseconds)
    {
        Thread[] threads = new Thread[threadMilliseconds.Length];
        for (int index = 0; index < threads.Length; index++)
            threads[index] = new Thread(index + 1,
                [new Node("Work", threadMilliseconds[index], ProfilerScopeKind.Unspecified, [])]);
        return new(16, session, publication, 0, (ulong)publication, (ulong)publication,
            0, 0, 0, 0, 0, threads, null);
    }
}
