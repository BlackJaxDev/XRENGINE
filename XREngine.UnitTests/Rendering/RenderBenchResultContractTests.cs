using NUnit.Framework;
using Shouldly;
using XREngine.RenderBench;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderBenchResultContractTests
{
    [Test]
    public void Statistics_UsesNearestRankSampleDeviationAndMedianAbsoluteDeviation()
    {
        RenderBenchMetricStatistics? statistics = RenderBenchMetricStatistics.FromNanoseconds(
            new long[] { 1_000_000, 2_000_000, 3_000_000, 4_000_000, 100_000_000 });
        statistics.ShouldNotBeNull();
        statistics.N.ShouldBe(5);
        statistics.P50.ShouldBe(3);
        statistics.P90.ShouldBe(100);
        statistics.P95.ShouldBe(100);
        statistics.P99.ShouldBe(100);
        statistics.Worst.ShouldBe(100);
        statistics.Mean.ShouldBe(22);
        statistics.StandardDeviation.ShouldBe(Math.Sqrt(1902.5), 1e-10);
        statistics.MedianAbsoluteDeviation.ShouldBe(1);
    }

    [Test]
    public void Statistics_RejectsEmptyNonFiniteAndNegativeSamples()
    {
        RenderBenchMetricStatistics.FromNanoseconds(ReadOnlySpan<long>.Empty).ShouldBeNull();
        RenderBenchMetricStatistics.FromNanoseconds(ReadOnlySpan<double>.Empty).ShouldBeNull();
        RenderBenchMetricStatistics.FromNanoseconds(new double[] { 1_000_000, double.NaN }).ShouldBeNull();
        RenderBenchMetricStatistics.FromNanoseconds(new double[] { double.PositiveInfinity }).ShouldBeNull();
        RenderBenchMetricStatistics.FromNanoseconds(new long[] { -1 }).ShouldBeNull();
    }

    [Test]
    public void Promotion_RequiresVerifiedBroaderLanesAndMeasuredFullFrameGain()
    {
        RenderBenchPromotionEvidence evidence = CompleteEvidence();
        Dictionary<string, bool> lanes = new(StringComparer.Ordinal)
        {
            ["subsystem"] = true,
            ["presentationless"] = true,
            ["desktop_wsi"] = true,
        };
        RenderBenchPromotionReport missingXr = RenderBenchPromotionReport.Evaluate(evidence, 0.4, 0.2, lanes);
        missingXr.Status.ShouldBe("Unresolved");
        missingXr.MissingOrFailedEvidence.ShouldContain(static issue => issue.Contains("open_xr", StringComparison.Ordinal));

        lanes["open_xr"] = true;
        RenderBenchPromotionReport eligible = RenderBenchPromotionReport.Evaluate(evidence, 0.4, 0.2, lanes);
        eligible.Status.ShouldBe("Eligible");
        RenderBenchPromotionReport noFrameGain = RenderBenchPromotionReport.Evaluate(evidence, 0.4, null, lanes);
        noFrameGain.Status.ShouldBe("Unresolved");
        RenderBenchPromotionReport invalidGain = RenderBenchPromotionReport.Evaluate(evidence, double.NaN, 0.2, lanes);
        invalidGain.Status.ShouldBe("Unresolved");
    }

    [Test]
    public void Promotion_RejectsMissingCorrectnessAndIntrusiveProxyEvidence()
    {
        Dictionary<string, bool> lanes = new(StringComparer.Ordinal)
        {
            ["subsystem"] = true, ["presentationless"] = true,
            ["desktop_wsi"] = true, ["open_xr"] = true,
        };
        RenderBenchPromotionEvidence evidence = CompleteEvidence() with
        {
            CorrectnessPassed = false,
            Intrusive = true,
            SyntheticProxy = true,
            RejectionReason = "Intrusive synthetic proxy cannot prove whole-frame gain.",
        };
        RenderBenchPromotionReport report = RenderBenchPromotionReport.Evaluate(evidence, 0.4, 0.2, lanes);
        report.Status.ShouldBe("Unresolved");
        report.MissingOrFailedEvidence.ShouldContain(static issue => issue.Contains("correctness", StringComparison.OrdinalIgnoreCase));
        report.MissingOrFailedEvidence.ShouldContain(static issue => issue.Contains("synthetic proxy", StringComparison.Ordinal));
    }

    private static RenderBenchPromotionEvidence CompleteEvidence() => new(
        CorrectnessPassed: true,
        OperationCountsPassed: true,
        AllocationsPassed: true,
        StabilityPassed: true,
        ValidationPassed: true,
        SynchronizationValidationPassed: true,
        SubsystemReportPath: "subsystem.json",
        PresentationlessReportPath: "presentationless.json",
        DesktopReportPath: "desktop.json",
        OpenXrReportPath: "xr.json",
        AffectsXr: true,
        FullFrameSavingsEvidencePath: "presentationless.json",
        RejectionReason: null);
}
