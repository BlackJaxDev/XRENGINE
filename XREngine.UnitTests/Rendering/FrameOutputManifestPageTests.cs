using NUnit.Framework;
using Shouldly;
using Outputs = XREngine.RuntimeEngine.Rendering.Stats.FrameOutputs;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class FrameOutputManifestPageTests
{
    [Test]
    public void PageCopyKeepsOneManifestEpochAndReachesEveryOutputWithoutCloning()
    {
        bool previousTracking = RuntimeEngine.Rendering.Stats.EnableTracking;
        RuntimeEngine.Rendering.Stats.EnableTracking = true;
        try
        {
            Outputs.SnapshotAndReset();
            const ulong frameId = 702;
            for (int index = 0; index < 37; index++)
            {
                RenderOutputRequest request = RenderOutputRequest.CreateDefault(
                    EVrOutputViewKind.DesktopEditor, EFrameOutputKind.DesktopScene, frameId)
                    with { OutputId = (ulong)(index + 1) };
                Outputs.RecordOutput(new FrameOutputTelemetry(
                    EFrameOutputKind.DesktopScene, EVrOutputViewKind.DesktopEditor,
                    EFrameOutputPhase.Render,
                    FrameOutputPacingDecision.Due(EVrOutputViewKind.DesktopEditor,
                        EFrameOutputKind.DesktopScene, frameId),
                    $"output-{index}", "pipeline", true, true, true,
                    false, false, false, 1, 1, 0, 3, 0.1, 0.2,
                    Request: request));
            }
            Outputs.SnapshotAndReset();

            RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot[] page = new
                RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot[16];
            HashSet<ulong> visited = [];
            long publicationRevision = 0;
            for (int requested = 0; requested < 3; requested++)
            {
                int total = Outputs.CopyLastManifestPage(page, requested,
                    out RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot manifest,
                    out int actual, out long revision);
                total.ShouldBe(37);
                actual.ShouldBe(requested);
                revision.ShouldBeGreaterThan(0);
                if (requested == 0)
                    publicationRevision = revision;
                else
                    revision.ShouldBe(publicationRevision);
                manifest.Outputs.Length.ShouldBe(0);
                for (int index = 0; index < Math.Min(16, total - requested * 16); index++)
                    visited.Add(page[index].Request.OutputId).ShouldBeTrue();
            }
            visited.Count.ShouldBe(37);
            for (ulong outputId = 1; outputId <= 37; outputId++)
                visited.ShouldContain(outputId);
            Outputs.CopyLastManifestPage(page, int.MaxValue, out _, out int clamped, out _).ShouldBe(37);
            clamped.ShouldBe(2);

            for (int index = 0; index < 8; index++)
                Outputs.CopyLastManifestPage(page, 1, out _, out _, out _);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 100; index++)
                Outputs.CopyLastManifestPage(page, 1, out _, out _, out _);
            (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);

            Outputs.GetLastManifest(out long fullRevision).Outputs.Length.ShouldBe(37);
            fullRevision.ShouldBe(publicationRevision);
        }
        finally
        {
            RuntimeEngine.Rendering.Stats.FrameOutputs.SnapshotAndReset();
            RuntimeEngine.Rendering.Stats.EnableTracking = previousTracking;
        }
    }
}
