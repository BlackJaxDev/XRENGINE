using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

public sealed partial class AgentSwarmRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) } };

    private static void ValidatePlan(AgentSwarmMutableNode parent, AgentSwarmPlan plan, AgentSwarmOptions options)
    {
        if (plan.Children.Count == 0 || plan.Children.Count > options.MaxChildren) throw Fail(parent, "An orchestrator plan must contain between one and the configured maximum number of children.");
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (AgentSwarmPlanChild child in plan.Children)
        {
            if (string.IsNullOrWhiteSpace(child.Objective) || child.Objective.Length > MaxObjectiveCharacters || child.Paths.Count == 0) throw Fail(parent, "Every child needs a bounded objective and at least one path.");
            if (child.Role == AgentSwarmRole.Leaf && child.Paths.Count != 1) throw Fail(parent, "A leaf plan entry must contain exactly one path.");
            if (child.Role == AgentSwarmRole.Orchestrator && parent.Depth + 1 >= options.MaxDepth) throw Fail(parent, "The depth limit requires a leaf, but the plan requested an orchestrator.");
            var childPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in child.Paths)
                if (!parent.Paths.Contains(path, StringComparer.Ordinal) || !childPaths.Add(path)) throw Fail(parent, "Each child path list must contain unique paths drawn from its parent's assignment.");
                else covered.Add(path);
        }
        if (!covered.SetEquals(parent.Paths)) throw Fail(parent, "Child paths must exactly cover their parent's assigned paths.");
    }
    private static AgentSwarmPlan ParsePlan(string json, AgentSwarmMutableNode node) => Parse<AgentSwarmPlan>(json, node, "plan");
    private static AgentSwarmReview ParseReview(string json, AgentSwarmMutableNode node) => Parse<AgentSwarmReview>(json, node, "review");
    private static AgentSwarmProposedChange ParseChange(string json, AgentSwarmMutableNode node) => Parse<AgentSwarmProposedChange>(json, node, "change");
    private static T Parse<T>(string json, AgentSwarmMutableNode node, string kind)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new JsonException($"The {kind} was null."); }
        catch (JsonException exception) { throw Fail(node, $"Invalid {kind} JSON: {exception.Message}"); }
    }
    private string BuildPlanPrompt(AgentSwarmMutableNode node, IReadOnlyList<string> criteria, AgentSwarmOptions options)
        => "You are an orchestrator. Do not write code or review yet. Ensure every assigned path is covered by at least one child task. A path may be assigned to multiple children only for independently scoped non-overlapping edits. Children can be orchestrators or leaves. A leaf owns one path. Return JSON only: {\"children\":[{\"objective\":\"...\",\"paths\":[\"...\"],\"role\":\"orchestrator|leaf\"}]}.\n" + $"Your objective: {node.Objective}\nAssigned paths: {JsonSerializer.Serialize(node.Paths)}\nSuccess criteria: {JsonSerializer.Serialize(criteria)}\nCurrent depth: {node.Depth}; maximum depth: {options.MaxDepth}; maximum children: {options.MaxChildren}; remaining node admissions: {Math.Max(0, options.MaxAgents - Volatile.Read(ref _admittedNodes))}.";
    private static string BuildLeafPrompt(AgentSwarmMutableNode node, AgentContextFileSnapshot file, int maxLines, int remainingArtifactCharacters)
        => "You are a leaf. Propose exactly one small text replacement for your one assigned file. Do not plan, spawn, or review. Return JSON only: {\"path\":\"...\",\"base_sha256\":\"...\",\"old_text\":\"...\",\"new_text\":\"...\"}. " + $"The combined old and new text may contain at most {maxLines} lines and your complete JSON response must fit within {remainingArtifactCharacters} remaining aggregate artifact characters. For a missing file, old_text must be empty and new_text is the full file content.\nObjective: {node.Objective}\nFile: {file.Path}; base SHA-256: {file.Sha256}";
    private static string BuildReviewPrompt(AgentSwarmMutableNode node, IReadOnlyList<AgentSwarmCodeChange> artifacts)
        => "You are an orchestrator reviewer. Review the unchanged child artifacts against your objective. Do not modify, plan, or spawn. Return JSON only: {\"approved\":true|false,\"summary\":\"...\"}.\n" + $"Objective: {node.Objective}\nArtifacts: {JsonSerializer.Serialize(artifacts)}";
    private static AgentRunRequest CreatePhaseRequest(AgentRunRequest root, AgentSwarmOptions options, IReadOnlyDictionary<string, AgentContextFileSnapshot> files, AgentSwarmMutableNode node, string instructions) => new()
    {
        Objective = node.Objective,
        SuccessCriteria = root.SuccessCriteria,
        Constraints = root.Constraints.Concat(["Return only the requested JSON object.", "You have no tools and must not claim to read or write files."]).ToArray(),
        RequestedModel = LunaModel,
        ReasoningEffort = LunaEffort,
        TextVerbosity = "low",
        EvidencePacket = root.EvidencePacket,
        ContextFileSnapshots = files.Values.ToArray(),
        RepositoryAccess = new(),
        ToolPolicy = new(),
        Budget = new AgentRunBudget { MaxTurns = 1, MaxToolCalls = 0, MaxRetries = 0, MaxOutputTokens = options.MaxPhaseOutputTokens, MaxElapsedSeconds = options.MaxElapsedSeconds },
        UseCompactHandoffPrompt = true,
        UseBackgroundMode = false,
        SystemInstructions = string.IsNullOrWhiteSpace(root.SystemInstructions) ? instructions : root.SystemInstructions + Environment.NewLine + instructions,
        AdditionalInstructions = root.AdditionalInstructions,
    };
}
