using System.Globalization;
using System.Text.Json;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Aggregated repetitions and invariants for one formal benchmark scenario.
/// </summary>
public sealed class SelfIterationScenarioMeasurement
{
    private static readonly HashSet<string> MaximumInvariantMetrics =
    [
        "GpuReadbackBytesTotal",
        "GpuMappedBuffersTotal",
        "ForbiddenFallbackEventsTotal",
        "AllForbiddenFallbackEventsTotal",
        "UnapprovedOutputPolicyEventsTotal",
        "VulkanSubmissionRejectionsTotal",
    ];

    public string ScenarioName { get; init; } = string.Empty;
    public string RequestedRenderBackend { get; init; } = string.Empty;
    public string RequestedStrategy { get; init; } = string.Empty;
    public string SummaryPath { get; init; } = string.Empty;
    public string EvidenceDirectory { get; init; } = string.Empty;
    public int ExpectedRepetitionCount { get; init; }
    public int RepetitionCount { get; init; }
    public bool AllStable { get; init; }
    public bool AllMcpDiagnosticsSucceeded { get; init; }
    public int MinimumCpuTimingDumpFiles { get; init; }
    public int MinimumGpuTimingDumpFiles { get; init; }
    public bool DetailedDiagnosticsSucceeded { get; private set; }
    public int DetailedCpuTimingDumpFiles { get; private set; }
    public int DetailedGpuTimingDumpFiles { get; private set; }
    public string DetailedEvidenceDirectory { get; private set; } = string.Empty;
    public int MinimumSamples { get; init; }
    public string[] ActiveRenderBackends { get; init; } = [];
    public string[] EffectiveStrategies { get; init; } = [];
    public string[] WorkloadIdentityHashes { get; init; } = [];
    public string[] LogDirectories { get; init; } = [];
    public string[] Notes { get; init; } = [];
    public IReadOnlyDictionary<string, double> Metrics { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, double> MetricCoefficientOfVariationPercent { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads either the one-object or array shape emitted by the PowerShell harness.
    /// </summary>
    public static SelfIterationScenarioMeasurement Load(
        SelfIterationScenario scenario,
        string summaryPath,
        string evidenceDirectory,
        int expectedRepetitionCount)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(summaryPath));
        JsonElement[] repetitions = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(static element => element.Clone()).ToArray()
            : [document.RootElement.Clone()];

        var numericValues = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var backends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var strategies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var logDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notes = new HashSet<string>(StringComparer.Ordinal);
        bool allStable = repetitions.Length > 0;
        bool allMcp = repetitions.Length > 0;
        int minimumCpuDumps = int.MaxValue;
        int minimumGpuDumps = int.MaxValue;
        int minimumSamples = int.MaxValue;

        foreach (JsonElement repetition in repetitions)
        {
            foreach (JsonProperty property in repetition.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetDouble(out double numeric) ||
                    !double.IsFinite(numeric))
                {
                    continue;
                }
                if (!numericValues.TryGetValue(property.Name, out List<double>? values))
                {
                    values = [];
                    numericValues.Add(property.Name, values);
                }
                values.Add(numeric);
            }

            allStable &= GetBoolean(repetition, "StabilityReady");
            allMcp &= GetBoolean(repetition, "McpDiagnosticsSucceeded");
            minimumCpuDumps = Math.Min(minimumCpuDumps, GetInt32(repetition, "CpuTimingDumpFiles"));
            minimumGpuDumps = Math.Min(minimumGpuDumps, GetInt32(repetition, "GpuTimingDumpFiles"));
            minimumSamples = Math.Min(minimumSamples, GetInt32(repetition, "Samples"));
            AddIfPresent(backends, GetString(repetition, "ActiveRenderBackend"));
            AddIfPresent(strategies, GetString(repetition, "EffectiveStrategy"));
            AddIfPresent(identities, GetString(repetition, "CaptureWorkloadIdentityHash"));
            AddIfPresent(logDirectories, GetString(repetition, "LogDir"));
            AddIfPresent(notes, GetString(repetition, "Note"));
        }

        var medians = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var coefficientOfVariation = new Dictionary<string, double>(
            StringComparer.OrdinalIgnoreCase);
        foreach ((string name, List<double> values) in numericValues)
        {
            values.Sort();
            // A single violating repetition invalidates the campaign. Performance
            // metrics use the median, but invariant counters retain the maximum.
            medians[name] = MaximumInvariantMetrics.Contains(name)
                ? values[^1]
                : Median(values);
            coefficientOfVariation[name] = CoefficientOfVariationPercent(values);
        }

