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

    private static readonly HashSet<string> s_textVerbosityLevels =
        new(StringComparer.OrdinalIgnoreCase) { "low", "medium", "high" };

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
        if (!s_textVerbosityLevels.Contains(request.TextVerbosity))
            errors.Add("text_verbosity must be one of: low, medium, high");
        if (!string.IsNullOrWhiteSpace(request.EditorSession) && !SessionNamePattern().IsMatch(request.EditorSession))
            errors.Add("editor_session must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$");
        if (request.EvidencePacket is null)
            errors.Add("evidence_packet cannot be null");
        if (request.ContextFiles is null)
            errors.Add("context_files cannot be null");
        if (request.ContextFileSnapshots is null)
            errors.Add("context_file_snapshots cannot be null");
        if (request.RepositoryAccess is null)
            errors.Add("repository_access cannot be null");
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
        if (request.RepositoryAccess?.AllowedRoots is null)
            errors.Add("repository_access.allowed_roots cannot be null");
        if (request.ContextFiles is not null)
        {
            foreach (AgentContextFileRequest contextFile in request.ContextFiles)
            {
                if (contextFile is null)
                {
                    errors.Add("context_files cannot contain null entries");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(contextFile.Path))
                    errors.Add("context_files.path is required");
                if (contextFile.StartLine is < 1)
                    errors.Add("context_files.start_line must be at least 1");
                if (contextFile.EndLine is < 1)
                    errors.Add("context_files.end_line must be at least 1");
                if (contextFile.StartLine.HasValue
                    && contextFile.EndLine.HasValue
                    && contextFile.EndLine < contextFile.StartLine)
                {
                    errors.Add("context_files.end_line must not precede start_line");
                }
                if (!string.IsNullOrWhiteSpace(contextFile.ExpectedSha256)
                    && !Sha256Pattern().IsMatch(contextFile.ExpectedSha256))
                {
                    errors.Add("context_files.expected_sha256 must be 64 hexadecimal characters");
                }
            }
        }
        if (CalculateRequestCharacters(request) > MaximumRequestCharacters)
            errors.Add($"request text cannot exceed {MaximumRequestCharacters} characters");

        if (request.Budget is null
            || request.ToolPolicy is null
            || request.ToolPolicy.AllowedTools is null
            || request.ToolPolicy.DeniedTools is null
            || request.RepositoryAccess is null
            || request.RepositoryAccess.AllowedRoots is null
            || request.ContextFiles is null)
            return errors;

        AgentRunBudget budget = request.Budget;
        if (budget.MaxTurns is < 1 or > 32)
            errors.Add("budget.max_turns must be between 1 and 32");
        if (budget.MaxToolCalls is < 0 or > 256)
            errors.Add("budget.max_tool_calls must be between 0 and 256");
        if (budget.MaxOutputTokens is < 0 or > 128_000
            || budget.MaxOutputTokens is > 0 and < 16)
        {
            errors.Add("budget.max_output_tokens must be 0 (no broker limit) or between 16 and 128000");
        }
        if (budget.MaxToolResultBytes is < 1_024 or > 4_194_304)
            errors.Add("budget.max_tool_result_bytes must be between 1024 and 4194304");
        if (budget.MaxContextFiles is < 0 or > 64)
            errors.Add("budget.max_context_files must be between 0 and 64");
        if (budget.MaxContextFileBytes is < 1_024 or > 1_048_576)
            errors.Add("budget.max_context_file_bytes must be between 1024 and 1048576");
        if (budget.MaxContextBytes is < 1_024 or > 4_194_304)
            errors.Add("budget.max_context_bytes must be between 1024 and 4194304");
        if (budget.MaxContextRenderedBytes is < 1_024 or > 8_388_608)
            errors.Add("budget.max_context_rendered_bytes must be between 1024 and 8388608");
        if (budget.MaxContextFileBytes > budget.MaxContextBytes)
            errors.Add("budget.max_context_file_bytes cannot exceed max_context_bytes");
        if (request.ContextFiles.Count > budget.MaxContextFiles)
            errors.Add("context_files exceeds budget.max_context_files");
        if (budget.MaxElapsedSeconds is < 0 or > 3_600)
            errors.Add("budget.max_elapsed_seconds must be 0 (no broker timeout) or between 1 and 3600");
        if (budget.MaxRetries is < 0 or > 5)
            errors.Add("budget.max_retries must be between 0 and 5");
        if (budget.MaxConcurrency is < 1 or > 8)
            errors.Add("budget.max_concurrency must be between 1 and 8");

        AgentToolPolicy policy = request.ToolPolicy;
        if (policy.AllowMutation && policy.AllowedTools.Count == 0)
            errors.Add("mutation requires a non-empty tool_policy.allowed_tools allowlist");
        if (policy.AllowDestructive && !policy.AllowMutation)
            errors.Add("destructive tool authorization also requires allow_mutation");
        AgentRepositoryAccessPolicy repositoryAccess = request.RepositoryAccess;
        if (repositoryAccess.Enabled)
        {
            if (repositoryAccess.AllowedRoots.Count == 0)
                errors.Add("repository_access.enabled requires at least one allowed_root");
            if (budget.MaxToolCalls == 0)
                errors.Add("repository_access.enabled requires a positive max_tool_calls budget");
        }
        else if (repositoryAccess.AllowedRoots.Count > 0)
        {
            errors.Add("repository_access.allowed_roots requires repository_access.enabled");
        }
        if (string.IsNullOrWhiteSpace(request.EditorSession))
        {
            if (policy.AllowMutation || policy.AllowDestructive)
                errors.Add("reasoning-only runs without editor_session cannot authorize mutation");
            if (policy.AllowedTools.Count > 0 || policy.DeniedTools.Count > 0)
                errors.Add("reasoning-only runs without editor_session cannot configure editor tools");
            if (request.RequireToolUse && !repositoryAccess.Enabled)
                errors.Add("runs without local tools cannot require tool use");
        }

        return errors;
    }

    private static long CalculateRequestCharacters(AgentRunRequest request)
    {
        long total = CharacterCount(request.Objective)
            + CharacterCount(request.RequestedModel)
            + CharacterCount(request.ReasoningEffort)
            + CharacterCount(request.TextVerbosity)
            + CharacterCount(request.EditorSession)
            + CharacterCount(request.SystemInstructions)
            + CharacterCount(request.AdditionalInstructions);
        total += CharacterCount(request.SuccessCriteria);
        total += CharacterCount(request.Constraints);
        if (request.ContextFiles is not null)
        {
            foreach (AgentContextFileRequest contextFile in request.ContextFiles)
            {
                if (contextFile is null)
                    continue;
                total += CharacterCount(contextFile.Path);
                total += CharacterCount(contextFile.ExpectedSha256);
            }
        }
        total += CharacterCount(request.RepositoryAccess?.AllowedRoots);

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

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
