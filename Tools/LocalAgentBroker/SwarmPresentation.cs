using System.Text;
using System.Text.Json;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>Human-readable hierarchy and exact proposals for the existing history viewer.</summary>
internal static class SwarmPresentation
{
    public static string Render(AgentRunSnapshot snapshot, string text)
    {
        if (snapshot.Swarm is not { } swarm)
            return text;
        var result = new StringBuilder();
        result.AppendLine("## Luna Max swarm").AppendLine();
        if (snapshot.SwarmOptions is { } options)
            result.Append("Mode: ").Append(options.AutoApply ? "Apply after parent review" : "Reviewed proposals")
                .Append(". Limits: ").Append(options.MaxAgents).Append(" agents, depth ")
                .Append(options.MaxDepth).Append(", ").Append(options.MaxParallelAgents).AppendLine(" parallel phases.").AppendLine();
        result.AppendLine("| Agent | Role | State | Review |").AppendLine("| --- | --- | --- | --- |");
        foreach (AgentSwarmNodeSnapshot node in swarm.Nodes)
        {
            result.Append("| ").Append(node.Id).Append(" | ").Append(node.Role).Append(" | ")
                .Append(node.Status).Append(" | ").Append(node.Approved switch { true => "Approved", false => "Rejected", _ => "—" }).AppendLine(" |");
        }
        result.AppendLine().AppendLine(text);
        foreach (AgentSwarmNodeSnapshot node in swarm.Nodes)
        {
            result.AppendLine().Append("### ").AppendLine(node.Id).AppendLine().AppendLine(node.Objective);
            if (!string.IsNullOrWhiteSpace(node.ReviewSummary))
                result.AppendLine().AppendLine(node.ReviewSummary);
        }
        if (snapshot.CodeChanges.Count > 0)
        {
            result.AppendLine().AppendLine("## Reviewed code changes").AppendLine();
            result.AppendLine("Exact replacements are included below. A base hash of `missing` creates a new file. Existing-file snippets use normalized LF newlines.");
            result.AppendLine().AppendLine("```json");
            // JSON escaping keeps model-controlled text from closing the Markdown fence.
            result.AppendLine(JsonSerializer.Serialize(snapshot.CodeChanges, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
            result.AppendLine("```");
        }
        if (snapshot.AppliedPaths.Count > 0)
            result.AppendLine().Append("Applied: ").AppendLine(string.Join(", ", snapshot.AppliedPaths));
        return result.ToString();
    }
}
