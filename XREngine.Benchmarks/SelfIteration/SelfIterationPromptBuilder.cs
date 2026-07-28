using System.Text;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Builds bounded evidence-first prompts for the two-phase agent protocol.
/// </summary>
public static class SelfIterationPromptBuilder
{
    public static string BuildProposal(
        SelfIterationConfiguration configuration,
        IReadOnlyList<SelfIterationScenarioMeasurement> baseline,
        string progressDocumentPath,
        string rejectedDocumentPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the read-only proposal phase of XRENGINE's rendering performance loop.");
        builder.AppendLine("Do not modify any file, run builds, launch the editor, use git mutation commands, or commit.");
        builder.AppendLine("Inspect the formal evidence and source, choose exactly one root-cause hypothesis, and propose one coherent change.");
        builder.AppendLine("Do not repeat an accepted or rejected attempt recorded in either ledger.");
        builder.AppendLine();
        AppendCommonContext(builder, configuration, baseline, progressDocumentPath, rejectedDocumentPath);
        builder.AppendLine();
        builder.AppendLine("Return exactly one JSON object with this shape:");
        builder.AppendLine("""
{
  "issueKey": "stable bottleneck identifier",
  "attemptKey": "stable implementation approach identifier",
  "targetScenario": "one configured scenario name",
  "hypothesis": "evidence-backed root cause",
  "plannedChange": "one targeted source change",
  "expectedMetric": "metric expected to improve",
  "reloadMode": "Auto|ShaderReload|RendererRestart|BuildAndReloadRenderer|EditorRestart"
}
""");
        return builder.ToString();
    }

    public static string BuildImplementation(
        SelfIterationConfiguration configuration,
        IReadOnlyList<SelfIterationScenarioMeasurement> baseline,
        SelfIterationAgentProposal proposal,
        string progressDocumentPath,
        string rejectedDocumentPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the implementation phase of XRENGINE's rendering performance loop.");
        builder.AppendLine("Implement only the approved proposal below. Make one coherent root-cause fix.");
        builder.AppendLine("Do not run the editor or benchmarks; the controller owns validation and measurement.");
        builder.AppendLine("Do not commit, stage, reset, checkout, alter dependencies/submodules, or edit the progress ledgers.");
        builder.AppendLine("Do not add silent CPU fallbacks for an explicitly requested GPU path.");
        builder.AppendLine();
        builder.AppendLine($"Approved issue key: {proposal.IssueKey}");
        builder.AppendLine($"Approved attempt key: {proposal.AttemptKey}");
        builder.AppendLine($"Hypothesis: {proposal.Hypothesis}");
        builder.AppendLine($"Planned change: {proposal.PlannedChange}");
        builder.AppendLine($"Expected metric: {proposal.ExpectedMetric}");
        builder.AppendLine($"Requested reload: {proposal.ReloadMode}");
        builder.AppendLine();
        AppendCommonContext(builder, configuration, baseline, progressDocumentPath, rejectedDocumentPath);
        builder.AppendLine();
        builder.AppendLine("You may edit only these repository path prefixes:");
        foreach (string prefix in configuration.AllowedPathPrefixes)
            builder.AppendLine($"- {prefix}");
        builder.AppendLine();
        builder.AppendLine("Return exactly one JSON object after editing:");
        builder.AppendLine("""
{
  "implemented": true,
  "changeSummary": "concise description of the actual edit",
  "reloadMode": "Auto|ShaderReload|RendererRestart|BuildAndReloadRenderer|EditorRestart"
}
""");
        return builder.ToString();
    }

    private static void AppendCommonContext(
        StringBuilder builder,
        SelfIterationConfiguration configuration,
        IReadOnlyList<SelfIterationScenarioMeasurement> baseline,
        string progressDocumentPath,
        string rejectedDocumentPath)
    {
        builder.AppendLine($"Campaign: {configuration.CampaignId}");
        builder.AppendLine($"Objective: {configuration.Objective}");
        builder.AppendLine($"Accepted ledger: {progressDocumentPath}");
        builder.AppendLine($"Rejected ledger: {rejectedDocumentPath}");
        builder.AppendLine("Formal evidence:");
        foreach (SelfIterationScenarioMeasurement measurement in baseline)
        {
            builder.AppendLine(
                $"- {measurement.ScenarioName}: {Path.Combine(measurement.EvidenceDirectory, "diagnosis.md")}");
            builder.AppendLine($"  clean formal summary: {measurement.SummaryPath}");
            builder.AppendLine(
                $"  detailed CPU/GPU/log evidence: {Path.Combine(measurement.DetailedEvidenceDirectory, "logs")}");
        }
        builder.AppendLine();
        builder.AppendLine("Open and read the diagnosis, summary, CPU hierarchy dump, and every GPU pipeline timing dump for the target scenario.");
        builder.AppendLine("For Vulkan structural C# edits, request EditorRestart; collectible Vulkan replacement is unsafe after Streamline initialization.");
    }
}