        var measurement = new SelfIterationScenarioMeasurement
        {
            ScenarioName = scenario.Name,
            RequestedRenderBackend = scenario.RenderBackend,
            RequestedStrategy = scenario.MeshSubmissionStrategy,
            SummaryPath = summaryPath,
            EvidenceDirectory = evidenceDirectory,
            ExpectedRepetitionCount = expectedRepetitionCount,
            RepetitionCount = repetitions.Length,
            AllStable = allStable,
            AllMcpDiagnosticsSucceeded = allMcp,
            MinimumCpuTimingDumpFiles = minimumCpuDumps == int.MaxValue ? 0 : minimumCpuDumps,
            MinimumGpuTimingDumpFiles = minimumGpuDumps == int.MaxValue ? 0 : minimumGpuDumps,
            MinimumSamples = minimumSamples == int.MaxValue ? 0 : minimumSamples,
            ActiveRenderBackends = backends.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            EffectiveStrategies = strategies.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            WorkloadIdentityHashes = identities.Order(StringComparer.Ordinal).ToArray(),
            LogDirectories = logDirectories.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Notes = notes.Order(StringComparer.Ordinal).ToArray(),
            Metrics = medians,
            MetricCoefficientOfVariationPercent = coefficientOfVariation,
        };
        measurement.UseDetailedDiagnosticsFrom(measurement);
        return measurement;
    }

    public void UseDetailedDiagnosticsFrom(SelfIterationScenarioMeasurement diagnostics)
    {
        DetailedDiagnosticsSucceeded = diagnostics.AllMcpDiagnosticsSucceeded;
        DetailedCpuTimingDumpFiles = diagnostics.MinimumCpuTimingDumpFiles;
        DetailedGpuTimingDumpFiles = diagnostics.MinimumGpuTimingDumpFiles;
        DetailedEvidenceDirectory = diagnostics.EvidenceDirectory;
    }

    public List<string> Validate(SelfIterationAcceptanceConfiguration acceptance)
    {
        List<string> errors = [];
        if (RepetitionCount != ExpectedRepetitionCount)
        {
            errors.Add(
                $"{ScenarioName}: expected {ExpectedRepetitionCount} repetitions, captured {RepetitionCount}.");
        }
        if (!AllStable)
            errors.Add($"{ScenarioName}: one or more repetitions did not pass the stability gate.");
        if (MinimumSamples <= 0)
            errors.Add($"{ScenarioName}: no steady-state samples were captured.");
        if (ActiveRenderBackends.Length != 1 ||
            !ActiveRenderBackends[0].Equals(RequestedRenderBackend, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{ScenarioName}: requested backend {RequestedRenderBackend}, captured [{string.Join(", ", ActiveRenderBackends)}].");
        }
        if (EffectiveStrategies.Length != 1 ||
            !EffectiveStrategies[0].Equals(RequestedStrategy, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{ScenarioName}: requested strategy {RequestedStrategy}, captured [{string.Join(", ", EffectiveStrategies)}].");
        }
        if (acceptance.RequireStableWorkloadIdentity && WorkloadIdentityHashes.Length != 1)
            errors.Add($"{ScenarioName}: capture did not retain exactly one workload identity.");
        if (acceptance.RequireCpuAndGpuDiagnosticDumps)
        {
            if (!DetailedDiagnosticsSucceeded)
                errors.Add($"{ScenarioName}: detailed MCP diagnostic collection failed.");
            if (DetailedCpuTimingDumpFiles < 1)
                errors.Add($"{ScenarioName}: no detailed CPU frame timing dump was captured.");
            if (DetailedGpuTimingDumpFiles < 1)
                errors.Add($"{ScenarioName}: no detailed per-pipeline GPU timing dump was captured.");
        }
        foreach (SelfIterationMetricRule rule in acceptance.Metrics)
        {
            if (rule.Required &&
                (!Metrics.TryGetValue(rule.Name, out double value) || !double.IsFinite(value)))
            {
                errors.Add($"{ScenarioName}: required metric '{rule.Name}' is missing.");
            }
            if (acceptance.MaximumMetricCoefficientOfVariationPercent > 0 &&
                Metrics.ContainsKey(rule.Name) &&
                (!MetricCoefficientOfVariationPercent.TryGetValue(rule.Name, out double variation) ||
                 !double.IsFinite(variation) ||
                 variation > acceptance.MaximumMetricCoefficientOfVariationPercent))
            {
                errors.Add(
                    $"{ScenarioName}: metric '{rule.Name}' coefficient of variation " +
                    $"{variation:F2}% exceeds {acceptance.MaximumMetricCoefficientOfVariationPercent:F2}%.");
            }
        }

        // Zero-readback is a steady-state submission contract. Startup may use bounded
        // initialization readbacks, which remain visible in the All* evidence fields.
        if (RequestedStrategy.Contains("ZeroReadback", StringComparison.OrdinalIgnoreCase))
        {
            RequireZero(errors, "GpuReadbackBytesTotal");
            RequireZero(errors, "GpuMappedBuffersTotal");
        }
        RequireZero(errors, "ForbiddenFallbackEventsTotal");
        RequireZero(errors, "AllForbiddenFallbackEventsTotal");
        RequireZero(errors, "UnapprovedOutputPolicyEventsTotal");
        RequireZero(errors, "VulkanSubmissionRejectionsTotal");
        return errors;
    }

    private void RequireZero(List<string> errors, string metric)
    {
        if (!Metrics.TryGetValue(metric, out double value) || !double.IsFinite(value))
        {
            errors.Add($"{ScenarioName}: required invariant metric {metric} is missing.");
            return;
        }
        if (value > 0.0)
            errors.Add($"{ScenarioName}: invariant {metric}=0 was violated ({value.ToString("F3", CultureInfo.InvariantCulture)}).");
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
            return double.NaN;
        int middle = sorted.Count / 2;
        return (sorted.Count & 1) == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) * 0.5;
    }

    private static double CoefficientOfVariationPercent(IReadOnlyList<double> values)
    {
        if (values.Count <= 1)
            return 0.0;
        double mean = values.Average();
        double variance = 0.0;
        foreach (double value in values)
        {
            double delta = value - mean;
            variance += delta * delta;
        }
        double standardDeviation = Math.Sqrt(variance / values.Count);
        double denominator = Math.Abs(mean);
        if (denominator < 1e-9)
            return standardDeviation < 1e-9 ? 0.0 : double.PositiveInfinity;
        return standardDeviation / denominator * 100.0;
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return string.Empty;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static bool GetBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        return bool.TryParse(value.ToString(), out bool result) && result;
    }

    private static int GetInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return 0;
        return value.TryGetInt32(out int result) ? result : 0;
    }

    private static void AddIfPresent(ISet<string> values, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(value.Trim());
    }
}
