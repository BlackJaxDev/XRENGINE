using System.Text.RegularExpressions;

namespace XREngine.AgentOrchestration;

/// <summary>
/// Applies provider-independent validation and hard safety bounds.
/// </summary>
public static partial class AgentRequestValidator
{
    private const int MaximumRequestCharacters = 262_144;

    private static readonly HashSet<string> s_reasoningEfforts =
        new(StringComparer.OrdinalIgnoreCase) { "none", "low", "medium", "high", "xhigh", "max" };

    public static IReadOnlyList<string> Validate(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(request.Objective))
            errors.Add("objective is required");
        if (string.IsNullOrWhiteSpace(request.RequestedModel))
            errors.Add("requested_model is required");
        if (!s_reasoningEfforts.Contains(request.ReasoningEffort))
            errors.Add("reasoning_effort must be one of: none, low, medium, high, xhigh, max");
        if (!string.IsNullOrWhiteSpace(request.EditorSession) && !SessionNamePattern().IsMatch(request.EditorSession))
            errors.Add("editor_session must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$");
        if (request.EvidencePacket is null)
            errors.Add("evidence_packet cannot be null");
        if (request.ToolPolicy is null)
            errors.Add("tool_policy cannot be null");
        if (request.Budget is null)
            errors.Add("budget cannot be null");
        if (request.SuccessCriteria is null || request.Constraints is null)
            errors.Add("success_criteria and constraints cannot be null");
        if (request.EvidencePacket is not null
            && (request.EvidencePacket.RelevantFilesAndSymbols is null
                || request.EvidencePacket.CommandsAndResults is null
                || request.EvidencePacket.FailedHypotheses is null
                || request.EvidencePacket.UnresolvedQuestions is null))
        {
            errors.Add("evidence_packet list fields cannot be null");
        }
        if (request.ToolPolicy is not null
            && (request.ToolPolicy.AllowedTools is null || request.ToolPolicy.DeniedTools is null))
        {
            errors.Add("tool_policy allowed_tools and denied_tools cannot be null");
        }
        if (CalculateRequestCharacters(request) > MaximumRequestCharacters)
            errors.Add($"request text cannot exceed {MaximumRequestCharacters} characters");

        if (request.Budget is null
            || request.ToolPolicy is null
            || request.ToolPolicy.AllowedTools is null
            || request.ToolPolicy.DeniedTools is null)
            return errors;

        AgentRunBudget budget = request.Budget;
        if (budget.MaxTurns is < 1 or > 32)
            errors.Add("budget.max_turns must be between 1 and 32");
        if (budget.MaxToolCalls is < 0 or > 256)
            errors.Add("budget.max_tool_calls must be between 0 and 256");
        if (budget.MaxOutputTokens is < 16 or > 128_000)
            errors.Add("budget.max_output_tokens must be between 16 and 128000");
        if (budget.MaxToolResultBytes is < 1_024 or > 4_194_304)
            errors.Add("budget.max_tool_result_bytes must be between 1024 and 4194304");
        if (budget.MaxElapsedSeconds is < 1 or > 3_600)
            errors.Add("budget.max_elapsed_seconds must be between 1 and 3600");
        if (budget.MaxRetries is < 0 or > 5)
            errors.Add("budget.max_retries must be between 0 and 5");
        if (budget.MaxConcurrency is < 1 or > 8)
            errors.Add("budget.max_concurrency must be between 1 and 8");

        AgentToolPolicy policy = request.ToolPolicy;
        if (policy.AllowMutation && policy.AllowedTools.Count == 0)
            errors.Add("mutation requires a non-empty tool_policy.allowed_tools allowlist");
        if (policy.AllowDestructive && !policy.AllowMutation)
            errors.Add("destructive tool authorization also requires allow_mutation");
        if (string.IsNullOrWhiteSpace(request.EditorSession))
        {
            if (policy.AllowMutation || policy.AllowDestructive)
                errors.Add("reasoning-only runs without editor_session cannot authorize mutation");
            if (policy.AllowedTools.Count > 0 || policy.DeniedTools.Count > 0)
                errors.Add("reasoning-only runs without editor_session cannot configure editor tools");
            if (request.RequireToolUse)
                errors.Add("reasoning-only runs without editor_session cannot require tool use");
        }

        return errors;
    }

    private static long CalculateRequestCharacters(AgentRunRequest request)
    {
        long total = CharacterCount(request.Objective)
            + CharacterCount(request.RequestedModel)
            + CharacterCount(request.ReasoningEffort)
            + CharacterCount(request.EditorSession)
            + CharacterCount(request.SystemInstructions)
            + CharacterCount(request.AdditionalInstructions);
        total += CharacterCount(request.SuccessCriteria);
        total += CharacterCount(request.Constraints);

        AgentEvidencePacket? evidence = request.EvidencePacket;
        if (evidence is null)
            return total;
        total += CharacterCount(evidence.RelevantFilesAndSymbols);
        total += CharacterCount(evidence.CurrentDiff);
        total += CharacterCount(evidence.CommandsAndResults);
        total += CharacterCount(evidence.FailedHypotheses);
        total += CharacterCount(evidence.UnresolvedQuestions);
        total += CharacterCount(evidence.NextDecision);
        return total;
    }

    private static long CharacterCount(string? value)
        => value?.Length ?? 0;

    private static long CharacterCount(IReadOnlyList<string>? values)
        => values?.Sum(static value => (long)(value?.Length ?? 0)) ?? 0;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionNamePattern();
}
