using NUnit.Framework;
using Shouldly;
using Churn = XREngine.RuntimeEngine.Rendering.Stats.ResourceChurn;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class ResourceChurnPublicationTests
{
    [Test]
    public void CopyReportsOneCoherentBoundedPublicationAndEmptyRevision()
    {
        bool previousTracking = XREngine.RuntimeEngine.Rendering.Stats.EnableTracking;
        XREngine.RuntimeEngine.Rendering.Stats.EnableTracking = true;
        try
        {
            Churn.SnapshotAndReset();
            Churn.SnapshotRow[] scratch = new Churn.SnapshotRow[32];
            Churn.CopyLastFrameRows(scratch, out long before, out _, out _);

            for (int index = 0; index < 40; index++)
                Churn.Record("Texture", $"texture-{index}", "Created", "reload");
            for (int index = 0; index < 5; index++)
                Churn.Record("Texture", "texture-39", "Created", "reload");
            Churn.Record("Framebuffer", "main", "Resized", "viewport");
            Churn.SnapshotAndReset();

            Churn.CopyLastFrameRows(scratch, out long published, out Churn.SnapshotCounts counts,
                out int omitted).ShouldBe(32);
            published.ShouldBe(before + 1);
            counts.Created.ShouldBe(45);
            counts.Resized.ShouldBe(1);
            counts.Recreated.ShouldBe(0);
            counts.Destroyed.ShouldBe(0);
            omitted.ShouldBe(9);
            scratch[0].ResourceName.ShouldBe("texture-39");
            scratch[0].Count.ShouldBe(6);
            for (int index = 1; index < 32; index++)
                scratch[index].Count.ShouldBe(1);

            Churn.SnapshotAndReset();
            Churn.CopyLastFrameRows(scratch, out long empty, out counts, out omitted).ShouldBe(0);
            empty.ShouldBe(published + 1);
            counts.ShouldBe(default(Churn.SnapshotCounts));
            omitted.ShouldBe(0);
        }
        finally
        {
            Churn.SnapshotAndReset();
            XREngine.RuntimeEngine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }

    [Test]
    public void ConcurrentRecordsStayInTheSameEpochAsTheirRows()
    {
        bool previousTracking = XREngine.RuntimeEngine.Rendering.Stats.EnableTracking;
        XREngine.RuntimeEngine.Rendering.Stats.EnableTracking = true;
        try
        {
            Churn.SnapshotAndReset();
            const int events = 1000;
            Task writer = Task.Run(() =>
            {
                for (int index = 0; index < events; index++)
                    Churn.Record("Texture", "concurrent-test", "Created");
            });
            Churn.SnapshotRow[] scratch = new Churn.SnapshotRow[32];
            int observed = 0;
            while (!writer.IsCompleted)
            {
                Churn.SnapshotAndReset();
                int rows = Churn.CopyLastFrameRows(scratch, out _, out Churn.SnapshotCounts counts, out _);
                counts.Created.ShouldBe(rows == 0 ? 0 : scratch[0].Count);
                observed += counts.Created;
                Thread.Yield();
            }
            writer.GetAwaiter().GetResult();
            Churn.SnapshotAndReset();
            int finalRows = Churn.CopyLastFrameRows(scratch, out _, out Churn.SnapshotCounts final, out _);
            final.Created.ShouldBe(finalRows == 0 ? 0 : scratch[0].Count);
            (observed + final.Created).ShouldBe(events);
        }
        finally
        {
            Churn.SnapshotAndReset();
            XREngine.RuntimeEngine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }

    [Test]
    public void WarmSingleKeyFramesDoNotAllocateDuringPublicationOrCopy()
    {
        bool previousTracking = XREngine.RuntimeEngine.Rendering.Stats.EnableTracking;
        XREngine.RuntimeEngine.Rendering.Stats.EnableTracking = true;
        try
        {
            Churn.SnapshotAndReset();
            Churn.SnapshotRow[] scratch = new Churn.SnapshotRow[32];
            for (int index = 0; index < 16; index++)
            {
                Churn.Record("Texture", "steady-test", "Created");
                Churn.SnapshotAndReset();
                Churn.CopyLastFrameRows(scratch, out _, out _, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 100; index++)
            {
                Churn.Record("Texture", "steady-test", "Created");
                Churn.SnapshotAndReset();
                Churn.CopyLastFrameRows(scratch, out _, out _, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            allocated.ShouldBe(0);
        }
        finally
        {
            Churn.SnapshotAndReset();
            XREngine.RuntimeEngine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }
}
