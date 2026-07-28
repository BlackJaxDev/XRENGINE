using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Maintains separate accepted-progress and rejected-attempt Markdown ledgers.
/// </summary>
public sealed partial class SelfIterationDocumentationWriter
{
    private readonly string _workspaceRoot;
    private readonly SelfIterationConfiguration _configuration;

    public SelfIterationDocumentationWriter(
        string workspaceRoot,
        SelfIterationConfiguration configuration)
    {
        _workspaceRoot = workspaceRoot;
        _configuration = configuration;
    }

    public IReadOnlySet<string> ReadKnownFingerprints()
    {
        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadFingerprints(Resolve(_configuration.ProgressDocument), fingerprints);
        ReadFingerprints(Resolve(_configuration.RejectedAttemptsDocument), fingerprints);
        return fingerprints;
    }

    public void Append(SelfIterationAttemptRecord record)
    {
        string path = Resolve(
            record.Accepted
                ? _configuration.ProgressDocument
                : _configuration.RejectedAttemptsDocument);
        EnsureHeader(path, record.Accepted);

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine($"<!-- xrengine-self-iteration-fingerprint: {record.Fingerprint} -->");
        builder.AppendLine($"## Iteration {record.Iteration}: {(record.Accepted ? "accepted" : "rejected")} — {SingleLine(record.Proposal.AttemptKey)}");
        builder.AppendLine();
        builder.AppendLine($"- Timestamp: {record.Timestamp:O}");
        builder.AppendLine($"- Outcome: {SingleLine(record.Outcome)}");
        builder.AppendLine($"- Issue key: `{SingleLine(record.Proposal.IssueKey)}`");
        builder.AppendLine($"- Attempt key: `{SingleLine(record.Proposal.AttemptKey)}`");
        builder.AppendLine($"- Target scenario: `{SingleLine(record.Proposal.TargetScenario)}`");
        builder.AppendLine($"- Hypothesis: {SingleLine(record.Proposal.Hypothesis)}");
        builder.AppendLine($"- Planned change: {SingleLine(record.Proposal.PlannedChange)}");
        builder.AppendLine($"- Expected metric: `{SingleLine(record.Proposal.ExpectedMetric)}`");
        if (record.Implementation is not null)
            builder.AppendLine($"- Implemented change: {SingleLine(record.Implementation.ChangeSummary)}");
        if (record.Reload is not null)
        {
            builder.AppendLine(
                $"- Reload: requested `{record.Reload.RequestedMode}`, effective `{record.Reload.EffectiveMode}`, " +
                $"relaunch `{record.Reload.EditorRelaunched}` — {SingleLine(record.Reload.Details)}");
        }
        builder.AppendLine($"- Evidence: `{NormalizeForDocument(record.EvidenceDirectory)}`");
        if (record.ChangedPaths.Count > 0)
            builder.AppendLine($"- Changed paths: {string.Join(", ", record.ChangedPaths.Select(pathValue => $"`{SingleLine(pathValue)}`"))}");

        if (record.Comparison is not null)
        {
            builder.AppendLine(
                $"- Weighted aggregate improvement: " +
                $"{record.Comparison.AggregateImprovementPercent.ToString("F2", CultureInfo.InvariantCulture)}%");
            if (record.Comparison.Reasons.Count > 0)
            {
                builder.AppendLine("- Decision reasons:");
                foreach (string reason in record.Comparison.Reasons)
                    builder.AppendLine($"  - {SingleLine(reason)}");
            }
            if (record.Comparison.Metrics.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("| Scenario | Metric | Baseline | Candidate | Improvement | Decision |");
                builder.AppendLine("|---|---|---:|---:|---:|---|");
                foreach (SelfIterationMetricComparison metric in record.Comparison.Metrics)
                {
                    string decision = metric.Regression
                        ? "regression"
                        : metric.MaterialImprovement
                            ? "material improvement"
                            : "within noise";
                    builder.AppendLine(
                        $"| {SingleLine(metric.Scenario)} | {SingleLine(metric.Metric)} | " +
                        $"{metric.Baseline.ToString("F3", CultureInfo.InvariantCulture)} | " +
                        $"{metric.Candidate.ToString("F3", CultureInfo.InvariantCulture)} | " +
                        $"{metric.ImprovementPercent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)}% | {decision} |");
                }
            }
        }

        File.AppendAllText(path, builder.ToString());
    }

    private void EnsureHeader(string path, bool accepted)
    {
        if (File.Exists(path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string title = accepted
            ? $"{_configuration.CampaignId} Self-Improvement Progress"
            : $"{_configuration.CampaignId} Rejected Self-Improvement Attempts";
        string purpose = accepted
            ? "Accepted changes whose formal benchmark matrix passed every invariant and improvement gate."
            : "Rejected, duplicate, invalid, or regressing attempts. The fingerprint markers prevent repeating the same proposal.";
        File.WriteAllText(
            path,
            $"# {title}{Environment.NewLine}{Environment.NewLine}" +
            $"Objective: {_configuration.Objective}{Environment.NewLine}{Environment.NewLine}" +
            $"{purpose}{Environment.NewLine}");
    }

    private static void ReadFingerprints(string path, ISet<string> output)
    {
        if (!File.Exists(path))
            return;
        foreach (Match match in FingerprintRegex().Matches(File.ReadAllText(path)))
            output.Add(match.Groups[1].Value);
    }

    private string NormalizeForDocument(string path)
    {
        string relative = Path.GetRelativePath(_workspaceRoot, path);
        return relative.Replace('\\', '/');
    }

    private string Resolve(string relativePath)
        => Path.GetFullPath(Path.Combine(
            _workspaceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string SingleLine(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "<none>"
            : value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    [GeneratedRegex(
        "<!--\\s*xrengine-self-iteration-fingerprint:\\s*([A-Fa-f0-9]{64})\\s*-->",
        RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintRegex();
}
