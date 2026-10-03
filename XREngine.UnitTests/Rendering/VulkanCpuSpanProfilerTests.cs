using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class VulkanCpuSpanProfilerTests
{
    [Test]
    public void TargetedCapture_RetainsOnlyWarmedSelectedStageWithParentage()
    {
        VulkanFrameTelemetry telemetry = new();
        VulkanCpuSpanProfiler.Configure([EVulkanCpuStage.PrimaryRecording, EVulkanCpuStage.SecondaryRecording], 8);
        VulkanCpuSpanProfiler.WarmCurrentThread();
        VulkanCpuSpanProfiler.Arm();
        try
        {
            using (VulkanCpuStageScope primary = new(telemetry, EVulkanCpuStage.PrimaryRecording))
            {
                using VulkanCpuStageScope secondary = new(telemetry, EVulkanCpuStage.SecondaryRecording);
            }
        }
        finally
        {
            VulkanCpuSpanProfiler.Disarm();
        }

        VulkanCpuSpanProfiler.VulkanCpuSpanRecord[] spans = VulkanCpuSpanProfiler.GetSnapshot();
        VulkanCpuSpanProfiler.VulkanCpuSpanRecord primarySpan = spans.Last(static span => span.Stage == EVulkanCpuStage.PrimaryRecording);
        VulkanCpuSpanProfiler.VulkanCpuSpanRecord secondarySpan = spans.Last(static span => span.Stage == EVulkanCpuStage.SecondaryRecording);
        primarySpan.ParentSpanId.ShouldBe(0);
        secondarySpan.ParentSpanId.ShouldBe(primarySpan.SpanId);
        primarySpan.EndTimestamp.ShouldBeGreaterThanOrEqualTo(primarySpan.StartTimestamp);
    }

    [Test]
    public void Configure_RejectsUnknownAndOutOfRangeStages()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => VulkanCpuSpanProfiler.Configure(["not-a-stage"], 4));
        Should.Throw<ArgumentOutOfRangeException>(() => VulkanCpuSpanProfiler.Configure(["-1"], 4));
        Should.Throw<ArgumentOutOfRangeException>(() => VulkanCpuSpanProfiler.Configure([EVulkanCpuStage.Count], 4));
        Should.Throw<ArgumentOutOfRangeException>(() => VulkanCpuSpanProfiler.Configure([(EVulkanCpuStage)(-1)], 4));
        Should.Throw<ArgumentOutOfRangeException>(() => VulkanCpuSpanProfiler.Configure([(EVulkanCpuStage)int.MaxValue], 4));
        VulkanCpuSpanProfiler.Configure([EVulkanCpuStage.ComponentFrame], 4);
    }

    [Test]
    public void Capture_RecordsFrameWorkerOrdinalAndMarksOverflowIncomplete()
    {
        VulkanFrameTelemetry telemetry = new();
        VulkanCpuSpanProfiler.Configure([EVulkanCpuStage.ComponentFrame], 1);
        VulkanCpuSpanProfiler.WarmCurrentThread(workerId: 7);
        VulkanCpuSpanProfiler.Arm();
        try
        {
            VulkanCpuSpanProfiler.SetFrameContext(42);
            using (new VulkanCpuStageScope(telemetry, EVulkanCpuStage.ComponentFrame)) { }
            using (new VulkanCpuStageScope(telemetry, EVulkanCpuStage.ComponentFrame)) { }
        }
        finally
        {
            VulkanCpuSpanProfiler.Disarm();
        }

        VulkanCpuSpanProfiler.VulkanCpuSpanRecord[] spans = VulkanCpuSpanProfiler.GetSnapshot();
        spans.Length.ShouldBe(1);
        spans[0].FrameId.ShouldBe(42);
        spans[0].WorkerId.ShouldBe(7);
        spans[0].InvocationOrdinal.ShouldBe(2);
        spans[0].ParentSpanId.ShouldBe(0);
        VulkanCpuSpanProfiler.Analyze().Complete.ShouldBeFalse();
        VulkanCpuSpanProfiler.GetDiagnostics().OverwrittenSpans.ShouldBe(1);
    }

    [Test]
    public async Task Capture_UnwarmedWorkerIsExplicitlyIncomplete()
    {
        VulkanFrameTelemetry telemetry = new();
        VulkanCpuSpanProfiler.Configure([EVulkanCpuStage.ComponentFrame], 2);
        VulkanCpuSpanProfiler.WarmCurrentThread();
        VulkanCpuSpanProfiler.Arm();
        try
        {
            await Task.Run(() =>
            {
                VulkanCpuSpanProfiler.SetFrameContext(9);
                using VulkanCpuStageScope _ = new(telemetry, EVulkanCpuStage.ComponentFrame);
            });
        }
        finally
        {
            VulkanCpuSpanProfiler.Disarm();
        }

        VulkanCpuSpanProfiler.GetDiagnostics().UnwarmedThreadSpans.ShouldBeGreaterThan(0);
        VulkanCpuSpanProfiler.Analyze().Complete.ShouldBeFalse();
    }

    [Test]
    public void Analyze_SubtractsChildUnionAndWorkerWaitFromActualOverlap()
    {
        VulkanCpuSpanProfiler.VulkanCpuSpanRecord[] spans =
        [
            Span(EVulkanCpuStage.PrimaryRecording, 1, 0, 0, 100, thread: 1),
            Span(EVulkanCpuStage.SecondaryRecording, 2, 1, 10, 50, thread: 1),
            Span(EVulkanCpuStage.SecondaryRecording, 3, 1, 30, 70, thread: 1),
            Span(EVulkanCpuStage.ComponentFrame, 4, 0, 0, 100, thread: 2, worker: 1),
            Span(EVulkanCpuStage.WorkerWait, 5, 0, 20, 40, thread: 2, worker: 1, wait: "WorkerWait"),
            Span(EVulkanCpuStage.ComponentFrame, 6, 0, 50, 120, thread: 3, worker: 2),
            Span(EVulkanCpuStage.WorkerWait, 7, 0, 70, 90, thread: 3, worker: 2, wait: "WorkerWait"),
            Span(EVulkanCpuStage.WorkerWait, 8, 0, 70, 110, thread: 1, wait: "WorkerWait"),
        ];

        VulkanCpuSpanAnalysis analysis = VulkanCpuSpanAnalysis.Create(spans, default);
        analysis.Complete.ShouldBeTrue();
        analysis.Stages.Single(static stage => stage.Stage == EVulkanCpuStage.PrimaryRecording)
            .ExclusiveTicks.ShouldBe(40);
        VulkanCpuWorkerFrameSummary first = analysis.Workers.Single(static worker => worker.WorkerId == 1);
        VulkanCpuWorkerFrameSummary second = analysis.Workers.Single(static worker => worker.WorkerId == 2);
        first.WorkTicks.ShouldBe(80);
        second.WorkTicks.ShouldBe(50);
        first.WaitTicks.ShouldBe(20);
        first.OverlapTicks.ShouldBe(30);
        second.OverlapTicks.ShouldBe(30);
        VulkanCpuFrameParallelism frame = analysis.Parallelism.Single();
        frame.WorkSumTicks.ShouldBe(130);
        frame.WorkUnionTicks.ShouldBe(100);
        frame.ConcurrentWorkerTicks.ShouldBe(30);
        frame.ImbalanceTicks.ShouldBe(30);
        frame.CoordinatorWaitTicks.ShouldBe(40);
    }

    [Test]
    public void Analyze_RejectsInvalidParentAndInterval()
    {
        VulkanCpuSpanProfiler.VulkanCpuSpanRecord[] spans =
        [
            Span(EVulkanCpuStage.PrimaryRecording, 1, 0, 0, 10, thread: 1),
            Span(EVulkanCpuStage.SecondaryRecording, 2, 1, 2, 8, thread: 2),
            Span(EVulkanCpuStage.ComponentFrame, 3, 0, 20, 10, thread: 1),
        ];
        VulkanCpuSpanAnalysis analysis = VulkanCpuSpanAnalysis.Create(spans, default);
        analysis.InvalidParentage.ShouldBe(1);
        analysis.InvalidIntervals.ShouldBe(1);
        analysis.Complete.ShouldBeFalse();
    }

    [Test]
    public void Analyze_OtherWorkerWaitDoesNotEraseOverlapBetweenActiveWorkers()
    {
        VulkanCpuSpanProfiler.VulkanCpuSpanRecord[] spans =
        [
            Span(EVulkanCpuStage.SecondaryRecording, 1, 0, 0, 10, thread: 1, worker: 1),
            Span(EVulkanCpuStage.SecondaryRecording, 2, 0, 0, 10, thread: 2, worker: 2),
            Span(EVulkanCpuStage.WorkerWait, 3, 2, 0, 10, thread: 2, worker: 2, wait: "WorkerWait"),
            Span(EVulkanCpuStage.SecondaryRecording, 4, 0, 0, 10, thread: 3, worker: 3),
        ];

        VulkanCpuSpanAnalysis analysis = VulkanCpuSpanAnalysis.Create(spans, default);
        analysis.Complete.ShouldBeTrue();
        analysis.Workers.Single(static worker => worker.WorkerId == 1).OverlapTicks.ShouldBe(10);
        analysis.Workers.Single(static worker => worker.WorkerId == 2).OverlapTicks.ShouldBe(0);
        analysis.Workers.Single(static worker => worker.WorkerId == 3).OverlapTicks.ShouldBe(10);
        VulkanCpuFrameParallelism frame = analysis.Parallelism.Single();
        frame.WorkSumTicks.ShouldBe(20);
        frame.WorkUnionTicks.ShouldBe(10);
        frame.ConcurrentWorkerTicks.ShouldBe(10);
    }

    private static VulkanCpuSpanProfiler.VulkanCpuSpanRecord Span(
        EVulkanCpuStage stage, long id, long parent, long start, long end, int thread,
        int worker = -1, string? wait = null)
        => new(stage, id, parent, start, end, 0, thread, 11, worker, id, wait);
}
