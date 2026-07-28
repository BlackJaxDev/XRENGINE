using System.Globalization;
using System.Text;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Produces a concise deterministic bottleneck orientation for the LLM evidence pack.
/// </summary>
public static class SelfIterationDiagnosisWriter
{
    public static void Write(SelfIterationScenarioMeasurement measurement)
    {
        double renderP95 = Metric(measurement, "RenderP95Ms");
        double gpuP95 = Metric(measurement, "GpuP95Ms");
        double updateP95 = Metric(measurement, "UpdateP95Ms");
        double collectP95 = Metric(measurement, "CollectVisibleP95Ms");
        double renderMinusGpuP95 = Metric(measurement, "RenderMinusGpuP95Ms");

        string classification = gpuP95 > 0.0 && gpuP95 >= renderP95 * 0.75
            ? "GPU-bound or present-bound"
            : renderMinusGpuP95 > Math.Max(1.0, renderP95 * 0.20)
                ? "CPU render-thread-bound"
                : collectP95 > updateP95 && collectP95 > renderP95 * 0.25
                    ? "visibility/collection CPU-bound"
                    : "mixed or inconclusive";

        (string stage, double milliseconds) = FindDominantStage(measurement);
        var builder = new StringBuilder();
        builder.AppendLine($"# Diagnosis: {measurement.ScenarioName}");
        builder.AppendLine();
        builder.AppendLine($"- Deterministic classification: **{classification}**");
        builder.AppendLine($"- Dominant reported CPU/Vulkan stage: `{stage}` at {milliseconds.ToString("F3", CultureInfo.InvariantCulture)} ms p95");
        builder.AppendLine($"- Formal summary: `{measurement.SummaryPath}`");
        builder.AppendLine($"- Detailed diagnostic evidence: `{measurement.DetailedEvidenceDirectory}`");
        builder.AppendLine($"- Detailed CPU timing dumps per repetition: at least {measurement.DetailedCpuTimingDumpFiles}");
        builder.AppendLine($"- Detailed GPU pipeline dumps per repetition: at least {measurement.DetailedGpuTimingDumpFiles}");
        builder.AppendLine();
        builder.AppendLine("| Metric | Median across repetitions | CV |");
        builder.AppendLine("|---|---:|---:|");
        foreach (string metric in new[]
                 {
                     "RenderP50Ms", "RenderP95Ms", "RenderP99Ms", "GpuP50Ms", "GpuP95Ms",
                     "UpdateP95Ms", "CollectVisibleP95Ms", "RenderWaitForCollectP95Ms",
                     "VulkanFrameP95Ms", "VulkanRecordCommandBufferP95Ms",
                     "VulkanSubmitP95Ms", "VulkanQueuePresentP95Ms",
                 })
        {
            if (measurement.Metrics.TryGetValue(metric, out double value))
            {
                measurement.MetricCoefficientOfVariationPercent.TryGetValue(
                    metric,
                    out double variation);
                builder.AppendLine(
                    $"| {metric} | {value.ToString("F3", CultureInfo.InvariantCulture)} | " +
                    $"{variation.ToString("F2", CultureInfo.InvariantCulture)}% |");
            }
        }
        builder.AppendLine();
        builder.AppendLine("Inspect the copied `profiler-cpu-frame-*.log` hierarchy and every `profiler-gpu-pipeline-*.log` before proposing a fix.");

        File.WriteAllText(Path.Combine(measurement.EvidenceDirectory, "diagnosis.md"), builder.ToString());
    }

    private static (string Stage, double Milliseconds) FindDominantStage(
        SelfIterationScenarioMeasurement measurement)
    {
        string[] stages =
        [
            "UpdateP95Ms",
            "CollectVisibleP95Ms",
            "CollectWaitForRenderP95Ms",
            "RenderWaitForCollectP95Ms",
            "VulkanWaitFrameSlotP95Ms",
            "VulkanSampleTimingQueriesP95Ms",
            "VulkanDrainRetiredResourcesP95Ms",
            "VulkanAcquireNextImageP95Ms",
            "VulkanWaitSwapchainImageP95Ms",
            "VulkanRecordCommandBufferP95Ms",
            "VulkanSubmitP95Ms",
            "VulkanQueuePresentP95Ms",
        ];
        string dominant = "<not reported>";
        double maximum = 0.0;
        foreach (string stage in stages)
        {
            if (!measurement.Metrics.TryGetValue(stage, out double value) || value <= maximum)
                continue;
            dominant = stage;
            maximum = value;
        }
        return (dominant, maximum);
    }

    private static double Metric(SelfIterationScenarioMeasurement measurement, string name)
        => measurement.Metrics.TryGetValue(name, out double value) ? value : 0.0;
}
